using PremiereCalendar.Services;

namespace PremiereCalendar.Models;

public enum PremiereDateSourceKind
{
    Unknown = 0,
    TmdbFirstAirDate = 1,
    TmdbSeasonOneEpisodeOne = 2,
    TmdbEpisodeAirDate = 3,
    TmdbMovieReleaseDate = 4,
    TmdbMoviePrimaryReleaseDate = 5,
    ExternalProviderDate = 6,
    Cache = 7
}

public enum PremiereDataConfidence
{
    Low = 0,
    Medium = 1,
    High = 2
}

public sealed record PremiereDateSemantics(
    DateOnly ChosenDate,
    PremiereDateSourceKind SourceKind,
    PremiereDataConfidence Confidence,
    string Reason);

public sealed record PremiereMergeContribution
{
    public required string Source { get; init; }
    public string Status { get; init; } = "accepted";
    public string MatchMethod { get; init; } = "";
    public string Reason { get; init; } = "";
    public int? TmdbId { get; init; }
    public string? ImdbId { get; init; }
    public int? TvdbId { get; init; }
    public DateOnly? CandidateDate { get; init; }
    public string? ExternalProviderId { get; init; }
}

public sealed record PremiereMissingDataIssue
{
    public required string Kind { get; init; }
    public string Severity { get; init; } = "info";
    public required string Message { get; init; }
}

public enum WeekAnomalyKind
{
    LowItemCount = 0,
    HighMissingScoreRate = 1,
    HighMissingExternalIdRate = 2,
    LanguageSkew = 3,
    UnmappedExternalCandidates = 4,
    SourceFailure = 5,
    SourceContributionDrop = 6
}

public sealed record WeekAnomaly(
    WeekAnomalyKind Kind,
    string Severity,
    string Message);

public sealed record WeekScoreCoverage(
    int TotalCount,
    int TmdbCount,
    int ImdbCount,
    int RottenTomatoesCriticCount,
    int RottenTomatoesAudienceCount,
    int MetacriticCount)
{
    public int MissingImdbCount => Math.Max(0, TotalCount - ImdbCount);
    public int MissingRottenTomatoesCount => Math.Max(0, TotalCount - Math.Max(RottenTomatoesCriticCount, RottenTomatoesAudienceCount));
}

public sealed record WeekSourceDiagnostic(
    string SourceName,
    string ProviderKey,
    int SourceItemCount,
    int TotalItemCount,
    int? AcceptedCount,
    int? TotalCandidateCount,
    int? ProcessedCount,
    int? FilteredCount,
    int? UnmappedCount,
    string Phase,
    string? ProgressText,
    bool HasErrors);

public sealed record WeekDiagnostics
{
    public required DateOnly WeekStart { get; init; }
    public required DateOnly WeekEnd { get; init; }
    public required string CacheKey { get; init; }
    public required DateTimeOffset RecordedUtc { get; init; }
    public int TotalItemCount { get; init; }
    public IReadOnlyDictionary<string, int> LanguageDistribution { get; init; } = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
    public WeekScoreCoverage ScoreCoverage { get; init; } = new(0, 0, 0, 0, 0, 0);
    public IReadOnlyList<WeekSourceDiagnostic> Sources { get; init; } = [];
    public IReadOnlyList<WeekAnomaly> Anomalies { get; init; } = [];
}

public sealed record SourceHealthOverview(
    IReadOnlyList<SourceHealthProviderState> Providers,
    OmdbProviderCacheState? Omdb,
    ImdbDatasetState? ImdbDataset,
    IReadOnlyList<BackgroundJobEvent> RecentJobs);

public sealed record SourceHealthProviderState(
    string Provider,
    ProviderCacheScope Scope,
    string Key,
    DateTimeOffset LastCheckedUtc,
    DateTimeOffset? LastChangedUtc,
    string? Watermark,
    int? ItemCount,
    string? MetadataJson);

public sealed record BackfillResult(
    IReadOnlyList<PremiereItem> Items,
    int ChangedCount,
    int ScannedCount);
