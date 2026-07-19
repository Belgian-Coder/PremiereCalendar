using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public interface IRottenTomatoesClient
{
    bool TryGetCachedScores(
        PremiereMediaType mediaType,
        string title,
        int? year,
        string? wikidataId,
        out RottenTomatoesScores scores)
    {
        scores = RottenTomatoesScores.Empty;
        return false;
    }

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
