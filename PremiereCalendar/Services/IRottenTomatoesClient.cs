using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public interface IRottenTomatoesClient
{
    Task<int?> GetTomatometerScoreAsync(
        PremiereMediaType mediaType,
        string title,
        int? year,
        string? wikidataId,
        CancellationToken cancellationToken,
        bool forceRefresh = false);
}
