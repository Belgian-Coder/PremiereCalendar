using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public interface IPremiereService
{
    IAsyncEnumerable<PremiereLoadProgress> StreamPremieresAsync(
        DateOnly start,
        DateOnly end,
        bool forceRefresh = false,
        CalendarFilters? filters = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PremiereItem>> GetPremieresAsync(
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken,
        bool forceRefresh = false,
        IProgress<PremiereLoadProgress>? progress = null,
        CalendarFilters? filters = null);
}
