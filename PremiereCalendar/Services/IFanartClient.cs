using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public interface IFanartClient
{
    Task<FanartMovieArtwork?> GetMovieArtworkAsync(
        int tmdbId,
        CancellationToken cancellationToken,
        bool forceRefresh = false);

    async Task<ProviderRefreshResult<FanartMovieArtwork?>> GetMovieArtworkWithStatusAsync(int tmdbId, CancellationToken cancellationToken, bool forceRefresh = false)
        => new(await GetMovieArtworkAsync(tmdbId, cancellationToken, forceRefresh), true);

    async Task<ProviderRefreshResult<FanartTvArtwork?>> GetTvArtworkWithStatusAsync(int tvdbId, CancellationToken cancellationToken, bool forceRefresh = false)
        => new(await GetTvArtworkAsync(tvdbId, cancellationToken, forceRefresh), true);

    Task<FanartTvArtwork?> GetTvArtworkAsync(
        int tvdbId,
        CancellationToken cancellationToken,
        bool forceRefresh = false);
}
