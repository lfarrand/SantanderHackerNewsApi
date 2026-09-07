namespace HackerNews.BestStories.Api.Models;

public sealed class StoryDtoV2
{
    public long HackerNewsId { get; init; }

    public string? Title { get; init; }

    public string? Url { get; init; }

    public string? PostedBy { get; init; }

    public DateTimeOffset Time { get; init; }

    public int Score { get; init; }

    public int CommentCount { get; init; }

    public static StoryDtoV2 From(StoryDto story)
    {
        return new StoryDtoV2
        {
            HackerNewsId = story.Id,
            Title = story.Title,
            Url = story.Url,
            PostedBy = story.PostedBy,
            Time = story.Time,
            Score = story.Score,
            CommentCount = story.CommentCount
        };
    }
}