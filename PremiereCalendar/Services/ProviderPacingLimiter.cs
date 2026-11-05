namespace PremiereCalendar.Services;

// Shared by every HTTP handler for a provider, including retries and background work.
internal sealed class ProviderPacingLimiter(TimeProvider clock, int minimumIntervalMilliseconds = 510)
{
    private readonly TimeSpan _interval = TimeSpan.FromMilliseconds(Math.Clamp(minimumIntervalMilliseconds, 1, 60_000));
    private readonly SemaphoreSlim _gate = new(1, 1);
    private long? _lastAdmission;

    internal async Task WaitAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // TVmaze documents at least 20 calls per 10 seconds per IP. Space
            // admissions rather than allowing simultaneous token-bucket bursts.
            if (_lastAdmission is { } last)
            {
                var delay = _interval - clock.GetElapsedTime(last);
                if (delay > TimeSpan.Zero) await Task.Delay(delay, clock, cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            _lastAdmission = clock.GetTimestamp();
        }
        finally { _gate.Release(); }
    }
}
