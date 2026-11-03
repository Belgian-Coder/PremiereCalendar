using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public interface IRottenTomatoesClient
{
    Task<RottenTomatoesScores?> TryGetCachedScoresAsync(
        PremiereMediaType mediaType,
        string title,
        int? year,
        string? wikidataId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(TryGetCachedScores(mediaType, title, year, wikidataId, out var scores)
            ? scores : null);
    }

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

    async Task<ProviderRefreshResult<RottenTomatoesScores>> GetScoresWithStatusAsync(
        PremiereMediaType mediaType, string title, int? year, string? wikidataId,
        CancellationToken cancellationToken, bool forceRefresh = false)
    {
        var scores = await GetScoresAsync(mediaType, title, year, wikidataId, cancellationToken, forceRefresh);
        return new(scores, scores.HasAnyScore);
    }

    Task<ProviderRefreshResult<RottenTomatoesScores>> GetScoresWithRefreshPolicyAsync(
        PremiereMediaType mediaType, string title, int? year, string? wikidataId,
        CancellationToken cancellationToken, bool forceRefresh = false, bool forceIdentifierRefresh = false)
        => GetScoresWithStatusAsync(mediaType, title, year, wikidataId, cancellationToken, forceRefresh);

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
