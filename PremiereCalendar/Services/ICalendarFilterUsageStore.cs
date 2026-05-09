using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public interface ICalendarFilterUsageStore
{
    Task RecordUseAsync(
        CalendarPageMode pageMode,
        CalendarFilters filters,
        int itemCount,
        DateTimeOffset usedAtUtc,
        CancellationToken cancellationToken);

    Task<CalendarFilterUsageProfile?> GetProfileAsync(
        string profileKey,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<CalendarFilterUsageProfile>> GetTopProfilesAsync(
        int count,
        DateTimeOffset nowUtc,
        TimeSpan retention,
        CancellationToken cancellationToken);

    Task MarkWarmedAsync(
        string profileKey,
        CalendarPageMode pageMode,
        CalendarFilters filters,
        bool isDefault,
        int itemCount,
        DateTimeOffset warmedAtUtc,
        CancellationToken cancellationToken);

    Task MarkWarmFailedAsync(
        string profileKey,
        CalendarPageMode pageMode,
        CalendarFilters filters,
        bool isDefault,
        string failure,
        DateTimeOffset failedAtUtc,
        CancellationToken cancellationToken);

    Task<int> CleanupAsync(
        DateTimeOffset cutoffUtc,
        IReadOnlySet<string> retainedProfileKeys,
        CancellationToken cancellationToken);
}

public sealed record CalendarFilterUsageProfile(
    string ProfileKey,
    CalendarPageMode PageMode,
    string CacheKey,
    CalendarFilters Filters,
    int UseCount,
    DateTimeOffset LastUsedUtc,
    DateTimeOffset? LastWarmedUtc,
    int? LastItemCount,
    string? LastFailure,
    bool IsDefault);
