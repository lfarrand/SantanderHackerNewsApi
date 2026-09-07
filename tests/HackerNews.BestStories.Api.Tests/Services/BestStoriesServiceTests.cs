using HackerNews.BestStories.Api.Caching;
using HackerNews.BestStories.Api.Clients;
using HackerNews.BestStories.Api.Configuration;
using HackerNews.BestStories.Api.Models;
using HackerNews.BestStories.Api.Services;

namespace HackerNews.BestStories.Api.Tests.Services;

public sealed class BestStoriesServiceTests
{
    [Fact]
    public async Task GetBestStoriesAsync_ReturnsSortedStories_UpToRequestedCount()
    {
        var client = new FakeClient(
            [1, 2, 3],
            new Dictionary<long, HackerNewsItem>
            {
                [1] = new() { Id = 1, Type = "story", Score = 10, Title = "A" },
                [2] = new() { Id = 2, Type = "story", Score = 99, Title = "B" },
                [3] = new() { Id = 3, Type = "comment", Score = 100, Title = "C" }
            });
        var cache = new InMemoryAppCache();
        var options = new HackerNewsOptions { MaxStories = 3, CacheTtlSeconds = 60, MaxConcurrency = 4 };
        var service = new BestStoriesService(client, cache, options);

        var result = await service.GetBestStoriesAsync(2, CancellationToken.None);

        Assert.Collection(
            result,
            story => Assert.Equal(2, story.Id),
            story => Assert.Equal(1, story.Id));
    }

    [Fact]
    public async Task GetBestStoriesAsync_DoesNotPoisonCache_WithVaryingN()
    {
        var client = new FakeClient(
            [1, 2],
            new Dictionary<long, HackerNewsItem>
            {
                [1] = new() { Id = 1, Type = "story", Score = 10, Title = "A" },
                [2] = new() { Id = 2, Type = "story", Score = 20, Title = "B" }
            });
        var cache = new InMemoryAppCache();
        var options = new HackerNewsOptions { MaxStories = 2, CacheTtlSeconds = 60 };
        var service = new BestStoriesService(client, cache, options);

        _ = await service.GetBestStoriesAsync(1, CancellationToken.None);
        var result = await service.GetBestStoriesAsync(2, CancellationToken.None);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public async Task GetBestStoriesAsync_PropagatesCancellation()
    {
        var client = new CancellationClient();
        var cache = new InMemoryAppCache();
        var service = new BestStoriesService(client, cache, new HackerNewsOptions());

        var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetBestStoriesAsync(10, cts.Token));
    }

    [Fact]
    public async Task GetBestStoriesAsync_ReturnsAndCachesLastKnownGood_OnFailureAfterRefresh()
    {
        var client = new AlternatingClient(
            [1],
            new Dictionary<long, HackerNewsItem> { [1] = new() { Id = 1, Type = "story", Score = 10, Title = "A" } });
        var cache = new InMemoryAppCache();
        var service = new BestStoriesService(client, cache, new HackerNewsOptions
        {
            CacheTtlSeconds = 60,
            FailureCacheTtlSeconds = 30
        });

        _ = await service.GetBestStoriesAsync(1, CancellationToken.None);
        client.FailNext = true;
        await service.RefreshAsync(CancellationToken.None);
        Assert.Equal(2, client.GetBestStoriesCalls);

        var result = await service.GetBestStoriesAsync(1, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal(1, result[0].Id);

        var callsAfterFailure = client.GetBestStoriesCalls;
        var cachedFallback = await service.GetBestStoriesAsync(1, CancellationToken.None);

        Assert.Single(cachedFallback);
        Assert.Equal(1, cachedFallback[0].Id);
        Assert.Equal(callsAfterFailure, client.GetBestStoriesCalls);
    }

    [Fact]
    public async Task GetBestStoriesAsync_UsesCache_OnSecondCall()
    {
        var client = new FakeClient([1], new Dictionary<long, HackerNewsItem>
        {
            [1] = new() { Id = 1, Type = "story", Score = 10, Title = "A" }
        });
        var cache = new InMemoryAppCache();
        var service = new BestStoriesService(client, cache, new HackerNewsOptions { CacheTtlSeconds = 60 });

        _ = await service.GetBestStoriesAsync(1, CancellationToken.None);
        _ = await service.GetBestStoriesAsync(1, CancellationToken.None);

        Assert.Equal(1, client.GetBestStoriesCalls);
    }

    [Fact]
    public async Task GetBestStoriesAsync_ReturnsEmpty_WhenClientFails_AndNoCache()
    {
        var client = new ThrowingClient();
        var cache = new InMemoryAppCache();
        var service = new BestStoriesService(client, cache, new HackerNewsOptions());

        await Assert.ThrowsAsync<HackerNews.BestStories.Api.Errors.UpstreamException>(
            () => service.GetBestStoriesAsync(10, CancellationToken.None));
    }

    [Fact]
    public async Task RefreshAsync_ClearsCache()
    {
        var client = new FakeClient([1], new Dictionary<long, HackerNewsItem>
        {
            [1] = new() { Id = 1, Type = "story", Score = 10, Title = "A" }
        });
        var cache = new InMemoryAppCache();
        var service = new BestStoriesService(client, cache, new HackerNewsOptions { CacheTtlSeconds = 60 });

        _ = await service.GetBestStoriesAsync(1, CancellationToken.None);
        await service.RefreshAsync(CancellationToken.None);
        Assert.Equal(2, client.GetBestStoriesCalls);
        _ = await service.GetBestStoriesAsync(1, CancellationToken.None);

        Assert.Equal(2, client.GetBestStoriesCalls);
    }

    [Fact]
    public async Task RefreshAsync_PreservesSnapshotUntilReplacementCompletes()
    {
        var client = new GatedClient();
        var service = new BestStoriesService(client, new InMemoryAppCache(), new HackerNewsOptions());
        client.Release.SetResult([1]);
        Assert.Equal(1, Assert.Single(await service.GetBestStoriesAsync(1, default)).Id);
        client.Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var refresh = service.RefreshAsync(default);
        var joined = service.RefreshAsync(default);
        Assert.Equal(2, client.IdCalls);
        Assert.False(refresh.IsCompleted);
        Assert.Equal(1, Assert.Single(await service.GetBestStoriesAsync(1, default)).Id);
        client.Release.SetResult([2]);
        await Task.WhenAll(refresh, joined).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, Assert.Single(await service.GetBestStoriesAsync(1, default)).Id);
        Assert.Equal(2, client.IdCalls);
        Assert.Equal(2, client.ItemCalls);
    }

    [Fact]
    public async Task ConcurrentColdReadsAndRefresh_ShareLoad_DespiteCallerCancellation()
    {
        var client = new GatedClient();
        var service = new BestStoriesService(client, new InMemoryAppCache(), new HackerNewsOptions());
        using var caller = new CancellationTokenSource();
        var cancelled = service.GetBestStoriesAsync(1, caller.Token);
        var remaining = service.GetBestStoriesAsync(2, default);
        var refresh = service.RefreshAsync(default);
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, client.IdCalls);
        Assert.False(client.LoaderToken.IsCancellationRequested);
        client.Release.SetResult([1, 2]);
        var stories = await remaining.WaitAsync(TimeSpan.FromSeconds(5));
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(new long[] { 2, 1 }, stories.Select(story => story.Id));
        Assert.Equal(1, client.IdCalls);
        Assert.Equal(2, client.ItemCalls);
    }

    private sealed class GatedClient : IHackerNewsClient
    {
        public TaskCompletionSource<long[]> Release { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int IdCalls;
        public int ItemCalls;
        public CancellationToken LoaderToken { get; private set; }

        public Task<long[]> GetBestStoryIdsAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref IdCalls);
            LoaderToken = cancellationToken;
            return Release.Task.WaitAsync(cancellationToken);
        }

        public Task<HackerNewsItem?> GetItemAsync(long id, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ItemCalls);
            return Task.FromResult<HackerNewsItem?>(new() { Id = id, Type = "story", Title = $"Story {id}", Score = (int)id });
        }
    }

    private sealed class FakeClient(long[] ids, Dictionary<long, HackerNewsItem> items) : IHackerNewsClient
    {
        public int GetBestStoriesCalls { get; private set; }

        public Task<long[]> GetBestStoryIdsAsync(CancellationToken cancellationToken)
        {
            GetBestStoriesCalls++;
            return Task.FromResult(ids);
        }

        public Task<HackerNewsItem?> GetItemAsync(long id, CancellationToken cancellationToken)
        {
            items.TryGetValue(id, out var value);
            return Task.FromResult(value);
        }
    }

    private sealed class CancellationClient : IHackerNewsClient
    {
        public Task<long[]> GetBestStoryIdsAsync(CancellationToken cancellationToken)
            => Task.FromCanceled<long[]>(cancellationToken);

        public Task<HackerNewsItem?> GetItemAsync(long id, CancellationToken cancellationToken)
            => Task.FromResult<HackerNewsItem?>(null);
    }

    private sealed class AlternatingClient(long[] ids, Dictionary<long, HackerNewsItem> items) : IHackerNewsClient
    {
        public bool FailNext { get; set; }

        public int GetBestStoriesCalls { get; private set; }

        public Task<long[]> GetBestStoryIdsAsync(CancellationToken cancellationToken)
        {
            GetBestStoriesCalls++;
            if (FailNext) throw new HttpRequestException("failure");
            return Task.FromResult(ids);
        }

        public Task<HackerNewsItem?> GetItemAsync(long id, CancellationToken cancellationToken)
        {
            if (FailNext) throw new HttpRequestException("failure");
            items.TryGetValue(id, out var value);
            return Task.FromResult(value);
        }
    }

    private sealed class ThrowingClient : IHackerNewsClient
    {
        public Task<long[]> GetBestStoryIdsAsync(CancellationToken cancellationToken)
            => throw new HttpRequestException("failure");

        public Task<HackerNewsItem?> GetItemAsync(long id, CancellationToken cancellationToken)
            => throw new HttpRequestException("failure");
    }
}
