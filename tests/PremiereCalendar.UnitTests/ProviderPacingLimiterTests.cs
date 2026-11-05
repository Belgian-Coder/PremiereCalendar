using System.Diagnostics;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class ProviderPacingLimiterTests
{
    [Fact]
    public async Task ConcurrentAdmissionsRemainSpacedAndCanceledWaiterDoesNotAdmit()
    {
        var limiter = new ProviderPacingLimiter(TimeProvider.System);
        await limiter.WaitAsync(CancellationToken.None);
        using var abandoned = new CancellationTokenSource();
        var canceled = limiter.WaitAsync(abandoned.Token);
        var timer = Stopwatch.StartNew();
        var survivor = limiter.WaitAsync(CancellationToken.None);
        abandoned.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        await survivor.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(timer.Elapsed >= TimeSpan.FromMilliseconds(450), "The second admission must not burst alongside the first.");
    }
}
