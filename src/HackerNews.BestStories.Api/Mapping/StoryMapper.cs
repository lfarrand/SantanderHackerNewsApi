using HackerNews.BestStories.Api.Models;
using HackerNews.BestStories.Api.Errors;

namespace HackerNews.BestStories.Api.Mapping;

public static class StoryMapper
{
    public static StoryDto? Map(HackerNewsItem item)
    {
        if (!string.Equals(item.Type, "story", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(item.Title))
        {
            return null;
        }

        if (item.Time < DateTimeOffset.MinValue.ToUnixTimeSeconds()
            || item.Time > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            throw new UpstreamException($"Hacker News item {item.Id} has an unsupported Unix timestamp.");
        }

        return new StoryDto
        {
            Id = item.Id,
            Title = item.Title,
            Url = string.IsNullOrWhiteSpace(item.Url) ? $"https://news.ycombinator.com/item?id={item.Id}" : item.Url,
            PostedBy = item.By ?? string.Empty,
            Time = DateTimeOffset.FromUnixTimeSeconds(item.Time),
            Score = item.Score,
            CommentCount = item.Descendants
        };
    }
}