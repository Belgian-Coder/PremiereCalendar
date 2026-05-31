namespace PremiereCalendar.Models;

public enum PremiereItemType
{
    SeriesPremiere,
    SeriesEpisode,
    MovieFirstRelease
}

public enum PremiereMediaType
{
    Series,
    Movie
}

public enum PremiereVerificationState
{
    Verified,
    Unverified
}

public sealed record PremiereSource
{
    public required string Name { get; init; }
    public int? Id { get; init; }
    public string Kind { get; init; } = "";
}

public sealed record PremiereItem
{
    public string CanonicalId { get; init; } = "";
    public PremiereItemType Type { get; init; }

    public required PremiereMediaType MediaType { get; init; }
    public required int TmdbId { get; init; }
    public string? ImdbId { get; init; }
    public int? TvdbId { get; init; }
    public string? WikidataId { get; init; }
    public PremiereVerificationState VerificationState { get; init; } = PremiereVerificationState.Verified;
    public string? VerificationNote { get; init; }
    public string? ExternalProviderId { get; init; }
    public string? ExternalUrl { get; init; }
    public string? ExternalCandidateKey { get; init; }

    public required string Title { get; init; }
    public string? OriginalTitle { get; init; }
    public required DateOnly PremiereDate { get; init; }

    public string? Overview { get; init; }
    public string? PosterUrl { get; init; }
    public string? BackdropUrl { get; init; }
    public string? ImageSource { get; init; }
    public string? TrailerUrl { get; init; }
    public string? TmdbUrl { get; init; }
    public string? ImdbUrl { get; init; }

    public string OriginalLanguage { get; init; } = "";
    public string[] OriginCountries { get; init; } = [];
    public string[] SourceNames { get; init; } = [];
    public PremiereSource[] Sources { get; init; } = [];
    public int[] GenreIds { get; init; } = [];
    public string[] Genres { get; init; } = [];
    public string[] Keywords { get; init; } = [];
    public int[] MovieReleaseTypes { get; init; } = [];
    public string[] Certifications { get; init; } = [];
    public string? TvStatus { get; init; }
    public string? TvType { get; init; }
    public string? EpisodeTitle { get; init; }
    public int? SeasonNumber { get; init; }
    public int? EpisodeNumber { get; init; }
    public string? EpisodeSource { get; init; }

    public int? RuntimeMinutes { get; init; }

    public double? TmdbScore { get; init; }
    public int? TmdbVoteCount { get; init; }

    public double? ImdbScore { get; init; }
    public int? ImdbVoteCount { get; init; }
    public int? RottenTomatoesScore { get; init; }
    public int? RottenTomatoesAudienceScore { get; init; }
    public int? MetacriticScore { get; init; }

    public PremiereDateSemantics? DateSemantics { get; init; }
    public PremiereMergeContribution[] MergeContributions { get; init; } = [];
    public PremiereMissingDataIssue[] MissingDataIssues { get; init; } = [];

    public string? NetworkName { get; init; }
    public string? WebChannelName { get; init; }
    public int? TvmazeAverageRuntimeMinutes { get; init; }
    public double? TvmazeRating { get; init; }
    public string? OfficialSiteUrl { get; init; }
    public string? TvmazeUrl { get; init; }

    public DateTimeOffset LastUpdatedUtc { get; init; } = DateTimeOffset.UtcNow;
}
