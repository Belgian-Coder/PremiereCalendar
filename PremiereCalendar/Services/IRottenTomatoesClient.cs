using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public interface IRottenTomatoesClient
{
    Task<RottenTomatoesScores> GetScoresAsync(
        PremiereMediaType mediaType,
        string title,
        int? year,
        string? wikidataId,
        CancellationToken cancellationToken,
        bool forceRefresh = false);

    async Task<int?> GetTomatometerScoreAsync(
        PremiereMediaType mediaType,
        string title,
        int? year,
        string? wikidataId,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        var scores = await GetScoresAsync(mediaType, title, year, wikidataId, cancellationToken, forceRefresh);
        return scores.CriticScore;
    }
}
