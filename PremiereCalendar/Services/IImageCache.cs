namespace PremiereCalendar.Services;

public enum ImageCacheFormat
{
    Original,
    Webp
}

public interface IImageCache
{
    Task<CachedImage> GetOrAddAsync(
        string sourceUrl,
        bool forceRefresh,
        CancellationToken cancellationToken,
        int? width = null,
        ImageCacheFormat format = ImageCacheFormat.Original);
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
