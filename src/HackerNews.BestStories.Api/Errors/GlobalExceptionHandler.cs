using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace HackerNews.BestStories.Api.Errors;

public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
            return false;

        var statusCode = exception switch
        {
            BadHttpRequestException { StatusCode: StatusCodes.Status400BadRequest } => StatusCodes.Status400BadRequest,
            UpstreamTimeoutException => StatusCodes.Status504GatewayTimeout,
            UpstreamException => StatusCodes.Status502BadGateway,
            HttpRequestException => StatusCodes.Status502BadGateway,
            _ => StatusCodes.Status500InternalServerError
        };

        logger.LogError(exception, "Unhandled exception while processing request");

        var problemDetails = new ProblemDetails
        {
            Status = statusCode,
            Title = "Request failed",
            Detail = statusCode switch
            {
                StatusCodes.Status400BadRequest => "Invalid request parameters. Query parameter 'n' must be an integer.",
                StatusCodes.Status502BadGateway => "Upstream request failed.",
                StatusCodes.Status504GatewayTimeout => "Upstream request timed out.",
                _ => "An unexpected error occurred."
            }
        };

        httpContext.Response.StatusCode = statusCode;
        await httpContext.Response.WriteAsJsonAsync(problemDetails, options: null,
            contentType: "application/problem+json", cancellationToken: cancellationToken);
        return true;
    }
}