using System.Net;
using System.Text;
using HackerNews.BestStories.Api.Clients;

namespace HackerNews.BestStories.Api.Tests.Clients;

public sealed class HackerNewsClientTests
{
    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[1,invalid]")]
    public async Task InvalidIdCollection_IsUpstreamFailure(string json)
    {
        var client = CreateClient(new StubHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent(json) }));
        await Assert.ThrowsAsync<HackerNews.BestStories.Api.Errors.UpstreamException>(() => client.GetBestStoryIdsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EmptyIdsAndNullItem_AreValidResponses()
    {
        var client = CreateClient(new StubHandler(request => new(HttpStatusCode.OK)
        { Content = new StringContent(request.RequestUri!.ToString().Contains("beststories") ? "[]" : "null") }));
        Assert.Empty(await client.GetBestStoryIdsAsync(TestContext.Current.CancellationToken));
        Assert.Null(await client.GetItemAsync(1, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task RequestTimeout_IsTyped_WhileCallerCancellationIsNot()
    {
        var client = CreateClient(new StubHandler(_ => throw new TaskCanceledException("HTTP timeout", new TimeoutException())));
        // A non-cancellable caller distinguishes the HTTP timeout from caller cancellation.
#pragma warning disable xUnit1051
        await Assert.ThrowsAsync<HackerNews.BestStories.Api.Errors.UpstreamTimeoutException>(() => client.GetBestStoryIdsAsync(CancellationToken.None));
#pragma warning restore xUnit1051
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetBestStoryIdsAsync(cancellation.Token));
    }
    [Fact]
    public async Task GetBestStoryIdsAsync_CallsExpectedEndpoint()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[1,2,3]", Encoding.UTF8, "application/json")
        });
        var client = CreateClient(handler);

        var result = await client.GetBestStoryIdsAsync(TestContext.Current.CancellationToken);

        Assert.Equal(new long[] { 1, 2, 3 }, result);
        Assert.EndsWith("beststories.json", handler.LastRequestPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetItemAsync_CallsExpectedEndpoint()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"id\":42,\"type\":\"story\"}", Encoding.UTF8, "application/json")
        });
        var client = CreateClient(handler);

        var result = await client.GetItemAsync(42, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(42, result!.Id);
        Assert.EndsWith("item/42.json", handler.LastRequestPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetBestStoryIdsAsync_Throws_WhenApiFails()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetBestStoryIdsAsync(TestContext.Current.CancellationToken));
    }

    private static HackerNewsClient CreateClient(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://hacker-news.firebaseio.com/v0/")
        };

        return new HackerNewsClient(httpClient);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory = responseFactory;

        public string? LastRequestPath { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestPath = request.RequestUri!.PathAndQuery.TrimStart('/');
            return Task.FromResult(_responseFactory(request));
        }
    }
}
