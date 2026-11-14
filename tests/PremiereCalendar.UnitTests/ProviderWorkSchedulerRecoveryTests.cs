using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class ProviderWorkSchedulerTests
{
    [Fact]
    public async Task BoundedProgressContinuationsKeepFailureBudgetAndUsePersistedDeadline()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        _scheduler = NewScheduler(clock);
        var handle = await _scheduler.EnqueueAsync(new(ProviderWorkKind.ProviderDeltaSync, "continuation", ProviderWorkPriority.Maintenance, "{}"), TestContext.Current.CancellationToken);
        for (var cycle = 0; cycle < 7; cycle++)
        {
            var job = (await _scheduler.ClaimNextAsync(false, "continuing", TestContext.Current.CancellationToken))!;
            var execution = _scheduler.BeginExecution(job, TestContext.Current.CancellationToken);
            try
            {
                var deadline = clock.GetUtcNow().AddMinutes(1);
                await _scheduler.DeferContinuationAsync(job, deadline, TestContext.Current.CancellationToken);
                var deferred = (await _scheduler.GetAsync(handle.JobId, TestContext.Current.CancellationToken))!;
                Assert.Equal(ProviderWorkState.RetryWaiting, deferred.State);
                Assert.Equal(deadline, deferred.NextAttemptUtc);
                Assert.Equal(0, deferred.AttemptCount);
                Assert.Null(deferred.LastError);
                Assert.Null(await _scheduler.ClaimNextAsync(false, "early", TestContext.Current.CancellationToken));
                clock.Advance(TimeSpan.FromMinutes(1));
            }
            finally { _scheduler.EndExecution(job, execution); }
        }
    }

    [Fact]
    public async Task SchedulerWaitsForDatabaseRecoveryAndShutdownCancelsWaiting()
    {
        await using var services = new ServiceCollection().BuildServiceProvider();
        var recovery = new DatabaseRecoveryState();
        using var worker = new ProviderWorkSchedulerHostedService(_scheduler,
            services.GetRequiredService<IServiceScopeFactory>(), recovery,
            Microsoft.Extensions.Options.Options.Create(new ProviderSchedulerOptions()),
            NullLogger<ProviderWorkSchedulerHostedService>.Instance);
        await worker.StartAsync(CancellationToken.None);
        Assert.False(worker.ExecuteTask!.IsCompleted);
        await worker.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RecoverySignalIsReusableAcrossHealthyAndUnhealthyTransitions()
    {
        var recovery = new DatabaseRecoveryState();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var pending = recovery.WaitUntilHealthyAsync(cancellation.Token);
        Assert.False(pending.IsCompleted);
        recovery.Set(new(1, 1, true, "ready", null));
        await pending;
        recovery.Set(new(1, 1, false, "unavailable", null));
        pending = recovery.WaitUntilHealthyAsync(cancellation.Token);
        Assert.False(pending.IsCompleted);
        recovery.Set(new(1, 1, true, "recovered", null));
        await pending;
    }

    [Fact]
    public async Task WaitingSchedulerStartsExistingQueuedWorkWhenDatabaseBecomesHealthy()
    {
        var start = new DateOnly(2026, 11, 2);
        var handle = await EnqueueCalendarAsync("startup-recovery", start);
        var pipeline = new ForegroundHandoffPipeline(start);
        await using var services = new ServiceCollection().AddSingleton<IPremiereLoadPipeline>(pipeline).BuildServiceProvider();
        var recovery = new DatabaseRecoveryState();
        using var worker = new ProviderWorkSchedulerHostedService(_scheduler,
            services.GetRequiredService<IServiceScopeFactory>(), recovery,
            Microsoft.Extensions.Options.Options.Create(new ProviderSchedulerOptions()),
            NullLogger<ProviderWorkSchedulerHostedService>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal(ProviderWorkState.Queued, (await _scheduler.GetAsync(handle.JobId, TestContext.Current.CancellationToken))!.State);
            recovery.Set(new(DatabaseSchema.CurrentVersion, DatabaseSchema.CurrentVersion, true, "recovered", null));
            await pipeline.Entered(start).WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            pipeline.Release(start);
            await WaitUntilAsync(async () => (await _scheduler.GetAsync(handle.JobId))!.State == ProviderWorkState.Completed);
        }
        finally { await worker.StopAsync(TestContext.Current.CancellationToken); }
    }

    [Fact]
    public async Task InterestChurnFinishesCleanupAndLeavesUnwatchedJobDemoted()
    {
        var handle = await _scheduler.EnqueueAsync(new(ProviderWorkKind.CalendarForeground, "interest-churn", ProviderWorkPriority.Foreground, "{}"), TestContext.Current.CancellationToken);
        await Task.WhenAll(Enumerable.Range(0, 24).Select(async _ =>
        {
            await using var watcher = _scheduler.WatchAsync(handle).GetAsyncEnumerator();
            Assert.True(await watcher.MoveNextAsync());
            await Task.Yield();
        })).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(0, _scheduler.BroadcastCount);
        Assert.Equal(ProviderWorkPriority.Adjacent, (await _scheduler.GetAsync(handle.JobId, TestContext.Current.CancellationToken))!.Priority);
    }

    [Fact]
    public async Task IdleWaitWakesBothWorkersImmediatelyOnLocalEnqueue()
    {
        var first = _scheduler.WaitForSignalAsync(_scheduler.WorkAvailable, true, CancellationToken.None);
        var second = _scheduler.WaitForSignalAsync(_scheduler.WorkAvailable, false, CancellationToken.None);
        await _scheduler.EnqueueAsync(new(ProviderWorkKind.CalendarForeground, "wake", ProviderWorkPriority.Foreground, "{}"), TestContext.Current.CancellationToken);
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task IdleWaitHonorsRetryDeadlineWithoutWaitingForIdlePoll()
    {
        var handle = await _scheduler.EnqueueAsync(new(ProviderWorkKind.CalendarForeground, "due", ProviderWorkPriority.Foreground, "{}"), TestContext.Current.CancellationToken);
        var job = (await _scheduler.ClaimNextAsync(false, "worker", TestContext.Current.CancellationToken))!;
        await _store.RequeueAsync(handle.JobId, job.LeaseOwner!, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMilliseconds(100), false, null, TestContext.Current.CancellationToken);
        await _scheduler.WaitForSignalAsync(_scheduler.WorkAvailable, true, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task LatestPrefetchRetiresOnlyQueuedUnpromotedAdjacentJobs()
    {
        var running = await _scheduler.EnqueueAsync(new(ProviderWorkKind.AdjacentWeekPrefetch, "running", ProviderWorkPriority.Adjacent, "{}"), TestContext.Current.CancellationToken);
        var claimed = (await _scheduler.ClaimNextAsync(false, "worker", TestContext.Current.CancellationToken))!;
        var obsolete = await _scheduler.EnqueueAsync(new(ProviderWorkKind.AdjacentWeekPrefetch, "old", ProviderWorkPriority.Adjacent, "{}"), TestContext.Current.CancellationToken);
        var active = await _scheduler.EnqueueAsync(new(ProviderWorkKind.AdjacentWeekPrefetch, "current", ProviderWorkPriority.Adjacent, "{}"), TestContext.Current.CancellationToken);
        var foreground = await _scheduler.EnqueueAsync(new(ProviderWorkKind.CalendarForeground, "foreground", ProviderWorkPriority.Foreground, "{}"), TestContext.Current.CancellationToken);
        var promoted = await _scheduler.EnqueueAsync(new(ProviderWorkKind.AdjacentWeekPrefetch, "promoted", ProviderWorkPriority.ActiveWeek, "{}"), TestContext.Current.CancellationToken);
        await _scheduler.RetireObsoletePrefetchAsync(["current"], TestContext.Current.CancellationToken);
        Assert.Equal(ProviderWorkState.Cancelled, (await _scheduler.GetAsync(obsolete.JobId, TestContext.Current.CancellationToken))!.State);
        Assert.Equal(ProviderWorkState.Queued, (await _scheduler.GetAsync(active.JobId, TestContext.Current.CancellationToken))!.State);
        Assert.Equal(ProviderWorkState.Queued, (await _scheduler.GetAsync(foreground.JobId, TestContext.Current.CancellationToken))!.State);
        Assert.Equal(ProviderWorkState.Queued, (await _scheduler.GetAsync(promoted.JobId, TestContext.Current.CancellationToken))!.State);
        Assert.Equal(ProviderWorkState.Running, (await _scheduler.GetAsync(running.JobId, TestContext.Current.CancellationToken))!.State);
        Assert.Equal(claimed.LeaseOwner, (await _scheduler.GetAsync(running.JobId, TestContext.Current.CancellationToken))!.LeaseOwner);
    }

    [Fact]
    public async Task PassiveObserverDemotesWithoutCancelAndFreshWatcherRestoresDemand()
    {
        var handle = await _scheduler.EnqueueAsync(new(ProviderWorkKind.CalendarForeground, "passive", ProviderWorkPriority.Foreground, "{}"), TestContext.Current.CancellationToken);
        var observation = new CalendarObservationScope();
        using var ambient = observation.Enter();
        await using var passive = _scheduler.WatchAsync(handle, TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await passive.MoveNextAsync());
        var job = (await _scheduler.ClaimNextAsync(false, "worker", TestContext.Current.CancellationToken))!;
        var execution = _scheduler.BeginExecution(job, TestContext.Current.CancellationToken);
        using var providerContext = _scheduler.UseProviderExecutionContext(job);
        var priority = ProviderExecutionContext.CapturePriorityAccessor();
        try
        {
            await observation.ReleaseForegroundAsync(TestContext.Current.CancellationToken);
            Assert.False(execution.IsCancellationRequested);
            Assert.Equal(ProviderWorkPriority.Adjacent, priority());
            Assert.Equal(ProviderWorkPriority.Foreground, (await _scheduler.GetAsync(handle.JobId, TestContext.Current.CancellationToken))!.Priority);
            Assert.Equal(job.LeaseOwner, (await _scheduler.GetAsync(handle.JobId, TestContext.Current.CancellationToken))!.LeaseOwner);
            using var freshObservation = new CalendarObservationScope().Enter();
            await using var fresh = _scheduler.WatchAsync(handle, TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
            Assert.True(await fresh.MoveNextAsync());
            Assert.Equal(ProviderWorkPriority.Foreground, (await _scheduler.GetAsync(handle.JobId, TestContext.Current.CancellationToken))!.Priority);
            Assert.Equal(ProviderWorkPriority.Foreground, priority());
            Assert.False(execution.IsCancellationRequested);
        }
        finally { _scheduler.EndExecution(job, execution); }
    }

    [Fact]
    public async Task PassiveObserverDoesNotReleaseAnotherBrowsersForegroundInterest()
    {
        var handle = await _scheduler.EnqueueAsync(new(ProviderWorkKind.CalendarForeground, "shared-passive", ProviderWorkPriority.Foreground, "{}"), TestContext.Current.CancellationToken);
        await using var active = _scheduler.WatchAsync(handle, TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await active.MoveNextAsync());
        var observation = new CalendarObservationScope();
        using var ambient = observation.Enter();
        await using var passive = _scheduler.WatchAsync(handle, TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await passive.MoveNextAsync());
        var job = (await _scheduler.ClaimNextAsync(false, "worker", TestContext.Current.CancellationToken))!;
        var execution = _scheduler.BeginExecution(job, TestContext.Current.CancellationToken);
        using var providerContext = _scheduler.UseProviderExecutionContext(job);
        var priority = ProviderExecutionContext.CapturePriorityAccessor();
        try
        {
            await observation.ReleaseForegroundAsync(TestContext.Current.CancellationToken);
            Assert.Equal(ProviderWorkPriority.Foreground, (await _scheduler.GetAsync(handle.JobId, TestContext.Current.CancellationToken))!.Priority);
            Assert.Equal(ProviderWorkPriority.Foreground, priority());
            Assert.False(execution.IsCancellationRequested);
            await active.DisposeAsync();
            Assert.Equal(ProviderWorkPriority.Adjacent, priority());
            Assert.Equal(ProviderWorkPriority.Foreground, (await _scheduler.GetAsync(handle.JobId, TestContext.Current.CancellationToken))!.Priority);
            Assert.Equal(job.LeaseOwner, (await _scheduler.GetAsync(handle.JobId, TestContext.Current.CancellationToken))!.LeaseOwner);
            Assert.False(execution.IsCancellationRequested);
        }
        finally { _scheduler.EndExecution(job, execution); }
    }

    [Fact]
    public async Task AlreadyPassiveObservationNeverReacquiresForegroundWhenWatcherStarts()
    {
        var observation = new CalendarObservationScope();
        await observation.ReleaseForegroundAsync(TestContext.Current.CancellationToken);
        using var ambient = observation.Enter();
        var handle = await _scheduler.EnqueueAsync(new(ProviderWorkKind.CalendarForeground, "already-passive", ProviderWorkPriority.Foreground, "{}"), TestContext.Current.CancellationToken);
        await using var watcher = _scheduler.WatchAsync(handle, TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await watcher.MoveNextAsync());
        Assert.Equal(ProviderWorkPriority.Adjacent, (await _scheduler.GetAsync(handle.JobId, TestContext.Current.CancellationToken))!.Priority);
        Assert.Null(await _scheduler.ClaimNextAsync(true, "foreground", TestContext.Current.CancellationToken));
    }
}
