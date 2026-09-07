using HackerNews.BestStories.Api.Caching;
using Microsoft.Extensions.Hosting;

namespace HackerNews.BestStories.Api.Tests.Caching;

public sealed class InMemoryAppCacheTests
{
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(5);
    private static TaskCompletionSource<T> Gate<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task<CacheValue<int>> Value(int value, int seconds = 10)
        => Task.FromResult(new CacheValue<int>(value, TimeSpan.FromSeconds(seconds)));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MixedDemand_SharesOneFill(bool expired)
    {
        var time = new ManualTime();
        IAppCache cache = new InMemoryAppCache(time);
        if (expired)
        {
            await cache.GetOrCreateAsync("key", _ => Value(1), default);
            time.Advance(10);
        }

        var gate = Gate<CacheValue<int>>();
        var entered = Gate<bool>();
        var calls = 0;
        Task<CacheValue<int>> Load(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult(true);
            return gate.Task;
        }

        var first = cache.GetOrCreateAsync("key", Load, default);
        await entered.Task.WaitAsync(Guard);
        var callers = Enumerable.Range(0, 40).Select(i => Task.Run(() =>
        {
            var wait = i % 2 == 0
                ? cache.RefreshAsync("key", Load, default)
                : cache.GetOrCreateAsync("key", Load, default);
            Assert.False(wait.IsCompleted);
            return Task.FromResult(wait);
        })).ToArray();
        var waits = await Task.WhenAll(callers).WaitAsync(Guard);
        Assert.Equal(1, calls);
        gate.SetResult(new(2, TimeSpan.FromSeconds(10)));
        Assert.Equal(2, await first.WaitAsync(Guard));
        Assert.All(await Task.WhenAll(waits).WaitAsync(Guard), value => Assert.Equal(2, value));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Refresh_PreservesWarmEntry_AndStartsTtlAtCompletion()
    {
        var time = new ManualTime();
        var cache = new InMemoryAppCache(time);
        await cache.GetOrCreateAsync("key", _ => Value(1), default);
        var gate = Gate<CacheValue<int>>();
        var refresh = cache.RefreshAsync("key", _ => gate.Task, default);
        Assert.Equal(1, await cache.GetOrCreateAsync<int>("key", _ => throw new Exception("Unexpected load"), default));
        time.Advance(100);
        var expiredRead = cache.GetOrCreateAsync<int>("key", _ => throw new Exception("Duplicate load"), default);
        Assert.False(expiredRead.IsCompleted);
        gate.SetResult(new(2, TimeSpan.FromSeconds(3)));
        Assert.Equal(2, await refresh.WaitAsync(Guard));
        Assert.Equal(2, await expiredRead.WaitAsync(Guard));
        time.Advance(2);
        Assert.Equal(2, await cache.GetOrCreateAsync("key", _ => Value(3), default));
        time.Advance(1);
        Assert.Equal(3, await cache.GetOrCreateAsync("key", _ => Value(3), default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledWaiter_DoesNotCancelSharedLoad(bool warm)
    {
        var cache = new InMemoryAppCache();
        if (warm) await cache.GetOrCreateAsync("key", _ => Value(1), default);
        var gate = Gate<CacheValue<int>>();
        using var caller = new CancellationTokenSource();
        CancellationToken loaderToken = default;
        Task<CacheValue<int>> Load(CancellationToken ct) { loaderToken = ct; return gate.Task; }
        var cancelled = warm
            ? cache.RefreshAsync("key", Load, caller.Token)
            : cache.GetOrCreateAsync("key", Load, caller.Token);
        var remaining = cache.RefreshAsync<int>("key", _ => throw new Exception("Duplicate load"), default);
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Guard));
        Assert.False(loaderToken.IsCancellationRequested);
        Assert.False(remaining.IsCompleted);
        gate.SetResult(new(7, TimeSpan.FromSeconds(10)));
        Assert.Equal(7, await remaining.WaitAsync(Guard));
        Assert.Equal(7, await cache.GetOrCreateAsync("key", _ => Value(8), default));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreCancelledCaller_DoesNotStartLoad(bool refresh)
    {
        var cache = new InMemoryAppCache();
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var calls = 0;
        Task<CacheValue<int>> Load(CancellationToken ct) { calls++; return Value(1); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh
            ? cache.RefreshAsync("key", Load, caller.Token)
            : cache.GetOrCreateAsync("key", Load, caller.Token));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Keys_LoadIndependently()
    {
        var cache = new InMemoryAppCache();
        var gate = Gate<CacheValue<int>>();
        var blocked = cache.GetOrCreateAsync("a", _ => gate.Task, default);
        Assert.Equal(2, await cache.GetOrCreateAsync("b", _ => Value(2), default).WaitAsync(Guard));
        Assert.False(blocked.IsCompleted);
        gate.SetResult(new(1, TimeSpan.FromSeconds(10)));
        Assert.Equal(1, await blocked.WaitAsync(Guard));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FaultedFill_AllowsRetry(bool warm)
    {
        var cache = new InMemoryAppCache();
        if (warm) await cache.GetOrCreateAsync("key", _ => Value(1), default);
        var gate = Gate<CacheValue<int>>();
        var first = cache.RefreshAsync("key", _ => gate.Task, default);
        var second = cache.RefreshAsync<int>("key", _ => throw new Exception("Duplicate load"), default);
        gate.SetException(new InvalidOperationException("broken"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.WaitAsync(Guard));
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.WaitAsync(Guard));
        if (warm) Assert.Equal(1, await cache.GetOrCreateAsync("key", _ => Value(99), default));
        Assert.Equal(2, await cache.RefreshAsync("key", _ => Value(2), default));
        Assert.Equal(2, await cache.GetOrCreateAsync("key", _ => Value(99), default));
    }

    [Fact]
    public async Task SynchronousFactoryFault_DoesNotLeaveInFlightState()
    {
        var cache = new InMemoryAppCache();
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetOrCreateAsync<int>("key",
            _ => throw new InvalidOperationException("broken"), default));
        Assert.Equal(2, await cache.GetOrCreateAsync("key", _ => Value(2), default));
    }

    [Fact]
    public async Task CancelledFill_AllowsRetry()
    {
        var cache = new InMemoryAppCache();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetOrCreateAsync<int>("key",
            _ => Task.FromCanceled<CacheValue<int>>(new CancellationToken(true)), default));
        Assert.Equal(2, await cache.GetOrCreateAsync("key", _ => Value(2), default));
    }

    [Fact]
    public async Task Shutdown_CancelsSharedLoad_WithoutPublishing()
    {
        using var lifetime = new Lifetime();
        var cache = new InMemoryAppCache(lifetime: lifetime);
        var entered = Gate<CancellationToken>();
        var pending = cache.GetOrCreateAsync<int>("key", async ct =>
        {
            entered.SetResult(ct);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new(1, TimeSpan.FromSeconds(10));
        }, default);
        var token = await entered.Task.WaitAsync(Guard);
        lifetime.StopApplication();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Guard));
        Assert.True(token.IsCancellationRequested);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetOrCreateAsync("key", _ => Value(2), default));
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(int seconds) => _now = _now.AddSeconds(seconds);
    }

    private sealed class Lifetime : IHostApplicationLifetime, IDisposable
    {
        private readonly CancellationTokenSource _stopping = new();
        public CancellationToken ApplicationStarted => default;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => default;
        public void StopApplication() => _stopping.Cancel();
        public void Dispose() => _stopping.Dispose();
    }
}