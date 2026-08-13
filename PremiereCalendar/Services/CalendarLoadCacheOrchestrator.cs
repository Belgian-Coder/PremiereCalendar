using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

/// <summary>Owns normalized calendar cache IO so the compatibility facade does not own persistence details.</summary>
public sealed class CalendarLoadCacheOrchestrator(ICalendarCache cache)
{
    public Task<IReadOnlyList<PremiereItem>?> ReadAsync(
        DateOnly start,
        DateOnly end,
        string cacheKey,
        CancellationToken cancellationToken,
        bool allowExpired = false)
        => cache.GetWeekAsync(start, end, cacheKey, cancellationToken, allowExpired);

    public Task WriteAsync(
        DateOnly start,
        DateOnly end,
        string cacheKey,
        IReadOnlyList<PremiereItem> items,
        CancellationToken cancellationToken)
        => cache.SetWeekAsync(start, end, cacheKey, items, cancellationToken);
}

/// <summary>Groups provider progress snapshots without exposing page component state.</summary>
public static class CalendarProgressAggregator
{
    public static IReadOnlyList<PremiereLoadProgress> LatestPerSource(IEnumerable<PremiereLoadProgress> progress)
        => progress
            .GroupBy(entry => entry.SourceName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .OrderBy(entry => entry.SourceName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
