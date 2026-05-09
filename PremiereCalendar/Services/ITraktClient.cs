using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public interface ITraktClient
{
    Task<IReadOnlyList<TraktMovieCalendarItem>> GetMovieCalendarAsync(
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken,
        bool forceRefresh = false);

    Task<IReadOnlyList<TraktShowCalendarItem>> GetNewShowCalendarAsync(
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken,
        bool forceRefresh = false);
}
