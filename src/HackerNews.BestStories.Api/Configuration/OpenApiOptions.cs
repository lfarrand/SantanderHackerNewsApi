namespace HackerNews.BestStories.Api.Configuration;

public sealed class OpenApiOptions
{
    public const string SectionName = "OpenApi";

    public bool Enabled { get; set; } = true;
}
