using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class ProviderWorkSchedulerTests
{
    [Fact]
    public async Task AbandonedRunningForegroundReleasesWorkerAndResumesItsCheckpointInBackground()
    {
        var abandonedStart = new DateOnly(2026, 10, 5);
        var keptStart = abandonedStart.AddDays(7);
        var visibleStart = abandonedStart.AddDays(14);
        var pipeline = new ForegroundHandoffPipeline(abandonedStart);
        var abandoned = await EnqueueCalendarAsync("abandoned-running", abandonedStart);
        var kept = await EnqueueCalendarAsync("still-visible", keptStart);
        using var abandonedCancellation = new CancellationTokenSource();
        await using var abandonedWatcher = _scheduler.WatchAsync(abandoned, abandonedCancellation.Token).GetAsyncEnumerator();
        await using var keptWatcher = _scheduler.WatchAsync(kept).GetAsyncEnumerator();
        Assert.True(await abandonedWatcher.MoveNextAsync());
        Assert.True(await keptWatcher.MoveNextAsync());
        await using var services = new ServiceCollection().AddSingleton<IPremiereLoadPipeline>(pipeline).BuildServiceProvider();
        var recovery = new DatabaseRecoveryState();
        recovery.Set(new(DatabaseSchema.CurrentVersion, DatabaseSchema.CurrentVersion, true, "ready", null, null));
        using var worker = new ProviderWorkSchedulerHostedService(_scheduler, services.GetRequiredService<IServiceScopeFactory>(),
            recovery, Microsoft.Extensions.Options.Options.Create(new ProviderSchedulerOptions { LeaseSeconds = 30 }),
            NullLogger<ProviderWorkSchedulerHostedService>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await Task.WhenAll(pipeline.Entered(abandonedStart), pipeline.Entered(keptStart)).WaitAsync(TimeSpan.FromSeconds(5));
            var visible = await EnqueueCalendarAsync("new-visible", visibleStart);
            await using var visibleWatcher = _scheduler.WatchAsync(visible).GetAsyncEnumerator();
            Assert.True(await visibleWatcher.MoveNextAsync());

            abandonedCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                while (await abandonedWatcher.MoveNextAsync()) { }
            });
            await pipeline.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await pipeline.Entered(visibleStart).WaitAsync(TimeSpan.FromSeconds(5));

            var checkpointed = (await _scheduler.GetAsync(abandoned.JobId))!;
            Assert.Contains(checkpointed.State, new[] { ProviderWorkState.Queued, ProviderWorkState.Running });
            Assert.Equal(ProviderWorkPriority.Adjacent, checkpointed.Priority);
            Assert.Equal(0, checkpointed.AttemptCount);
            Assert.Contains("completed-source", ProviderWorkScheduler.TryReadCheckpoint(checkpointed.CheckpointJson)!.CompletedSources.Keys);
            Assert.Equal(ProviderWorkState.Running, (await _scheduler.GetAsync(kept.JobId))!.State);
            Assert.Equal(0, pipeline.CancellationCount(keptStart));

            pipeline.Release(visibleStart);
            pipeline.Release(keptStart);
            await WaitUntilAsync(async () => (await _scheduler.GetAsync(visible.JobId))!.State == ProviderWorkState.Completed
                && (await _scheduler.GetAsync(kept.JobId))!.State == ProviderWorkState.Completed);
            await pipeline.Resumed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Contains("completed-source", pipeline.AbandonedResume!.CompletedSources.Keys);
            Assert.Equal(1, pipeline.SourceFetchCount(abandonedStart));
            pipeline.Release(abandonedStart);
            await WaitUntilAsync(async () => (await _scheduler.GetAsync(abandoned.JobId))!.State == ProviderWorkState.Completed);
            Assert.Equal(0, (await _scheduler.GetAsync(abandoned.JobId))!.AttemptCount);
        }
        finally { await worker.StopAsync(CancellationToken.None); }
    }

    [Fact]
    public async Task RunningForegroundRemainsActiveUntilItsLastWatcherDetaches()
    {
        var handle = await _scheduler.EnqueueAsync(new(ProviderWorkKind.CalendarForeground, "shared-watchers", ProviderWorkPriority.Foreground, "{}"));
        await using var first = _scheduler.WatchAsync(handle).GetAsyncEnumerator();
        await using var second = _scheduler.WatchAsync(handle).GetAsyncEnumerator();
        Assert.True(await first.MoveNextAsync());
        Assert.True(await second.MoveNextAsync());
        var job = (await _scheduler.ClaimNextAsync(false, "shared-worker", CancellationToken.None))!;
        var execution = _scheduler.BeginExecution(job, CancellationToken.None);
        try
        {
            await first.DisposeAsync();
            Assert.False(execution.IsCancellationRequested);
            Assert.Equal(ProviderWorkPriority.Foreground, (await _scheduler.GetAsync(job.JobId))!.Priority);
            await second.DisposeAsync();
            Assert.True(execution.IsCancellationRequested);
            await _scheduler.PreemptAsync(job, CancellationToken.None);
            Assert.Equal(ProviderWorkPriority.Adjacent, (await _scheduler.GetAsync(job.JobId))!.Priority);
            Assert.Equal(0, (await _scheduler.GetAsync(job.JobId))!.AttemptCount);
        }
        finally { _scheduler.EndExecution(job, execution); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WatcherDetachBetweenClaimAndExecutionPreservesReattachedDemand(bool reattach)
    {
        var handle = await _scheduler.EnqueueAsync(new(ProviderWorkKind.CalendarForeground, "claim-detach", ProviderWorkPriority.Foreground, "{}"));
        await using var watcher = _scheduler.WatchAsync(handle).GetAsyncEnumerator();
        Assert.True(await watcher.MoveNextAsync());
        var job = (await _scheduler.ClaimNextAsync(false, "claim-worker", CancellationToken.None))!;
        await watcher.DisposeAsync();
        await using var replacement = reattach ? _scheduler.WatchAsync(handle).GetAsyncEnumerator() : null;
        if (replacement is not null) Assert.True(await replacement.MoveNextAsync());
        var execution = _scheduler.BeginExecution(job, CancellationToken.None);
        try
        {
            Assert.Equal(!reattach, execution.IsCancellationRequested);
            if (!reattach) await _scheduler.PreemptAsync(job, CancellationToken.None);
            Assert.Equal(reattach ? ProviderWorkPriority.Foreground : ProviderWorkPriority.Adjacent,
                (await _scheduler.GetAsync(job.JobId))!.Priority);
        }
        finally { _scheduler.EndExecution(job, execution); }
    }

    [Fact]
    public async Task ForegroundExecutionCanStartBeforeItsFirstWatcherWithoutBeingCancelled()
    {
        var handle = await _scheduler.EnqueueAsync(new(ProviderWorkKind.CalendarForeground, "initial-watch-race", ProviderWorkPriority.Foreground, "{}"));
        var job = (await _scheduler.ClaimNextAsync(false, "early-worker", CancellationToken.None))!;
        var execution = _scheduler.BeginExecution(job, CancellationToken.None);
        try
        {
            Assert.False(execution.IsCancellationRequested);
            await using var watcher = _scheduler.WatchAsync(handle).GetAsyncEnumerator();
            Assert.True(await watcher.MoveNextAsync());
            Assert.False(execution.IsCancellationRequested);
        }
        finally { _scheduler.EndExecution(job, execution); }
    }

    [Fact]
    public async Task PreviouslyWatchedQueuedWorkRunsInBackgroundWithoutBeingCancelledAgain()
    {
        var handle = await _scheduler.EnqueueAsync(new(ProviderWorkKind.CalendarForeground, "queued-background", ProviderWorkPriority.Foreground, "{}"));
        await using (var watcher = _scheduler.WatchAsync(handle).GetAsyncEnumerator())
            Assert.True(await watcher.MoveNextAsync());
        var job = (await _scheduler.ClaimNextAsync(false, "background-worker", CancellationToken.None))!;
        Assert.Equal(ProviderWorkPriority.Adjacent, job.Priority);
        var execution = _scheduler.BeginExecution(job, CancellationToken.None);
        try { Assert.False(execution.IsCancellationRequested); }
        finally { _scheduler.EndExecution(job, execution); }
    }

    private Task<ProviderWorkHandle> EnqueueCalendarAsync(string key, DateOnly start)
        => _scheduler.EnqueueAsync(new(ProviderWorkKind.CalendarForeground, key, ProviderWorkPriority.Foreground,
            JsonSerializer.Serialize(new CalendarProviderWorkPayload(start, start.AddDays(6), false, null))));

    private sealed class ForegroundHandoffPipeline(DateOnly abandonedStart) : IPremiereLoadPipeline
    {
        private readonly ConcurrentDictionary<DateOnly, TaskCompletionSource> _entered = new();
        private readonly ConcurrentDictionary<DateOnly, TaskCompletionSource> _released = new();
        private readonly ConcurrentDictionary<DateOnly, int> _fetches = new();
        private readonly ConcurrentDictionary<DateOnly, int> _cancellations = new();
        private readonly ConcurrentDictionary<DateOnly, Func<ProviderWorkPriority>> _priorities = new();
        public TaskCompletionSource Cancelled { get; } = NewSignal();
        public TaskCompletionSource Resumed { get; } = NewSignal();
        public ProviderWorkResumeState? AbandonedResume { get; private set; }
        public Task Entered(DateOnly start) => _entered.GetOrAdd(start, _ => NewSignal()).Task;
        public void Release(DateOnly start) => _released.GetOrAdd(start, _ => NewSignal()).TrySetResult();
        public int SourceFetchCount(DateOnly start) => _fetches.GetValueOrDefault(start);
        public int CancellationCount(DateOnly start) => _cancellations.GetValueOrDefault(start);
        public ProviderWorkPriority CurrentPriority(DateOnly start) => _priorities[start]();
        private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<PremiereLoadProgress> StreamCoreAsync(DateOnly start, DateOnly end, bool forceRefresh,
            CalendarFilters? filters, ProviderWorkResumeState? resumeState,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            _priorities[start] = ProviderExecutionContext.CapturePriorityAccessor();
            if (resumeState?.CompletedSources.ContainsKey("completed-source") != true)
            {
                _fetches.AddOrUpdate(start, 1, (_, count) => count + 1);
                yield return new PremiereLoadProgress("Completed source", 1, 2, [Item(start.Day)])
                {
                    Phase = "complete", CheckpointKey = "completed-source", SourceItems = [Item(start.Day)]
                };
            }
            else if (start == abandonedStart)
            {
                AbandonedResume = resumeState;
                Resumed.TrySetResult();
            }
            _entered.GetOrAdd(start, _ => NewSignal()).TrySetResult();
            try { await _released.GetOrAdd(start, _ => NewSignal()).Task.WaitAsync(cancellationToken); }
            catch (OperationCanceledException)
            {
                _cancellations.AddOrUpdate(start, 1, (_, count) => count + 1);
                if (start == abandonedStart) Cancelled.TrySetResult();
                throw;
            }
            yield return new PremiereLoadProgress("Complete", 2, 2, [Item(start.Day)], IsFinal: true);
        }
    }
}
