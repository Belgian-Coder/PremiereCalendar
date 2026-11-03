using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public interface IWatchmodeClient
{
    async Task<ProviderRefreshResult<IReadOnlyList<PremiereSource>>> GetTitleSourcesWithStatusAsync(
        PremiereMediaType mediaType, int tmdbId, string? imdbId,
        IReadOnlyList<string> regions, CancellationToken cancellationToken, bool forceRefresh = false)
        => new(await GetTitleSourcesAsync(mediaType, tmdbId, imdbId, regions, cancellationToken, forceRefresh), true);

    Task<IReadOnlyList<PremiereSource>> GetTitleSourcesAsync(
        PremiereMediaType mediaType,
        int tmdbId,
        string? imdbId,
        IReadOnlyList<string> regions,
        CancellationToken cancellationToken,
        bool forceRefresh = false);

    Task<IReadOnlyList<ExternalPremiereCandidate>> GetReleaseCandidatesAsync(
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken,
        bool forceRefresh = false);
}
