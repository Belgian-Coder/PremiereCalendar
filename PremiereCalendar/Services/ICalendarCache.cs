using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public interface ICalendarCache
{
    // Implementations with an envelope can return items and freshness from one read.
    // Keep the fallback for existing cache adapters that only expose item reads.
    async Task<CalendarCacheSnapshot?> GetWeekSnapshotAsync(
        DateOnly start,
        DateOnly end,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        var fresh = await GetWeekAsync(start, end, cacheKey, cancellationToken);
        if (fresh is not null)
        {
            return new CalendarCacheSnapshot(fresh,
                new CalendarCacheMetadata(default, fresh.Count, 0, CalendarCacheCompleteness.Complete), true, null);
        }

        var expired = await GetWeekAsync(start, end, cacheKey, cancellationToken, allowExpired: true);
        return expired is null ? null : new CalendarCacheSnapshot(expired,
            new CalendarCacheMetadata(default, expired.Count, 0, CalendarCacheCompleteness.Partial), false, null);
    }

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

    // Partial discovery must remain ineligible for a fresh, complete cache hit.
    // Existing adapters opt in explicitly instead of publishing partial data as complete.
    Task SetPartialWeekAsync(
        DateOnly start,
        DateOnly end,
        string cacheKey,
        IReadOnlyList<PremiereItem> items,
        CancellationToken cancellationToken)
        => Task.CompletedTask;

    // Final reconciliation supplies the complete retained projection, including
    // deliberate removals. Never union an older file back into this write.
    Task<bool> SetValidatedPartialWeekAsync(DateOnly start, DateOnly end, string cacheKey,
        IReadOnlyList<PremiereItem> items, DateTimeOffset refreshStartedUtc,
        CancellationToken cancellationToken, string? expectedGeneration = null)
        => Task.FromResult(false);

    // A usable snapshot remains explicitly partial while an optional discovery
    // provider is access-denied. The caller supplies its full validated projection,
    // including retained prior occurrences, and the original generation fence.
    // Adapters must opt in to preserving its notices.
    Task<bool> SetDegradedWeekAsync(DateOnly start, DateOnly end, string cacheKey,
        IReadOnlyList<PremiereItem> items, IReadOnlyList<SourceUnavailableNotice> unavailableSources,
        DateTimeOffset refreshStartedUtc, CancellationToken cancellationToken, string? expectedGeneration = null)
        => Task.FromResult(false);

    // An adapter must explicitly support metadata writes; replacing the whole week
    // would lose concurrent discoveries and incorrectly renew discovery freshness.
    Task<IReadOnlyList<PremiereItem>?> UpdateWeekMetadataAsync(
        DateOnly start,
        DateOnly end,
        string cacheKey,
        CalendarCacheSnapshot original,
        IReadOnlyList<PremiereItem> maintainedItems,
        CalendarCacheMaintenanceKind kind,
        CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<PremiereItem>?>(null);
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

public sealed record CalendarCacheSnapshot(
    IReadOnlyList<PremiereItem> Items,
    CalendarCacheMetadata Metadata,
    bool IsFresh,
    string? Generation)
{
    public bool IsComplete => IsFresh && Metadata.Completeness == CalendarCacheCompleteness.Complete;
    public IReadOnlyList<SourceUnavailableNotice> UnavailableSources { get; init; } = [];
    public bool CanReuseDegraded(DateTimeOffset now) => IsFresh
        && Metadata.Completeness == CalendarCacheCompleteness.Partial && UnavailableSources.Count > 0
        && UnavailableSources.All(source => source is not null && source.IsOptionalAccessDenial && source.RetryAtUtc > now);
}

public enum CalendarCacheMaintenanceKind
{
    Scores,
    ExternalIds
}

public enum CalendarCacheCompleteness
{
    Complete = 0,
    Partial = 1
}
