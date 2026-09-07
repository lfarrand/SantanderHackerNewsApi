namespace HackerNews.BestStories.Api.Caching;

public sealed record CacheValue<T>(T Value, TimeSpan Ttl);