using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class PremiereServiceTests
{
    [Fact]
    public async Task ColdDiscovery_ReadinessFlushConflictRetainsHealthyCompletionAndReturnsPartialFinal()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var telemetry = new PremiereTelemetry();
        var cache = new FakeCalendarCache();
        using var tomatoes = new FullWeekHeldTomatoes();
        var tmdb = new FakeTmdbClient
        {
            MovieItems = [new() { Id = 1, Title = "Healthy", ReleaseDate = "2026-11-02", OriginalLanguage = "en" }]
        };
        tmdb.MovieDetailsById[1] = new() { Id = 1, Runtime = 110, OriginalLanguage = "en", ExternalIds = new() { ImdbId = "tt0000001" } };
        var service = new PremiereService(tmdb, new FakeOmdbClient(), new FakeTvmazeClient(), new FakeWatchmodeClient(),
            cache, new TrailerSelector(), new RatingMapper(), [], [],
            Microsoft.Extensions.Options.Options.Create(new TmdbOptions { BearerToken = "fixture" }),
            NullLogger<PremiereService>.Instance, rottenTomatoesClient: tomatoes,
            itemEnrichmentCache: ItemCacheCoordinator(store, clock), telemetry: telemetry);
        var sessionLocal = (AsyncLocal<ItemEnrichmentCacheCoordinator.ItemEnrichmentSession?>)typeof(PremiereService)
            .GetField("_itemCacheSession", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        var sourceLocal = typeof(PremiereService).GetField("_coldDiscoverySource", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(service)!;
        ItemEnrichmentCacheCoordinator.ItemEnrichmentSession? session = null;
        object? coreSource = null;
        store.BeforeWriteBatch = (_, _) =>
        {
            session ??= sessionLocal.Value;
            coreSource ??= sourceLocal.GetType().GetProperty("Value")!.GetValue(sourceLocal);
            return Task.CompletedTask;
        };
        var conflictInjected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var updates = new ConcurrentQueue<PremiereLoadProgress>();
        var staged = false;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var run = Task.Run(async () =>
        {
            await foreach (var update in service.StreamCoreAsync(date, date, filters: new CalendarFilters
                { ShowSeries = false, MovieFilters = { OriginalLanguages = ["en"] } }, cancellationToken: budget.Token))
            {
                updates.Enqueue(update);
                if (staged || coreSource?.GetType().GetProperty("Exhausted")!.GetValue(coreSource) is not true) continue;
                staged = true;
                var capturedSession = session ?? throw new InvalidOperationException("Expected the live source session.");
                var pendingIdentity = new ItemEnrichmentIdentity(PremiereMediaType.Movie, 999);
                await capturedSession.PrepareAsync([pendingIdentity], null, budget.Token);
                await capturedSession.GetAsync(pendingIdentity, false, (_, _, _, _) => Task.FromResult(new ItemEnrichmentRefresh(
                    new ItemEnrichmentMetadata { Title = "Concurrent optional proposal", CoreMetadataOnly = false }, null,
                    RefreshRatingsClock: false)), budget.Token);
                store.BeforeWriteBatch = async (writes, token) =>
                {
                    if (!writes.Any(write => write.Identity == pendingIdentity)) return;
                    // Source iterators carry a source key. The root readiness
                    // fence must own this flush, rather than a later source flush.
                    Assert.Null(telemetry.CaptureDiagnosticContext()?.SourceKey);
                    await store.InvalidateTmdbAsync(PremiereMediaType.Movie, [999], clock.GetUtcNow(), token);
                    conflictInjected.TrySetResult();
                };
            }
        });
        try
        {
            await conflictInjected.Task.WaitAsync(budget.Token);
            tomatoes.Release.TrySetResult();
            await run.WaitAsync(budget.Token);
        }
        finally
        {
            tomatoes.Release.TrySetResult();
            if (!run.IsCompleted) await budget.CancelAsync();
            try { await run.WaitAsync(TimeSpan.FromSeconds(3)); } catch { }
        }
        var final = Assert.Single(updates, update => update.IsFinal);
        Assert.True(final.HasSourceErrors);
        Assert.Contains("Item cache validation", final.FailedSourceNames);
        Assert.Equal(1, Assert.Single(final.Items).TmdbId);
        Assert.Equal(81, Assert.Single(final.Items).RottenTomatoesScore);
        Assert.DoesNotContain(updates, update => update.IsCalendarReady);
        Assert.Empty(cache.SetCalls);
    }

    [Fact]
    public async Task ColdDiscovery_ReadinessIsNotRecordedAsAProviderOrCountedAsAnotherUnmappedSource()
    {
        var date = new DateOnly(2026, 11, 2);
        var tmdb = new FakeTmdbClient
        {
            MovieItems = [new() { Id = 1, Title = "Known", ReleaseDate = "2026-11-02", OriginalLanguage = "en" }]
        };
        tmdb.MovieDetailsById[1] = new() { Id = 1, Runtime = 110, OriginalLanguage = "en" };
        var external = new FakeDiscoveryProvider { DisplayName = "External fixture", Candidates =
            [new(PremiereMediaType.Movie, date, "Unresolved fixture", null, null, null, "External fixture",
                OriginalLanguage: "en", ExternalProviderId: "unknown-1")] };
        var clock = new ItemCacheClock();
        var diagnostics = new ColdWeekDiagnosticsStore();
        using var tomatoes = new FullWeekHeldTomatoes();
        var service = new PremiereService(tmdb, new FakeOmdbClient(), new FakeTvmazeClient(), new FakeWatchmodeClient(),
            new FakeCalendarCache(), new TrailerSelector(), new RatingMapper(), [], [external],
            Microsoft.Extensions.Options.Options.Create(new TmdbOptions { BearerToken = "fixture" }),
            NullLogger<PremiereService>.Instance, rottenTomatoesClient: tomatoes,
            itemEnrichmentCache: ItemCacheCoordinator(new MemoryItemEnrichmentStore(clock), clock),
            weekDiagnosticsService: new WeekDiagnosticsService(diagnostics, clock));
        var sawReady = false;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await foreach (var update in service.StreamCoreAsync(date, date, filters: new CalendarFilters
                { ShowSeries = false, MovieFilters = { OriginalLanguages = ["en"] } }, cancellationToken: budget.Token))
            {
                if (!update.IsCalendarReady || update.IsFinal || update.SourceName != "Discovery") continue;
                sawReady = true;
                Assert.Equal("enrichment", update.Phase);
                tomatoes.Release.TrySetResult();
            }
        }
        finally { tomatoes.Release.TrySetResult(); }
        Assert.True(sawReady);
        var saved = diagnostics.Saved ?? throw new InvalidOperationException("Expected recorded week diagnostics.");
        Assert.DoesNotContain(saved.Sources, source => source.SourceName == "Discovery");
        Assert.Equal(1, saved.Sources.Sum(source => source.UnmappedCount ?? 0));
    }

    [Fact]
    public async Task ColdDiscovery_SuccessfulExistingSeriesFallbackDoesNotBlockWeekReadiness()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var tmdb = new FakeTmdbClient
        {
            TvItems = [new() { Id = 91, Name = "Existing series", FirstAirDate = "2026-11-02", OriginalLanguage = "en" }],
            MovieItems = [new() { Id = 1, Title = "Cold movie", ReleaseDate = "2026-11-02", OriginalLanguage = "en" }]
        };
        tmdb.SeasonOneEpisodeOneDatesById[91] = date;
        tmdb.TvDetailsById[91] = new() { Id = 91, Name = "Existing series", EpisodeRunTime = [43], PosterPath = "/series.jpg" };
        tmdb.MovieDetailsById[1] = new() { Id = 1, Runtime = 110, OriginalLanguage = "en" };
        using var tomatoes = new ColdMovieHeldTomatoes();
        PremiereService Service() => new(tmdb, new FakeOmdbClient(), new FakeTvmazeClient(), new FakeWatchmodeClient(),
            new FakeCalendarCache(), new TrailerSelector(), new RatingMapper(), [], [],
            Microsoft.Extensions.Options.Options.Create(new TmdbOptions { BearerToken = "fixture" }),
            NullLogger<PremiereService>.Instance, rottenTomatoesClient: tomatoes, itemEnrichmentCache: coordinator);
        await Service().GetPremieresAsync(date, date, CancellationToken.None, forceRefresh: true,
            filters: new CalendarFilters { ShowMovies = false, SeriesFilters = { OriginalLanguages = ["en"] } });
        clock.Advance(TimeSpan.FromMinutes(1));
        await store.InvalidateTmdbAsync(PremiereMediaType.Series, [91], clock.GetUtcNow(), CancellationToken.None);
        var sawReady = false;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await foreach (var update in Service().StreamCoreAsync(date, date, filters: new CalendarFilters
                { SeriesFilters = { OriginalLanguages = ["en"] }, MovieFilters = { OriginalLanguages = ["en"] } },
                cancellationToken: budget.Token))
            {
                if (!update.IsCalendarReady || update.IsFinal) continue;
                Assert.Contains(update.Items, item => item.TmdbId == 91);
                Assert.Contains(update.Items, item => item.TmdbId == 1);
                sawReady = true;
                tomatoes.Release.TrySetResult();
            }
        }
        finally { tomatoes.Release.TrySetResult(); }
        Assert.True(sawReady);
    }

    // The readiness probe and the final check both validate through ReconcileAsync;
    // the first such read (readiness) is the injection point, with no timing involved.
    private static bool InReconcileRead() => Environment.StackTrace.Contains("ReconcileAsync", StringComparison.Ordinal);

    [Fact]
    public async Task ColdDiscovery_ForeignInvalidationOfCoreRejectedMovieAtReadinessKeepsTheLoadPartial()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var cache = new FakeCalendarCache();
        var tmdb = new FakeTmdbClient
        {
            MovieItems =
            [
                new() { Id = 1, Title = "Healthy", ReleaseDate = "2026-11-02", OriginalLanguage = "en" },
                new() { Id = 2, Title = "Too short", ReleaseDate = "2026-11-02", OriginalLanguage = "en" }
            ]
        };
        tmdb.MovieDetailsById[1] = new() { Id = 1, Runtime = 110, OriginalLanguage = "en" };
        tmdb.MovieDetailsById[2] = new() { Id = 2, Runtime = 30, OriginalLanguage = "en" };
        var service = new PremiereService(tmdb, new FakeOmdbClient(), new FakeTvmazeClient(), new FakeWatchmodeClient(),
            cache, new TrailerSelector(), new RatingMapper(), [], [],
            Microsoft.Extensions.Options.Options.Create(new TmdbOptions { BearerToken = "fixture" }),
            NullLogger<PremiereService>.Instance, itemEnrichmentCache: ItemCacheCoordinator(store, clock));
        var injected = 0;
        var validationReads = 0;
        store.BeforeCurrentRead = identities =>
        {
            if (InReconcileRead() && identities.Contains(new ItemEnrichmentIdentity(PremiereMediaType.Movie, 2)))
                Interlocked.Increment(ref validationReads);
            if (!InReconcileRead() || !identities.Contains(new ItemEnrichmentIdentity(PremiereMediaType.Movie, 2))
                || Interlocked.Exchange(ref injected, 1) == 1) return;
            clock.Advance(TimeSpan.FromMinutes(1));
            store.InvalidateTmdbAsync(PremiereMediaType.Movie, [2], clock.GetUtcNow(), default).GetAwaiter().GetResult();
        };
        var updates = new List<PremiereLoadProgress>();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await foreach (var update in service.StreamCoreAsync(date, date, filters: new CalendarFilters
            { ShowSeries = false, MovieFilters = { OriginalLanguages = ["en"], RuntimeMinMinutes = 45 } }, cancellationToken: budget.Token))
            updates.Add(update);
        Assert.Equal(1, injected);
        // The readiness probe and the final reconcile must both have validated the rejected row.
        Assert.True(validationReads >= 2, $"Expected the readiness probe and the final reconcile to read the row, saw {validationReads}.");
        var final = Assert.Single(updates, update => update.IsFinal);
        Assert.DoesNotContain(updates, update => update.IsCalendarReady);
        Assert.True(final.HasSourceErrors);
        Assert.Contains(PremiereLoadProgress.ItemCacheValidationSourceName, final.FailedSourceNames);
        Assert.Equal([1], final.Items.Select(item => item.TmdbId));
        Assert.Empty(cache.SetCalls);
    }

    [Fact]
    public async Task ColdDiscovery_CanonicalDateMovedOutOfWindowAtReadinessKeepsTheLoadPartial()
    {
        if (typeof(PremiereService).GetMethod("CanDiscoverCoreFirst", BindingFlags.Instance | BindingFlags.NonPublic) is null) return;
        var start = new DateOnly(2026, 11, 2);
        var rows = FullWeekRows(start, true);
        using var fixture = CreateColdFixture(rows, held: true);
        fixture.Held!.Release.TrySetResult();
        var injected = 0;
        var validationReads = 0;
        fixture.Store.BeforeCurrentRead = identities =>
        {
            if (InReconcileRead() && identities.Contains(new ItemEnrichmentIdentity(PremiereMediaType.Series, 20_000)))
                Interlocked.Increment(ref validationReads);
            if (!InReconcileRead() || !identities.Contains(new ItemEnrichmentIdentity(PremiereMediaType.Series, 20_000))
                || Interlocked.Exchange(ref injected, 1) == 1) return;
            var key = Assert.Single(fixture.Store.Records.Keys, key => key.Identity.TmdbId == 20_000);
            var core = fixture.Store.Records[key];
            var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(core.MetadataJson!)!;
            var foreignClock = core.MetadataFetchedUtc!.Value.AddMinutes(1);
            fixture.Store.Records[key] = core with
            {
                Generation = core.Generation + 1, MetadataRevision = core.MetadataRevision + 1,
                MetadataFetchedUtc = foreignClock, MetadataInvalidatedUtc = foreignClock,
                MetadataJson = JsonSerializer.Serialize(metadata with
                { Title = "Foreign canonical repair", FirstEpisodeDateChecked = true, FirstEpisodeDate = start.AddDays(-7) })
            };
        };
        var updates = new List<PremiereLoadProgress>();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await foreach (var update in fixture.Service.StreamCoreAsync(start, start.AddDays(6), filters: ColdFilters(true),
            cancellationToken: budget.Token)) updates.Add(update);
        Assert.Equal(1, injected);
        Assert.True(validationReads >= 2, $"Expected the readiness probe and the final reconcile to read the row, saw {validationReads}.");
        var final = Assert.Single(updates, update => update.IsFinal);
        Assert.DoesNotContain(updates, update => update.IsCalendarReady);
        Assert.True(final.HasSourceErrors);
        Assert.Contains(PremiereLoadProgress.ItemCacheValidationSourceName, final.FailedSourceNames);
        Assert.DoesNotContain(final.Items, item => item.TmdbId == 20_000);
        Assert.Empty(fixture.Cache.SetCalls);
    }

    [Fact]
    public async Task ColdDiscovery_ReadinessProbeDoesNotRollBackConcurrentlyCompletedEnrichment()
    {
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var identity = new ItemEnrichmentIdentity(PremiereMediaType.Movie, 901);
        await using var session = await coordinator.CreateSessionAsync(default);
        await session.PrepareAsync([identity], null, default);
        var metadata = new ItemEnrichmentMetadata { Title = "Completed", ImdbId = "tt0000901", RuntimeMinutes = 90, AvailabilityComplete = true };
        var ratings = new ItemEnrichmentRatings(7.4, 740, null, null, null, ImdbId: metadata.ImdbId, Title: metadata.Title, Year: 2026);
        var providerCalls = 0;
        Task<ItemEnrichmentRefresh> Provider(ItemEnrichmentSnapshot _, bool __, bool ___, CancellationToken ____)
        {
            providerCalls++;
            return Task.FromResult(new ItemEnrichmentRefresh(metadata, ratings));
        }
        var completed = await session.GetAsync(identity, true, Provider, default);
        Assert.Equal(1, providerCalls);
        Assert.True(session.HasPendingWrites); // completed after the readiness flush; the store still has the older reserved row
        var probe = await session.ReconcileAsync([], includeConsumed: true, default, readOnly: true);
        Assert.Same(completed, session.Find(identity));
        Assert.True(session.HasPendingWrites);
        // A second occurrence of the same item still reuses the completed result.
        Assert.Same(completed, await session.GetAsync(identity, false, Provider, default));
        Assert.Equal(1, providerCalls);
        Assert.Empty(probe.DirtyMedia);
    }

    [Fact]
    public async Task ColdDiscovery_ReadinessStaysLatchedWhenALaterEnrichmentJobFails()
    {
        var date = new DateOnly(2026, 11, 2);
        var tmdb = new FakeTmdbClient
        {
            MovieItems = [new() { Id = 1, Title = "Known", ReleaseDate = "2026-11-02", OriginalLanguage = "en" }]
        };
        tmdb.MovieDetailsById[1] = new() { Id = 1, Runtime = 110, OriginalLanguage = "en" };
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        using var tomatoes = new ColdReleasedTomatoes();
        var service = new PremiereService(tmdb, new FakeOmdbClient(), new FakeTvmazeClient(), new FakeWatchmodeClient(),
            new FakeCalendarCache(), new TrailerSelector(), new RatingMapper(), [], [],
            Microsoft.Extensions.Options.Options.Create(new TmdbOptions { BearerToken = "fixture" }),
            NullLogger<PremiereService>.Instance, rottenTomatoesClient: tomatoes,
            itemEnrichmentCache: ItemCacheCoordinator(store, clock));
        var sawReady = false;
        var laterErrorUpdates = 0;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await foreach (var update in service.StreamCoreAsync(date, date, filters: new CalendarFilters
                { ShowSeries = false, MovieFilters = { OriginalLanguages = ["en"] } }, cancellationToken: budget.Token))
            {
                if (update.IsFinal) continue;
                if (!sawReady)
                {
                    if (!update.IsCalendarReady) continue;
                    sawReady = true;
                    // The next write batch loses its generation to a foreign invalidation, which fails
                    // the enrichment job after readiness was already shown.
                    store.BeforeWriteBatch = (_, token) =>
                    {
                        store.InvalidateTmdbAsync(PremiereMediaType.Movie, [1], clock.GetUtcNow(), token).GetAwaiter().GetResult();
                        return Task.CompletedTask;
                    };
                    tomatoes.Release.TrySetResult();
                    continue;
                }
                if (!update.HasSourceErrors) continue;
                laterErrorUpdates++;
                Assert.True(update.IsCalendarReady);
            }
        }
        finally { tomatoes.Release.TrySetResult(); }
        Assert.True(sawReady);
        Assert.True(laterErrorUpdates > 0);
    }

    [Fact]
    public async Task ColdDiscovery_ExpiredRowsDoNotComeBackInALaterUpdateThatCarriesASourceErrorAfterReadiness()
    {
        var date = new DateOnly(2026, 11, 2);
        var tmdb = new FakeTmdbClient
        {
            MovieItems = [new() { Id = 1, Title = "Known", ReleaseDate = "2026-11-02", OriginalLanguage = "en" }]
        };
        tmdb.MovieDetailsById[1] = new() { Id = 1, Runtime = 110, OriginalLanguage = "en" };
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        using var tomatoes = new ColdReleasedTomatoes();
        var stale = new PremiereItem
        {
            CanonicalId = PremiereIdentity.CanonicalId(PremiereMediaType.Movie, 777), Title = "Expired card",
            MediaType = PremiereMediaType.Movie, Type = PremiereItemType.MovieFirstRelease, TmdbId = 777,
            PremiereDate = date, OriginalLanguage = "en", RuntimeMinutes = 100
        };
        var service = new PremiereService(tmdb, new FakeOmdbClient(), new FakeTvmazeClient(), new FakeWatchmodeClient(),
            new FakeCalendarCache { ExpiredItems = [stale] }, new TrailerSelector(), new RatingMapper(), [], [],
            Microsoft.Extensions.Options.Options.Create(new TmdbOptions { BearerToken = "fixture" }),
            NullLogger<PremiereService>.Instance, rottenTomatoesClient: tomatoes,
            itemEnrichmentCache: ItemCacheCoordinator(store, clock));
        var sawReady = false;
        var laterErrorUpdates = 0;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await foreach (var update in service.StreamCoreAsync(date, date, filters: new CalendarFilters
                { ShowSeries = false, MovieFilters = { OriginalLanguages = ["en"] } }, cancellationToken: budget.Token))
            {
                if (update.IsFinal) continue;
                if (!sawReady)
                {
                    if (!update.IsCalendarReady) continue;
                    sawReady = true;
                    Assert.DoesNotContain(update.Items, item => item.TmdbId == 777);
                    store.BeforeWriteBatch = (_, token) =>
                    {
                        store.InvalidateTmdbAsync(PremiereMediaType.Movie, [1], clock.GetUtcNow(), token).GetAwaiter().GetResult();
                        return Task.CompletedTask;
                    };
                    tomatoes.Release.TrySetResult();
                    continue;
                }
                // Once the calendar was ready with fresh rows, no later (error-carrying) update may resurrect expired ones.
                Assert.DoesNotContain(update.Items, item => item.TmdbId == 777);
                if (update.HasSourceErrors) laterErrorUpdates++;
            }
        }
        finally { tomatoes.Release.TrySetResult(); }
        Assert.True(sawReady);
        Assert.True(laterErrorUpdates > 0);
    }

    private sealed class ColdReleasedTomatoes : IRottenTomatoesClient, IDisposable
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<RottenTomatoesScores> GetScoresAsync(PremiereMediaType type, string title, int? year,
            string? wikidataId, CancellationToken token, bool forceRefresh = false)
        {
            await Release.Task.WaitAsync(token);
            return new(81, 73);
        }
        public void Dispose() => Release.TrySetResult();
    }

    private sealed class ColdWeekDiagnosticsStore : IWeekDiagnosticsStore
    {
        public WeekDiagnostics? Saved;
        public Task<WeekDiagnostics?> GetAsync(DateOnly start, string key, CancellationToken token) => Task.FromResult(Saved);
        public Task<IReadOnlyList<WeekDiagnostics>> GetRecentAsync(int take, CancellationToken token)
            => Task.FromResult<IReadOnlyList<WeekDiagnostics>>(Saved is { } saved ? [saved] : []);
        public Task SaveAsync(WeekDiagnostics diagnostics, CancellationToken token) { Saved = diagnostics; return Task.CompletedTask; }
    }

    private sealed class ColdMovieHeldTomatoes : IRottenTomatoesClient, IDisposable
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<RottenTomatoesScores> GetScoresAsync(PremiereMediaType type, string title, int? year,
            string? wikidataId, CancellationToken token, bool forceRefresh = false)
        {
            if (type == PremiereMediaType.Movie) await Release.Task.WaitAsync(token);
            return new(81, 73);
        }
        public void Dispose() => Release.TrySetResult();
    }
}
