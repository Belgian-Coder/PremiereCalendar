namespace PremiereCalendar.Options;

public sealed class TmdbOptions
{
    public string BaseUrl { get; set; } = "https://api.themoviedb.org/3/";
    public string ImageBaseUrl { get; set; } = "https://image.tmdb.org/t/p/";
    public string PosterSize { get; set; } = "w185";
    public string BackdropSize { get; set; } = "w780";
    public string? BearerToken { get; set; }

    public string[] SourceRegions { get; set; } = [];
    public int DefaultLookAheadDays { get; set; } = 42;
    public int MaxPagesPerQuery { get; set; } = 500;
    public int MaxUnfilteredPagesPerQuery { get; set; } = 500;
    public int SourceFetchConcurrency { get; set; } = 12;
    public int PageBatchSize { get; set; } = 10;
    public int PageFetchConcurrency { get; set; } = 6;
    public int MaxEnrichmentConcurrency { get; set; } = 16;
    public int EnrichmentProgressBatchSize { get; set; } = 25;
    public int ExternalCandidateBatchSize { get; set; } = 100;
    public int RequestTimeoutSeconds { get; set; } = 20;
    public int SourceTimeoutSeconds { get; set; } = 45;
    public int MaxRequestsPerSecond { get; set; } = 20;
    public int MaxConcurrentRequests { get; set; } = 4;
}
