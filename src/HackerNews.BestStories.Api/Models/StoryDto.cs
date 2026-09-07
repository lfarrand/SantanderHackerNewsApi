using System.Text.Json.Serialization;

namespace HackerNews.BestStories.Api.Models;

public sealed class StoryDto
{
    [JsonIgnore] internal long Id { get; init; }

    [JsonRequired, JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonRequired, JsonPropertyName("uri")]
    public string Url { get; init; } = string.Empty;

    [JsonRequired, JsonPropertyName("postedBy")]
    public string PostedBy { get; init; } = string.Empty;

    [JsonRequired, JsonPropertyName("time")]
    public DateTimeOffset Time { get; init; }

    [JsonRequired, JsonPropertyName("score")]
    [JsonNumberHandling(JsonNumberHandling.Strict)]
    public int Score { get; init; }

    [JsonRequired, JsonPropertyName("commentCount")]
    [JsonNumberHandling(JsonNumberHandling.Strict)]
    public int CommentCount { get; init; }
}