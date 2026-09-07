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
            await cache.GetOrCreateAsync("key", _ => Value(1), TestContext.Current.CancellationToken);
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

        var first = cache.GetOrCreateAsync("key", Load, TestContext.Current.CancellationToken);
        await entered.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);
        var callers = Enumerable.Range(0, 40).Select(i => Task.Run(() =>
        {
            var wait = i % 2 == 0
                ? cache.RefreshAsync("key", Load, TestContext.Current.CancellationToken)
                : cache.GetOrCreateAsync("key", Load, TestContext.Current.CancellationToken);
            Assert.False(wait.IsCompleted);
            return Task.FromResult(wait);
        }, TestContext.Current.CancellationToken)).ToArray();
        var waits = await Task.WhenAll(callers).WaitAsync(Guard, TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
        gate.SetResult(new(2, TimeSpan.FromSeconds(10)));
        Assert.Equal(2, await first.WaitAsync(Guard, TestContext.Current.CancellationToken));
        Assert.All(await Task.WhenAll(waits).WaitAsync(Guard, TestContext.Current.CancellationToken), value => Assert.Equal(2, value));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Refresh_PreservesWarmEntry_AndStartsTtlAtCompletion()
    {
        var time = new ManualTime();
        var cache = new InMemoryAppCache(time);
        await cache.GetOrCreateAsync("key", _ => Value(1), TestContext.Current.CancellationToken);
        var gate = Gate<CacheValue<int>>();
        var refresh = cache.RefreshAsync("key", _ => gate.Task, TestContext.Current.CancellationToken);
        Assert.Equal(1, await cache.GetOrCreateAsync<int>("key", _ => throw new Exception("Unexpected load"), TestContext.Current.CancellationToken));
        time.Advance(100);
        var expiredRead = cache.GetOrCreateAsync<int>("key", _ => throw new Exception("Duplicate load"), TestContext.Current.CancellationToken);
        Assert.False(expiredRead.IsCompleted);
        gate.SetResult(new(2, TimeSpan.FromSeconds(3)));
        Assert.Equal(2, await refresh.WaitAsync(Guard, TestContext.Current.CancellationToken));
        Assert.Equal(2, await expiredRead.WaitAsync(Guard, TestContext.Current.CancellationToken));
        time.Advance(2);
        Assert.Equal(2, await cache.GetOrCreateAsync("key", _ => Value(3), TestContext.Current.CancellationToken));
        time.Advance(1);
        Assert.Equal(3, await cache.GetOrCreateAsync("key", _ => Value(3), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledWaiter_DoesNotCancelSharedLoad(bool warm)
    {
        var cache = new InMemoryAppCache();
        if (warm) await cache.GetOrCreateAsync("key", _ => Value(1), TestContext.Current.CancellationToken);
        var gate = Gate<CacheValue<int>>();
        using var caller = new CancellationTokenSource();
        CancellationToken loaderToken = CancellationToken.None;
        Task<CacheValue<int>> Load(CancellationToken ct) { loaderToken = ct; return gate.Task; }
        var cancelled = warm
            ? cache.RefreshAsync("key", Load, caller.Token)
            : cache.GetOrCreateAsync("key", Load, caller.Token);
        // The remaining waiter deliberately cannot cancel; only caller.Token is cancelled.
#pragma warning disable xUnit1051
        var remaining = cache.RefreshAsync<int>("key", _ => throw new Exception("Duplicate load"), CancellationToken.None);
#pragma warning restore xUnit1051
        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(Guard, TestContext.Current.CancellationToken));
        Assert.False(loaderToken.IsCancellationRequested);
        Assert.False(remaining.IsCompleted);
        gate.SetResult(new(7, TimeSpan.FromSeconds(10)));
        Assert.Equal(7, await remaining.WaitAsync(Guard, TestContext.Current.CancellationToken));
        Assert.Equal(7, await cache.GetOrCreateAsync("key", _ => Value(8), TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreCancelledCaller_DoesNotStartLoad(bool refresh)
    {
        var cache = new InMemoryAppCache();
        using var caller = new CancellationTokenSource();
        await caller.CancelAsync();
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
        var blocked = cache.GetOrCreateAsync("a", _ => gate.Task, TestContext.Current.CancellationToken);
        Assert.Equal(2, await cache.GetOrCreateAsync("b", _ => Value(2), TestContext.Current.CancellationToken).WaitAsync(Guard, TestContext.Current.CancellationToken));
        Assert.False(blocked.IsCompleted);
        gate.SetResult(new(1, TimeSpan.FromSeconds(10)));
        Assert.Equal(1, await blocked.WaitAsync(Guard, TestContext.Current.CancellationToken));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FaultedFill_AllowsRetry(bool warm)
    {
        var cache = new InMemoryAppCache();
        if (warm) await cache.GetOrCreateAsync("key", _ => Value(1), TestContext.Current.CancellationToken);
        var gate = Gate<CacheValue<int>>();
        var first = cache.RefreshAsync("key", _ => gate.Task, TestContext.Current.CancellationToken);
        var second = cache.RefreshAsync<int>("key", _ => throw new Exception("Duplicate load"), TestContext.Current.CancellationToken);
        gate.SetException(new InvalidOperationException("broken"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => first.WaitAsync(Guard, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<InvalidOperationException>(() => second.WaitAsync(Guard, TestContext.Current.CancellationToken));
        if (warm) Assert.Equal(1, await cache.GetOrCreateAsync("key", _ => Value(99), TestContext.Current.CancellationToken));
        Assert.Equal(2, await cache.RefreshAsync("key", _ => Value(2), TestContext.Current.CancellationToken));
        Assert.Equal(2, await cache.GetOrCreateAsync("key", _ => Value(99), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SynchronousFactoryFault_DoesNotLeaveInFlightState()
    {
        var cache = new InMemoryAppCache();
        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.GetOrCreateAsync<int>("key",
            _ => throw new InvalidOperationException("broken"), TestContext.Current.CancellationToken));
        Assert.Equal(2, await cache.GetOrCreateAsync("key", _ => Value(2), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancelledFill_AllowsRetry()
    {
        var cache = new InMemoryAppCache();
        // Cancellation must originate in the fill, never in this caller.
#pragma warning disable xUnit1051
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetOrCreateAsync("key",
            _ => Task.FromCanceled<CacheValue<int>>(new CancellationToken(true)), CancellationToken.None));
#pragma warning restore xUnit1051
        Assert.Equal(2, await cache.GetOrCreateAsync("key", _ => Value(2), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Shutdown_CancelsSharedLoad_WithoutPublishing()
    {
        using var lifetime = new Lifetime();
        var cache = new InMemoryAppCache(lifetime: lifetime);
        var entered = Gate<CancellationToken>();
        // A non-cancellable caller isolates application shutdown as the cancellation source.
#pragma warning disable xUnit1051
        var pending = cache.GetOrCreateAsync<int>("key", async ct =>
        {
            entered.SetResult(ct);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new(1, TimeSpan.FromSeconds(10));
        }, CancellationToken.None);
#pragma warning restore xUnit1051
        var token = await entered.Task.WaitAsync(Guard, TestContext.Current.CancellationToken);
        lifetime.StopApplication();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(Guard, TestContext.Current.CancellationToken));
        Assert.True(token.IsCancellationRequested);
        // Shutdown must also reject subsequent non-cancellable callers.
#pragma warning disable xUnit1051
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetOrCreateAsync("key", _ => Value(2), CancellationToken.None));
#pragma warning restore xUnit1051
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
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => _stopping.Token;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() => _stopping.Cancel();
        public void Dispose() => _stopping.Dispose();
    }
}
