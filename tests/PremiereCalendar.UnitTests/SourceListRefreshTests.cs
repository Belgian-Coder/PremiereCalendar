using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class PremiereServiceTests
{
    [Fact]
    public async Task SourceListRefresh_RediscoveryRemovesRowsWithoutReenrichingFreshItems()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var cache = new FakeCalendarCache();
        var initialTomatoes = new FakeRottenTomatoesClient();
        foreach (var id in new[] { 901, 902 }) initialTomatoes.Scores[(PremiereMediaType.Movie, $"Source list movie {id}", 2026, null)] = new RottenTomatoesScores(81, 87);
        await CreateService(SourceListTmdb(date, 901, 902), omdb: ItemCacheOmdb("7.1"), calendarCache: cache,
            rottenTomatoes: initialTomatoes, artworkProviders: [new RecordingArtworkProvider()],
            itemEnrichmentCache: coordinator).GetPremieresAsync(date, date, default, forceRefresh: true, filters: SourceListFilters());
        var before = store.Records.Values.Single(record => record.Identity.TmdbId == 901);
        clock.Advance(TimeSpan.FromMinutes(5));
        var tmdb = SourceListTmdb(date, 901);
        var omdb = new FakeOmdbClient();
        var tomatoes = new FakeRottenTomatoesClient();
        var watchmode = new FakeWatchmodeClient();
        var artwork = new RecordingArtworkProvider();
        var external = new SourceListDiscoveryProvider();
        var service = CreateService(tmdb, omdb: omdb, rottenTomatoes: tomatoes, watchmode: watchmode,
            artworkProviders: [artwork], discoveryProviders: [external], calendarCache: cache, itemEnrichmentCache: coordinator);
        var updates = new List<PremiereLoadProgress>();
        await foreach (var update in service.StreamSourceRefreshAsync(date, date, SourceListFilters())) updates.Add(update);

        var final = Assert.Single(updates, update => update.IsFinal);
        Assert.False(final.HasSourceErrors);
        var item = Assert.Single(final.Items);
        Assert.Equal(901, item.TmdbId);
        Assert.Equal(112, item.RuntimeMinutes);
        Assert.Equal(7.1, item.ImdbScore);
        Assert.True(Assert.Single(tmdb.MovieCalls).ForceRefresh);
        Assert.True(Assert.Single(external.ForceRequests));
        Assert.Empty(tmdb.MovieDetailCalls);
        Assert.Empty(omdb.Calls);
        Assert.Empty(tomatoes.Calls);
        Assert.Empty(watchmode.SourceCalls);
        Assert.Empty(artwork.Calls);
        var after = store.Records.Values.Single(record => record.Identity.TmdbId == 901);
        Assert.Equal(before.MetadataFetchedUtc, after.MetadataFetchedUtc);
        Assert.Equal(before.RatingsFetchedUtc, after.RatingsFetchedUtc);
        Assert.Equal(before.MetadataInvalidatedUtc, after.MetadataInvalidatedUtc);
        Assert.DoesNotContain(cache.SetCalls.Last().Items, cached => cached.TmdbId == 902);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("expired")]
    public async Task SourceListRefresh_FetchesMissingOrExpiredEnrichment(string state)
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var cache = new FakeCalendarCache();
        await CreateService(SourceListTmdb(date, 901), omdb: ItemCacheOmdb("7.1"), calendarCache: cache,
            itemEnrichmentCache: coordinator).GetPremieresAsync(date, date, default, forceRefresh: true, filters: SourceListFilters());
        var id = state == "missing" ? 903 : 901;
        if (state == "expired") clock.Advance(TimeSpan.FromDays(8));
        var tmdb = SourceListTmdb(date, id);
        tmdb.MovieDetailsById[id] = tmdb.MovieDetailsById[id] with { Runtime = 130 };
        var omdb = new FakeOmdbClient();
        omdb.ItemsByImdbId[$"tt{id:0000000}"] = new OmdbItem { Response = "True", ImdbRating = "8.2" };
        var service = CreateService(tmdb, omdb: omdb, calendarCache: cache, itemEnrichmentCache: coordinator);
        PremiereLoadProgress? final = null;
        await foreach (var update in service.StreamSourceRefreshAsync(date, date, SourceListFilters())) if (update.IsFinal) final = update;

        Assert.NotNull(final);
        Assert.False(final.HasSourceErrors);
        var item = Assert.Single(final.Items);
        Assert.Equal(id, item.TmdbId);
        Assert.Equal(130, item.RuntimeMinutes);
        Assert.Equal(8.2, item.ImdbScore);
        Assert.True(Assert.Single(tmdb.MovieCalls).ForceRefresh);
        Assert.Single(tmdb.MovieDetailCalls);
        Assert.Equal($"tt{id:0000000}", Assert.Single(omdb.Calls));
    }

    [Fact]
    public async Task SourceListRefresh_LegacyForceAllStillRefreshesFreshEnrichment()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var coordinator = ItemCacheCoordinator(new MemoryItemEnrichmentStore(clock), clock);
        var cache = new FakeCalendarCache();
        await CreateService(SourceListTmdb(date, 901), omdb: ItemCacheOmdb("7.1"), calendarCache: cache,
            itemEnrichmentCache: coordinator).GetPremieresAsync(date, date, default, forceRefresh: true, filters: SourceListFilters());
        var tmdb = SourceListTmdb(date, 901);
        var omdb = ItemCacheOmdb("8.2");
        var item = Assert.Single(await CreateService(tmdb, omdb: omdb, calendarCache: cache, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, default, forceRefresh: true, filters: SourceListFilters()));
        Assert.Equal(8.2, item.ImdbScore);
        Assert.True(Assert.Single(tmdb.MovieDetailCalls).ForceRefresh);
        Assert.Single(omdb.Calls);
    }

    [Fact]
    public async Task SourceListRefresh_QueuesItsOwnIntentDespiteCompleteFreshWeekCache()
    {
        var date = new DateOnly(2026, 11, 2);
        var scheduler = new SourceListRecordingScheduler();
        var cache = new FakeCalendarCache { Items = [ItemCacheLegacyItem(date, new ItemCacheClock())] };
        var service = CreateService(new FakeTmdbClient(), calendarCache: cache, workScheduler: scheduler);
        await service.GetPremieresAsync(date, date, default, filters: SourceListFilters());
        Assert.Empty(scheduler.Requests);
        await foreach (var _ in service.StreamSourceRefreshAsync(date, date, SourceListFilters())) { }
        var request = Assert.Single(scheduler.Requests);
        Assert.Contains("sources", request.DedupeKey, StringComparison.Ordinal);
        var payload = JsonSerializer.Deserialize<CalendarProviderWorkPayload>(request.PayloadJson)!;
        Assert.True(payload.RefreshSourceLists);
        Assert.False(payload.ForceRefresh);
        Assert.Equal(date, payload.Start);
        Assert.Equal(45, payload.Filters!.MovieFilters.RuntimeMinMinutes);
    }

    [Fact]
    public async Task SourceListRefresh_WithoutCoordinatorBypassesFreshDiscoveryMemoAndReusesEnrichment()
    {
        var date = new DateOnly(2026, 11, 2);
        var rows = FullWeekRows(date, false).Where(row => row.Date == date && row.Language == "en").ToArray();
        using var handler = new FullWeekHttpHandler(rows);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://full-week.invalid/3/") };
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var options = Microsoft.Extensions.Options.Options.Create(new TmdbOptions { BearerToken = "local-fixture" });
        using var limiter = new TmdbRequestLimiter(options);
        var tmdb = new TmdbClient(http, memory, options, NullLogger<TmdbClient>.Instance, requestLimiter: limiter);
        var service = new PremiereService(tmdb, new FakeOmdbClient(), new FakeTvmazeClient(), new FakeWatchmodeClient(),
            new FakeCalendarCache(), new TrailerSelector(), new RatingMapper(), [], [], options, NullLogger<PremiereService>.Instance);
        var primed = await service.GetPremieresAsync(date, date, default, forceRefresh: true, filters: SourceListFilters());
        var before = handler.Paths.Count;
        PremiereLoadProgress? final = null;
        await foreach (var update in service.StreamSourceRefreshAsync(date, date, SourceListFilters())) if (update.IsFinal) final = update;
        Assert.NotNull(final);
        Assert.False(final.HasSourceErrors);
        Assert.Equal(before + 1, handler.Paths.Count);
        Assert.Equal(2, handler.Paths.Count(path => path.StartsWith("/3/discover/movie?", StringComparison.Ordinal)));
        Assert.Equal(JsonSerializer.Serialize(primed.Select(FullWeekNormalize)), JsonSerializer.Serialize(final.Items.Select(FullWeekNormalize)));
    }

    private static CalendarFilters SourceListFilters() => new()
    { ShowSeries = false, MovieFilters = { OriginalLanguages = ["en"], RuntimeMinMinutes = 45 } };

    private static FakeTmdbClient SourceListTmdb(DateOnly date, params int[] ids)
    {
        var tmdb = new FakeTmdbClient { FilterMovieItemsByDate = true, MovieItems = ids.Select(id => new TmdbMovieDiscoverItem
            { Id = id, Title = $"Source list movie {id}", ReleaseDate = date.ToString("yyyy-MM-dd"), OriginalLanguage = "en" }).ToArray() };
        foreach (var id in ids) tmdb.MovieDetailsById[id] = new TmdbDetailsWithExtras
        { Id = id, Runtime = 112, OriginalLanguage = "en", ExternalIds = new TmdbExternalIds { ImdbId = $"tt{id:0000000}" } };
        return tmdb;
    }

    private sealed class SourceListDiscoveryProvider : INamedPremiereDiscoveryProvider
    {
        public string DisplayName => "Source list fixture";
        public ConcurrentBag<bool> ForceRequests { get; } = [];
        public Task<IReadOnlyList<ExternalPremiereCandidate>> GetCandidatesAsync(DateOnly start, DateOnly end,
            CancellationToken token, bool forceRefresh = false)
        { ForceRequests.Add(forceRefresh); return Task.FromResult<IReadOnlyList<ExternalPremiereCandidate>>([]); }
    }
    private sealed class SourceListRecordingScheduler : IProviderWorkScheduler
    {
        public List<ProviderWorkRequest> Requests { get; } = [];
        public Task<ProviderWorkHandle> EnqueueAsync(ProviderWorkRequest request, CancellationToken token = default)
        { Requests.Add(request); return Task.FromResult(new ProviderWorkHandle("source-list", true)); }
        public async IAsyncEnumerable<ProviderWorkProgress> WatchAsync(ProviderWorkHandle handle,
            [EnumeratorCancellation] CancellationToken token = default)
        {
            await Task.Yield();
            token.ThrowIfCancellationRequested();
            yield return new(handle.JobId, ProviderWorkState.Completed, "Complete", CalendarProgress:
                new PremiereLoadProgress("Complete", 0, 0, [], IsFinal: true));
        }
        public Task<ProviderWorkSnapshot?> GetAsync(string jobId, CancellationToken token = default) => Task.FromResult<ProviderWorkSnapshot?>(null);
    }
}

public sealed partial class ProviderWorkSchedulerTests
{
    [Fact]
    public async Task SourceListRefresh_NewJobDoesNotInheritPreviousSuccessfulSources()
    {
        var request = SuccessorRequest();
        var payload = JsonSerializer.Deserialize<CalendarProviderWorkPayload>(request.PayloadJson)! with { RefreshSourceLists = true };
        request = request with { PayloadJson = JsonSerializer.Serialize(payload) };
        var previous = await PublishPartialTerminalAsync(request);
        var next = await _scheduler.EnqueueAsync(request);
        Assert.True(next.Created);
        Assert.NotEqual(previous.JobId, next.JobId);
        Assert.Null((await _scheduler.GetAsync(next.JobId))!.CheckpointJson);
    }

    [Fact]
    public async Task SourceListRefresh_HostedWorkerPropagatesSnapshotAndFeedRefreshIntent()
    {
        var pipeline = new SourceListResumeCapturePipeline();
        var payload = new CalendarProviderWorkPayload(new(2026, 11, 2), new(2026, 11, 8), false,
            new CalendarFilters { ShowSeries = false }) { RefreshSourceLists = true };
        await _scheduler.EnqueueAsync(new(ProviderWorkKind.CalendarForeground, "source-list-resume", ProviderWorkPriority.Foreground,
            JsonSerializer.Serialize(payload)));
        await using var services = new ServiceCollection().AddSingleton<IPremiereLoadPipeline>(pipeline).BuildServiceProvider();
        var recovery = new DatabaseRecoveryState();
        recovery.Set(new(DatabaseSchema.CurrentVersion, DatabaseSchema.CurrentVersion, true, "ready", null, null));
        using var worker = new ProviderWorkSchedulerHostedService(_scheduler, services.GetRequiredService<IServiceScopeFactory>(), recovery,
            Microsoft.Extensions.Options.Options.Create(new ProviderSchedulerOptions { LeaseSeconds = 30 }), NullLogger<ProviderWorkSchedulerHostedService>.Instance);
        await worker.StartAsync(default);
        try { await pipeline.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { await worker.StopAsync(default); }
        Assert.False(pipeline.ForceRefresh);
        Assert.NotNull(pipeline.Resume);
        Assert.True(pipeline.Resume.RefreshSourceLists);
        Assert.True(pipeline.Resume.RefreshCalendarSnapshot);
        Assert.Empty(pipeline.Resume.CompletedSources);
    }

    private sealed class SourceListResumeCapturePipeline : IPremiereLoadPipeline
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ProviderWorkResumeState? Resume;
        public bool ForceRefresh;
        public async IAsyncEnumerable<PremiereLoadProgress> StreamCoreAsync(DateOnly start, DateOnly end, bool forceRefresh,
            CalendarFilters? filters, ProviderWorkResumeState? resumeState, [EnumeratorCancellation] CancellationToken token)
        {
            Resume = resumeState;
            ForceRefresh = forceRefresh;
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            yield break;
        }
    }
}
