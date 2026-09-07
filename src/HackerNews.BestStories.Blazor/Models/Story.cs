using System.Text.Json.Serialization;

namespace HackerNews.BestStories.Blazor.Models;

public sealed record Story(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("uri")] string Uri,
    [property: JsonPropertyName("postedBy")] string PostedBy,
    [property: JsonPropertyName("time")] DateTimeOffset Time,
    [property: JsonPropertyName("score")] int Score,
    [property: JsonPropertyName("commentCount")] int CommentCount);