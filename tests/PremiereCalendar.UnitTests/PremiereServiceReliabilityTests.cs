using System.Runtime.CompilerServices;
using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class PremiereServiceTests
{
    [Fact]
    public async Task FreshCacheBypassesSchedulerAndRemoteScoreHydration()
    {
        var start = new DateOnly(2026, 10, 26);
        var cached = ReliabilityItems(start, 2_000);
        var scheduler = new RecordingWorkScheduler([]);
        var tmdb = new FakeTmdbClient();
        var service = CreateService(tmdb, calendarCache: new FakeCalendarCache { Items = cached }, workScheduler: scheduler);
        var result = await service.GetPremieresAsync(start, start.AddDays(6), CancellationToken.None,
            filters: new CalendarFilters { ShowSeries = false });
        Assert.Equal(2_000, result.Count);
        Assert.Equal(0, scheduler.EnqueueCount);
        Assert.Empty(tmdb.MovieCalls);
    }

    [Fact]
    public async Task StaleCacheIsVisibleBeforeSchedulerAdmission()
    {
        var start = new DateOnly(2026, 10, 26);
        var cached = ReliabilityItems(start, 2_000);
        var scheduler = new RecordingWorkScheduler([]);
        var service = CreateService(new FakeTmdbClient(), calendarCache: new FakeCalendarCache { ExpiredItems = cached }, workScheduler: scheduler);
        await using var updates = service.StreamPremieresAsync(start, start.AddDays(6), filters: new CalendarFilters { ShowSeries = false }).GetAsyncEnumerator();
        Assert.True(await updates.MoveNextAsync());
        Assert.Equal(2_000, updates.Current.Items.Count);
        Assert.True(updates.Current.IsStaleCache);
        Assert.False(updates.Current.IsFinal);
        Assert.Equal(0, scheduler.EnqueueCount);
    }

    [Fact]
    public async Task MissingSelectedRottenTomatoesScoresAreProvisionalWithoutRemoteWorkOnCachePath()
    {
        var start = new DateOnly(2026, 10, 26);
        var cached = ReliabilityItems(start, 1);
        var rt = new FakeRottenTomatoesClient();
        var final = new PremiereLoadProgress("Complete", 1, 1, [cached[0] with { RottenTomatoesScore = 90 }], IsFinal: true);
        var scheduler = new RecordingWorkScheduler([new ProviderWorkProgress("job", ProviderWorkState.Running, "Complete", CalendarProgress: final)]);
        var service = CreateService(new FakeTmdbClient(), calendarCache: new FakeCalendarCache { Items = cached },
            rottenTomatoes: rt, workScheduler: scheduler);
        var updates = new List<PremiereLoadProgress>();
        await foreach (var update in service.StreamPremieresAsync(start, start.AddDays(6),
            filters: new CalendarFilters { ShowSeries = false, ScoreSource = ScoreSource.RottenTomatoes })) updates.Add(update);
        Assert.False(updates[0].IsFinal);
        Assert.True(updates[0].FromCache);
        Assert.True(updates[^1].IsFinal);
        Assert.Equal(90, Assert.Single(updates[^1].Items).RottenTomatoesScore);
        Assert.Empty(rt.Calls);
        Assert.Equal(1, scheduler.EnqueueCount);
    }

    [Fact]
    public async Task SourceFailureIsExplicitAndNeverWritesFreshCache()
    {
        var start = new DateOnly(2026, 10, 26);
        var cache = new FakeCalendarCache();
        var service = CreateService(new FakeTmdbClient(), discoveryProviders: [new ReliabilityFailedProvider()], calendarCache: cache);
        var updates = new List<PremiereLoadProgress>();
        await foreach (var update in service.StreamCoreAsync(start, start, filters: new CalendarFilters { ShowSeries = false })) updates.Add(update);
        var failed = Assert.Single(updates, update => update.HasSourceErrors && !update.IsFinal);
        Assert.True(failed.HasSourceErrors);
        Assert.Equal("complete", failed.Phase);
        Assert.NotNull(failed.CheckpointKey);
        Assert.True(updates[^1].HasSourceErrors);
        Assert.NotNull(updates[^1].RetryAfterUtc);
        Assert.Empty(cache.SetCalls);
    }

    [Fact]
    public async Task StoppingEnumerationCancelsEveryActiveSource()
    {
        var start = new DateOnly(2026, 10, 26);
        var stalled = new ReliabilityWaitingProvider();
        var tmdb = new FakeTmdbClient { MovieItems = [new TmdbMovieDiscoverItem { Id = 1, Title = "One", ReleaseDate = "2026-10-26" }] };
        var service = CreateService(tmdb, discoveryProviders: [stalled]);
        await using (var updates = service.StreamCoreAsync(start, start,
            filters: new CalendarFilters { ShowSeries = false }).GetAsyncEnumerator())
        {
            Assert.True(await updates.MoveNextAsync());
            await stalled.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        await stalled.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostClaimCannotPublishCalendarFiles(bool combinedMedia)
    {
        var start = new DateOnly(2026, 10, 26);
        var cache = new FakeCalendarCache();
        var service = CreateService(new FakeTmdbClient(), calendarCache: cache);
        var guarded = false;
        var resume = new ProviderWorkResumeState(new Dictionary<string, IReadOnlyList<PremiereItem>>(), null)
        {
            PublishCacheAsync = (_, _) =>
            {
                guarded = true;
                throw new ProviderWorkLeaseLostException("old-claim");
            }
        };
        await Assert.ThrowsAsync<ProviderWorkLeaseLostException>(async () =>
        {
            await foreach (var update in service.StreamCoreAsync(start, start, forceRefresh: true,
                filters: new CalendarFilters { ShowSeries = combinedMedia }, resumeState: resume)) { }
        });
        Assert.True(guarded);
        Assert.Empty(cache.SetCalls);
    }

    [Fact]
    public async Task DeferredFinalReturnsToCallerWhileItsDurableJobWaits()
    {
        var start = new DateOnly(2026, 10, 26);
        var final = new PremiereLoadProgress("Complete", 0, 0, [], IsFinal: true)
        { HasSourceErrors = true, RetryAfterUtc = DateTimeOffset.UtcNow.AddHours(1) };
        var scheduler = new RecordingWorkScheduler([new ProviderWorkProgress("job", ProviderWorkState.Running,
            "deferred", CalendarProgress: final)]) { WaitAfterUpdates = true };
        var service = CreateService(new FakeTmdbClient(), workScheduler: scheduler);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var result = await service.GetPremieresAsync(start, start.AddDays(6), budget.Token);
        Assert.Empty(result);
        Assert.False(budget.IsCancellationRequested);
        Assert.Equal(1, scheduler.EnqueueCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForcedRefreshKeepsExpiredRowsWhenAllDiscoveryIsDeferred(bool combinedMedia)
    {
        var start = new DateOnly(2026, 10, 26);
        var cached = ReliabilityItems(start, 2);
        var cache = new FakeCalendarCache { ExpiredItems = cached };
        var tmdb = new FakeTmdbClient();
        var service = CreateService(tmdb, discoveryProviders: [new ReliabilityFailedProvider()], calendarCache: cache);
        var updates = new List<PremiereLoadProgress>();
        await foreach (var update in service.StreamCoreAsync(start, start.AddDays(6), forceRefresh: true,
            filters: new CalendarFilters { ShowSeries = combinedMedia })) updates.Add(update);
        var final = updates[^1];
        Assert.True(final.IsFinal);
        Assert.True(final.IsStaleCache);
        Assert.True(final.HasSourceErrors);
        Assert.True(final.RetryAfterUtc > DateTimeOffset.UtcNow);
        Assert.Equal(2, final.Items.Count);
        Assert.Empty(cache.SetCalls);
    }

    private static PremiereItem[] ReliabilityItems(DateOnly start, int count) => Enumerable.Range(1, count).Select(id => new PremiereItem
    {
        CanonicalId = $"movie:{id}", MediaType = PremiereMediaType.Movie, Type = PremiereItemType.MovieFirstRelease, TmdbId = id,
        Title = $"Movie {id}", PremiereDate = start.AddDays(id % 7), TmdbScore = 7, ImdbScore = 7
    }).ToArray();

    private sealed class RecordingWorkScheduler(IReadOnlyList<ProviderWorkProgress> updates) : IProviderWorkScheduler
    {
        public int EnqueueCount { get; private set; }
        public bool WaitAfterUpdates { get; init; }
        public Task<ProviderWorkHandle> EnqueueAsync(ProviderWorkRequest request, CancellationToken cancellationToken = default)
        { EnqueueCount++; return Task.FromResult(new ProviderWorkHandle("job", true)); }
        public async IAsyncEnumerable<ProviderWorkProgress> WatchAsync(ProviderWorkHandle handle,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var update in updates) { cancellationToken.ThrowIfCancellationRequested(); yield return update; }
            if (WaitAfterUpdates) await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
        public Task<ProviderWorkSnapshot?> GetAsync(string jobId, CancellationToken cancellationToken = default) => Task.FromResult<ProviderWorkSnapshot?>(null);
    }

    private sealed class ReliabilityFailedProvider : IPremiereDiscoveryProvider
    {
        public string DisplayName => "Failed fixture";
        public Task<IReadOnlyList<ExternalPremiereCandidate>> GetCandidatesAsync(DateOnly start, DateOnly end, CancellationToken cancellationToken, bool forceRefresh = false)
            => throw new ProviderRetryAfterException("fixture", DateTimeOffset.UtcNow.AddMinutes(5));
    }

    private sealed class ReliabilityWaitingProvider : IPremiereDiscoveryProvider
    {
        public string DisplayName => "Waiting fixture";
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IReadOnlyList<ExternalPremiereCandidate>> GetCandidatesAsync(DateOnly start, DateOnly end, CancellationToken cancellationToken, bool forceRefresh = false)
        {
            Started.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return []; }
            finally { Stopped.TrySetResult(); }
        }
    }
}
