using System.Text.Json;
using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class ProviderWorkSchedulerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonfinalReadinessIsLiveOnlyWhenCheckpointIsSavedAndWorkIsRequeued(bool retry)
    {
        var job = await ClaimCalendarAsync("ready-checkpoint");
        await _scheduler.PublishAsync(job, Progress(job, "initial"), CancellationToken.None);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var watcher = _scheduler.WatchAsync(new(job.JobId, false), budget.Token).GetAsyncEnumerator();
        Assert.True(await watcher.MoveNextAsync());
        var ready = new PremiereLoadProgress("Details", 1, 1, [Item(1)])
        {
            IsCalendarReady = true,
            IsEnriching = true
        };
        await _scheduler.PublishAsync(job, Progress(job, "details updating", ready), CancellationToken.None);
        Assert.True(await watcher.MoveNextAsync());
        Assert.True(watcher.Current.CalendarProgress!.IsCalendarReady);
        Assert.True(watcher.Current.CalendarProgress.IsEnriching);

        if (retry)
            await _scheduler.FailAsync(job, new HttpRequestException("temporary failure"), CancellationToken.None);
        else
            await _scheduler.PreemptAsync(job, CancellationToken.None);

        var saved = (await _scheduler.GetAsync(job.JobId))!;
        Assert.Equal(retry ? ProviderWorkState.RetryWaiting : ProviderWorkState.Queued, saved.State);
        var calendar = ProviderWorkScheduler.TryReadCheckpoint(saved.CheckpointJson)!.CalendarProgress!;
        Assert.False(calendar.IsFinal);
        Assert.False(calendar.IsCalendarReady);
        Assert.False(calendar.IsEnriching);
        Assert.Equal("Movie 1", Assert.Single(calendar.Items).Title);
    }

    [Theory]
    [InlineData(ProviderWorkState.Queued, false)]
    [InlineData(ProviderWorkState.Queued, true)]
    [InlineData(ProviderWorkState.RetryWaiting, false)]
    [InlineData(ProviderWorkState.RetryWaiting, true)]
    [InlineData(ProviderWorkState.Running, false)]
    [InlineData(ProviderWorkState.Running, true)]
    public async Task SavedNonfinalReadinessIsClearedForDurableAndRetainedBroadcastReplay(
        ProviderWorkState state, bool retainedLive)
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        var job = await ClaimCalendarAsync("legacy-ready-replay");
        var ready = new PremiereLoadProgress("Details", 1, 1, [Item(1)])
        {
            IsCalendarReady = true,
            IsEnriching = true
        };
        // Simulate a checkpoint written before readiness became a live-only assertion.
        var saved = job with
        {
            State = state,
            NextAttemptUtc = state == ProviderWorkState.RetryWaiting ? clock.GetUtcNow().AddMinutes(5) : null,
            CheckpointJson = JsonSerializer.Serialize(new ProviderWorkCheckpoint(
                "details updating", 1, 2, ready,
                new Dictionary<string, IReadOnlyList<PremiereItem>>()))
        };
        var broadcast = new ProviderWorkScheduler.JobBroadcast(clock);
        var reader = broadcast.Subscribe();
        if (retainedLive)
            broadcast.Publish(Progress(job, "details updating", ready));

        broadcast.Seed(saved);
        Assert.True(reader.TryRead(out var replay));
        AssertProvisionalReadyReplay(replay!);
        while (reader.TryRead(out replay)) AssertProvisionalReadyReplay(replay!);

        // A later subscriber reuses the normalized retained snapshot too.
        var lateReader = broadcast.Subscribe();
        Assert.True(lateReader.TryRead(out replay));
        AssertProvisionalReadyReplay(replay!);
        while (lateReader.TryRead(out replay)) AssertProvisionalReadyReplay(replay!);

        static void AssertProvisionalReadyReplay(ProviderWorkProgress progress)
        {
            var calendar = Assert.IsType<PremiereLoadProgress>(progress.CalendarProgress);
            Assert.False(calendar.IsFinal);
            Assert.False(calendar.IsCalendarReady);
            Assert.False(calendar.IsEnriching);
            Assert.Equal("Movie 1", Assert.Single(calendar.Items).Title);
        }
    }
}
