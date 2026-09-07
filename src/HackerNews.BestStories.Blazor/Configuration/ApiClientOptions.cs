namespace HackerNews.BestStories.Blazor.Configuration;

public sealed class ApiClientOptions
{
    public const string SectionName = "ApiClient";

    public int RequestTimeoutSeconds { get; set; } = 130;
    public int RefreshTimeoutSeconds { get; set; } = 120;
}