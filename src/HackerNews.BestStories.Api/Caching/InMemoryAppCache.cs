using System.Collections.Concurrent;

namespace HackerNews.BestStories.Api.Caching;

public sealed class InMemoryAppCache(TimeProvider? timeProvider = null, IHostApplicationLifetime? lifetime = null) : IAppCache
{
    private readonly ConcurrentDictionary<string, KeyState> _entries = new();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly CancellationToken _stopping = lifetime?.ApplicationStopping ?? CancellationToken.None;

    public Task<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<CacheValue<T>>> valueFactory,
        CancellationToken cancellationToken)
        => GetAsync(key, valueFactory, false, cancellationToken);

    public Task<T> RefreshAsync<T>(
        string key,
        Func<CancellationToken, Task<CacheValue<T>>> valueFactory,
        CancellationToken cancellationToken)
        => GetAsync(key, valueFactory, true, cancellationToken);

    private async Task<T> GetAsync<T>(string key,
        Func<CancellationToken, Task<CacheValue<T>>> valueFactory,
        bool refresh, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _stopping.ThrowIfCancellationRequested();
        var state = _entries.GetOrAdd(key, _ => new KeyState());
        TaskCompletionSource<object?>? population = null;
        Task<object?> pending;
        lock (state)
        {
            if (!refresh && state.Entry is { } existing && existing.ExpiresAt > _timeProvider.GetUtcNow())
                return (T)existing.Value!;

            if (state.InFlight is null)
            {
                population = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                state.InFlight = population.Task;
            }

            pending = state.InFlight;
        }

        if (population is not null)
            _ = PopulateAsync(state, population, valueFactory);

        return (T)(await pending.WaitAsync(cancellationToken))!;
    }

    private async Task PopulateAsync<T>(KeyState state, TaskCompletionSource<object?> population,
        Func<CancellationToken, Task<CacheValue<T>>> valueFactory)
    {
        try
        {
            _stopping.ThrowIfCancellationRequested();
            var value = await valueFactory(_stopping);
            _stopping.ThrowIfCancellationRequested();
            lock (state)
            {
                state.Entry = new CacheEntry(value.Value, _timeProvider.GetUtcNow().Add(value.Ttl));
                state.InFlight = null;
                population.SetResult(value.Value);
            }
        }
        catch (OperationCanceledException ex)
        {
            lock (state)
            {
                state.InFlight = null;
                population.SetCanceled(ex.CancellationToken);
            }
        }
        catch (Exception ex)
        {
            lock (state)
            {
                state.InFlight = null;
                population.SetException(ex);
                _ = population.Task.Exception;
            }
        }
    }

    private sealed class KeyState
    {
        public CacheEntry? Entry { get; set; }
        public Task<object?>? InFlight { get; set; }
    }

    private sealed record CacheEntry(object? Value, DateTimeOffset ExpiresAt);
}
