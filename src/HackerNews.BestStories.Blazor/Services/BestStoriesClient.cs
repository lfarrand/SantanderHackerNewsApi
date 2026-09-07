using System.Net;
using HackerNews.BestStories.Blazor.Models;

namespace HackerNews.BestStories.Blazor.Services;

public sealed class BestStoriesClient(HttpClient http, Func<int, TimeSpan> delay)
{
    public const int MaxStories = 500;
    public const int MaxAttempts = 4;
    public const int BaseDelayMs = 250;

    [ActivatorUtilitiesConstructor]
    public BestStoriesClient(HttpClient http)
        : this(http, attempt => RetryDelay(attempt))
    {
    }

    public async Task<IReadOnlyList<Story>> GetBestStoriesAsync(
        int n = MaxStories,
        CancellationToken cancellationToken = default)
    {
        HttpRequestException? lastError = null;

        for (var attempt = 0; attempt < MaxAttempts; attempt++)
        {
            try
            {
                using var response = await http
                    .GetAsync($"api/best-stories?n={n}", cancellationToken)
                    .ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    var stories = await response.Content
                        .ReadFromJsonAsync<List<Story>>(cancellationToken)
                        .ConfigureAwait(false);
                    return stories ?? [];
                }

                var detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                lastError = new HttpRequestException(
                    string.IsNullOrWhiteSpace(detail)
                        ? $"Request failed ({(int)response.StatusCode})"
                        : detail,
                    inner: null,
                    statusCode: response.StatusCode);

                if (!IsRetryable(response.StatusCode) || attempt == MaxAttempts - 1)
                {
                    throw lastError;
                }
            }
            catch (HttpRequestException ex) when (attempt < MaxAttempts - 1 && IsRetryable(ex))
            {
                lastError = ex;
            }

            var wait = delay(attempt);
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
            }
        }

        throw lastError ?? new HttpRequestException("Request failed");
    }

    public static TimeSpan RetryDelay(int attemptIndex, int baseDelayMs = BaseDelayMs)
        => TimeSpan.FromMilliseconds(baseDelayMs * Math.Pow(2, attemptIndex));

    public static bool IsRetryable(HttpStatusCode status)
        => status is HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout
            or HttpStatusCode.TooManyRequests;

    private static bool IsRetryable(HttpRequestException ex)
    {
        if (ex.StatusCode is { } status)
        {
            return IsRetryable(status);
        }

        return ex.StatusCode is null;
    }
}