using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public interface IFanartClient
{
    Task<FanartMovieArtwork?> GetMovieArtworkAsync(
        int tmdbId,
        CancellationToken cancellationToken,
        bool forceRefresh = false);

    Task<FanartTvArtwork?> GetTvArtworkAsync(
        int tvdbId,
        CancellationToken cancellationToken,
        bool forceRefresh = false);
}
