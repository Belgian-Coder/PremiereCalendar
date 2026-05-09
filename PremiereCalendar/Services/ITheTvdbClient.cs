using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public interface ITheTvdbClient
{
    Task<IReadOnlyList<TheTvdbArtwork>> GetSeriesArtworkAsync(
        int tvdbId,
        CancellationToken cancellationToken,
        bool forceRefresh = false);
}
