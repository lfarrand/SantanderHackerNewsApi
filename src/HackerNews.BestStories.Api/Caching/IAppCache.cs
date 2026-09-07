namespace HackerNews.BestStories.Api.Caching;

public interface IAppCache
{
    Task<T> GetOrCreateAsync<T>(
        string key,
        Func<CancellationToken, Task<CacheValue<T>>> valueFactory,
        CancellationToken cancellationToken);

    Task<T> RefreshAsync<T>(
        string key,
        Func<CancellationToken, Task<CacheValue<T>>> valueFactory,
        CancellationToken cancellationToken);
}
