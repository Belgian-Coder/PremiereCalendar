using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class ProviderWorkSchedulerTests
{
    [Fact]
    public async Task WorkerReplayAndPreemptionDoNotRenewSuccessfulSourceAge()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        _scheduler = NewScheduler(clock, cacheHours: 1);
        var job = await ClaimCalendarAsync("source-age-replay");
        await PublishSourceAsync(job, "successful", 1);
        var original = ProviderWorkScheduler.TryReadCheckpoint((await _scheduler.GetAsync(job.JobId))!.CheckpointJson)!.SourceCompletedUtc["successful"];
        await _scheduler.PreemptAsync(job, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(55));
        var pipeline = new BlockingResumePipeline();
        await using var services = new ServiceCollection().AddSingleton<IPremiereLoadPipeline>(pipeline).BuildServiceProvider();
        var recovery = new DatabaseRecoveryState();
        recovery.Set(new(DatabaseSchema.CurrentVersion, DatabaseSchema.CurrentVersion, true, "ready", null, null));
        using var worker = new ProviderWorkSchedulerHostedService(_scheduler, services.GetRequiredService<IServiceScopeFactory>(),
            recovery, Microsoft.Extensions.Options.Options.Create(new ProviderSchedulerOptions { LeaseSeconds = 30 }),
            NullLogger<ProviderWorkSchedulerHostedService>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try { await pipeline.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { await worker.StopAsync(CancellationToken.None); }
        Assert.Contains("successful", pipeline.Resume!.CompletedSources.Keys);
        var persisted = (await _scheduler.GetAsync(job.JobId))!;
        Assert.Equal(ProviderWorkState.Queued, persisted.State);
        Assert.Equal(original, ProviderWorkScheduler.TryReadCheckpoint(persisted.CheckpointJson)!.SourceCompletedUtc["successful"]);
        clock.Advance(TimeSpan.FromMinutes(6));
        var third = (await _scheduler.ClaimNextAsync(false, "third", CancellationToken.None))!;
        Assert.Equal(job.JobId, third.JobId);
        Assert.Empty(_scheduler.ReadResumeCheckpoint(third)!.CompletedSources);
    }

    [Theory]
    [InlineData("preempt", false)]
    [InlineData("preempt", true)]
    [InlineData("retry", false)]
    [InlineData("retry", true)]
    [InlineData("defer", false)]
    [InlineData("defer", true)]
    public async Task RequeuedForegroundPriorityReflectsCurrentWatcherDemand(string transition, bool keepWatcher)
    {
        var handle = await _scheduler.EnqueueAsync(new(ProviderWorkKind.CalendarForeground, "queued-demand", ProviderWorkPriority.Foreground, "{}"));
        await using var watcher = _scheduler.WatchAsync(handle).GetAsyncEnumerator();
        Assert.True(await watcher.MoveNextAsync());
        var job = (await _scheduler.ClaimNextAsync(false, "owner", CancellationToken.None))!;
        if (!keepWatcher) await watcher.DisposeAsync();
        Assert.Equal(ProviderWorkPriority.Foreground, (await _scheduler.GetAsync(job.JobId))!.Priority);
        switch (transition)
        {
            case "preempt": await _scheduler.PreemptAsync(job, CancellationToken.None); break;
            case "retry": await _scheduler.FailAsync(job, new HttpRequestException("failed"), CancellationToken.None); break;
            case "defer":
                await _scheduler.PublishAsync(job, Progress(job, "partial",
                    new PremiereLoadProgress("Complete", 0, 0, [], IsFinal: true)
                    { HasSourceErrors = true, RetryAfterUtc = DateTimeOffset.UtcNow.AddMinutes(5) }), CancellationToken.None);
                await _scheduler.CompleteAsync(job, CancellationToken.None);
                break;
        }
        var queued = (await _scheduler.GetAsync(job.JobId))!;
        Assert.Contains(queued.State, new[] { ProviderWorkState.Queued, ProviderWorkState.RetryWaiting });
        Assert.Equal(keepWatcher ? ProviderWorkPriority.Foreground : ProviderWorkPriority.Adjacent, queued.Priority);
        if (!keepWatcher)
        {
            await using var reattached = _scheduler.WatchAsync(handle).GetAsyncEnumerator();
            Assert.True(await reattached.MoveNextAsync());
            Assert.Equal(ProviderWorkPriority.Foreground, (await _scheduler.GetAsync(job.JobId))!.Priority);
        }
    }

    [Fact]
    public async Task TerminalBroadcastRaceReplaysDurableFinalBeforeTerminalNotification()
    {
        var job = await ClaimCalendarAsync("terminal-race");
        var final = new PremiereLoadProgress("Complete", 1, 1, [Item(9)], IsFinal: true);
        await _scheduler.PublishAsync(job, Progress(job, "final", final), CancellationToken.None);
        await _scheduler.CompleteAsync(job, CancellationToken.None);
        var snapshot = (await _scheduler.GetAsync(job.JobId))!;
        var broadcast = new ProviderWorkScheduler.JobBroadcast();
        var reader = broadcast.Subscribe();
        broadcast.Publish(new(job.JobId, ProviderWorkState.Completed, "completed"));
        broadcast.Seed(snapshot);
        var updates = new List<ProviderWorkProgress>();
        await foreach (var update in reader.ReadAllAsync()) updates.Add(update);
        Assert.Collection(updates,
            update => Assert.Equal("Movie 9", Assert.Single(update.CalendarProgress!.Items).Title),
            update => Assert.Equal(ProviderWorkState.Completed, update.State));
        Assert.True(updates[0].CalendarProgress!.IsFinal);
    }

    [Fact]
    public async Task WatchCleanupDatabaseFailureDoesNotReplaceDeliveredCompletion()
    {
        var job = await ClaimCalendarAsync("cleanup-failure");
        await _scheduler.PublishAsync(job, Progress(job, "final",
            new PremiereLoadProgress("Complete", 1, 1, [Item(9)], IsFinal: true)), CancellationToken.None);
        await _scheduler.CompleteAsync(job, CancellationToken.None);
        await using var watcher = _scheduler.WatchAsync(new(job.JobId, false)).GetAsyncEnumerator();
        Assert.True(await watcher.MoveNextAsync());
        Assert.True(watcher.Current.CalendarProgress!.IsFinal);
        Assert.True(await watcher.MoveNextAsync());
        Assert.Equal(ProviderWorkState.Completed, watcher.Current.State);
        await ExecuteSqlAsync("DROP TABLE ProviderWorkLeases; DROP TABLE ProviderWorkJobs", job.JobId);
        Assert.False(await watcher.MoveNextAsync());
        Assert.Equal(0, _scheduler.BroadcastCount);
    }

    [Theory]
    [InlineData(5, true)]
    [InlineData(61, false)]
    public async Task ResumedSourcesRespectTheirCompletionAgeAndConfiguredCacheLifetime(int delayMinutes, bool retained)
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        _scheduler = NewScheduler(clock, cacheHours: 1);
        var job = await ClaimCalendarAsync("aged-source");
        await PublishSourceAsync(job, "successful", 1);
        await _scheduler.PreemptAsync(job, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(delayMinutes));
        var restarted = NewScheduler(clock, cacheHours: 1);
        var resumed = (await restarted.ClaimNextAsync(false, "restart", CancellationToken.None))!;
        var checkpoint = restarted.ReadResumeCheckpoint(resumed)!;
        Assert.Equal(retained, checkpoint.CompletedSources.ContainsKey("successful"));
        Assert.Equal(retained, checkpoint.SourceCompletedUtc.ContainsKey("successful"));
        Assert.Equal("Movie 1", Assert.Single(checkpoint.CalendarProgress!.Items).Title);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void TimestampFreeSourceMapsRemainDisplayableButAreNotResumable(int version)
    {
        var json = JsonSerializer.Serialize(new ProviderWorkCheckpoint("saved", 1, 1,
            new PremiereLoadProgress("Complete", 1, 1, [Item(1)], IsFinal: true),
            new Dictionary<string, IReadOnlyList<PremiereItem>> { ["undated"] = [Item(1)] }) { Version = version });
        var checkpoint = ProviderWorkScheduler.TryReadCheckpoint(json)!;
        Assert.Empty(checkpoint.CompletedSources);
        Assert.Equal("Movie 1", Assert.Single(checkpoint.CalendarProgress!.Items).Title);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeferredFinalIsProvisionalWhenAClaimResumesEvenIfAlreadyRetained(bool retainedLive)
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        _scheduler = NewScheduler(clock);
        var job = await ClaimCalendarAsync("deferred-replay");
        var calendar = new PremiereLoadProgress("Complete", 1, 1, [Item(1)], IsFinal: true)
        {
            HasSourceErrors = true, FailedSourceNames = ["throttled"], RetryAfterUtc = clock.GetUtcNow().AddMinutes(5)
        };
        var final = Progress(job, "partial", calendar);
        await _scheduler.PublishAsync(job, final, CancellationToken.None);
        await _scheduler.CompleteAsync(job, CancellationToken.None);
        var waiting = (await _scheduler.GetAsync(job.JobId))!;
        var future = new ProviderWorkScheduler.JobBroadcast(clock);
        var futureReader = future.Subscribe();
        future.Seed(waiting);
        Assert.True(futureReader.TryRead(out var partial));
        Assert.True(partial!.CalendarProgress!.IsFinal);

        clock.Advance(TimeSpan.FromMinutes(5));
        var resumed = (await _scheduler.ClaimNextAsync(false, "resumed", CancellationToken.None))!;
        var broadcast = new ProviderWorkScheduler.JobBroadcast(clock);
        var reader = broadcast.Subscribe();
        if (retainedLive) broadcast.Publish(final);
        broadcast.Seed(resumed);
        Assert.True(reader.TryRead(out var replay));
        Assert.False(replay!.CalendarProgress!.IsFinal);
        Assert.Equal("cache", replay.CalendarProgress.Phase);
        Assert.True(replay.CalendarProgress.HasSourceErrors);
        Assert.True(replay.CalendarProgress.IsStaleCache);
        Assert.Equal("Movie 1", Assert.Single(replay.CalendarProgress.Items).Title);
    }

    [Fact]
    public async Task ResumingWorkerSkipsOldDeferredFinalBeforePublishingNewResults()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        _scheduler = NewScheduler(clock);
        var job = await ClaimCalendarAsync("resumed-final");
        await PublishSourceAsync(job, "successful", 1);
        await _scheduler.PublishAsync(job, Progress(job, "old final",
            new PremiereLoadProgress("Complete", 1, 1, [Item(1)], IsFinal: true)
            { HasSourceErrors = true, RetryAfterUtc = clock.GetUtcNow().AddMinutes(5) }), CancellationToken.None);
        await _scheduler.CompleteAsync(job, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(5));
        var pipeline = new ResumeCapturePipeline();
        await using var services = new ServiceCollection().AddSingleton<IPremiereLoadPipeline>(pipeline).BuildServiceProvider();
        var recovery = new DatabaseRecoveryState();
        recovery.Set(new(DatabaseSchema.CurrentVersion, DatabaseSchema.CurrentVersion, true, "ready", null, null));
        using var worker = new ProviderWorkSchedulerHostedService(_scheduler, services.GetRequiredService<IServiceScopeFactory>(),
            recovery, Microsoft.Extensions.Options.Options.Create(new ProviderSchedulerOptions { LeaseSeconds = 30 }),
            NullLogger<ProviderWorkSchedulerHostedService>.Instance);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var updates = new List<ProviderWorkProgress>();
        await using var watcher = _scheduler.WatchAsync(new(job.JobId, false), timeout.Token).GetAsyncEnumerator();
        Assert.True(await watcher.MoveNextAsync());
        updates.Add(watcher.Current);
        await worker.StartAsync(timeout.Token);
        try
        {
            while (await watcher.MoveNextAsync()) updates.Add(watcher.Current);
        }
        finally { await worker.StopAsync(timeout.Token); }
        Assert.Equal("Movie 2", Assert.Single(Assert.Single(updates, update => update.CalendarProgress?.IsFinal == true).CalendarProgress!.Items).Title);
        Assert.Contains("successful", pipeline.Resume!.CompletedSources.Keys);
        Assert.NotNull(pipeline.Resume.PublishCacheAsync);
    }

    [Fact]
    public async Task StaleClaimCannotPublishCacheAndLosingOwnershipCancelsExecution()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        _scheduler = NewScheduler(clock);
        var stale = await ClaimCalendarAsync("cache-fenced");
        var execution = _scheduler.BeginExecution(stale, CancellationToken.None);
        try
        {
            clock.Advance(TimeSpan.FromSeconds(31));
            await _scheduler.RecoverAsync(CancellationToken.None);
            var replacement = (await _scheduler.ClaimNextAsync(false, "replacement", CancellationToken.None))!;
            var cachePath = Path.Combine(_root, "calendar-cache.json");
            await _scheduler.PublishOwnedCacheAsync(replacement,
                token => File.WriteAllTextAsync(cachePath, "new owner", token), CancellationToken.None);
            var staleCallbackInvoked = false;
            await Assert.ThrowsAsync<ProviderWorkLeaseLostException>(() => _scheduler.PublishOwnedCacheAsync(stale,
                async token =>
                {
                    staleCallbackInvoked = true;
                    await File.WriteAllTextAsync(cachePath, "stale owner", token);
                }, CancellationToken.None));
            Assert.False(staleCallbackInvoked);
            Assert.True(execution.IsCancellationRequested);
            Assert.Equal("new owner", await File.ReadAllTextAsync(cachePath));
            Assert.Equal(replacement.LeaseOwner, (await _scheduler.GetAsync(stale.JobId))!.LeaseOwner);
        }
        finally { _scheduler.EndExecution(stale, execution); }
    }

    [Fact]
    public async Task OwnedCachePublicationHoldsWriterReservationUntilFilesArePublished()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        _scheduler = NewScheduler(clock);
        var original = await ClaimCalendarAsync("cache-publication-lock");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recoveryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cachePath = Path.Combine(_root, "calendar-cache.json");
        var publication = _scheduler.PublishOwnedCacheAsync(original, async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            await File.WriteAllTextAsync(cachePath, "original owner", token);
        }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromSeconds(31));
        var recovery = Task.Run(async () =>
        {
            recoveryStarted.TrySetResult();
            await _scheduler.RecoverAsync(CancellationToken.None);
        });
        try
        {
            await recoveryStarted.Task;
            Assert.NotSame(recovery, await Task.WhenAny(recovery, Task.Delay(100)));
            Assert.False(File.Exists(cachePath));
        }
        finally { release.TrySetResult(); }
        await publication.WaitAsync(TimeSpan.FromSeconds(5));
        await recovery.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("original owner", await File.ReadAllTextAsync(cachePath));
        var replacement = (await _scheduler.ClaimNextAsync(false, "replacement", CancellationToken.None))!;
        await _scheduler.PublishOwnedCacheAsync(replacement,
            token => File.WriteAllTextAsync(cachePath, "replacement owner", token), CancellationToken.None);
        Assert.Equal("replacement owner", await File.ReadAllTextAsync(cachePath));
        Assert.NotEqual(original.LeaseOwner, replacement.LeaseOwner);
    }

    [Fact]
    public async Task CancelledCachePublicationRollsBackLeaseRenewalAndReleasesReservation()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        _scheduler = NewScheduler(clock);
        var job = await ClaimCalendarAsync("cache-publication-cancel");
        var before = await ScalarAsync("SELECT LeaseExpiresUtc FROM ProviderWorkLeases WHERE JobId = $id", job.JobId);
        clock.Advance(TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publication = _scheduler.PublishOwnedCacheAsync(job, async token =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        }, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => publication);
        Assert.Equal(before, await ScalarAsync("SELECT LeaseExpiresUtc FROM ProviderWorkLeases WHERE JobId = $id", job.JobId));
        Assert.Equal(before, await ScalarAsync("SELECT LeaseExpiresUtc FROM ProviderWorkJobs WHERE JobId = $id", job.JobId));
        Assert.Equal(ProviderWorkState.Running, (await _scheduler.GetAsync(job.JobId))!.State);
        await _scheduler.PublishOwnedCacheAsync(job, _ => Task.CompletedTask, CancellationToken.None);
        Assert.NotEqual(before, await ScalarAsync("SELECT LeaseExpiresUtc FROM ProviderWorkLeases WHERE JobId = $id", job.JobId));
    }

    [Fact]
    public async Task CheckpointsAccumulateSuccessfulSourcesAndExcludeFailuresAcrossRestart()
    {
        var job = await ClaimCalendarAsync("cumulative");
        await PublishSourceAsync(job, "first", 1);
        await PublishSourceAsync(job, "second", 2);
        await PublishSourceAsync(job, "failed", 3, failed: true);
        await _scheduler.PublishAsync(job, Progress(job, "final", new PremiereLoadProgress("Complete", 2, 2,
            [Item(1), Item(2)], IsFinal: true) { HasSourceErrors = true, FailedSourceNames = ["failed"] }), CancellationToken.None);
        await _scheduler.PreemptAsync(job, CancellationToken.None);

        // A new scheduler can reconstruct both successes solely from durable JSON.
        var restarted = NewScheduler(TimeProvider.System);
        var resumed = await restarted.ClaimNextAsync(false, "restarted", CancellationToken.None);
        var checkpoint = ProviderWorkScheduler.TryReadCheckpoint(resumed!.CheckpointJson)!;
        Assert.Equal(3, checkpoint.Version);
        Assert.Equal(["first", "second"], checkpoint.CompletedSources.Keys.Order().ToArray());
        Assert.Equal("Movie 1", Assert.Single(checkpoint.CompletedSources["first"]).Title);
        Assert.Equal("Movie 2", Assert.Single(checkpoint.CompletedSources["second"]).Title);
        Assert.Equal(3, checkpoint.SourceSummaries.Count);
        Assert.True(checkpoint.CalendarProgress!.IsFinal);
        await restarted.CompleteAsync(resumed, CancellationToken.None);
        var replay = new List<ProviderWorkProgress>();
        await foreach (var update in restarted.WatchAsync(new ProviderWorkHandle(job.JobId, false))) replay.Add(update);
        Assert.Equal(3, replay.Count(update => update.CalendarProgress is { IsFinal: false, Phase: "complete" }));
        Assert.Contains(replay, update => update.CalendarProgress is { IsFinal: true });
        Assert.Equal(ProviderWorkState.Completed, replay[^1].State);
        Assert.Equal(0, restarted.BroadcastCount);
    }

    [Fact]
    public void LegacyCheckpointKeepsDisplayButRerunsUncertainCompletedSources()
    {
        var legacy = JsonSerializer.Serialize(new
        {
            Message = "legacy",
            CompletedWork = 1,
            TotalWork = 2,
            CalendarProgress = new PremiereLoadProgress("legacy source", 1, 1, [Item(1)]),
            CompletedSources = new Dictionary<string, IReadOnlyList<PremiereItem>> { ["uncertain"] = [Item(1)] }
        });
        var checkpoint = ProviderWorkScheduler.TryReadCheckpoint(legacy)!;
        Assert.Empty(checkpoint.CompletedSources);
        Assert.Equal("Movie 1", Assert.Single(checkpoint.CalendarProgress!.Items).Title);
    }

    [Fact]
    public async Task IntermediateWritesAreCoalescedAndPreemptionFlushesLatestProgress()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        _scheduler = NewScheduler(clock);
        var job = await ClaimCalendarAsync("coalesced");
        await _scheduler.PublishAsync(job, Progress(job, "page 1"), CancellationToken.None);
        for (var index = 2; index <= 100; index++)
            await _scheduler.PublishAsync(job, Progress(job, $"page {index}"), CancellationToken.None);
        Assert.Equal("page 1", ProviderWorkScheduler.TryReadCheckpoint((await _scheduler.GetAsync(job.JobId))!.CheckpointJson)!.Message);

        clock.Advance(TimeSpan.FromSeconds(2));
        await _scheduler.PublishAsync(job, Progress(job, "page 101"), CancellationToken.None);
        Assert.Equal("page 101", ProviderWorkScheduler.TryReadCheckpoint((await _scheduler.GetAsync(job.JobId))!.CheckpointJson)!.Message);
        await _scheduler.PublishAsync(job, Progress(job, "page 102"), CancellationToken.None);
        await _scheduler.PreemptAsync(job, CancellationToken.None);
        Assert.Equal("page 102", ProviderWorkScheduler.TryReadCheckpoint((await _scheduler.GetAsync(job.JobId))!.CheckpointJson)!.Message);
    }

    [Fact]
    public async Task RestartRechecksAnInitiallyUnexpiredLeaseAndReclaimsItAfterExpiry()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        _scheduler = NewScheduler(clock);
        var original = await ClaimCalendarAsync("restart-lease");
        var restarted = NewScheduler(clock);
        await restarted.RecoverAsync(CancellationToken.None);
        Assert.Null(await restarted.ClaimNextAsync(false, "new-process", CancellationToken.None));
        clock.Advance(TimeSpan.FromSeconds(31));
        await restarted.RecoverAsync(CancellationToken.None);
        var reclaimed = await restarted.ClaimNextAsync(false, "new-process", CancellationToken.None);
        Assert.Equal(original.JobId, reclaimed!.JobId);
        Assert.NotEqual(original.LeaseOwner, reclaimed.LeaseOwner);
        Assert.Equal(0, reclaimed.AttemptCount);
    }

    [Fact]
    public async Task StaleClaimCannotChangeProgressLeaseOrTerminalStateOfReclaimedJob()
    {
        var start = DateTimeOffset.UtcNow;
        var clock = new MutableTimeProvider(start);
        _scheduler = NewScheduler(clock);
        var old = await ClaimCalendarAsync("fenced");
        clock.Advance(TimeSpan.FromSeconds(31));
        await _scheduler.RecoverAsync(CancellationToken.None);
        var current = (await _scheduler.ClaimNextAsync(false, "same-worker", CancellationToken.None))!;
        Assert.NotEqual(old.LeaseOwner, current.LeaseOwner);
        var now = clock.GetUtcNow();
        Assert.False(await _store.UpdateProgressAsync(old.JobId, old.LeaseOwner!, "stale", now, CancellationToken.None));
        Assert.False(await _store.RenewLeaseAsync(old.JobId, old.LeaseOwner!, now, now.AddMinutes(10), CancellationToken.None));
        Assert.False(await _store.RequeueAsync(old.JobId, old.LeaseOwner!, now, now, true, "stale", CancellationToken.None));
        Assert.False(await _store.FailAsync(old.JobId, old.LeaseOwner!, "stale", now, CancellationToken.None));
        Assert.False(await _store.CompleteAsync(old.JobId, old.LeaseOwner!, now, CancellationToken.None));
        await _scheduler.PublishAsync(current, Progress(current, "current"), CancellationToken.None);
        var persisted = (await _scheduler.GetAsync(old.JobId))!;
        Assert.Equal(ProviderWorkState.Running, persisted.State);
        Assert.Equal(current.LeaseOwner, persisted.LeaseOwner);
        Assert.Equal(0, persisted.AttemptCount);
        Assert.Equal("current", ProviderWorkScheduler.TryReadCheckpoint(persisted.CheckpointJson)!.Message);
        Assert.Equal(1L, await ScalarAsync("SELECT COUNT(*) FROM ProviderWorkLeases WHERE JobId = $id AND LeaseOwner = $owner", old.JobId, current.LeaseOwner));
    }

    [Fact]
    public async Task IndependentHeartbeatRenewsAnIdleWorkerAndCancelsItWhenOwnershipIsLost()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        _scheduler = NewScheduler(clock);
        var job = await ClaimCalendarAsync("heartbeat");
        var execution = _scheduler.BeginExecution(job, CancellationToken.None);
        var heartbeat = _scheduler.MaintainLeaseAsync(job, execution);
        try
        {
            clock.Advance(TimeSpan.FromSeconds(11));
            await WaitUntilAsync(async () =>
            {
                var expiration = await ScalarAsync("SELECT LeaseExpiresUtc FROM ProviderWorkLeases WHERE JobId = $id", job.JobId);
                return DateTimeOffset.Parse((string)expiration!, CultureInfo.InvariantCulture) > clock.GetUtcNow().AddSeconds(29);
            });
            clock.Advance(TimeSpan.FromSeconds(20));
            await _scheduler.RecoverAsync(CancellationToken.None);
            Assert.Equal(ProviderWorkState.Running, (await _scheduler.GetAsync(job.JobId))!.State);
            await ExecuteSqlAsync("UPDATE ProviderWorkJobs SET LeaseOwner = 'replacement' WHERE JobId = $id", job.JobId);
            clock.Advance(TimeSpan.FromSeconds(11));
            await heartbeat.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(execution.IsCancellationRequested);
        }
        finally
        {
            await execution.CancelAsync();
            await heartbeat;
            _scheduler.EndExecution(job, execution);
        }
    }

    [Fact]
    public async Task ProviderCooldownPersistsPartialFinalAndDefersWithoutCompletingOrConsumingRetry()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        _scheduler = NewScheduler(clock);
        var job = await ClaimCalendarAsync("deferred");
        await PublishSourceAsync(job, "successful", 1);
        var deadline = clock.GetUtcNow().AddMinutes(10);
        await _scheduler.PublishAsync(job, Progress(job, "partial", new PremiereLoadProgress("Complete", 1, 1, [Item(1)], IsFinal: true)
        {
            HasSourceErrors = true, FailedSourceNames = ["throttled"], RetryAfterUtc = deadline
        }), CancellationToken.None);
        await _scheduler.CompleteAsync(job, CancellationToken.None);
        var snapshot = (await _scheduler.GetAsync(job.JobId))!;
        Assert.Equal(ProviderWorkState.RetryWaiting, snapshot.State);
        Assert.Equal(deadline, snapshot.NextAttemptUtc);
        Assert.Null(snapshot.CompletedUtc);
        Assert.Equal(0, snapshot.AttemptCount);
        Assert.Contains("successful", ProviderWorkScheduler.TryReadCheckpoint(snapshot.CheckpointJson)!.CompletedSources.Keys);
        Assert.Null(await _scheduler.ClaimNextAsync(false, "early", CancellationToken.None));
        clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(job.JobId, (await _scheduler.ClaimNextAsync(false, "due", CancellationToken.None))!.JobId);
    }

    [Fact]
    public async Task NestedProviderCooldownFailureUsesItsDeadlineWithoutConsumingRetry()
    {
        var job = await ClaimCalendarAsync("deferred-failure");
        var deadline = DateTimeOffset.UtcNow.AddMinutes(10);
        await _scheduler.FailAsync(job, new AggregateException(new InvalidOperationException("source failed",
            new ProviderCircuitOpenException("tmdb", deadline))), CancellationToken.None);
        var snapshot = (await _scheduler.GetAsync(job.JobId))!;
        Assert.Equal(ProviderWorkState.RetryWaiting, snapshot.State);
        Assert.Equal(deadline, snapshot.NextAttemptUtc);
        Assert.Equal(0, snapshot.AttemptCount);
    }

    [Fact]
    public async Task LastWatcherDetachDemotesOnlyQueuedWorkAndReattachPromotesIt()
    {
        var handle = await _scheduler.EnqueueAsync(new(ProviderWorkKind.CalendarForeground, "watchers", ProviderWorkPriority.Foreground, "{}"));
        await using (var watcher = _scheduler.WatchAsync(handle).GetAsyncEnumerator())
        {
            Assert.True(await watcher.MoveNextAsync());
            Assert.Equal(ProviderWorkPriority.Foreground, (await _scheduler.GetAsync(handle.JobId))!.Priority);
        }
        Assert.Equal(ProviderWorkPriority.Adjacent, (await _scheduler.GetAsync(handle.JobId))!.Priority);
        Assert.Equal(0, _scheduler.BroadcastCount);
        await using (var watcher = _scheduler.WatchAsync(handle).GetAsyncEnumerator())
        {
            Assert.True(await watcher.MoveNextAsync());
            Assert.Equal(ProviderWorkPriority.Foreground, (await _scheduler.GetAsync(handle.JobId))!.Priority);
            var claimed = (await _scheduler.ClaimNextAsync(false, "worker", CancellationToken.None))!;
            Assert.Equal(handle.JobId, claimed.JobId);
        }
        Assert.Equal(ProviderWorkPriority.Foreground, (await _scheduler.GetAsync(handle.JobId))!.Priority);
        Assert.Equal(ProviderWorkState.Running, (await _scheduler.GetAsync(handle.JobId))!.State);
    }

    [Fact]
    public async Task SlowWatcherCoalescesPagesWhileAnotherWatcherReceivesUpdatesAndAllSummaries()
    {
        var broadcast = new ProviderWorkScheduler.JobBroadcast();
        var slow = broadcast.Subscribe();
        var fast = broadcast.Subscribe();
        for (var index = 0; index < 5_000; index++)
        {
            var update = new ProviderWorkProgress("broadcast", ProviderWorkState.Running, $"page {index}",
                CalendarProgress: new PremiereLoadProgress("Source A", index, index, []) { CheckpointKey = "a" });
            broadcast.Publish(update);
            Assert.True(fast.TryRead(out var received));
            Assert.Equal(update.Message, received!.Message);
        }
        foreach (var source in new[] { "a", "b", "failed" })
            broadcast.Publish(new("broadcast", ProviderWorkState.Running, source, CalendarProgress:
                new PremiereLoadProgress(source, 0, 0, []) { Phase = "complete", CheckpointKey = source, HasSourceErrors = source == "failed" }));
        broadcast.Publish(new("broadcast", ProviderWorkState.Running, "final", CalendarProgress:
            new PremiereLoadProgress("Complete", 1, 1, [Item(99)], IsFinal: true)));
        broadcast.Publish(new("broadcast", ProviderWorkState.Completed, "done"));
        var updates = new List<ProviderWorkProgress>();
        await foreach (var update in slow.ReadAllAsync()) updates.Add(update);
        Assert.InRange(updates.Count, 5, 6);
        Assert.Equal(["a", "b", "failed"], updates.Where(update => update.CalendarProgress is { IsFinal: false, Phase: "complete" }).Select(update => update.Message).ToArray());
        Assert.Equal("Movie 99", Assert.Single(updates.Single(update => update.CalendarProgress?.IsFinal == true).CalendarProgress!.Items).Title);
        Assert.Equal(ProviderWorkState.Completed, updates[^1].State);
    }

    [Fact]
    public async Task RepeatedCompletedJobsReleaseBroadcastsAndReplayLateSubscribers()
    {
        for (var index = 0; index < 100; index++)
        {
            var job = await ClaimCalendarAsync($"soak-{index}");
            var execution = _scheduler.BeginExecution(job, CancellationToken.None);
            try
            {
                await PublishSourceAsync(job, "source", index);
                await _scheduler.PublishAsync(job, Progress(job, "final",
                    new PremiereLoadProgress("Complete", 1, 1, [Item(index)], IsFinal: true)), CancellationToken.None);
                await _scheduler.CompleteAsync(job, CancellationToken.None);
            }
            finally { _scheduler.EndExecution(job, execution); }

            Assert.Equal(0, _scheduler.BroadcastCount);
            var replay = new List<ProviderWorkProgress>();
            await foreach (var update in _scheduler.WatchAsync(new ProviderWorkHandle(job.JobId, false))) replay.Add(update);
            Assert.Equal(ProviderWorkState.Completed, replay[^1].State);
            Assert.Contains(replay, update => update.CalendarProgress is { IsFinal: true });
            Assert.Equal(0, _scheduler.BroadcastCount);
        }
    }

    private ProviderWorkScheduler NewScheduler(TimeProvider clock, int cacheHours = 6) => new(_store, clock,
        Microsoft.Extensions.Options.Options.Create(new ProviderSchedulerOptions { LeaseSeconds = 30, MaximumAttempts = 3 }),
        cacheOptions: Microsoft.Extensions.Options.Options.Create(new CalendarCacheOptions { WeekCacheHours = cacheHours }));

    private sealed class ResumeCapturePipeline : IPremiereLoadPipeline
    {
        public ProviderWorkResumeState? Resume { get; private set; }
        public async IAsyncEnumerable<PremiereLoadProgress> StreamCoreAsync(DateOnly start, DateOnly end, bool forceRefresh,
            CalendarFilters? filters, ProviderWorkResumeState? resumeState,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Resume = resumeState;
            yield return new PremiereLoadProgress("Loading", 0, 0, []);
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            yield return new PremiereLoadProgress("Complete", 1, 1, [Item(2)], IsFinal: true);
        }
    }

    private sealed class BlockingResumePipeline : IPremiereLoadPipeline
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ProviderWorkResumeState? Resume { get; private set; }
        public async IAsyncEnumerable<PremiereLoadProgress> StreamCoreAsync(DateOnly start, DateOnly end, bool forceRefresh,
            CalendarFilters? filters, ProviderWorkResumeState? resumeState,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Resume = resumeState;
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield break;
        }
    }

    private async Task<ProviderWorkSnapshot> ClaimCalendarAsync(string key)
    {
        await _scheduler.EnqueueAsync(new(ProviderWorkKind.CalendarForeground, key, ProviderWorkPriority.Foreground, "{}"));
        return (await _scheduler.ClaimNextAsync(false, "worker", CancellationToken.None))!;
    }

    private Task PublishSourceAsync(ProviderWorkSnapshot job, string source, int id, bool failed = false)
        => _scheduler.PublishAsync(job, Progress(job, source, new PremiereLoadProgress(source, 1, 1, [Item(id)])
        {
            Phase = "complete", CheckpointKey = source, SourceItems = [Item(id)], HasSourceErrors = failed
        }), CancellationToken.None);

    private static ProviderWorkProgress Progress(ProviderWorkSnapshot job, string message, PremiereLoadProgress? calendar = null)
        => new(job.JobId, ProviderWorkState.Running, message, CalendarProgress: calendar);

    private static PremiereItem Item(int id) => new()
    {
        MediaType = PremiereMediaType.Movie, Type = PremiereItemType.MovieFirstRelease, TmdbId = id,
        Title = $"Movie {id}", PremiereDate = new DateOnly(2026, 10, 26)
    };

    private async Task<object?> ScalarAsync(string sql, string jobId, string? owner = null)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_root, "scheduler.db")}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", jobId);
        if (owner is not null) command.Parameters.AddWithValue("$owner", owner);
        return await command.ExecuteScalarAsync();
    }

    private async Task ExecuteSqlAsync(string sql, string jobId)
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_root, "scheduler.db")}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$id", jobId);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!await predicate()) await Task.Delay(10, timeout.Token);
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        public override DateTimeOffset GetUtcNow() { lock (_gate) return now; }
        public void Advance(TimeSpan interval)
        {
            lock (_gate)
            {
                now += interval;
                foreach (var timer in _timers.ToArray()) timer.Tick(now);
            }
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                var timer = new ManualTimer(this, callback, state, dueTime, period);
                _timers.Add(timer);
                return timer;
            }
        }
        private sealed class ManualTimer(MutableTimeProvider clock, TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) : ITimer
        {
            private DateTimeOffset? _due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.GetUtcNow() + dueTime;
            private TimeSpan _period = period;
            public void Tick(DateTimeOffset now)
            {
                if (_due is null || _due > now) return;
                _due = _period == Timeout.InfiniteTimeSpan ? null : now + _period;
                callback(state);
            }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                _due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.GetUtcNow() + dueTime;
                _period = period;
                return true;
            }
            public void Dispose() => _due = null;
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
