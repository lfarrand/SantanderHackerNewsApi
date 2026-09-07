namespace HackerNews.BestStories.Api.Configuration;

public sealed class ReverseProxyOptions
{
    public const string SectionName = "ReverseProxy";

    public string[] KnownProxies { get; set; } = [];
    public string[] KnownNetworks { get; set; } = [];
}