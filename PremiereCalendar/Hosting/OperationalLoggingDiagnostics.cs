using Serilog.Debugging;

namespace PremiereCalendar.Hosting;

public sealed record OperationalLoggingDiagnosticSnapshot(long FailureCount, DateTimeOffset? LastFailureUtc, string? Reason);

/// <summary>Process-wide Serilog sink failures, retaining only a count, timestamp and fixed reason code.</summary>
public static class OperationalLoggingDiagnostics
{
    private static readonly OperationalLoggingDiagnosticState State = new(WriteFirstFailureNotice);

    public static OperationalLoggingDiagnosticSnapshot Snapshot => State.Snapshot;

    public static void Enable() => SelfLog.Enable(State.Record);

    private static void WriteFirstFailureNotice(string reason)
        => Console.Error.WriteLine($"{{\"event\":\"operational_logging_failure\",\"reason\":\"{reason}\"}}");
}

internal sealed class OperationalLoggingDiagnosticState(Action<string>? firstFailureNotice = null)
{
    private readonly object _gate = new();
    private long _failureCount;
    private DateTimeOffset? _lastFailureUtc;
    private string? _reason;

    internal OperationalLoggingDiagnosticSnapshot Snapshot
    {
        get { lock (_gate) return new(_failureCount, _lastFailureUtc, _reason); }
    }

    internal void Record(string message)
    {
        var reason = Classify(message);
        bool first;
        lock (_gate)
        {
            first = ++_failureCount == 1;
            _lastFailureUtc = DateTimeOffset.UtcNow;
            _reason = reason;
        }
        if (first)
        {
            // Do not feed Serilog itself, retain input, or flood stderr when a sink repeatedly fails.
            try { firstFailureNotice?.Invoke(reason); }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException) { }
        }
    }

    private static string Classify(string message)
    {
        if (Contains("UnauthorizedAccessException") || Contains("Permission denied") || Contains("Access is denied")
            || Contains("Access to the path")) return "permission";
        if (Contains("mutex") || (Contains("named") && Contains("synchronization"))) return "mutex";
        if (Contains("IOException") || Contains("I/O error") || Contains("No space left") || Contains("disk full")
            || Contains("FileNotFoundException") || Contains("DirectoryNotFoundException") || Contains("PathTooLongException")
            || Contains("file system") || Contains("sharing violation")) return "io";
        return "other";

        bool Contains(string value) => message.Contains(value, StringComparison.OrdinalIgnoreCase);
    }
}
