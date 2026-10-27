using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace PremiereCalendar.Services;

public sealed partial class PremiereTelemetry
{
    private static readonly AsyncLocal<PerformanceDiagnosticContext?> DiagnosticContext = new();
    private readonly PerformanceDiagnosticStore? _diagnosticStore;
    private readonly string _diagnosticInstanceId;
    private readonly long _diagnosticStarted = Stopwatch.GetTimestamp();
    private long _diagnosticSequence;

    public PerformanceDiagnosticHealth DiagnosticsHealth => _diagnosticStore?.Health ?? new(false, true, 0, 0, 0, null, null);

    public PerformanceDiagnosticContext CreateDiagnosticContext(string? loadId = null, string? jobId = null, string? source = null)
        => new() { LoadId = loadId ?? Guid.NewGuid().ToString("N"), JobId = jobId, SourceKey = source };

    public PerformanceDiagnosticContext? CaptureDiagnosticContext() => DiagnosticContext.Value;

    public IDisposable UseDiagnosticContext(PerformanceDiagnosticContext? context)
    {
        var previous = DiagnosticContext.Value;
        DiagnosticContext.Value = context;
        return new DiagnosticContextScope(previous);
    }

    /// <summary>Records fixed identifiers and numeric measurements; never pass exception text, titles or request URLs.</summary>
    public void RecordDiagnostic(string eventName, PerformanceDiagnosticContext? context = null, PerformanceDiagnosticDetails? details = null)
    {
        if (_diagnosticStore is null || !_diagnosticStore.Enabled) return;
        context ??= DiagnosticContext.Value;
        var activity = Activity.Current;
        _diagnosticStore.Write(new PerformanceDiagnosticEvent(
            DateTimeOffset.UtcNow, Stopwatch.GetElapsedTime(_diagnosticStarted).TotalMilliseconds, _diagnosticInstanceId,
            Interlocked.Increment(ref _diagnosticSequence), DiagnosticIdentifier.Code(eventName) ?? "redacted",
            DiagnosticIdentifier.Correlation(context?.LoadId), DiagnosticIdentifier.Correlation(context?.JobId),
            DiagnosticIdentifier.Code(context?.SourceKey), DiagnosticIdentifier.Correlation(context?.OperationId),
            DiagnosticIdentifier.Correlation(context?.RequestId),
            activity?.TraceId.ToString(), activity?.SpanId.ToString(), details?.Sanitize()));
    }

    /// <summary>Installs context while producer code runs and restores it before yielding to the consumer.</summary>
    public async IAsyncEnumerable<T> WithDiagnosticContext<T>(
        IAsyncEnumerable<T> source,
        PerformanceDiagnosticContext? context,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IAsyncEnumerator<T> enumerator;
        using (UseDiagnosticContext(context)) enumerator = source.GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                bool moved;
                T current = default!;
                using (UseDiagnosticContext(context))
                {
                    moved = await enumerator.MoveNextAsync();
                    if (moved) current = enumerator.Current;
                }
                if (!moved) yield break;
                yield return current;
            }
        }
        finally
        {
            using (UseDiagnosticContext(context)) await enumerator.DisposeAsync();
        }
    }

    public void FlushDiagnostics() => _diagnosticStore?.Flush();

    private sealed class DiagnosticContextScope(PerformanceDiagnosticContext? previous) : IDisposable
    {
        private bool _disposed;
        public void Dispose()
        {
            if (_disposed) return;
            DiagnosticContext.Value = previous;
            _disposed = true;
        }
    }
}
