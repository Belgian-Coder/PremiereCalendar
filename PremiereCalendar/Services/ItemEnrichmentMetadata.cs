using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

// This projection intentionally has no calendar identity, date, episode, verification,
// or discovery provenance. It is safe to apply to another occurrence of a title.
public sealed record ItemEnrichmentMetadata
{
    public string? ImdbId { get; init; }
    public int? TvdbId { get; init; }
    public string? WikidataId { get; init; }
    public string? OriginalTitle { get; init; }
    public string? Title { get; init; }
    public string? Overview { get; init; }
    public string? OmdbPlot { get; init; }
    public string? OmdbPosterUrl { get; init; }
    public string? PosterUrl { get; init; }
    public string? BackdropUrl { get; init; }
    public string? ImageSource { get; init; }
    public string? TrailerUrl { get; init; }
    public string? ImdbUrl { get; init; }
    public string OriginalLanguage { get; init; } = "";
    public string[] OriginCountries { get; init; } = [];
    public PremiereSource[] Sources { get; init; } = [];
    public int[] GenreIds { get; init; } = [];
    public string[] Genres { get; init; } = [];
    public string[] Keywords { get; init; } = [];
    public int[] MovieReleaseTypes { get; init; } = [];
    public string[] Certifications { get; init; } = [];
    public string? TvStatus { get; init; }
    public string? TvType { get; init; }
    public int? RuntimeMinutes { get; init; }
    public string? NetworkName { get; init; }
    public string? WebChannelName { get; init; }
    public int? TvmazeAverageRuntimeMinutes { get; init; }
    public double? TvmazeRating { get; init; }
    public string? OfficialSiteUrl { get; init; }
    public string? TvmazeUrl { get; init; }
    public int? TvmazeShowId { get; init; }
    public ItemEnrichmentTmdbObservation? TmdbVotes { get; init; }
    public bool AvailabilityComplete { get; init; }
    // Only Watchmode may replace its prior availability entries during an
    // optional retry. Legacy and TMDb entries have no Watchmode ownership.
    public bool AvailabilityOwnedByWatchmode { get; init; }
    // Optional providers have their own completeness. A failure must not make
    // successfully fetched TMDb fields stale or replace their corrected values.
    public bool OptionalMetadataComplete { get; init; } = true;
    public bool TvmazeComplete { get; init; } = true;
    public bool ArtworkComplete { get; init; } = true;
    // A filter preflight verified core TMDb fields without running optional work.
    // Its record metadata clock is the proof clock when another profile completes it.
    public bool CoreMetadataOnly { get; init; }
    // A preflight retained a complete, matching OMDb cover. This is separate
    // from an incomplete artwork retry or a cover whose owner verified absence.
    public bool CoreOmdbCoverReusable { get; init; }
    // The displayed poster is a retained fallback whose owner verified absence.
    // Unrelated empty providers cannot create this evidence for legacy covers.
    public bool PosterOwnerVerifiedEmpty { get; init; }
    public bool RuntimeFromTvmaze { get; init; }
    public bool OptionalFreshnessTracked { get; init; }
    public DateTimeOffset? AvailabilityFetchedUtc { get; init; }
    public DateTimeOffset? TvmazeFetchedUtc { get; init; }
    public DateTimeOffset? ArtworkFetchedUtc { get; init; }
    // A provider validation result, not the date of the occurrence being mapped.
    public bool FirstEpisodeDateChecked { get; init; }
    public DateOnly? FirstEpisodeDate { get; init; }

    // Date semantics on a calendar row can describe a fallback. Only an explicit
    // successful season validation may set FirstEpisodeDateChecked.
    public static ItemEnrichmentMetadata FromItem(PremiereItem item) => new()
    {
        ImdbId = item.ImdbId, TvdbId = item.TvdbId, WikidataId = item.WikidataId,
        Title = item.Title, OriginalTitle = item.OriginalTitle, Overview = item.Overview, PosterUrl = item.PosterUrl,
        BackdropUrl = item.BackdropUrl, ImageSource = item.ImageSource, TrailerUrl = item.TrailerUrl,
        ImdbUrl = item.ImdbUrl, OriginalLanguage = item.OriginalLanguage, OriginCountries = item.OriginCountries,
        Sources = item.Sources.Where(source => !string.Equals(source.Kind, "schedule", StringComparison.OrdinalIgnoreCase)).ToArray(),
        GenreIds = item.GenreIds, Genres = item.Genres, Keywords = item.Keywords,
        MovieReleaseTypes = item.MovieReleaseTypes, Certifications = item.Certifications,
        TvStatus = item.TvStatus, TvType = item.TvType, RuntimeMinutes = item.RuntimeMinutes,
        NetworkName = item.Sources.Any(source => string.Equals(source.Kind, "schedule", StringComparison.OrdinalIgnoreCase)
                && string.Equals(source.Name, item.NetworkName, StringComparison.OrdinalIgnoreCase)) ? null : item.NetworkName,
        WebChannelName = item.WebChannelName,
        TvmazeAverageRuntimeMinutes = item.TvmazeAverageRuntimeMinutes, TvmazeRating = item.TvmazeRating,
        OfficialSiteUrl = item.OfficialSiteUrl, TvmazeUrl = item.TvmazeUrl,
        TvmazeShowId = ParseTvmazeShowId(item.TvmazeUrl),
        TmdbVotes = ItemEnrichmentTmdbObservation.Observe(null, item.TmdbScore, item.TmdbVoteCount, item.LastUpdatedUtc)
    };

    public PremiereItem Apply(PremiereItem discovered, bool preferDiscoveryMetadata = true) => discovered with
    {
        ImdbId = ImdbId, TvdbId = TvdbId, WikidataId = WikidataId,
        Title = preferDiscoveryMetadata ? Text(discovered.Title, Title) ?? discovered.Title : Text(Title, discovered.Title) ?? discovered.Title,
        OriginalTitle = preferDiscoveryMetadata ? Text(discovered.OriginalTitle, OriginalTitle) : OriginalTitle,
        Overview = preferDiscoveryMetadata ? Text(discovered.Overview, Overview) : Overview,
        PosterUrl = preferDiscoveryMetadata ? Text(discovered.PosterUrl, PosterUrl) : PosterUrl,
        BackdropUrl = preferDiscoveryMetadata ? Text(discovered.BackdropUrl, BackdropUrl) : BackdropUrl,
        ImageSource = preferDiscoveryMetadata && discovered.PosterUrl is not null ? discovered.ImageSource : ImageSource, TrailerUrl = TrailerUrl, ImdbUrl = ImdbUrl,
        OriginalLanguage = preferDiscoveryMetadata ? Text(discovered.OriginalLanguage, OriginalLanguage) ?? "" : OriginalLanguage,
        OriginCountries = preferDiscoveryMetadata && OriginCountries.Length == 0 ? discovered.OriginCountries : OriginCountries,
        Sources = Sources, SourceNames = Sources.Select(source => source.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
        GenreIds = preferDiscoveryMetadata && GenreIds.Length == 0 ? discovered.GenreIds : GenreIds, Genres = Genres, Keywords = Keywords,
        MovieReleaseTypes = MovieReleaseTypes, Certifications = Certifications, TvStatus = TvStatus, TvType = TvType,
        RuntimeMinutes = RuntimeMinutes, NetworkName = NetworkName, WebChannelName = WebChannelName,
        TmdbScore = preferDiscoveryMetadata ? discovered.TmdbScore ?? TmdbVotes?.TmdbScore : TmdbVotes?.TmdbScore,
        TmdbVoteCount = preferDiscoveryMetadata ? discovered.TmdbVoteCount ?? TmdbVotes?.TmdbVoteCount : TmdbVotes?.TmdbVoteCount,
        TvmazeAverageRuntimeMinutes = TvmazeAverageRuntimeMinutes, TvmazeRating = TvmazeRating,
        OfficialSiteUrl = OfficialSiteUrl, TvmazeUrl = TvmazeUrl
    };

    private static string? Text(string? preferred, string? fallback) => string.IsNullOrWhiteSpace(preferred) ? fallback : preferred;

    private static int? ParseTvmazeShowId(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !(uri.Host.Equals("tvmaze.com", StringComparison.OrdinalIgnoreCase)
                || uri.Host.Equals("www.tvmaze.com", StringComparison.OrdinalIgnoreCase))) return null;
        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && parts[0] == "shows" && int.TryParse(parts[1], out var id) && id > 0 ? id : null;
    }
}

// TMDb votes are observed during discovery independently of external rating
// requests. Keeping their clock here avoids fabricating an OMDb/RT success.
public sealed record ItemEnrichmentTmdbObservation(double? TmdbScore, int? TmdbVoteCount, DateTimeOffset? TmdbObservedUtc)
{
    public static ItemEnrichmentTmdbObservation? Observe(ItemEnrichmentTmdbObservation? previous,
        double? score, int? voteCount, DateTimeOffset observedUtc)
    {
        if (score is null && voteCount is null) return previous;
        // If a partial observation retains an older field, retain its older clock too.
        var keepsOldField = score is null && previous?.TmdbScore is not null
            || voteCount is null && previous?.TmdbVoteCount is not null;
        var clock = keepsOldField && previous?.TmdbObservedUtc is { } old && old < observedUtc ? old : observedUtc;
        return new(score ?? previous?.TmdbScore, voteCount ?? previous?.TmdbVoteCount, clock);
    }
}

public sealed record ItemEnrichmentRatings(
    double? ImdbScore, int? ImdbVoteCount, int? RottenTomatoesScore,
    int? RottenTomatoesAudienceScore, int? MetacriticScore,
    string? ImdbId = null, string? WikidataId = null, string? Title = null, int? Year = null,
    bool FreshnessVerified = true)
{
    public bool ProviderFreshnessTracked { get; init; }
    public bool ExternalRequired { get; init; } = true;
    public bool TomatoesRequired { get; init; } = true;
    public DateTimeOffset? ExternalFetchedUtc { get; init; }
    public DateTimeOffset? TomatoesFetchedUtc { get; init; }
    public ItemEnrichmentRatingValues? ExternalValues { get; init; }
    public ItemEnrichmentRatingValues? TomatoesValues { get; init; }
    public bool DatasetRequired { get; init; }
    public ItemEnrichmentDatasetRating? Dataset { get; init; }
    public static ItemEnrichmentRatings FromItem(PremiereItem item) => new(
        item.ImdbScore, item.ImdbVoteCount, item.RottenTomatoesScore, item.RottenTomatoesAudienceScore, item.MetacriticScore,
        item.ImdbId, item.WikidataId, item.Title, item.PremiereDate.Year);

    public bool Matches(PremiereItem item) => Same(ImdbId, item.ImdbId) && Same(WikidataId, item.WikidataId)
        && Same(Title, item.Title) && Year == item.PremiereDate.Year;

    internal bool MatchesMetadata(ItemEnrichmentMetadata? metadata) => metadata is null
        || Same(ImdbId, metadata.ImdbId) && Same(WikidataId, metadata.WikidataId);

    internal bool MatchesImdb(string? imdbId) => Same(ImdbId, imdbId);
    internal bool MatchesTomatoes(string? wikidataId, string? title, int? year) => Same(WikidataId, wikidataId) && Same(Title, title) && Year == year;

    public PremiereItem Apply(PremiereItem item)
    {
        var externalMatches = Same(ImdbId, item.ImdbId);
        var tomatoesMatch = MatchesTomatoes(item.WikidataId, item.Title, item.PremiereDate.Year);
        var critic = externalMatches && tomatoesMatch ? RottenTomatoesScore
            : !ProviderFreshnessTracked ? null : externalMatches ? ExternalValues?.RottenTomatoesScore
            : tomatoesMatch ? TomatoesValues?.RottenTomatoesScore : null;
        var audience = externalMatches && tomatoesMatch ? RottenTomatoesAudienceScore
            : !ProviderFreshnessTracked ? null : externalMatches ? ExternalValues?.RottenTomatoesAudienceScore
            : tomatoesMatch ? TomatoesValues?.RottenTomatoesAudienceScore : null;
        return item with
        {
            ImdbScore = externalMatches ? ImdbScore : null,
            ImdbVoteCount = externalMatches ? ImdbVoteCount : null,
            RottenTomatoesScore = critic, RottenTomatoesAudienceScore = audience,
            MetacriticScore = externalMatches ? MetacriticScore : null
        };
    }

    public ItemEnrichmentRatings PreserveMissing(ItemEnrichmentRatings? previous) => previous is null ? this : this with
    {
        ImdbScore = ImdbScore ?? (Same(ImdbId, previous.ImdbId) ? previous.ImdbScore : null),
        ImdbVoteCount = ImdbVoteCount ?? (Same(ImdbId, previous.ImdbId) ? previous.ImdbVoteCount : null),
        RottenTomatoesScore = RottenTomatoesScore ?? (Same(ImdbId, previous.ImdbId) && MatchesTomatoes(previous.WikidataId, previous.Title, previous.Year) ? previous.RottenTomatoesScore : null),
        RottenTomatoesAudienceScore = RottenTomatoesAudienceScore ?? (Same(ImdbId, previous.ImdbId) && MatchesTomatoes(previous.WikidataId, previous.Title, previous.Year) ? previous.RottenTomatoesAudienceScore : null),
        MetacriticScore = MetacriticScore ?? (Same(ImdbId, previous.ImdbId) ? previous.MetacriticScore : null)
    };

    private static bool Same(string? first, string? second) => string.Equals(first?.Trim(), second?.Trim(), StringComparison.OrdinalIgnoreCase);
}

public sealed record ItemEnrichmentRatingValues(double? ImdbScore = null, int? ImdbVoteCount = null,
    int? RottenTomatoesScore = null, int? RottenTomatoesAudienceScore = null, int? MetacriticScore = null);

public sealed record ItemEnrichmentDatasetRating(string? ImdbId, double? ImdbScore, int? ImdbVoteCount, DateTimeOffset FetchedUtc);
