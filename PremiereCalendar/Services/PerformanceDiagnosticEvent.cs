using System.Buffers;

namespace PremiereCalendar.Services;

/// <summary>Opaque technical correlation identifiers; never use titles, filters, URLs or credentials.</summary>
public sealed record PerformanceDiagnosticContext
{
    public string? LoadId { get; init; }
    public string? JobId { get; init; }
    public string? SourceKey { get; init; }
    public string? OperationId { get; init; }
    public string? RequestId { get; init; }
}

/// <summary>Fixed diagnostic schema. String values must be fixed code identifiers, not user/provider text.</summary>
public sealed record PerformanceDiagnosticDetails
{
    public string? Stage { get; init; }
    public string? Provider { get; init; }
    public string? Outcome { get; init; }
    public string? Reason { get; init; }
    public string? Cache { get; init; }
    public string? CacheKey { get; init; }
    public string? JobKind { get; init; }
    public string? SourceKey { get; init; }
    public DateOnly? WeekStart { get; init; }
    public DateOnly? WeekEnd { get; init; }
    public int? Attempt { get; init; }
    public int? ItemId { get; init; }
    public string? MediaType { get; init; }
    public int? Count { get; init; }
    public int? Total { get; init; }
    public int? StatusCode { get; init; }
    public DateTimeOffset? RetryAtUtc { get; init; }
    public DateTimeOffset? RequestedFetchedUtc { get; init; }
    public DateTimeOffset? AcceptedFetchedUtc { get; init; }
    public long? Sequence { get; init; }
    public double? DurationMilliseconds { get; init; }
    public double? WaitMilliseconds { get; init; }
    public double? SerializeMilliseconds { get; init; }
    public double? EncodeMilliseconds { get; init; }
    public double? StoreMilliseconds { get; init; }
    public int? RawBytes { get; init; }
    public int? StoredBytes { get; init; }
    public int? Active { get; init; }
    public int? Limit { get; init; }
    public bool? IsFinal { get; init; }
    public bool? FromCache { get; init; }

    internal PerformanceDiagnosticDetails Sanitize()
    {
        var mediaType = DiagnosticIdentifier.Code(MediaType);
        var stage = DiagnosticIdentifier.Code(Stage);
        var provider = DiagnosticIdentifier.Code(Provider);
        var outcome = DiagnosticIdentifier.Code(Outcome);
        var reason = DiagnosticIdentifier.Code(Reason);
        var cache = DiagnosticIdentifier.Code(Cache);
        var jobKind = DiagnosticIdentifier.Code(JobKind);
        var sourceKey = DiagnosticIdentifier.Code(SourceKey);
        var cacheKey = DiagnosticIdentifier.CacheKey(CacheKey);
        var duration = Finite(DurationMilliseconds);
        var wait = Finite(WaitMilliseconds);
        var serialize = Finite(SerializeMilliseconds);
        var encode = Finite(EncodeMilliseconds);
        var store = Finite(StoreMilliseconds);

        // Producers normally pass fixed code identifiers, so the common case needs no copy of
        // this wide record. Strings are compared by reference because the sanitizers return
        // either their input instance or a replacement; doubles are compared bitwise so a
        // negative zero (normalized to positive zero) still counts as a change.
        if (ReferenceEquals(mediaType, MediaType) && ReferenceEquals(stage, Stage)
            && ReferenceEquals(provider, Provider) && ReferenceEquals(outcome, Outcome)
            && ReferenceEquals(reason, Reason) && ReferenceEquals(cache, Cache)
            && ReferenceEquals(jobKind, JobKind) && ReferenceEquals(sourceKey, SourceKey)
            && ReferenceEquals(cacheKey, CacheKey)
            && SameBits(duration, DurationMilliseconds) && SameBits(wait, WaitMilliseconds)
            && SameBits(serialize, SerializeMilliseconds) && SameBits(encode, EncodeMilliseconds)
            && SameBits(store, StoreMilliseconds))
            return this;

        return this with
        {
            MediaType = mediaType,
            Stage = stage, Provider = provider,
            Outcome = outcome, Reason = reason,
            Cache = cache, JobKind = jobKind,
            SourceKey = sourceKey, CacheKey = cacheKey,
            DurationMilliseconds = duration, WaitMilliseconds = wait,
            SerializeMilliseconds = serialize, EncodeMilliseconds = encode,
            StoreMilliseconds = store
        };
    }

    private static bool SameBits(double? sanitized, double? original)
        => sanitized is { } left
            ? original is { } right && BitConverter.DoubleToInt64Bits(left) == BitConverter.DoubleToInt64Bits(right)
            : original is null;

    private static double? Finite(double? value) => value is { } number && double.IsFinite(number) ? Math.Max(0, number) : null;
}

internal sealed record PerformanceDiagnosticEvent(
    DateTimeOffset TimestampUtc,
    double ProcessElapsedMilliseconds,
    string InstanceId,
    long EventSequence,
    string Event,
    string? LoadId,
    string? JobId,
    string? SourceKey,
    string? OperationId,
    string? RequestId,
    string? TraceId,
    string? SpanId,
    PerformanceDiagnosticDetails? Details)
{
    // Includes record/object overhead and every retained string. The fixed schema
    // makes this an upper bound without allocating JSON on a producer thread.
    internal int EstimatedSnapshotBytes
    {
        get
        {
            long characters = 0;
            Add(InstanceId); Add(Event); Add(LoadId); Add(JobId); Add(SourceKey);
            Add(OperationId); Add(RequestId); Add(TraceId); Add(SpanId);
            if (Details is { } details)
            {
                Add(details.Stage); Add(details.Provider); Add(details.Outcome); Add(details.Reason);
                Add(details.Cache); Add(details.CacheKey); Add(details.JobKind); Add(details.SourceKey); Add(details.MediaType);
            }
            return (int)Math.Min(int.MaxValue, 2048 + characters * 2);
            void Add(string? value) => characters += value?.Length ?? 0;
        }
    }
}

public sealed record PerformanceDiagnosticHealth(
    bool Enabled,
    bool Healthy,
    long WrittenEvents,
    long DroppedEvents,
    long WriterFailures,
    DateTimeOffset? LastSuccessUtc,
    DateTimeOffset? LastFailureUtc,
    long QueuedEvents = 0,
    int QueueCapacity = 0,
    bool QueueFull = false,
    bool WriterRunning = false,
    long FlushTimeouts = 0,
    bool ShutdownTimedOut = false,
    bool WriterStalled = false,
    double ActiveIoMs = 0,
    long QueuedBytes = 0,
    long QueueByteCapacity = 0,
    long SerializationFailures = 0,
    long ArchiveFailures = 0,
    bool ArchiveActive = false,
    int ArchivePending = 0,
    bool ArchiveWorkerRunning = false);

internal static class DiagnosticIdentifier
{
    private static readonly SearchValues<char> CodeCharacters =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-");

    private static readonly SearchValues<char> HexCharacters = SearchValues.Create("0123456789abcdefABCDEF");
    private static readonly SearchValues<char> LowerHexCharacters = SearchValues.Create("0123456789abcdef");

    // Reject whole values instead of retaining fragments of a URL, query, exception or free text.
    internal static string? Code(string? value)
        => value is null ? null : value.Length is > 0 and <= 64 && !value.AsSpan().ContainsAnyExcept(CodeCharacters)
            ? value : "redacted";

    internal static string? Correlation(string? value)
    {
        if (value is null) return null;
        // 32 lowercase hex digits is already the "N" form Guid.ToString("N") would produce.
        if (value.Length == 32 && !value.AsSpan().ContainsAnyExcept(LowerHexCharacters)) return value;
        return Guid.TryParse(value, out var id) ? id.ToString("N") : "redacted";
    }

    internal static string? CacheKey(string? value)
        => value is null ? null : value == "default" || (value.Length is 20 or 64 && !value.AsSpan().ContainsAnyExcept(HexCharacters))
            ? value : "redacted";
}
