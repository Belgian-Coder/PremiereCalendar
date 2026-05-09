using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public interface ICalendarCache
{
    Task<IReadOnlyList<PremiereItem>?> GetWeekAsync(
        DateOnly start,
        DateOnly end,
        string cacheKey,
        CancellationToken cancellationToken,
        bool allowExpired = false);

    Task SetWeekAsync(
        DateOnly start,
        DateOnly end,
        string cacheKey,
        IReadOnlyList<PremiereItem> items,
        CancellationToken cancellationToken);
}

public interface ICalendarCacheMaintenance
{
    Task<CalendarCacheMetadata?> GetWeekMetadataAsync(
        DateOnly start,
        DateOnly end,
        string cacheKey,
        CancellationToken cancellationToken);

    Task<int> CleanupAsync(
        DateTimeOffset nowUtc,
        TimeSpan retention,
        CancellationToken cancellationToken);
}

public sealed record CalendarCacheMetadata(
    DateTimeOffset CachedAtUtc,
    int ItemCount,
    int SchemaVersion,
    CalendarCacheCompleteness Completeness);

public enum CalendarCacheCompleteness
{
    Complete = 0,
    Partial = 1
}
