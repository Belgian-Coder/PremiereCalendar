using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class PremiereServiceTests
{
    // Copy this file and FullWeekRefreshPerformanceTests.cs unchanged into the
    // frozen baseline. All providers are local fixtures; timing is evidence,
    // while complete projection and call parity are assertions.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ColdDiscoveryPerformance_RecordsColdWeekAndProjection(bool includeSeries)
    {
        var start = new DateOnly(2026, 11, 2);
        var rows = FullWeekRows(start, includeSeries);
        using var fixture = CreateColdFixture(rows, held: false);
        var filters = ColdFilters(includeSeries);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var timer = Stopwatch.StartNew();
        double? firstVerified = null, allCore = null, ready = null;
        PremiereLoadProgress? final = null;
        await foreach (var update in fixture.Service.StreamCoreAsync(start, start.AddDays(6), filters: filters,
            cancellationToken: budget.Token))
        {
            if (update.Items.Any(item => item.RuntimeMinutes is > 0)) firstVerified ??= timer.Elapsed.TotalMilliseconds;
            if (HasEveryColdCore(update.Items, rows)) allCore ??= timer.Elapsed.TotalMilliseconds;
            if (typeof(PremiereLoadProgress).GetProperty("IsCalendarReady")?.GetValue(update) is true)
                ready ??= timer.Elapsed.TotalMilliseconds;
            if (update.IsFinal) final = update;
        }
        Assert.NotNull(final);
        Assert.False(final.HasSourceErrors);
        Assert.Equal(rows.Select(row => row.Id).Order(), final.Items.Select(item => item.TmdbId).Order());
        Assert.NotNull(allCore);
        var elapsed = timer.Elapsed.TotalMilliseconds;
        var projection = ColdProjection(final.Items);
        var measurement = new
        {
            include_series = includeSeries, first_verified_ms = firstVerified, all_core_ms = allCore,
            ready_ms = ready, final_ms = elapsed, rows = final.Items.Count,
            discovery_http = fixture.Handler.Paths.Count(path => path.Contains("/discover/", StringComparison.Ordinal)),
            detail_http = fixture.Handler.Paths.Count(path => !path.Contains("/discover/", StringComparison.Ordinal)),
            omdb = fixture.Omdb.Calls, tomatoes = fixture.Tomatoes.Calls, artwork = fixture.Artwork.Calls,
            availability = fixture.Availability.Calls,
            projection_sha256 = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(projection)))
        };
        _evidence.WriteLine("COLD_DISCOVERY_METRICS " + JsonSerializer.Serialize(measurement));
        var directory = Environment.GetEnvironmentVariable("PREMIERE_FULL_WEEK_EVIDENCE_DIR");
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
            var name = includeSeries ? "cold_discovery_movies_series" : "cold_discovery_movies";
            await File.WriteAllTextAsync(Path.Combine(directory, name + ".projection.json"), projection);
            await File.WriteAllTextAsync(Path.Combine(directory, name + ".measurement.json"),
                JsonSerializer.Serialize(measurement, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ColdDiscovery_ConsumesEveryPageBeforeOptionalWorkAndOwnsCancellation(bool cancel)
    {
        var start = new DateOnly(2026, 11, 2);
        var rows = FullWeekRows(start, true);
        using var fixture = CreateColdFixture(rows, held: true);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var updates = new ConcurrentQueue<PremiereLoadProgress>();
        var allCore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = Task.Run(async () =>
        {
            await foreach (var update in fixture.Service.StreamCoreAsync(start, start.AddDays(6), filters: ColdFilters(true),
                cancellationToken: budget.Token))
            {
                updates.Enqueue(update);
                if (HasEveryColdCore(update.Items, rows)) allCore.TrySetResult();
            }
        });
        await fixture.Held!.Started.Task.WaitAsync(budget.Token);
        var accelerated = typeof(PremiereService).GetMethod("CanDiscoverCoreFirst", BindingFlags.Instance | BindingFlags.NonPublic) is not null;
        if (accelerated)
        {
            await allCore.Task.WaitAsync(budget.Token);
            Assert.Equal(32, fixture.Handler.Paths.Count(path => path.Contains("/discover/", StringComparison.Ordinal)));
            Assert.Contains(updates, update => HasEveryColdCore(update.Items, rows));
            Assert.DoesNotContain(updates, update => update.IsFinal || update.Phase == "complete");
            Assert.Empty(fixture.Cache.SetCalls);
            Assert.All(fixture.Store.Records.Values, record => Assert.NotNull(record.MetadataJson));
        }
        Assert.Equal(0, fixture.Held.Completed);
        if (cancel)
        {
            await budget.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(0, fixture.Held.Active);
            Assert.Empty(fixture.Cache.SetCalls);
            Assert.DoesNotContain(updates, update => update.Phase == "complete");
            return;
        }
        fixture.Held.Release.TrySetResult();
        await run.WaitAsync(budget.Token);
        var final = Assert.Single(updates, update => update.IsFinal);
        Assert.False(final.HasSourceErrors);
        Assert.Equal(16, updates.Where(update => !update.IsFinal && update.Phase == "complete")
            .Select(update => update.CheckpointKey).Distinct().Count());
        using var control = CreateColdFixture(rows, held: false);
        var expected = await control.Service.GetPremieresAsync(start, start.AddDays(6), budget.Token,
            forceRefresh: true, filters: ColdFilters(true));
        Assert.Equal(ColdProjection(expected), ColdProjection(final.Items));
        Assert.Equal(control.Handler.Paths.Order(), fixture.Handler.Paths.Order());
        Assert.Equal(control.Omdb.Calls, fixture.Omdb.Calls);
        Assert.Equal(control.Artwork.Calls, fixture.Artwork.Calls);
        Assert.Equal(control.Availability.Calls, fixture.Availability.Calls);
        Assert.Equal(control.Tomatoes.Calls, fixture.Held.Calls);
    }

    [Theory]
    [InlineData("force")]
    [InlineData("score")]
    [InlineData("source")]
    [InlineData("series_runtime")]
    [InlineData("keyword")]
    public async Task ColdDiscovery_RestrictiveProfilesRetainCompleteItemPath(string profile)
    {
        var start = new DateOnly(2026, 11, 2);
        var rows = FullWeekRows(start, true);
        using var fixture = CreateColdFixture(rows, held: true);
        var filters = ColdFilters(true);
        if (profile == "score") filters.ScoreSource = ScoreSource.Imdb;
        if (profile == "source") filters.SeriesFilters.SelectedSources = ["Fixture availability"];
        if (profile == "series_runtime") filters.SeriesFilters.RuntimeMinMinutes = 20;
        if (profile == "keyword") filters.KeywordText = "Fixture";
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var updates = new ConcurrentQueue<PremiereLoadProgress>();
        var run = Task.Run(async () =>
        {
            await foreach (var update in fixture.Service.StreamCoreAsync(start, start.AddDays(6), forceRefresh: profile == "force",
                filters: filters, cancellationToken: budget.Token)) updates.Enqueue(update);
        });
        await fixture.Held!.Started.Task.WaitAsync(budget.Token);
        Assert.True(fixture.Handler.Paths.Count(path => path.Contains("/discover/", StringComparison.Ordinal)) < 32);
        Assert.DoesNotContain(updates, update => typeof(PremiereLoadProgress).GetProperty("IsCalendarReady")?.GetValue(update) is true);
        fixture.Held.Release.TrySetResult();
        await run.WaitAsync(budget.Token);
        Assert.False(Assert.Single(updates, update => update.IsFinal).HasSourceErrors);
    }

    private static CalendarFilters ColdFilters(bool series) => new()
    {
        ShowSeries = series, MovieFilters = { OriginalLanguages = ["en", "nl"], RuntimeMinMinutes = 45 },
        SeriesFilters = { OriginalLanguages = ["en", "nl"], SeriesDateMode = SeriesDateMode.NewSeriesOnly }
    };

    [Theory]
    [InlineData(null, "TVmaze summary")]
    [InlineData("OMDb plot", "OMDb plot")]
    public async Task ColdDiscovery_SeriesContinuationPreservesSummaryPriorityAndCanonicalDate(string? plot, string expectedOverview)
    {
        var start = new DateOnly(2026, 11, 2);
        async Task<PremiereItem> RunAsync(bool force)
        {
            var tmdb = new FakeTmdbClient
            {
                TvItems = [new() { Id = 91, Name = "Summary show", FirstAirDate = "2026-11-03", OriginalLanguage = "en" }]
            };
            tmdb.SeasonOneEpisodeOneDatesById[91] = start;
            tmdb.TvDetailsById[91] = new TmdbDetailsWithExtras
            { Id = 91, Name = "Summary show", ExternalIds = new() { ImdbId = "tt0000091", TvdbId = 191 }, EpisodeRunTime = [] };
            var omdb = new FakeOmdbClient();
            omdb.ItemsByImdbId["tt0000091"] = new OmdbItem { Response = "True", Plot = plot };
            var tvmaze = new FakeTvmazeClient { LookupResult = new TvmazeShow
            { Id = 191, Name = "Summary show", Summary = "TVmaze summary", AverageRuntime = 48 } };
            var clock = new ItemCacheClock();
            var store = new MemoryItemEnrichmentStore(clock);
            var options = Microsoft.Extensions.Options.Options.Create(new TmdbOptions { BearerToken = "fixture" });
            var service = new PremiereService(tmdb, omdb, tvmaze, new FakeWatchmodeClient(), new FakeCalendarCache(),
                new TrailerSelector(), new RatingMapper(), [], [], options, NullLogger<PremiereService>.Instance,
                itemEnrichmentCache: ItemCacheCoordinator(store, clock));
            var item = Assert.Single(await service.GetPremieresAsync(start, start.AddDays(6), CancellationToken.None,
                forceRefresh: force, filters: new CalendarFilters { ShowMovies = false, SeriesFilters = { OriginalLanguages = ["en"] } }));
            var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!)!;
            Assert.True(metadata.FirstEpisodeDateChecked);
            Assert.Equal(start, metadata.FirstEpisodeDate);
            Assert.False(metadata.CoreMetadataOnly);
            Assert.True(metadata.RuntimeFromTvmaze);
            return item;
        }
        var candidate = await RunAsync(false);
        var baseline = await RunAsync(true);
        Assert.Equal(expectedOverview, candidate.Overview);
        Assert.Equal(48, candidate.RuntimeMinutes);
        Assert.Equal(start, candidate.PremiereDate);
        Assert.Equal(ColdProjection([baseline]), ColdProjection([candidate]));
    }

    [Fact]
    public async Task ColdDiscovery_RetainsExternalProvenanceAndFailureDeadlineAfterOptionalDrain()
    {
        var start = new DateOnly(2026, 11, 2);
        var row = new FullWeekRow(10_000, false, start, "en", Discoverable: false);
        var retry = DateTimeOffset.UtcNow.AddMinutes(2);
        var candidate = new ExternalPremiereCandidate(PremiereMediaType.Movie, start, row.Title, row.Id,
            $"tt{row.Id:0000000}", null, "Cold external", ExternalProviderId: "external-1",
            ExternalUrl: "https://fixture.invalid/external-1", SourceNames: ["Fixture channel"]);
        var provider = new ColdPartialExternalProvider(candidate, retry);
        using var fixture = CreateColdFixture([row], held: false, discovery: [provider]);
        var updates = new List<PremiereLoadProgress>();
        await foreach (var update in fixture.Service.StreamCoreAsync(start, start, filters: ColdFilters(false))) updates.Add(update);
        var final = Assert.Single(updates, update => update.IsFinal);
        Assert.True(final.HasSourceErrors);
        Assert.Equal(retry, final.RetryAfterUtc);
        var item = Assert.Single(final.Items);
        Assert.Equal("external-1", item.ExternalProviderId);
        Assert.Equal("https://fixture.invalid/external-1", item.ExternalUrl);
        Assert.Equal(81, item.RottenTomatoesScore);
        Assert.Contains(item.Sources, source => source.Kind == "schedule" && source.Name == "Fixture channel");
        using var control = CreateColdFixture([row], held: false,
            discovery: [new ColdPartialExternalProvider(candidate, retry)]);
        var expected = await control.Service.GetPremieresAsync(start, start, CancellationToken.None,
            forceRefresh: true, filters: ColdFilters(false));
        Assert.Equal(ColdProjection(expected), ColdProjection(final.Items));
        Assert.Contains(item.MergeContributions, contribution => contribution.Source == "Fixture channel");
        Assert.DoesNotContain(updates, update => update.ProviderKey == "cold-external" && update.Phase == "complete" && !update.HasSourceErrors);
        Assert.True(provider.Disposed);
    }

    [Fact]
    public async Task ColdDiscovery_NewerCanonicalGenerationWinsWhileOptionalContinuationIsHeld()
    {
        if (typeof(PremiereService).GetMethod("CanDiscoverCoreFirst", BindingFlags.Instance | BindingFlags.NonPublic) is null) return;
        var start = new DateOnly(2026, 11, 2);
        var rows = FullWeekRows(start, true);
        using var fixture = CreateColdFixture(rows, held: true);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var updates = new ConcurrentQueue<PremiereLoadProgress>();
        var allCore = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = Task.Run(async () =>
        {
            await foreach (var update in fixture.Service.StreamCoreAsync(start, start.AddDays(6), filters: ColdFilters(true),
                cancellationToken: budget.Token))
            {
                updates.Enqueue(update);
                // Only the readiness update proves the readiness flush and probe have
                // finished; the last source's own progress update precedes them.
                if (update.IsCalendarReady && HasEveryColdCore(update.Items, rows)) allCore.TrySetResult();
            }
        });
        await fixture.Held!.Started.Task.WaitAsync(budget.Token);
        await allCore.Task.WaitAsync(budget.Token);
        // Readiness has been emitted, so the foreign writer below cannot race the
        // readiness probe: it moves a premiere out of this week afterwards and the
        // final reconcile must still classify the load as dirty.
        var key = Assert.Single(fixture.Store.Records.Keys, key => key.Identity.TmdbId == 20_000);
        var core = fixture.Store.Records[key];
        var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(core.MetadataJson!)!;
        var foreignClock = core.MetadataFetchedUtc!.Value.AddMinutes(1);
        var foreign = core with
        {
            Generation = core.Generation + 1, MetadataRevision = core.MetadataRevision + 1,
            MetadataFetchedUtc = foreignClock, MetadataInvalidatedUtc = foreignClock,
            MetadataJson = JsonSerializer.Serialize(metadata with
            { Title = "Foreign canonical repair", FirstEpisodeDateChecked = true, FirstEpisodeDate = start.AddDays(-7) })
        };
        fixture.Store.Records[key] = foreign;
        fixture.Held.Release.TrySetResult();
        await run.WaitAsync(budget.Token);
        var final = Assert.Single(updates, update => update.IsFinal);
        Assert.True(final.HasSourceErrors);
        Assert.False((bool?)typeof(PremiereLoadProgress).GetProperty("IsCalendarReady")?.GetValue(final) ?? false);
        Assert.DoesNotContain(final.Items, item => item.TmdbId == 20_000);
        Assert.Equal(foreign.Generation, fixture.Store.Records[key].Generation);
        Assert.Equal(foreign.MetadataJson, fixture.Store.Records[key].MetadataJson);
    }

    [Fact]
    public async Task ColdDiscovery_FailedDetailsRetainCompleteOptionalBehaviorWithoutRepeatingTheRequest()
    {
        var start = new DateOnly(2026, 11, 2);
        async Task<(PremiereItem Item, int Details, int Tomatoes, int Artwork, int Availability)> RunAsync(bool force)
        {
            var tmdb = new FakeTmdbClient
            {
                MovieItems = [new() { Id = 77, Title = "Failed details", ReleaseDate = "2026-11-02", OriginalLanguage = "en" }],
                MovieDetailExceptions = new() { [77] = new ExternalApiException("Fixture detail outage") }
            };
            var options = Microsoft.Extensions.Options.Options.Create(new TmdbOptions { BearerToken = "fixture" });
            var clock = new ItemCacheClock();
            var store = new MemoryItemEnrichmentStore(clock);
            using var tomatoes = new FullWeekTomatoes();
            using var artwork = new FullWeekArtwork();
            using var availability = new FullWeekAvailability();
            var service = new PremiereService(tmdb, new FakeOmdbClient(), new FakeTvmazeClient(), availability,
                new FakeCalendarCache(), new TrailerSelector(), new RatingMapper(), [artwork], [], options,
                NullLogger<PremiereService>.Instance, rottenTomatoesClient: tomatoes,
                itemEnrichmentCache: ItemCacheCoordinator(store, clock));
            var item = Assert.Single(await service.GetPremieresAsync(start, start, CancellationToken.None,
                forceRefresh: force, filters: new CalendarFilters { ShowSeries = false }));
            return (item, tmdb.MovieDetailCalls.Count, tomatoes.Calls, artwork.Calls, availability.Calls);
        }
        var candidate = await RunAsync(false);
        var control = await RunAsync(true);
        Assert.Equal(1, candidate.Details);
        Assert.Equal(control.Details, candidate.Details);
        Assert.Equal(control.Tomatoes, candidate.Tomatoes);
        Assert.Equal(control.Artwork, candidate.Artwork);
        Assert.Equal(control.Availability, candidate.Availability);
        Assert.Equal(ColdProjection([control.Item]), ColdProjection([candidate.Item]));
    }

    [Fact]
    public async Task ColdDiscovery_QueuedOptionalSourceDoesNotSpendItsInactivityBudgetBeforeAdmission()
    {
        var start = new DateOnly(2026, 11, 2);
        var availability = new ColdSlowAvailability();
        var tmdb = new FakeTmdbClient
        {
            FilterMovieItemsByDate = true,
            MovieItems = Enumerable.Range(1, 4).Select(id => new TmdbMovieDiscoverItem
            { Id = id, Title = $"Queued {id}", ReleaseDate = start.AddDays(id == 4 ? 1 : 0).ToString("yyyy-MM-dd"), OriginalLanguage = "en" }).ToArray(),
            MovieDetailOverride = async (id, token) =>
            {
                if (id == 4) await availability.Started.Task.WaitAsync(token);
                return new TmdbDetailsWithExtras { Id = id, Runtime = 110, OriginalLanguage = "en" };
            }
        };
        var clock = new ItemCacheClock();
        var options = Microsoft.Extensions.Options.Options.Create(new TmdbOptions
        // Three serialized 800 ms mappings (2.4 s of queue wait) still exceed the 2 s
        // inactivity budget, so a charged queue wait would fail; each single operation
        // keeps a 2.5x margin below that budget under CPU starvation.
        { BearerToken = "fixture", MaxEnrichmentConcurrency = 1, SourceFetchConcurrency = 2, SourceTimeoutSeconds = 2 });
        var service = new PremiereService(tmdb, new FakeOmdbClient(), new FakeTvmazeClient(), availability,
            new FakeCalendarCache(), new TrailerSelector(), new RatingMapper(), [], [], options, NullLogger<PremiereService>.Instance,
            itemEnrichmentCache: ItemCacheCoordinator(new MemoryItemEnrichmentStore(clock), clock));
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var updates = new List<PremiereLoadProgress>();
        await foreach (var update in service.StreamCoreAsync(start, start.AddDays(1),
            filters: new CalendarFilters { ShowSeries = false, MovieFilters = { OriginalLanguages = ["en"] } }, cancellationToken: budget.Token))
            updates.Add(update);
        var final = Assert.Single(updates, update => update.IsFinal);
        Assert.False(final.HasSourceErrors);
        Assert.Equal([1, 2, 3, 4], final.Items.Select(item => item.TmdbId).Order());
        Assert.Equal(1, availability.Maximum);
        Assert.Equal([1, 2, 3, 4], availability.Completed.ToArray());
    }

    [Fact]
    public async Task ColdDiscovery_CompletionClockPausesOnlyWhenAllOwnMappingsAwaitAdmission()
    {
        var sourceType = typeof(PremiereService).GetNestedType("ColdDiscoverySource", BindingFlags.NonPublic);
        if (sourceType is null) return;
        using var gate = new SemaphoreSlim(1);
        var source = Activator.CreateInstance(sourceType, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
            binder: null, args: [gate], culture: null)!;
        void Call(string method, params object[] arguments) => sourceType.GetMethod(method)!.Invoke(source, arguments);
        using var deadline = new CancellationTokenSource();
        using var tracking = (IDisposable)sourceType.GetMethod("TrackCompletionDeadline")!
            .Invoke(source, [deadline, TimeSpan.FromMilliseconds(500)])!;
        Call("OptionalWaiting");
        Call("OptionalAdmitted");
        Call("OptionalWaiting");
        Call("OptionalFinished", true);
        // This is the later-admission gap: one mapping already completed, and
        // this source now has only queued work while other sources own permits.
        await Task.Delay(1000);
        Assert.False(deadline.IsCancellationRequested);
        Call("OptionalAdmitted");
        Call("OptionalWaiting");
        // A waiting sibling must not mask this source's own hung active work.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await Task.Delay(Timeout.InfiniteTimeSpan, deadline.Token).WaitAsync(TimeSpan.FromSeconds(3)));
        Call("OptionalFinished", true);
        Call("OptionalFinished", false);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ColdDiscovery_ExistingSeriesPreservesTvmazeAndArtworkFailureEvidence(bool removeTmdbPoster)
    {
        var start = new DateOnly(2026, 11, 2);
        var tmdb = new FakeTmdbClient
        {
            TvItems = [new() { Id = 91, Name = "Preserved show", FirstAirDate = "2026-11-02", OriginalLanguage = "en" }]
        };
        tmdb.SeasonOneEpisodeOneDatesById[91] = start;
        TmdbDetailsWithExtras Details(string? poster) => new()
        {
            Id = 91, Name = "Preserved show", EpisodeRunTime = [], PosterPath = poster,
            ExternalIds = new() { ImdbId = "tt0000091", TvdbId = 191 }
        };
        tmdb.TvDetailsById[91] = Details(removeTmdbPoster ? "/old-poster.jpg" : null);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var options = Microsoft.Extensions.Options.Options.Create(new TmdbOptions { BearerToken = "fixture" });
        PremiereService Service(FakeTvmazeClient tvmaze) => new(tmdb, new FakeOmdbClient(), tvmaze,
            new FakeWatchmodeClient(), new FakeCalendarCache(), new TrailerSelector(), new RatingMapper(),
            [new ThrowingArtworkProvider()], [], options, NullLogger<PremiereService>.Instance, itemEnrichmentCache: coordinator);
        var filters = new CalendarFilters { ShowMovies = false, SeriesFilters = { OriginalLanguages = ["en"] } };
        var first = Service(new FakeTvmazeClient { LookupResult = new()
        {
            Id = 191, Name = "Preserved show", Summary = "Prior TVmaze summary", AverageRuntime = 48,
            Network = new() { Name = "Prior network" }, WebChannel = new() { Name = "Prior web channel" },
            Rating = new() { Average = 8.1 }, OfficialSite = "https://fixture.invalid/official",
            Url = "https://www.tvmaze.com/shows/191/preserved", Image = new() { Original = "https://fixture.invalid/tvmaze.jpg" }
        } });
        var prime = Assert.Single(await first.GetPremieresAsync(start, start, CancellationToken.None, forceRefresh: true, filters: filters));
        var before = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!)!;
        clock.Advance(TimeSpan.FromMinutes(1));
        await store.InvalidateTmdbAsync(PremiereMediaType.Series, [91], clock.GetUtcNow(), default);
        tmdb.TvDetailsById[91] = Details(null);
        var refreshed = Assert.Single(await Service(new FakeTvmazeClient { ThrowOnLookup = true, ThrowOnSearch = true })
            .GetPremieresAsync(start, start, CancellationToken.None, filters: filters));
        var after = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!)!;
        Assert.Equal(prime.NetworkName, refreshed.NetworkName);
        Assert.Equal(prime.WebChannelName, refreshed.WebChannelName);
        Assert.Equal(prime.TvmazeAverageRuntimeMinutes, refreshed.TvmazeAverageRuntimeMinutes);
        Assert.Equal(prime.TvmazeRating, refreshed.TvmazeRating);
        Assert.Equal(prime.OfficialSiteUrl, refreshed.OfficialSiteUrl);
        Assert.Equal(prime.TvmazeUrl, refreshed.TvmazeUrl);
        Assert.Equal(prime.Overview, refreshed.Overview);
        Assert.Equal(prime.RuntimeMinutes, refreshed.RuntimeMinutes);
        Assert.Equal(before.TvmazeFetchedUtc, after.TvmazeFetchedUtc);
        Assert.False(after.TvmazeComplete);
        Assert.Equal(before.ArtworkFetchedUtc, after.ArtworkFetchedUtc);
        if (removeTmdbPoster)
        {
            Assert.Equal("TMDb poster", prime.ImageSource);
            Assert.Equal(prime.PosterUrl, refreshed.PosterUrl);
            Assert.True(after.PosterOwnerVerifiedEmpty);
            Assert.False(after.ArtworkComplete);
        }
    }

    private static bool HasEveryColdCore(IReadOnlyList<PremiereItem> items, FullWeekRow[] rows)
        => items.Count == rows.Length && items.All(item => item.RuntimeMinutes is > 0)
            && items.Select(item => item.TmdbId).Order().SequenceEqual(rows.Select(row => row.Id).Order());

    private static string ColdProjection(IReadOnlyList<PremiereItem> items)
        => JsonSerializer.Serialize(items.OrderBy(item => item.TmdbId)
            .Select(item => FullWeekNormalize(item) with { EnrichmentVersion = null }));

    private ColdFixture CreateColdFixture(FullWeekRow[] rows, bool held, IReadOnlyList<IPremiereDiscoveryProvider>? discovery = null)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new TmdbOptions
        { BearerToken = "local-fixture", SourceRegions = ["US"], SourceFetchConcurrency = 12 });
        var memory = new MemoryCache(new MemoryCacheOptions());
        var handler = new FullWeekHttpHandler(rows, paginate: true);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://full-week.invalid/3/") };
        var limiter = new TmdbRequestLimiter(options);
        var tmdb = new TmdbClient(http, memory, options, NullLogger<TmdbClient>.Instance, requestLimiter: limiter);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var omdb = new FullWeekOmdb();
        var tomatoes = new FullWeekTomatoes();
        var heldTomatoes = held ? new FullWeekHeldTomatoes() : null;
        var availability = new FullWeekAvailability();
        var artwork = new FullWeekArtwork();
        var cache = new FakeCalendarCache();
        var service = new PremiereService(tmdb, omdb, new FakeTvmazeClient(), availability, cache,
            new TrailerSelector(), new RatingMapper(), [artwork], discovery ?? [], options, NullLogger<PremiereService>.Instance,
            rottenTomatoesClient: (IRottenTomatoesClient?)heldTomatoes ?? tomatoes,
            itemEnrichmentCache: ItemCacheCoordinator(store, clock));
        return new(service, handler, http, memory, limiter, omdb, tomatoes, heldTomatoes, availability, artwork, cache, store);
    }

    private sealed class ColdPartialExternalProvider(ExternalPremiereCandidate candidate, DateTimeOffset retry)
        : INamedPremiereDiscoveryProvider, IStreamingPremiereDiscoveryProvider
    {
        public string DisplayName => "Cold external";
        public bool PreserveBatchBoundaries => true;
        public bool Disposed { get; private set; }
        public Task<IReadOnlyList<ExternalPremiereCandidate>> GetCandidatesAsync(DateOnly start, DateOnly end,
            CancellationToken cancellationToken, bool forceRefresh = false) => throw new NotSupportedException();
        public async IAsyncEnumerable<IReadOnlyList<ExternalPremiereCandidate>> StreamCandidatesAsync(DateOnly start, DateOnly end,
            bool forceRefresh = false, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try { yield return [candidate]; await Task.Yield(); throw new ProviderRetryAfterException("fixture", retry); }
            finally { Disposed = true; }
        }
    }

    private sealed class ColdSlowAvailability : IWatchmodeClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<int> Completed { get; } = new();
        private int _active;
        public int Maximum;
        public async Task<IReadOnlyList<PremiereSource>> GetTitleSourcesAsync(PremiereMediaType type, int tmdbId, string? imdbId,
            IReadOnlyList<string> regions, CancellationToken token, bool forceRefresh = false)
        {
            FullWeekSetMaximum(ref Maximum, Interlocked.Increment(ref _active));
            Started.TrySetResult();
            try
            {
                if (tmdbId < 4) await Task.Delay(800, token);
                Completed.Enqueue(tmdbId);
                return [new PremiereSource { Id = 31, Name = "Fixture availability", Kind = "flatrate" }];
            }
            finally { Interlocked.Decrement(ref _active); }
        }
        public Task<IReadOnlyList<ExternalPremiereCandidate>> GetReleaseCandidatesAsync(DateOnly start, DateOnly end,
            CancellationToken token, bool forceRefresh = false) => Task.FromResult<IReadOnlyList<ExternalPremiereCandidate>>([]);
    }

    private sealed record ColdFixture(PremiereService Service, FullWeekHttpHandler Handler, HttpClient Http,
        MemoryCache Memory, TmdbRequestLimiter Limiter, FullWeekOmdb Omdb, FullWeekTomatoes Tomatoes,
        FullWeekHeldTomatoes? Held, FullWeekAvailability Availability, FullWeekArtwork Artwork,
        FakeCalendarCache Cache, MemoryItemEnrichmentStore Store) : IDisposable
    {
        public void Dispose()
        {
            Http.Dispose(); Memory.Dispose(); Limiter.Dispose(); Omdb.Dispose(); Tomatoes.Dispose();
            Held?.Dispose(); Availability.Dispose(); Artwork.Dispose();
        }
    }
}
