using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class TmdbRequestLimiter : IDisposable
{
    private readonly TokenBucketRateLimiter _limiter;
    private readonly SemaphoreSlim _concurrentRequests;

    public TmdbRequestLimiter(IOptions<TmdbOptions> options)
    {
        var requestsPerSecond = Math.Clamp(options.Value.MaxRequestsPerSecond, 1, 40);
        var maxConcurrentRequests = Math.Clamp(options.Value.MaxConcurrentRequests, 1, 40);
        _limiter = new TokenBucketRateLimiter(new TokenBucketRateLimiterOptions
        {
            TokenLimit = requestsPerSecond,
            TokensPerPeriod = requestsPerSecond,
            ReplenishmentPeriod = TimeSpan.FromSeconds(1),
            QueueLimit = requestsPerSecond * 4,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            AutoReplenishment = true
        });
        _concurrentRequests = new SemaphoreSlim(maxConcurrentRequests, maxConcurrentRequests);
    }

    public async ValueTask<TmdbRequestLease> AcquireAsync(CancellationToken cancellationToken)
    {
        await _concurrentRequests.WaitAsync(cancellationToken);

        RateLimitLease? rateLease = null;
        try
        {
            rateLease = await _limiter.AcquireAsync(1, cancellationToken);
            if (!rateLease.IsAcquired)
            {
                _concurrentRequests.Release();
                return new TmdbRequestLease(null, rateLease, IsAcquired: false);
            }

            return new TmdbRequestLease(_concurrentRequests, rateLease, IsAcquired: true);
        }
        catch
        {
            rateLease?.Dispose();
            _concurrentRequests.Release();
            throw;
        }
    }

    public void Dispose()
    {
        _limiter.Dispose();
        _concurrentRequests.Dispose();
    }
}

public sealed class TmdbRequestLease : IDisposable
{
    private SemaphoreSlim? _concurrentRequests;
    private RateLimitLease? _rateLease;

    public TmdbRequestLease(SemaphoreSlim? concurrentRequests, RateLimitLease? rateLease, bool IsAcquired)
    {
        _concurrentRequests = concurrentRequests;
        _rateLease = rateLease;
        this.IsAcquired = IsAcquired;
    }

    public bool IsAcquired { get; }

    public void Dispose()
    {
        Interlocked.Exchange(ref _rateLease, null)?.Dispose();
        Interlocked.Exchange(ref _concurrentRequests, null)?.Release();
    }
}
