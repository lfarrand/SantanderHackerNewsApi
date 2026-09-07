using HackerNews.BestStories.Api.Caching;
using HackerNews.BestStories.Api.Clients;
using HackerNews.BestStories.Api.Configuration;
using HackerNews.BestStories.Api.Models;
using HackerNews.BestStories.Api.Services;
using HackerNews.BestStories.Api.Errors;
using System.Text.Json;

namespace HackerNews.BestStories.Api.Tests.Services;

public sealed class CompleteSnapshotTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PartialFailure_UsesColdErrorOrStale_CooldownDoesNotExtend_RecoversAtExactExpiry(bool warm)
    {
        var clock = new ManualTime();
        var upstream = new Upstream();
        var service = new BestStoriesService(upstream, new InMemoryAppCache(clock), new(), clock);
        if (warm) Assert.Equal(2, (await service.GetBestStoriesAsync(10, default)).Count);
        upstream.Item = (id, _) => id == 2 ? throw new JsonException("malformed item") : Task.FromResult<HackerNewsItem?>(Story(99));
        if (warm) await service.RefreshAsync(default);
        else await Assert.ThrowsAsync<UpstreamException>(() => service.RefreshAsync(default));
        var calls = upstream.IdCalls;
        clock.Advance(TimeSpan.FromSeconds(29));
        if (warm)
        {
            await service.RefreshAsync(default);
            Assert.Equal(new long[] { 1, 2 }, (await service.GetBestStoriesAsync(10, default)).Select(x => x.Id));
        }
        else
        {
            await Assert.ThrowsAsync<UpstreamException>(() => service.RefreshAsync(default));
            await Assert.ThrowsAsync<UpstreamException>(() => service.GetBestStoriesAsync(1, default));
        }
        Assert.Equal(calls, upstream.IdCalls);
        upstream.Ids = [3];
        upstream.Item = (id, _) => Task.FromResult<HackerNewsItem?>(Story(id));
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(3, Assert.Single(await service.GetBestStoriesAsync(10, default)).Id);
        Assert.Equal(calls + 1, upstream.IdCalls);
    }

    [Fact]
    public async Task SuccessfulConcurrentFill_HydratesAllCandidates_WithinWorkerLimit()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var upstream = new Upstream { Ids = Enumerable.Range(1, 50).Select(x => (long)x).ToArray() };
        upstream.Item = async (id, ct) =>
        {
            var count = Interlocked.Increment(ref active);
            Assert.InRange(count, 1, 3);
            if (count == 3) entered.TrySetResult();
            try { await release.Task.WaitAsync(ct); return Story(id, (int)id); }
            finally { Interlocked.Decrement(ref active); }
        };
        var service = new BestStoriesService(upstream, new InMemoryAppCache(), new() { MaxConcurrency = 3 });
        var first = service.GetBestStoriesAsync(10, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = service.GetBestStoriesAsync(50, default);
        var refresh = service.RefreshAsync(default);
        Assert.Equal(3, upstream.ItemCalls);
        release.SetResult();
        Assert.Equal(10, (await first.WaitAsync(TimeSpan.FromSeconds(5))).Count);
        Assert.Equal(50, (await second.WaitAsync(TimeSpan.FromSeconds(5))).Count);
        await refresh.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, upstream.IdCalls);
        Assert.Equal(50, upstream.ItemCalls);
        Assert.Equal(0, active);
    }

    [Fact]
    public async Task RepeatedFailedRetries_PreserveLastGoodBeyondFailureTtl()
    {
        var clock = new ManualTime();
        var upstream = new Upstream();
        var service = new BestStoriesService(upstream, new InMemoryAppCache(clock), new(), clock);
        await service.GetBestStoriesAsync(2, default);
        upstream.Item = (_, _) => throw new UpstreamTimeoutException("request timeout");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            clock.Advance(TimeSpan.FromSeconds(60));
            Assert.Equal(new long[] { 1, 2 }, (await service.GetBestStoriesAsync(2, default)).Select(x => x.Id));
            await service.RefreshAsync(default);
            Assert.Equal(attempt + 2, upstream.IdCalls);
        }
    }

    [Fact]
    public async Task EmptyCollection_IsSuccessfulAndReplacesPreviousSnapshot()
    {
        var upstream = new Upstream();
        var service = new BestStoriesService(upstream, new InMemoryAppCache(), new());
        Assert.Equal(2, (await service.GetBestStoriesAsync(10, default)).Count);
        upstream.Ids = [];
        await service.RefreshAsync(default);
        Assert.Empty(await service.GetBestStoriesAsync(10, default));
        Assert.Equal(2, upstream.ItemCalls);
    }

    [Fact]
    public async Task ConcurrentDemand_BoundsWorkers_AndDrainsOnFailure()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var upstream = new Upstream { Ids = Enumerable.Range(1, 100).Select(x => (long)x).ToArray() };
        upstream.Item = async (id, ct) =>
        {
            var count = Interlocked.Increment(ref active);
            Assert.InRange(count, 1, 3);
            if (count == 3) entered.TrySetResult();
            try
            {
                await release.Task.WaitAsync(ct);
                if (id == 1) throw new HttpRequestException("failure");
                await Task.Delay(Timeout.Infinite, ct);
                return Story(id);
            }
            finally { Interlocked.Decrement(ref active); }
        };
        var service = new BestStoriesService(upstream, new InMemoryAppCache(), new() { MaxConcurrency = 3 });
        var reads = Enumerable.Range(0, 10).Select(_ => service.GetBestStoriesAsync(1, default)).ToArray();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, upstream.ItemCalls);
        release.SetResult();
        foreach (var read in reads) await Assert.ThrowsAsync<UpstreamException>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, active);
        Assert.Equal(1, upstream.IdCalls);
    }

    [Fact]
    public async Task RefreshDeadline_CancelsWorkers_CachesTypedTimeout()
    {
        var clock = new ManualTime();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var upstream = new Upstream();
        upstream.Item = async (_, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return null; };
        var service = new BestStoriesService(upstream, new InMemoryAppCache(clock), new() { RefreshTimeoutSeconds = 2 }, clock);
        var read = service.GetBestStoriesAsync(1, default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(2));
        await Assert.ThrowsAsync<UpstreamTimeoutException>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
        await Assert.ThrowsAsync<UpstreamTimeoutException>(() => service.GetBestStoriesAsync(1, default));
        Assert.Equal(1, upstream.IdCalls);
    }

    [Fact]
    public async Task ProgrammingFault_DoesNotServeStale_AndAllowsRecovery()
    {
        var upstream = new Upstream();
        var service = new BestStoriesService(upstream, new InMemoryAppCache(), new());
        await service.GetBestStoriesAsync(1, default);
        upstream.Item = (_, _) => throw new InvalidOperationException("bug");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RefreshAsync(default));
        upstream.Item = (id, _) => Task.FromResult<HackerNewsItem?>(Story(id));
        await service.RefreshAsync(default);
        Assert.Equal(3, upstream.IdCalls);
    }

    [Fact]
    public async Task Ranking_HydratesDistinctCandidatesBeyondCap_ThenTakesN_WithStableTies()
    {
        var upstream = new Upstream { Ids = [2, 1, 2, 3, 4, 5, 6] };
        upstream.Item = (id, _) => Task.FromResult<HackerNewsItem?>(id switch
        {
            4 => null,
            5 => new() { Id = id, Type = "comment", Title = "comment" },
            6 => new() { Id = id, Type = "story", Title = " " },
            _ => Story(id, id == 3 ? 100 : 10)
        });
        var service = new BestStoriesService(upstream, new InMemoryAppCache(), new() { MaxStories = 2 });
        Assert.Equal(new long[] { 3, 1 }, (await service.GetBestStoriesAsync(2, default)).Select(x => x.Id));
        Assert.Equal(new long[] { 3, 1, 2 }, (await service.GetBestStoriesAsync(10, default)).Select(x => x.Id));
        Assert.Equal(1, upstream.IdCalls);
        Assert.Equal(6, upstream.ItemCalls);
    }

    internal static HackerNewsItem Story(long id, int score = 10) => new() { Id = id, Type = "story", Title = $"Story {id}", Score = score };

    internal sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        private readonly List<ManualTimer> _timers = [];
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan amount)
        {
            _now += amount;
            foreach (var timer in _timers.ToArray()) timer.Fire(_now);
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period);
            _timers.Add(timer);
            return timer;
        }
        private sealed class ManualTimer(ManualTime clock, TimerCallback callback, object? state) : ITimer
        {
            private DateTimeOffset _due;
            private bool _disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period) { _due = clock.GetUtcNow() + dueTime; return true; }
            public void Fire(DateTimeOffset now) { if (!_disposed && now >= _due) { _disposed = true; callback(state); } }
            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    internal sealed class Upstream : IHackerNewsClient
    {
        public long[] Ids = [1, 2];
        public int IdCalls;
        public int ItemCalls;
        public Func<long, CancellationToken, Task<HackerNewsItem?>> Item = (id, _) => Task.FromResult<HackerNewsItem?>(Story(id));
        public Task<long[]> GetBestStoryIdsAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref IdCalls);
            return Task.FromResult(Ids);
        }
        public Task<HackerNewsItem?> GetItemAsync(long id, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref ItemCalls);
            return Item(id, cancellationToken);
        }
    }
}
