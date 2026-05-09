namespace PremiereCalendar.Options;

public sealed class WatchmodeOptions
{
    public string BaseUrl { get; set; } = "https://api.watchmode.com/v1/";
    public bool Enabled { get; set; } = true;
    public string ApiKey { get; set; } = "";
    public string[] Regions { get; set; } = [];
    public bool EnableReleaseDiscovery { get; set; }
    public bool EnableAvailabilityEnrichment { get; set; } = true;
    public int RequestTimeoutSeconds { get; set; } = 20;
    public int MaxRetryAfterDelaySeconds { get; set; } = 2;
    public int CacheHours { get; set; } = 12;
    public int MaxConcurrentRequests { get; set; } = 2;
}
