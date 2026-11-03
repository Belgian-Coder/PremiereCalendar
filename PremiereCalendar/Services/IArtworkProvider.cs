using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public interface IArtworkProvider
{
    string? OwnedSource => null;

    async Task<ProviderRefreshResult<ArtworkCandidate?>> GetArtworkWithStatusAsync(ArtworkRequest request, CancellationToken cancellationToken, bool forceRefresh = false)
        => new(await GetArtworkAsync(request, cancellationToken, forceRefresh), true);

    Task<ArtworkCandidate?> GetArtworkAsync(
        ArtworkRequest request,
        CancellationToken cancellationToken,
        bool forceRefresh = false);
}

public sealed record ArtworkRequest(
    PremiereMediaType MediaType,
    int TmdbId,
    string? ImdbId,
    int? TvdbId,
    string? WikidataId,
    string Title);

public sealed record ArtworkCandidate(string Url, string Source);
