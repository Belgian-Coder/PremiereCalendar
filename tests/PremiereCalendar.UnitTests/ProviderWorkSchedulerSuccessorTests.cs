using System.Text.Json;
using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class ProviderWorkSchedulerTests
{
    [Theory]
    [InlineData(10, true)]
    [InlineData(61, false)]
    public async Task NewCalendarJobInheritsOnlyFreshHealthySourcesFromExactPreviousPartial(int elapsedMinutes, bool reusable)
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        _scheduler = NewScheduler(clock, cacheHours: 1);
        var request = SuccessorRequest();
        var previous = await PublishPartialTerminalAsync(request);
        var completedUtc = ProviderWorkScheduler.TryReadCheckpoint((await _scheduler.GetAsync(previous.JobId))!.CheckpointJson)!
            .SourceCompletedUtc["healthy"];
        clock.Advance(TimeSpan.FromMinutes(elapsedMinutes));
        var next = await _scheduler.EnqueueAsync(request);
        Assert.True(next.Created);
        Assert.NotEqual(previous.JobId, next.JobId);
        var checkpoint = ProviderWorkScheduler.TryReadCheckpoint((await _scheduler.GetAsync(next.JobId))!.CheckpointJson);
        if (!reusable) { Assert.Null(checkpoint); return; }
        Assert.NotNull(checkpoint);
        Assert.Equal("healthy", Assert.Single(checkpoint.CompletedSources).Key);
        Assert.Equal(completedUtc, checkpoint.SourceCompletedUtc["healthy"]);
        Assert.False(checkpoint.CalendarProgress!.IsFinal);
        Assert.True(checkpoint.CalendarProgress.IsStaleCache);
        Assert.All(checkpoint.SourceSummaries.Values, summary => Assert.False(summary.CalendarProgress!.HasSourceErrors));
        await using var watcher = _scheduler.WatchAsync(next).GetAsyncEnumerator();
        Assert.True(await watcher.MoveNextAsync());
        Assert.False(watcher.Current.CalendarProgress?.IsFinal ?? false);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ForcedOrDifferentCriteriaCalendarJobsDoNotInheritPreviousSources(bool force)
    {
        var request = SuccessorRequest();
        await PublishPartialTerminalAsync(request);
        var payload = JsonSerializer.Deserialize<CalendarProviderWorkPayload>(request.PayloadJson)!;
        payload = force ? payload with { ForceRefresh = true }
            : payload with { Filters = new CalendarFilters { ShowMovies = false } };
        var next = await _scheduler.EnqueueAsync(request with { PayloadJson = JsonSerializer.Serialize(payload) });
        Assert.Null((await _scheduler.GetAsync(next.JobId))!.CheckpointJson);
    }

    [Fact]
    public async Task NewestFullSuccessPreventsReusingAnOlderPartialCheckpoint()
    {
        var request = SuccessorRequest();
        await PublishPartialTerminalAsync(request);
        var full = await _scheduler.EnqueueAsync(request);
        var claimed = (await _scheduler.ClaimNextAsync(false, "full", default))!;
        await _scheduler.PublishAsync(claimed, Progress(claimed, "complete",
            new PremiereLoadProgress("Complete", 1, 1, [Item(1)], IsFinal: true)), default);
        await _scheduler.CompleteAsync(claimed, default);
        Assert.Equal(ProviderWorkState.Completed, (await _scheduler.GetAsync(full.JobId))!.State);
        var next = await _scheduler.EnqueueAsync(request);
        Assert.Null((await _scheduler.GetAsync(next.JobId))!.CheckpointJson);
    }

    [Fact]
    public async Task DeferredOldestWindowRejoinsBehindAlreadyQueuedPeerAtSamePriority()
    {
        var now = DateTimeOffset.UtcNow;
        var old = await _store.EnqueueAsync(new(ProviderWorkKind.AdjacentWeekPrefetch, "old-window", ProviderWorkPriority.Adjacent, "{}"), now, default);
        var claimed = (await _store.ClaimNextAsync(false, "old", now, TimeSpan.FromMinutes(1), default))!;
        var peer = await _store.EnqueueAsync(new(ProviderWorkKind.AdjacentWeekPrefetch, "pending-window", ProviderWorkPriority.Adjacent, "{}"), now.AddSeconds(1), default);
        await _store.RequeueAsync(old.JobId, claimed.LeaseOwner!, now.AddSeconds(2), now.AddSeconds(2), false, null, default, retryWaiting: true);
        var next = await _store.ClaimNextAsync(false, "fair", now.AddSeconds(3), TimeSpan.FromMinutes(1), default);
        Assert.Equal(peer.JobId, next!.JobId);
        Assert.Equal(old.JobId, (await _store.ClaimNextAsync(false, "next", now.AddSeconds(3), TimeSpan.FromMinutes(1), default))!.JobId);
    }

    private static ProviderWorkRequest SuccessorRequest() => new(ProviderWorkKind.CalendarForeground, "exact-calendar-successor",
        ProviderWorkPriority.Foreground, JsonSerializer.Serialize(new CalendarProviderWorkPayload(new(2026, 11, 2), new(2026, 11, 8), false,
            new CalendarFilters { ShowSeries = false })));

    private async Task<ProviderWorkHandle> PublishPartialTerminalAsync(ProviderWorkRequest request)
    {
        var handle = await _scheduler.EnqueueAsync(request);
        var claimed = (await _scheduler.ClaimNextAsync(false, "partial", default))!;
        await PublishSourceAsync(claimed, "healthy", 1);
        await PublishSourceAsync(claimed, "failed", 2, failed: true);
        await _scheduler.PublishAsync(claimed, Progress(claimed, "partial",
            new PremiereLoadProgress("Complete", 1, 1, [Item(1)], IsFinal: true)
                { HasSourceErrors = true, FailedSourceNames = ["failed"] }), default);
        await _scheduler.CompleteAsync(claimed, default);
        Assert.Equal(ProviderWorkState.Completed, (await _scheduler.GetAsync(handle.JobId))!.State);
        return handle;
    }
}
