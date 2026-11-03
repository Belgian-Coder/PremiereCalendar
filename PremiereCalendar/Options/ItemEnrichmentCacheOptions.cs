namespace PremiereCalendar.Options;

public sealed class ItemEnrichmentCacheOptions
{
    public bool Enabled { get; set; } = true;
    public int RetentionMonths { get; set; } = 3;
    public int MetadataRefreshDays { get; set; } = 7;
    public int RatingsRefreshHours { get; set; } = 24;
    public int MetadataFailureRetryMinutes { get; set; } = 5;
    public int RatingsFailureRetryMinutes { get; set; } = 5;
}
