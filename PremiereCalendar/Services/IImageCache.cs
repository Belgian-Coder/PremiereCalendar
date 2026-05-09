namespace PremiereCalendar.Services;

public interface IImageCache
{
    Task<CachedImage> GetOrAddAsync(
        string sourceUrl,
        bool forceRefresh,
        CancellationToken cancellationToken,
        int? width = null);
}

public interface IImageCacheMaintenance
{
    Task<int> CleanupAsync(
        DateTimeOffset nowUtc,
        TimeSpan retention,
        CancellationToken cancellationToken);
}

public sealed record CachedImage(
    string FilePath,
    string ContentType,
    DateTimeOffset LastModifiedUtc,
    string CacheKey,
    TimeSpan BrowserMaxAge);
