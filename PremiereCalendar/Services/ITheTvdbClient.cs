using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public interface ITheTvdbClient
{
    async Task<ProviderRefreshResult<IReadOnlyList<TheTvdbArtwork>>> GetSeriesArtworkWithStatusAsync(int tvdbId, CancellationToken cancellationToken, bool forceRefresh = false)
        => new(await GetSeriesArtworkAsync(tvdbId, cancellationToken, forceRefresh), true);

    Task<IReadOnlyList<TheTvdbArtwork>> GetSeriesArtworkAsync(
        int tvdbId,
        CancellationToken cancellationToken,
        bool forceRefresh = false);
}
