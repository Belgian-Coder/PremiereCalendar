using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public interface IOmdbClient
{
    Task<OmdbItem?> GetByImdbIdAsync(string imdbId, CancellationToken cancellationToken, bool forceRefresh = false);

    async Task<ProviderRefreshResult<OmdbItem?>> GetByImdbIdWithStatusAsync(
        string imdbId, CancellationToken cancellationToken, bool forceRefresh = false)
    {
        var item = await GetByImdbIdAsync(imdbId, cancellationToken, forceRefresh);
        return new(item, item is not null && (string.Equals(item.Response, "True", StringComparison.OrdinalIgnoreCase)
            || string.Equals(item.Error, "Movie not found!", StringComparison.OrdinalIgnoreCase)));
    }
}
