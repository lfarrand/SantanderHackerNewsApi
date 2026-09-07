using HackerNews.BestStories.Api.Models;

namespace HackerNews.BestStories.Api.Services;

public interface IBestStoriesService
{
    Task<IReadOnlyList<StoryDto>> GetBestStoriesAsync(int requestedCount, CancellationToken cancellationToken);

    Task RefreshAsync(CancellationToken cancellationToken);
}
