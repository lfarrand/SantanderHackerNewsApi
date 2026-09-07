using HackerNews.BestStories.Api.Models;
using HackerNews.BestStories.Api.Errors;
using System.Text.Json;

namespace HackerNews.BestStories.Api.Clients;

public sealed class HackerNewsClient(HttpClient httpClient) : IHackerNewsClient
{
    public async Task<long[]> GetBestStoryIdsAsync(CancellationToken cancellationToken)
    {
        var ids = await GetAsync<long[]>("beststories.json", cancellationToken)
                  ?? throw new UpstreamException("Hacker News returned a null ID collection.");
        return ids;
    }

    public Task<HackerNewsItem?> GetItemAsync(long id, CancellationToken cancellationToken)
        => GetAsync<HackerNewsItem>($"item/{id}.json", cancellationToken);

    private async Task<T?> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        try
        {
            return await httpClient.GetFromJsonAsync<T>(path, cancellationToken);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested && ex.InnerException is TimeoutException)
        {
            throw new UpstreamTimeoutException("Hacker News request timed out.", ex);
        }
        catch (JsonException ex)
        {
            throw new UpstreamException("Hacker News returned invalid JSON.", ex);
        }
        catch (IOException ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new UpstreamException("Hacker News response body could not be read.", ex);
        }
    }
}
