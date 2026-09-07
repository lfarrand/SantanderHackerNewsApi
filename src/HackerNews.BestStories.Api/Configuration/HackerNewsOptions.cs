namespace HackerNews.BestStories.Api.Configuration;

public sealed class HackerNewsOptions
{
    public const string SectionName = "HackerNews";

    public string BaseUrl { get; set; } = "https://hacker-news.firebaseio.com/v0/";

    public int MaxStories { get; set; } = 500;

    public int CacheTtlSeconds { get; set; } = 60;

    public int FailureCacheTtlSeconds { get; set; } = 30;

    public int MaxConcurrency { get; set; } = 8;

    public int RequestTimeoutSeconds { get; set; } = 10;

    public int RefreshTimeoutSeconds { get; set; } = 120;
}
