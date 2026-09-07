using System.Net;
using System.Text;
using HackerNews.BestStories.Api.Clients;
using HackerNews.BestStories.Api.Configuration;
using HackerNews.BestStories.Api.Errors;
using HackerNews.BestStories.Api.Services;
using HackerNews.BestStories.Api.Tests.Services;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace HackerNews.BestStories.Api.Tests.Endpoints;

public sealed class UpstreamResponseTests
{
    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, false)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    [InlineData(true, true, true)]
    public async Task InterruptedBody_UsesFailurePolicy_AndRecovers(bool stale, bool failList, bool httpIo)
    {
        using var handler = new ResponseHandler();
        handler.Failure = path => path.EndsWith(failList ? "beststories.json" : "item/2.json")
            ? new StreamContent(new InterruptedStream(httpIo, failList ? "[1," : "{\"id\":2,"))
            : null;
        await AssertFailurePolicy(handler, stale);
        Assert.True(handler.Interruptions > 0);
    }

    [Theory]
    [InlineData(false, -62135596801L)]
    [InlineData(false, 253402300800L)]
    [InlineData(true, -62135596801L)]
    [InlineData(true, 253402300800L)]
    public async Task InvalidTimestamp_UsesFailurePolicy_AndRecovers(bool stale, long seconds)
    {
        using var handler = new ResponseHandler();
        handler.Failure = path => path.EndsWith("item/2.json")
            ? new StringContent($$"""{"id":2,"type":"story","title":"Invalid timestamp","time":{{seconds}},"score":999}""",
                Encoding.UTF8, "application/json")
            : null;
        await AssertFailurePolicy(handler, stale);
    }

    private static async Task AssertFailurePolicy(ResponseHandler handler, bool stale)
    {
        var clock = new CompleteSnapshotTests.ManualTime();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<TimeProvider>(clock);
                services.PostConfigure<HackerNewsOptions>(options => options.MaxConcurrency = 1);
                services.AddHttpClient<IHackerNewsClient, HackerNewsClient>()
                    .ConfigurePrimaryHttpMessageHandler(() => handler);
            }));
        using var client = factory.CreateClient();
        const string route = "/api/best-stories?n=2";
        var good = stale ? await client.GetStringAsync(route) : null;
        handler.Broken = true;
        clock.Advance(TimeSpan.FromSeconds(60));
        var failed = await client.GetAsync(route);
        Assert.Equal(stale ? HttpStatusCode.OK : HttpStatusCode.BadGateway, failed.StatusCode);
        if (stale) Assert.Equal(good, await failed.Content.ReadAsStringAsync());
        var calls = handler.Calls;
        var service = factory.Services.GetRequiredService<IBestStoriesService>();
        if (stale) await service.RefreshAsync(default);
        else await Assert.ThrowsAsync<UpstreamException>(() => service.RefreshAsync(default));
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(failed.StatusCode, (await client.GetAsync(route)).StatusCode);
        Assert.Equal(calls, handler.Calls);
        handler.Broken = false;
        handler.Score = 200;
        clock.Advance(TimeSpan.FromSeconds(1));
        var recovered = await client.GetStringAsync(route);
        Assert.Contains("\"score\":200", recovered);
        Assert.Equal(calls + 3, handler.Calls);
        Assert.Equal(recovered, await client.GetStringAsync(route));
        Assert.Equal(calls + 3, handler.Calls);
    }

    private sealed class ResponseHandler : HttpMessageHandler
    {
        public bool Broken;
        public int Score = 10;
        public int Calls;
        public int Interruptions;
        public Func<string, HttpContent?> Failure = _ => null;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            var path = request.RequestUri!.AbsolutePath;
            var content = Broken ? Failure(path) : null;
            if (content is not null) Interlocked.Increment(ref Interruptions);
            content ??= new StringContent(path.EndsWith("beststories.json") ? "[1,2]" :
                $$"""{"id":{{(path.EndsWith("item/1.json") ? 1 : 2)}},"type":"story","title":"Complete story","time":0,"score":{{Score}}}""",
                Encoding.UTF8, "application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }

    private sealed class InterruptedStream(bool httpIo, string prefix) : MemoryStream(Encoding.UTF8.GetBytes(prefix))
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Position < Length) return base.ReadAsync(buffer, cancellationToken);
            throw httpIo ? new HttpIOException(HttpRequestError.ResponseEnded, "Response ended prematurely.")
                : new IOException("Connection dropped while reading the response body.");
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}