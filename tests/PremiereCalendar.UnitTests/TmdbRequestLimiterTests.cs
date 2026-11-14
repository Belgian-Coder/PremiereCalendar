using Microsoft.Extensions.Options;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class TmdbRequestLimiterTests
{
    [Fact]
    public void ExecutionContextRestoresNestedScopesAndCapturesALiveAccessor()
    {
        Assert.Equal(ProviderWorkPriority.Foreground, ProviderExecutionContext.CapturePriorityAccessor()());
        var priority = ProviderWorkPriority.Warmup;
        Func<ProviderWorkPriority> captured;
        using (ProviderExecutionContext.Push(() => priority))
        {
            captured = ProviderExecutionContext.CapturePriorityAccessor();
            using (ProviderExecutionContext.Push(() => ProviderWorkPriority.Maintenance))
                Assert.Equal(ProviderWorkPriority.Maintenance, ProviderExecutionContext.CapturePriorityAccessor()());
            Assert.Equal(ProviderWorkPriority.Warmup, ProviderExecutionContext.CapturePriorityAccessor()());
            priority = ProviderWorkPriority.Foreground;
            Assert.Equal(ProviderWorkPriority.Foreground, captured());
        }
        Assert.Equal(ProviderWorkPriority.Foreground, ProviderExecutionContext.CapturePriorityAccessor()());
    }

    [Fact]
    public async Task AcquireAsync_GivesForegroundTheNextSlotAndPreservesFifo()
    {
        using var limiter = CreateLimiter();
        using var active = await limiter.AcquireAsync(CancellationToken.None);
        var backgroundFirst = AcquireWithPriorityAsync(limiter, () => ProviderWorkPriority.Warmup, TestContext.Current.CancellationToken);
        var backgroundSecond = AcquireWithPriorityAsync(limiter, () => ProviderWorkPriority.Maintenance, TestContext.Current.CancellationToken);
        var foregroundFirst = AcquireWithPriorityAsync(limiter, () => ProviderWorkPriority.Foreground, TestContext.Current.CancellationToken);
        var foregroundSecond = AcquireWithPriorityAsync(limiter, () => ProviderWorkPriority.Foreground, TestContext.Current.CancellationToken);

        active.Dispose();
        using var first = await foregroundFirst.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(foregroundSecond.IsCompleted);
        Assert.False(backgroundFirst.IsCompleted);
        first.Dispose();
        using var second = await foregroundSecond.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(backgroundFirst.IsCompleted);
        second.Dispose();
        using var third = await backgroundFirst.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(backgroundSecond.IsCompleted);
        third.Dispose();
        using var fourth = await backgroundSecond.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(fourth.IsAcquired);
    }

    [Fact]
    public async Task AcquireAsync_ReevaluatesPromotionWhileQueued()
    {
        using var limiter = CreateLimiter();
        using var active = await limiter.AcquireAsync(CancellationToken.None);
        var background = AcquireWithPriorityAsync(limiter, () => ProviderWorkPriority.Warmup, TestContext.Current.CancellationToken);
        var priority = ProviderWorkPriority.Maintenance;
        var promoted = AcquireWithPriorityAsync(limiter, () => priority, TestContext.Current.CancellationToken);
        priority = ProviderWorkPriority.Foreground;

        active.Dispose();
        using var first = await promoted.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(background.IsCompleted);
        first.Dispose();
        using var second = await background.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(second.IsAcquired);
    }

    [Fact]
    public async Task AcquireAsync_BoundsForegroundBurstAndSkipsCancelledWaiters()
    {
        using var limiter = CreateLimiter();
        using var active = await limiter.AcquireAsync(CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var cancelled = AcquireWithPriorityAsync(limiter, () => ProviderWorkPriority.Foreground, cancellation.Token);
        var background = AcquireWithPriorityAsync(limiter, () => ProviderWorkPriority.Maintenance, TestContext.Current.CancellationToken);
        var foreground = Enumerable.Range(0, 9)
            .Select(_ => AcquireWithPriorityAsync(limiter, () => ProviderWorkPriority.Foreground)).ToArray();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        active.Dispose();
        for (var index = 0; index < 8; index++)
        {
            using var lease = await foreground[index].WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.False(background.IsCompleted);
            lease.Dispose();
        }
        using var backgroundLease = await background.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(foreground[8].IsCompleted);
        backgroundLease.Dispose();
        using var last = await foreground[8].WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(last.IsAcquired);
    }

    [Fact]
    public async Task AcquireAsync_ChoosesForegroundWhenRateCapacityReturns()
    {
        using var limiter = CreateLimiter(concurrency: 4, requestsPerSecond: 1);
        using var active = await limiter.AcquireAsync(CancellationToken.None);
        var background = AcquireWithPriorityAsync(limiter, () => ProviderWorkPriority.Warmup, TestContext.Current.CancellationToken);
        var foreground = AcquireWithPriorityAsync(limiter, () => ProviderWorkPriority.Foreground, TestContext.Current.CancellationToken);
        Assert.False(background.IsCompleted);
        Assert.False(foreground.IsCompleted);

        using var next = await foreground.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(background.IsCompleted);
        using var last = await background.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(last.IsAcquired);
    }

    [Fact]
    public async Task AcquireAsync_CancellationDuringRateWaitLeavesCapacityForNextCaller()
    {
        using var limiter = CreateLimiter(concurrency: 1, requestsPerSecond: 1);
        using var active = await limiter.AcquireAsync(CancellationToken.None);
        active.Dispose();
        using var cancellation = new CancellationTokenSource();
        var cancelled = AcquireWithPriorityAsync(limiter, () => ProviderWorkPriority.Foreground, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        var next = AcquireWithPriorityAsync(limiter, () => ProviderWorkPriority.Foreground, TestContext.Current.CancellationToken);
        using var lease = await next.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(lease.IsAcquired);
    }

    private static TmdbRequestLimiter CreateLimiter(int concurrency = 1, int requestsPerSecond = 40)
        => new(Microsoft.Extensions.Options.Options.Create(new TmdbOptions
        {
            MaxRequestsPerSecond = requestsPerSecond,
            MaxConcurrentRequests = concurrency
        }));

    private static Task<TmdbRequestLease> AcquireWithPriorityAsync(TmdbRequestLimiter limiter,
        Func<ProviderWorkPriority> priority, CancellationToken cancellationToken = default)
    {
        using var scope = ProviderExecutionContext.Push(priority);
        return limiter.AcquireAsync(cancellationToken).AsTask();
    }

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
        using var secondLease = await secondAcquire.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(secondLease.IsAcquired);
    }
}
