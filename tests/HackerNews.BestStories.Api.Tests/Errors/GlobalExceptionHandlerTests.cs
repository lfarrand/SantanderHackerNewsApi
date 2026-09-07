using System.Net;
using HackerNews.BestStories.Api.Tests.Infrastructure;

namespace HackerNews.BestStories.Api.Tests.Errors;

public sealed class GlobalExceptionHandlerTests
{
    [Fact]
    public async Task CallerCancellation_IsNotHandledAsTimeout()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext { RequestAborted = cancellation.Token };
        var handler = new HackerNews.BestStories.Api.Errors.GlobalExceptionHandler(
            Microsoft.Extensions.Logging.Abstractions.NullLogger<HackerNews.BestStories.Api.Errors.GlobalExceptionHandler>.Instance);
        Assert.False(await handler.TryHandleAsync(context, new OperationCanceledException(cancellation.Token), default));
        Assert.Equal(200, context.Response.StatusCode);
    }
    [Theory]
    [InlineData(typeof(HttpRequestException), HttpStatusCode.BadGateway)]
    [InlineData(typeof(TaskCanceledException), HttpStatusCode.InternalServerError)]
    [InlineData(typeof(InvalidOperationException), HttpStatusCode.InternalServerError)]
    public async Task Exceptions_AreMapped_ToExpectedStatusCodes(Type exceptionType, HttpStatusCode expected)
    {
        Exception exception = exceptionType == typeof(HttpRequestException)
            ? new HttpRequestException("upstream")
            : exceptionType == typeof(TaskCanceledException)
                ? new TaskCanceledException("timeout")
                : new InvalidOperationException("boom");

        var service = new ThrowingBestStoriesService(exception);
        await using var factory = new ApiFactory(service);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/best-stories?n=1");

        Assert.Equal(expected, response.StatusCode);
    }
}