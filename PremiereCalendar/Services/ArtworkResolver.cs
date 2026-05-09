namespace PremiereCalendar.Services;

public static class ArtworkResolver
{
    public static ArtworkCandidate? ResolveKnownCover(
        string? tmdbPosterUrl,
        string? omdbPosterUrl,
        string? tvmazeEnrichmentImageUrl)
    {
        if (!string.IsNullOrWhiteSpace(tmdbPosterUrl))
        {
            return new ArtworkCandidate(tmdbPosterUrl, "TMDb poster");
        }

        if (!string.IsNullOrWhiteSpace(omdbPosterUrl))
        {
            return new ArtworkCandidate(omdbPosterUrl, "OMDb poster");
        }

        return string.IsNullOrWhiteSpace(tvmazeEnrichmentImageUrl)
            ? null
            : new ArtworkCandidate(tvmazeEnrichmentImageUrl, ArtworkSources.TvmazeImage);
    }

    public static ArtworkCandidate? Resolve(
        string? tmdbPosterUrl,
        IReadOnlyList<ArtworkCandidate> providerCandidates,
        string? omdbPosterUrl,
        string? tvmazeEnrichmentImageUrl,
        string? tmdbBackdropUrl)
    {
        if (!string.IsNullOrWhiteSpace(tmdbPosterUrl))
        {
            return new ArtworkCandidate(tmdbPosterUrl, "TMDb poster");
        }

        var fanart = FirstFromSource(providerCandidates, ArtworkSources.Fanart);
        if (fanart is not null)
        {
            return fanart;
        }

        if (!string.IsNullOrWhiteSpace(omdbPosterUrl))
        {
            return new ArtworkCandidate(omdbPosterUrl, "OMDb poster");
        }

        if (!string.IsNullOrWhiteSpace(tvmazeEnrichmentImageUrl))
        {
            return new ArtworkCandidate(tvmazeEnrichmentImageUrl, ArtworkSources.TvmazeImage);
        }

        var tvmaze = FirstFromSource(providerCandidates, ArtworkSources.TvmazeImage);
        if (tvmaze is not null)
        {
            return tvmaze;
        }

        var theTvdb = FirstFromSource(providerCandidates, ArtworkSources.TheTvdb);
        if (theTvdb is not null)
        {
            return theTvdb;
        }

        var wikimedia = FirstFromSource(providerCandidates, ArtworkSources.Wikimedia);
        if (wikimedia is not null)
        {
            return wikimedia;
        }

        return string.IsNullOrWhiteSpace(tmdbBackdropUrl)
            ? null
            : new ArtworkCandidate(tmdbBackdropUrl, "TMDb backdrop");
    }

    private static ArtworkCandidate? FirstFromSource(
        IEnumerable<ArtworkCandidate> candidates,
        string source)
    {
        return candidates.FirstOrDefault(candidate =>
            string.Equals(candidate.Source, source, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(candidate.Url));
    }
}

public static class ArtworkSources
{
    public const string Fanart = "Fanart.tv";
    public const string TvmazeImage = "TVmaze image";
    public const string TheTvdb = "TheTVDB";
    public const string Wikimedia = "Wikimedia";
}
