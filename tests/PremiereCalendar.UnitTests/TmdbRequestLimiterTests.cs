using Microsoft.Extensions.Options;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class TmdbRequestLimiterTests
{
    [Fact]
    public async Task AcquireAsync_EnforcesConfiguredConcurrentRequestLimit()
    {
        using var limiter = new TmdbRequestLimiter(Microsoft.Extensions.Options.Options.Create(new TmdbOptions
        {
            MaxRequestsPerSecond = 40,
            MaxConcurrentRequests = 1
        }));

        using var firstLease = await limiter.AcquireAsync(CancellationToken.None);
        Assert.True(firstLease.IsAcquired);

        var secondAcquire = limiter.AcquireAsync(CancellationToken.None).AsTask();
        Assert.False(secondAcquire.IsCompleted);

        firstLease.Dispose();
        using var secondLease = await secondAcquire.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(secondLease.IsAcquired);
    }
}
