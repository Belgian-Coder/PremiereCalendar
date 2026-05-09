using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public interface IWatchmodeClient
{
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
