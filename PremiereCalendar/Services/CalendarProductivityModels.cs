using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public sealed record CalendarFilterPreset(
    string Id,
    string Name,
    CalendarPageMode PageMode,
    CalendarFilters Filters,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);

public sealed record CalendarVisitScope(CalendarPageMode PageMode, DateOnly WeekStart, string CacheKey);

public sealed record CalendarVisitChangeSummary(
    bool HasPreviousVisit,
    int NewCount,
    int RemovedCount,
    DateTimeOffset? PreviousSeenUtc,
    DateTimeOffset SeenUtc);
