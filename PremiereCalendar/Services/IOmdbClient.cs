using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public interface IOmdbClient
{
    Task<OmdbItem?> GetByImdbIdAsync(string imdbId, CancellationToken cancellationToken, bool forceRefresh = false);
}
