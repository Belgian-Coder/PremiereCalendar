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
    public int? Count { get; init; }
    public int? Total { get; init; }
    public int? StatusCode { get; init; }
    public long? Sequence { get; init; }
    public double? DurationMilliseconds { get; init; }
    public double? WaitMilliseconds { get; init; }
    public int? Active { get; init; }
    public int? Limit { get; init; }
    public bool? IsFinal { get; init; }
    public bool? FromCache { get; init; }

    internal PerformanceDiagnosticDetails Sanitize() => this with
    {
        Stage = DiagnosticIdentifier.Code(Stage), Provider = DiagnosticIdentifier.Code(Provider),
        Outcome = DiagnosticIdentifier.Code(Outcome), Reason = DiagnosticIdentifier.Code(Reason),
        Cache = DiagnosticIdentifier.Code(Cache), JobKind = DiagnosticIdentifier.Code(JobKind),
        SourceKey = DiagnosticIdentifier.Code(SourceKey), CacheKey = DiagnosticIdentifier.CacheKey(CacheKey),
        DurationMilliseconds = Finite(DurationMilliseconds), WaitMilliseconds = Finite(WaitMilliseconds)
    };

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
    PerformanceDiagnosticDetails? Details);

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
    double ActiveIoMs = 0);

internal static class DiagnosticIdentifier
{
    // Reject whole values instead of retaining fragments of a URL, query, exception or free text.
    internal static string? Code(string? value)
        => value is null ? null : value.Length is > 0 and <= 64 && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-') ? value : "redacted";

    internal static string? Correlation(string? value)
        => value is null ? null : Guid.TryParse(value, out var id) ? id.ToString("N") : "redacted";

    internal static string? CacheKey(string? value)
        => value is null ? null : value == "default" || (value.Length is 20 or 64 && value.All(Uri.IsHexDigit))
            ? value : "redacted";
}
