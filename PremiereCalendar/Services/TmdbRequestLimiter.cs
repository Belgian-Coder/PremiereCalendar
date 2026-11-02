using System.Diagnostics;
using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class TmdbRequestLimiter : IDisposable
{
    private readonly TokenBucketRateLimiter _limiter;
    private readonly object _gate = new();
    private readonly ProviderAdmissionQueue<TaskCompletionSource<TmdbRequestLease>> _admissions = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly int _maximumConcurrency;
    private readonly int _requestsPerSecond;
    private readonly PremiereTelemetry? _telemetry;
    private int _activeRequests;
    private CancellationTokenSource? _rateWaitCancellation;
    private bool _processing;
    private bool _disposed;

    public TmdbRequestLimiter(IOptions<TmdbOptions> options, PremiereTelemetry? telemetry = null)
    {
        var requestsPerSecond = Math.Clamp(options.Value.MaxRequestsPerSecond, 1, 40);
        _requestsPerSecond = requestsPerSecond;
        _telemetry = telemetry;
        _maximumConcurrency = Math.Clamp(options.Value.MaxConcurrentRequests, 1, 40);
        _limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = requestsPerSecond,
            TokensPerPeriod = requestsPerSecond,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            QueueLimit = requestsPerSecond * 4,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true
        });
    }

    public async ValueTask<TmdbRequestLease> AcquireAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var started = Stopwatch.GetTimestamp();
        var context = _telemetry?.CaptureDiagnosticContext();
        var completion = new TaskCompletionSource<TmdbRequestLease>(TaskCreationOptions.RunContinuationsAsynchronously);
        ProviderAdmissionQueue<TaskCompletionSource<TmdbRequestLease>>.Entry waiter;
        int queued;
        int active;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            waiter = _admissions.Enqueue(completion, ProviderExecutionContext.CapturePriorityAccessor(), cancellationToken);
            queued = _admissions.Count;
            active = _activeRequests;
        }
        using var registration = cancellationToken.Register(() =>
        {
            lock (_gate)
            {
                if (_admissions.Remove(waiter)) completion.TrySetCanceled(cancellationToken);
                if (_admissions.PeekNext() is null) _rateWaitCancellation?.Cancel();
            }
        });
        StartProcessing();
        var outcome = "failed";
        try
        {
            var lease = await completion.Task;
            outcome = lease.IsAcquired ? "acquired" : "rejected";
            return lease;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = "cancelled";
            throw;
        }
        finally
        {
            // This admission precedes HttpClient and is deliberately separate
            // from remote request/headers timing in AdaptiveProviderHandler.
            _telemetry?.RecordDiagnostic("provider.local_admission.completed", context, new()
            {
                Provider = "tmdb", Stage = "local_admission", Outcome = outcome,
                DurationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                Count = queued, Active = active, Limit = _maximumConcurrency, Total = _requestsPerSecond
            });
        }
    }

    private void StartProcessing()
    {
        lock (_gate)
        {
            if (_disposed || _processing || _activeRequests >= _maximumConcurrency || _admissions.PeekNext() is null) return;
            _processing = true;
        }
        _ = ProcessQueueAsync();
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            while (true)
            {
                CancellationTokenSource rateWait;
                lock (_gate)
                {
                    if (_disposed || _activeRequests >= _maximumConcurrency || _admissions.PeekNext() is null)
                    {
                        _processing = false;
                        return;
                    }
                    rateWait = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                    _rateWaitCancellation = rateWait;
                }
                // Only one token acquisition is pending. Requests retain their
                // live priority until that token is available, instead of taking
                // all concurrency slots while queued in the FIFO token bucket.
                RateLimitLease rateLease;
                try
                {
                    rateLease = await _limiter.AcquireAsync(1, rateWait.Token);
                }
                catch (OperationCanceledException) when (!_stopping.IsCancellationRequested)
                {
                    // All pending callers cancelled. A new arrival may already
                    // be queued, so recheck before stopping the admission pump.
                    continue;
                }
                finally
                {
                    lock (_gate)
                    {
                        _rateWaitCancellation = null;
                        rateWait.Dispose();
                    }
                }
                lock (_gate)
                {
                    var next = _disposed ? null : _admissions.PeekNext();
                    if (next is null)
                    {
                        rateLease.Dispose();
                        _processing = false;
                        return;
                    }
                    _admissions.Admit(next);
                    if (rateLease.IsAcquired)
                    {
                        _activeRequests++;
                        next.Value.TrySetResult(new TmdbRequestLease(Release, rateLease, true));
                    }
                    else
                    {
                        next.Value.TrySetResult(new TmdbRequestLease((Action?)null, rateLease, false));
                    }
                    if (_activeRequests >= _maximumConcurrency || _admissions.PeekNext() is null)
                    {
                        _processing = false;
                        return;
                    }
                }
            }
        }
        catch (Exception exception)
        {
            lock (_gate)
            {
                _processing = false;
                foreach (var pending in _admissions.Drain()) pending.Value.TrySetException(exception);
            }
        }
    }

    private void Release()
    {
        lock (_gate) _activeRequests--;
        StartProcessing();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var pending in _admissions.Drain()) pending.Value.TrySetException(new ObjectDisposedException(nameof(TmdbRequestLimiter)));
        }
        _stopping.Cancel();
        _limiter.Dispose();
        _stopping.Dispose();
    }
}

public sealed class TmdbRequestLease : IDisposable
{
    private Action? _release;
    private RateLimitLease? _rateLease;

    public TmdbRequestLease(SemaphoreSlim? concurrentRequests, RateLimitLease? rateLease, bool IsAcquired)
        : this(concurrentRequests is null ? null : () => concurrentRequests.Release(), rateLease, IsAcquired)
    {
    }

    internal TmdbRequestLease(Action? release, RateLimitLease? rateLease, bool IsAcquired)
    {
        _release = release;
        _rateLease = rateLease;
        this.IsAcquired = IsAcquired;
    }

    public bool IsAcquired { get; }

    public void Dispose()
    {
        Interlocked.Exchange(ref _rateLease, null)?.Dispose();
        Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
