using System.Threading.RateLimiting;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class TmdbRequestLimiter : IDisposable
{
    private readonly TokenBucketRateLimiter _limiter;

    public TmdbRequestLimiter(IOptions<TmdbOptions> options)
    {
        var requestsPerSecond = Math.Clamp(options.Value.MaxRequestsPerSecond, 1, 40);
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

    public async ValueTask<RateLimitLease> AcquireAsync(CancellationToken cancellationToken)
    {
        return await _limiter.AcquireAsync(1, cancellationToken);
    }

    public void Dispose()
    {
        _limiter.Dispose();
    }
}
