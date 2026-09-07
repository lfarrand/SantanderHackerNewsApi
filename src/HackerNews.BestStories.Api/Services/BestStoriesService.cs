using System.Collections.Concurrent;
using System.Text.Json;
using HackerNews.BestStories.Api.Caching;
using HackerNews.BestStories.Api.Clients;
using HackerNews.BestStories.Api.Configuration;
using HackerNews.BestStories.Api.Errors;
using HackerNews.BestStories.Api.Mapping;
using HackerNews.BestStories.Api.Models;

namespace HackerNews.BestStories.Api.Services;

public sealed class BestStoriesService(IHackerNewsClient client, IAppCache cache, HackerNewsOptions options,
    TimeProvider? timeProvider = null) : IBestStoriesService
{
    private const string CacheKey = "best-stories";
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private IReadOnlyList<StoryDto>? _lastKnownGood;
    private Snapshot? _failureOutcome;
    private DateTimeOffset _retryAt;

    public async Task<IReadOnlyList<StoryDto>> GetBestStoriesAsync(int requestedCount, CancellationToken cancellationToken)
    {
        var snapshot = await cache.GetOrCreateAsync(CacheKey, PopulateAsync, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return snapshot.GetStories().Take(requestedCount).ToArray();
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var snapshot = await cache.RefreshAsync(CacheKey, PopulateAsync, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        _ = snapshot.GetStories();
    }

    private async Task<CacheValue<Snapshot>> PopulateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var remaining = _retryAt - _timeProvider.GetUtcNow();
        if (_failureOutcome is not null && remaining > TimeSpan.Zero)
            return new(_failureOutcome, remaining);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(options.RefreshTimeoutSeconds), _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var stories = await LoadStoriesAsync(linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            _lastKnownGood = stories;
            _failureOutcome = null;
            return new(new(stories, null), TimeSpan.FromSeconds(options.CacheTtlSeconds));
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            return Failure(new UpstreamTimeoutException("Snapshot refresh timed out.", ex));
        }
        catch (Exception ex) when (ex is UpstreamException or HttpRequestException or JsonException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Failure(ex as UpstreamException ?? new UpstreamException("Snapshot fetch failed.", ex));
        }
    }

    private CacheValue<Snapshot> Failure(UpstreamException error)
    {
        var ttl = TimeSpan.FromSeconds(options.FailureCacheTtlSeconds);
        _retryAt = _timeProvider.GetUtcNow().Add(ttl);
        _failureOutcome = new(_lastKnownGood, _lastKnownGood is null ? error : null);
        return new(_failureOutcome, ttl);
    }

    private async Task<IReadOnlyList<StoryDto>> LoadStoriesAsync(CancellationToken cancellationToken)
    {
        var ids = await client.GetBestStoryIdsAsync(cancellationToken);
        var candidates = ids.Distinct().Take(options.MaxStories);
        var stories = new ConcurrentBag<StoryDto>();
        await Parallel.ForEachAsync(candidates, new ParallelOptions
        {
            MaxDegreeOfParallelism = options.MaxConcurrency,
            CancellationToken = cancellationToken
        }, async (id, token) =>
        {
            var item = await client.GetItemAsync(id, token);
            var story = item is null ? null : StoryMapper.Map(item);
            if (story is not null) stories.Add(story);
        });
        return stories.OrderByDescending(story => story.Score).ThenBy(story => story.Id).ToArray();
    }

    private sealed record Snapshot(IReadOnlyList<StoryDto>? Stories, UpstreamException? Error)
    {
        public IReadOnlyList<StoryDto> GetStories()
        {
            if (Error is not null) throw Error;
            return Stories!;
        }
    }
}
