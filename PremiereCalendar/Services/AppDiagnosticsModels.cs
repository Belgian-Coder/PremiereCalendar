namespace PremiereCalendar.Services;

public sealed record CacheInspectorSummary(CacheBucketSummary Calendar, CacheBucketSummary Image);

public sealed record CacheBucketSummary(
    string Label,
    string Directory,
    bool Exists,
    int FileCount,
    long TotalBytes,
    DateTimeOffset? LastWriteUtc);

public enum BackgroundJobStatus
{
    Started,
    Succeeded,
    Failed,
    Skipped
}

public sealed record BackgroundJobEvent(
    string Id,
    string JobName,
    BackgroundJobStatus Status,
    string Message,
    DateTimeOffset OccurredUtc,
    long? DurationMilliseconds);
