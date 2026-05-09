using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public interface ITvmazeClient
{
    Task<TvmazeShow?> LookupShowAsync(
        int? tvdbId,
        string? imdbId,
        CancellationToken cancellationToken,
        bool forceRefresh = false);

    Task<TvmazeShow?> SearchShowByNameAsync(
        string title,
        CancellationToken cancellationToken,
        bool forceRefresh = false);

    Task<IReadOnlyList<TvmazeShowImage>> GetShowImagesAsync(
        int showId,
        CancellationToken cancellationToken,
        bool forceRefresh = false);

    Task<IReadOnlyList<TvmazeScheduleEpisode>> GetScheduleAsync(
        DateOnly date,
        string? country,
        bool webSchedule,
        CancellationToken cancellationToken,
        bool forceRefresh = false);
}
