using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public interface IPremiereDiscoveryProvider
{
    Task<IReadOnlyList<ExternalPremiereCandidate>> GetCandidatesAsync(
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken,
        bool forceRefresh = false);
}

public interface INamedPremiereDiscoveryProvider : IPremiereDiscoveryProvider
{
    string DisplayName { get; }
}

public interface IMediaScopedPremiereDiscoveryProvider : IPremiereDiscoveryProvider
{
    bool SupportsMediaType(PremiereMediaType mediaType);
}

public interface IStreamingPremiereDiscoveryProvider : IPremiereDiscoveryProvider
{
    IAsyncEnumerable<IReadOnlyList<ExternalPremiereCandidate>> StreamCandidatesAsync(
        DateOnly start,
        DateOnly end,
        bool forceRefresh = false,
        CancellationToken cancellationToken = default);
}

public sealed record ExternalPremiereCandidate(
    PremiereMediaType MediaType,
    DateOnly PremiereDate,
    string? Title,
    int? TmdbId,
    string? ImdbId,
    int? TvdbId,
    string Source,
    bool IsSeriesEpisode = false,
    string? EpisodeTitle = null,
    int? SeasonNumber = null,
    int? EpisodeNumber = null,
    string? OriginalLanguage = null,
    DateOnly? SeriesPremiereDate = null,
    IReadOnlyList<string>? SourceNames = null,
    string? ExternalProviderId = null,
    string? ExternalUrl = null,
    string? PosterUrl = null,
    string? BackdropUrl = null,
    int? ReleaseYear = null,
    double? ImdbScore = null,
    int? ImdbVoteCount = null);
