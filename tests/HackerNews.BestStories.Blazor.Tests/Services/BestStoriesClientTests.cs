using System.Net;
using System.Text;
using AwesomeAssertions;
using HackerNews.BestStories.Blazor.Models;
using HackerNews.BestStories.Blazor.Services;

namespace HackerNews.BestStories.Blazor.Tests.Services;

public class BestStoriesClientTests
{
    [Fact]
    public async Task GetBestStoriesAsync_UsesDefaultConstructorDelayDelegate()
    {
        var handler = new FakeHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            }));

        var sut = new BestStoriesClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });

        var result = await sut.GetBestStoriesAsync(1);

        result.Should().BeEmpty();
        handler.Calls.Should().Be(1);
    }
    [Fact]
    public async Task GetBestStoriesAsync_ReturnsPayload_OnFirstSuccess()
    {
        var handler = new FakeHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                                            [{"title":"t","uri":"u","postedBy":"p","time":"2024-01-01T00:00:00+00:00","score":1,"commentCount":2}]
                                            """, Encoding.UTF8, "application/json")
            }));

        var sut = new BestStoriesClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, _ => TimeSpan.Zero);

        var result = await sut.GetBestStoriesAsync(1);

        result.Should().ContainSingle();
        result[0].Should().BeEquivalentTo(new Story("t", "u", "p", DateTimeOffset.Parse("2024-01-01T00:00:00+00:00"), 1, 2));
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task GetBestStoriesAsync_RetriesRetryableStatus_ThenSucceeds()
    {
        var calls = 0;
        var handler = new FakeHandler(_ =>
        {
            calls++;
            if (calls == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
                {
                    Content = new StringContent("bad gateway")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            });
        });

        var delays = new List<TimeSpan>();
        var sut = new BestStoriesClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, attempt =>
        {
            var delay = BestStoriesClient.RetryDelay(attempt);
            delays.Add(delay);
            return TimeSpan.Zero;
        });

        var result = await sut.GetBestStoriesAsync(1);

        result.Should().BeEmpty();
        handler.Calls.Should().Be(2);
        delays.Should().ContainSingle().Which.Should().Be(TimeSpan.FromMilliseconds(250));
    }

    [Fact]
    public async Task GetBestStoriesAsync_WaitsWhenDelayIsPositive_BeforeRetrying()
    {
        var calls = 0;
        var handler = new FakeHandler(_ =>
        {
            calls++;
            if (calls == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
                {
                    Content = new StringContent("bad gateway")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            });
        });

        var sut = new BestStoriesClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, _ => TimeSpan.FromMilliseconds(1));

        var result = await sut.GetBestStoriesAsync(1);

        result.Should().BeEmpty();
        handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task GetBestStoriesAsync_ReturnsEmpty_WhenPayloadIsJsonNull()
    {
        var handler = new FakeHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("null", Encoding.UTF8, "application/json")
            }));

        var sut = new BestStoriesClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, _ => TimeSpan.Zero);

        var result = await sut.GetBestStoriesAsync(5);

        result.Should().BeEmpty();
        handler.Calls.Should().Be(1);
    }

    [Fact]
    public async Task GetBestStoriesAsync_ThrowsImmediately_ForNonRetryableStatus()
    {
        var delays = new List<int>();
        var handler = new FakeHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("forbidden detail")
            }));

        var sut = new BestStoriesClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, attempt =>
        {
            delays.Add(attempt);
            return TimeSpan.Zero;
        });

        var act = () => sut.GetBestStoriesAsync(1);

        var ex = await act.Should().ThrowAsync<HttpRequestException>();
        ex.Which.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        ex.Which.Message.Should().Be("forbidden detail");
        handler.Calls.Should().Be(1);
        delays.Should().BeEmpty();
    }

    [Fact]
    public async Task GetBestStoriesAsync_RetriesRetryableStatus_UntilMaxAttempts_ThenThrows()
    {
        var delayedAttempts = new List<int>();
        var handler = new FakeHandler(_ =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent("service unavailable detail")
            }));

        var sut = new BestStoriesClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, attempt =>
        {
            delayedAttempts.Add(attempt);
            return TimeSpan.Zero;
        });

        var act = () => sut.GetBestStoriesAsync(2);

        var ex = await act.Should().ThrowAsync<HttpRequestException>();
        ex.Which.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        ex.Which.Message.Should().Be("service unavailable detail");
        handler.Calls.Should().Be(BestStoriesClient.MaxAttempts);
        delayedAttempts.Should().Equal(0, 1, 2);
    }

    [Fact]
    public async Task GetBestStoriesAsync_RetriesHttpRequestException_WithNullStatusCode_ThenSucceeds()
    {
        var calls = 0;
        var delayedAttempts = new List<int>();
        var handler = new FakeHandler(_ =>
        {
            calls++;
            if (calls == 1)
            {
                throw new HttpRequestException("transient network failure", inner: null, statusCode: null);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json")
            });
        });

        var sut = new BestStoriesClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, attempt =>
        {
            delayedAttempts.Add(attempt);
            return TimeSpan.Zero;
        });

        var result = await sut.GetBestStoriesAsync(3);

        result.Should().BeEmpty();
        handler.Calls.Should().Be(2);
        delayedAttempts.Should().Equal(0);
    }

    [Fact]
    public async Task GetBestStoriesAsync_ThrowsImmediately_ForNonRetryableHttpRequestException()
    {
        var delayedAttempts = new List<int>();
        var handler = new FakeHandler(_ => throw new HttpRequestException("not found", inner: null, statusCode: HttpStatusCode.NotFound));

        var sut = new BestStoriesClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, attempt =>
        {
            delayedAttempts.Add(attempt);
            return TimeSpan.Zero;
        });

        var act = () => sut.GetBestStoriesAsync(1);

        var ex = await act.Should().ThrowAsync<HttpRequestException>();
        ex.Which.StatusCode.Should().Be(HttpStatusCode.NotFound);
        ex.Which.Message.Should().Be("not found");
        handler.Calls.Should().Be(1);
        delayedAttempts.Should().BeEmpty();
    }

    [Fact]
    public async Task GetBestStoriesAsync_RetriesRetryableHttpRequestException_UntilMaxAttempts_ThenThrows()
    {
        var delayedAttempts = new List<int>();
        var handler = new FakeHandler(_ => throw new HttpRequestException("gateway timeout", inner: null, statusCode: HttpStatusCode.GatewayTimeout));

        var sut = new BestStoriesClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, attempt =>
        {
            delayedAttempts.Add(attempt);
            return TimeSpan.Zero;
        });

        var act = () => sut.GetBestStoriesAsync(1);

        var ex = await act.Should().ThrowAsync<HttpRequestException>();
        ex.Which.StatusCode.Should().Be(HttpStatusCode.GatewayTimeout);
        ex.Which.Message.Should().Be("gateway timeout");
        handler.Calls.Should().Be(BestStoriesClient.MaxAttempts);
        delayedAttempts.Should().Equal(0, 1, 2);
    }

    [Theory]
    [InlineData(0, 250)]
    [InlineData(1, 500)]
    [InlineData(2, 1000)]
    [InlineData(3, 2000)]
    public void RetryDelay_GrowsExponentially_ByAttemptIndex(int attemptIndex, int expectedMilliseconds)
    {
        var result = BestStoriesClient.RetryDelay(attemptIndex);

        result.Should().Be(TimeSpan.FromMilliseconds(expectedMilliseconds));
    }

    [Theory]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData(HttpStatusCode.GatewayTimeout, true)]
    [InlineData(HttpStatusCode.TooManyRequests, true)]
    [InlineData(HttpStatusCode.Forbidden, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    [InlineData(HttpStatusCode.InternalServerError, false)]
    public void IsRetryable_ReturnsExpectedValue_ForGivenStatus(HttpStatusCode status, bool expected)
    {
        var result = BestStoriesClient.IsRetryable(status);

        result.Should().Be(expected);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> impl) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return impl(request);
        }
    }
}