using PremiereCalendar.Models;
using PremiereCalendar.Services;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace PremiereCalendar.UnitTests;

public sealed partial class PremiereServiceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ComponentWriteDiagnosticsRejectEqualPayloadWithDifferentGenerationOrNewerClock(bool generationChanged)
    {
        var path = Path.Combine(Path.GetTempPath(), "PremiereCalendar-cache-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            using var diagnostics = new PerformanceDiagnosticStore(new(), path);
            var telemetry = new PremiereTelemetry(diagnostics);
            var date = new DateOnly(2026, 11, 2);
            var clock = new ItemCacheClock();
            var store = new MemoryItemEnrichmentStore(clock);
            var coordinator = new ItemEnrichmentCacheCoordinator(store,
                Microsoft.Extensions.Options.Options.Create(new PremiereCalendar.Options.ItemEnrichmentCacheOptions()),
                Microsoft.Extensions.Options.Options.Create(new PremiereCalendar.Options.TmdbOptions()),
                NullLogger<ItemEnrichmentCacheCoordinator>.Instance, timeProvider: clock, telemetry: telemetry);
            var item = ItemCacheLegacyItem(date, clock);
            await coordinator.ImportAsync([item], CancellationToken.None);
            clock.Advance(TimeSpan.FromHours(1));
            var identity = new ItemEnrichmentIdentity(item.MediaType, item.TmdbId);
            var session = await coordinator.CreateSessionAsync(CancellationToken.None);
            await session.PrepareAsync([identity], null, CancellationToken.None);
            await session.GetAsync(identity, true, (previous, _, _, _) =>
                Task.FromResult(new ItemEnrichmentRefresh(previous.Metadata, previous.Ratings)), CancellationToken.None);
            store.BeforeWrite = () =>
            {
                var pair = Assert.Single(store.Records);
                store.Records[pair.Key] = pair.Value with
                {
                    Generation = pair.Value.Generation + (generationChanged ? 1 : 0),
                    MetadataFetchedUtc = clock.GetUtcNow().AddHours(generationChanged ? 0 : 1),
                    RatingsFetchedUtc = clock.GetUtcNow().AddHours(generationChanged ? 0 : 1)
                };
            };
            await session.FlushAsync(CancellationToken.None, rejectGenerationConflicts: false);
            telemetry.FlushDiagnostics();
            static string ReadActiveFile(string file)
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            var events = Directory.GetFiles(Path.Combine(path, "App_Data", "logs", "performance"), "*.ndjson")
                .SelectMany(file => ReadActiveFile(file).Split('\n', StringSplitOptions.RemoveEmptyEntries)).Select(line => JsonSerializer.Deserialize<JsonElement>(line))
                .Where(document => document.GetProperty("event").GetString() == "item_cache.write.component")
                .Select(document => document.GetProperty("details")).ToArray();
            var rejected = events.Where(details => details.GetProperty("outcome").GetString() == "concurrency_rejected").ToArray();
            Assert.Equal(2, rejected.Length);
            Assert.All(rejected, details => Assert.Equal(generationChanged ? "metadata_generation_changed" : "component_clock_or_revision_changed",
                details.GetProperty("reason").GetString()));
        }
        finally { Directory.Delete(path, recursive: true); }
    }

    [Fact]
    public async Task LegacyAggregateScoresAreNotInventedAsDedicatedTomatoesProvenanceWhenProviderIsAbsent()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        await coordinator.ImportAsync([ItemCacheLegacyItem(date, clock) with { RottenTomatoesScore = 83 }], CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(25));
        var omdb = new FakeOmdbClient();
        omdb.ItemsByImdbId["tt0000901"] = new OmdbItem { Response = "True" };
        var item = Assert.Single(await CreateService(ItemCacheMovie(date), omdb: omdb, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: new CalendarFilters { ShowSeries = false }));
        Assert.Null(item.ImdbScore);
        Assert.Null(item.RottenTomatoesScore);
    }

    [Fact]
    public async Task ChangedExternalIdentityPreservesOnlyIndependentlyMatchedTomatoesValues()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        await CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"),
            rottenTomatoes: new ComponentTomatoesClient { Result = new(83, 91) }, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: new CalendarFilters { ShowSeries = false });
        clock.Advance(TimeSpan.FromHours(1));
        await store.InvalidateTmdbAsync(PremiereMediaType.Movie, [901], clock.GetUtcNow(), CancellationToken.None);
        var changed = ItemCacheMovie(date);
        changed.MovieDetailsById[901] = changed.MovieDetailsById[901] with
            { ExternalIds = new TmdbExternalIds { ImdbId = "tt0000902", WikidataId = "Q901" } };
        var tomatoes = new ComponentTomatoesClient { Successful = false };
        var result = Assert.Single(await CreateService(changed, omdb: new FakeOmdbClient { ThrowOnGet = true },
            rottenTomatoes: tomatoes, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: new CalendarFilters { ShowSeries = false }));
        Assert.Null(result.ImdbScore);
        Assert.Equal(83, result.RottenTomatoesScore);
        Assert.Equal(0, tomatoes.Calls);
    }

    [Fact]
    public async Task FailedExternalProviderNeverAcquiresPhantomTomatoesValuesFromCombinedProjection()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var cache = new FakeCalendarCache();
        var failed = new FakeOmdbClient { ThrowOnGet = true };
        var tomatoes = new ComponentTomatoesClient { Result = new(83, 91) };
        var filters = new CalendarFilters { ShowSeries = false };
        await CreateService(ItemCacheMovie(date), omdb: failed, rottenTomatoes: tomatoes, calendarCache: cache, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: filters);
        clock.Advance(TimeSpan.FromHours(25));
        tomatoes.Result = new(94, 96);
        await CreateService(new FakeTmdbClient(), omdb: failed, rottenTomatoes: tomatoes, calendarCache: cache, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: filters);
        clock.Advance(TimeSpan.FromHours(25));
        tomatoes.Result = RottenTomatoesScores.Empty;
        var result = Assert.Single(await CreateService(new FakeTmdbClient(), omdb: failed, rottenTomatoes: tomatoes,
            calendarCache: cache, itemEnrichmentCache: coordinator).GetPremieresAsync(date, date, CancellationToken.None, filters: filters));
        Assert.Null(result.RottenTomatoesScore);
        Assert.Null(JsonSerializer.Deserialize<ItemEnrichmentRatings>(Assert.Single(store.Records.Values).RatingsJson!)?.ExternalValues);
    }

    [Fact]
    public async Task OptionalRetryKeepsRecoveredTvmazeImageBeforeEmptyArtworkFallback()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        FakeTmdbClient Tmdb()
        {
            var tmdb = new FakeTmdbClient { TvItems = [new TmdbTvDiscoverItem { Id = 901, Name = "Recovering artwork", FirstAirDate = date.ToString("yyyy-MM-dd") }] };
            tmdb.TvDetailsById[901] = new TmdbDetailsWithExtras { Id = 901, ExternalIds = new TmdbExternalIds { ImdbId = "tt0000901" } };
            tmdb.SeasonOneEpisodeOneDatesById[901] = date;
            return tmdb;
        }
        var filters = new CalendarFilters { ShowMovies = false };
        await CreateService(Tmdb(), tvmaze: new FakeTvmazeClient { ThrowOnLookup = true, ThrowOnSearch = true },
            omdb: ItemCacheOmdb("7.1"), artworkProviders: [new ThrowingArtworkProvider()], itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: filters);
        var before = Assert.Single(store.Records.Values).MetadataFetchedUtc;
        clock.Advance(TimeSpan.FromMinutes(6));
        var artwork = new RecordingArtworkProvider(returnCandidate: false);
        var tmdbRetry = Tmdb();
        var result = Assert.Single(await CreateService(tmdbRetry, tvmaze: new FakeTvmazeClient { LookupResult = new TvmazeShow
            { Id = 91, Image = new TvmazeImage { Original = "https://tvmaze.example/recovered.jpg" } } },
            artworkProviders: [artwork], itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: filters));
        Assert.Equal("https://tvmaze.example/recovered.jpg", result.PosterUrl);
        Assert.Equal(ArtworkSources.TvmazeImage, result.ImageSource);
        Assert.Empty(artwork.Calls);
        Assert.Empty(tmdbRetry.TvDetailCalls);
        Assert.Equal(before, Assert.Single(store.Records.Values).MetadataFetchedUtc);
    }

    [Fact]
    public async Task ChangedTitleFailureRetriesTomatoesFromCompleteWeekDespiteFreshImdbClock()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var cache = new FakeCalendarCache();
        var filters = new CalendarFilters { ShowSeries = false };
        await CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"),
            rottenTomatoes: new ComponentTomatoesClient { Result = new(83, 91) }, calendarCache: cache, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: filters);
        clock.Advance(TimeSpan.FromHours(1));
        await store.InvalidateTmdbAsync(PremiereMediaType.Movie, [901], clock.GetUtcNow(), CancellationToken.None);
        var original = ItemCacheMovie(date);
        var changed = new FakeTmdbClient { MovieItems = [original.MovieItems.Single() with { Title = "Corrected movie title" }] };
        changed.MovieDetailsById[901] = original.MovieDetailsById[901] with { Title = "Corrected movie title" };
        var tomatoes = new ComponentTomatoesClient { Successful = false };
        await CreateService(changed, rottenTomatoes: tomatoes, calendarCache: cache, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: filters);
        Assert.Equal(2, cache.SetCalls.Count);
        clock.Advance(TimeSpan.FromMinutes(6));
        var omdb = new FakeOmdbClient { ThrowOnGet = true };
        var item = Assert.Single(await CreateService(new FakeTmdbClient(), omdb: omdb, rottenTomatoes: tomatoes,
            calendarCache: cache, itemEnrichmentCache: coordinator).GetPremieresAsync(date, date, CancellationToken.None, filters: filters));
        Assert.Equal("Corrected movie title", item.Title);
        Assert.Equal(7.1, item.ImdbScore);
        Assert.Null(item.RottenTomatoesScore);
        Assert.Equal(2, tomatoes.Calls);
        Assert.Empty(omdb.Calls);
    }

    [Fact]
    public async Task ChangedIdentityFailureDoesNotBorrowOldProvidersFreshnessClock()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        await CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"),
            rottenTomatoes: new ComponentTomatoesClient { Result = new(83, 91) }, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: new CalendarFilters { ShowSeries = false });
        clock.Advance(TimeSpan.FromHours(1));
        await store.InvalidateTmdbAsync(PremiereMediaType.Movie, [901], clock.GetUtcNow(), CancellationToken.None);
        var changed = ItemCacheMovie(date.AddDays(1));
        changed.MovieDetailsById[901] = changed.MovieDetailsById[901] with
            { ExternalIds = new TmdbExternalIds { ImdbId = "tt0000902", WikidataId = "Q902" } };
        var failed = new FakeOmdbClient { ThrowOnGet = true };
        var tomatoes = new ComponentTomatoesClient { Result = new(94, 96) };
        var first = Assert.Single(await CreateService(changed, omdb: failed, rottenTomatoes: tomatoes, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date.AddDays(1), date.AddDays(1), CancellationToken.None, filters: new CalendarFilters { ShowSeries = false }));
        Assert.Null(first.ImdbScore);
        Assert.Equal(94, first.RottenTomatoesScore);
        var record = Assert.Single(store.Records.Values);
        Assert.Null(System.Text.Json.JsonSerializer.Deserialize<ItemEnrichmentRatings>(record.RatingsJson!)?.ExternalFetchedUtc);
        clock.Advance(TimeSpan.FromMinutes(6));
        await CreateService(changed, omdb: failed, rottenTomatoes: tomatoes, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date.AddDays(1), date.AddDays(1), CancellationToken.None, filters: new CalendarFilters { ShowSeries = false });
        Assert.Equal(2, failed.Calls.Count);
        Assert.Equal(1, tomatoes.Calls);
    }

    [Fact]
    public async Task EarlyInvalidationForcesOptionalProvidersAndReplacesFallbackArtwork()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var oldArtwork = new ForceRecordingArtwork("https://art.example/old.jpg");
        var filters = new CalendarFilters { ShowSeries = false };
        await CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"), artworkProviders: [oldArtwork], itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: filters);
        clock.Advance(TimeSpan.FromHours(1));
        await store.InvalidateTmdbAsync(PremiereMediaType.Movie, [901], clock.GetUtcNow(), CancellationToken.None);
        var artwork = new ForceRecordingArtwork("https://art.example/new.jpg");
        var watchmode = new FakeWatchmodeClient { Sources = [new PremiereSource { Name = "New streaming", Kind = "flatrate" }] };

        var result = Assert.Single(await CreateService(ItemCacheMovie(date.AddDays(1)), watchmode: watchmode,
            artworkProviders: [artwork], itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date.AddDays(1), date.AddDays(1), CancellationToken.None, filters: filters));

        Assert.Equal("https://art.example/new.jpg", result.PosterUrl);
        Assert.True(Assert.Single(artwork.Forced));
        Assert.True(Assert.Single(watchmode.SourceCalls).ForceRefresh);
        Assert.Contains("New streaming", result.SourceNames);
    }

    [Fact]
    public async Task PreviouslyInvalidatedSeriesMetadataOnlyMergeRetainsDiscoveryReservationAndPublishesComplete()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var cache = new FakeCalendarCache();
        FakeTmdbClient Tmdb()
        {
            var client = new FakeTmdbClient
            {
                TvItems = [new TmdbTvDiscoverItem { Id = 901, Name = "Unscored premiere", FirstAirDate = date.ToString("yyyy-MM-dd") }]
            };
            client.TvDetailsById[901] = new TmdbDetailsWithExtras { Id = 901 };
            client.SeasonOneEpisodeOneDatesById[901] = date;
            return client;
        }
        var filters = new CalendarFilters { ShowMovies = false };
        await CreateService(Tmdb(), calendarCache: cache, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: filters);
        clock.Advance(TimeSpan.FromMinutes(1));
        await store.InvalidateTmdbAsync(PremiereMediaType.Series, [901], clock.GetUtcNow(), CancellationToken.None);
        var updates = new List<PremiereLoadProgress>();
        await foreach (var update in CreateService(Tmdb(), calendarCache: cache, itemEnrichmentCache: coordinator)
            .StreamCoreAsync(date, date, filters: filters)) updates.Add(update);

        var final = Assert.Single(updates, update => update.IsFinal);
        Assert.False(final.HasSourceErrors);
        Assert.Equal(clock.GetUtcNow(), Assert.Single(final.Items).EnrichmentVersion?.MetadataInvalidatedUtc);
        Assert.Equal(2, cache.SetCalls.Count);
        var unused = new FakeTmdbClient();
        await CreateService(unused, calendarCache: cache, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: filters);
        Assert.Empty(unused.TvCalls);
        Assert.Empty(unused.TvDetailCalls);
    }

    [Fact]
    public async Task VerifiedEmptyRatingsClearWithdrawnValuesAndCachedFileOverlay()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var cache = new FakeCalendarCache();
        var tomatoes = new ComponentTomatoesClient { Result = new(83, 91) };
        var filters = new CalendarFilters { ShowSeries = false };
        await CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"), rottenTomatoes: tomatoes,
            calendarCache: cache, itemEnrichmentCache: coordinator).GetPremieresAsync(date, date, CancellationToken.None, filters: filters);
        clock.Advance(TimeSpan.FromHours(25));
        tomatoes.Result = RottenTomatoesScores.Empty;
        var empty = ItemCacheOmdb("N/A");
        empty.ItemsByImdbId["tt0000901"] = new OmdbItem { Response = "True", ImdbRating = "N/A" };
        var result = Assert.Single(await CreateService(new FakeTmdbClient(), omdb: empty, rottenTomatoes: tomatoes,
            calendarCache: cache, itemEnrichmentCache: coordinator).GetPremieresAsync(date, date, CancellationToken.None, filters: filters));

        Assert.Null(result.ImdbScore);
        Assert.Null(result.ImdbVoteCount);
        Assert.Null(result.RottenTomatoesScore);
        Assert.Null(result.RottenTomatoesAudienceScore);
        Assert.Equal(clock.GetUtcNow(), Assert.Single(store.Records.Values).RatingsFetchedUtc);
        var cached = Assert.Single(await CreateService(new FakeTmdbClient(), omdb: new FakeOmdbClient { ThrowOnGet = true },
            rottenTomatoes: tomatoes, calendarCache: cache, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: filters));
        Assert.Null(cached.ImdbScore);
        Assert.Null(cached.RottenTomatoesScore);
        Assert.Equal(2, tomatoes.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SuccessfulRatingsProviderUpdatesAndIsReusedWhileOtherProviderRetries(bool externalFails)
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var cache = new FakeCalendarCache();
        var initialTomatoes = new ComponentTomatoesClient { Result = new(83, 91) };
        var filters = new CalendarFilters { ShowSeries = false };
        var initialOmdb = ItemCacheOmdb("7.1");
        initialOmdb.ItemsByImdbId["tt0000901"] = initialOmdb.ItemsByImdbId["tt0000901"] with
            { Ratings = [new OmdbRating { Source = "Rotten Tomatoes", Value = "83%" }] };
        await CreateService(ItemCacheMovie(date), omdb: initialOmdb, rottenTomatoes: initialTomatoes,
            calendarCache: cache, itemEnrichmentCache: coordinator).GetPremieresAsync(date, date, CancellationToken.None, filters: filters);
        clock.Advance(TimeSpan.FromHours(25));
        var omdb = externalFails ? new FakeOmdbClient { ThrowOnGet = true } : ItemCacheOmdb("8.4");
        var tomatoes = new ComponentTomatoesClient { Result = new(94, 96), Successful = externalFails };
        for (var visit = 0; visit < 2; visit++)
        {
            var result = Assert.Single(await CreateService(new FakeTmdbClient(), omdb: omdb, rottenTomatoes: tomatoes,
                calendarCache: cache, itemEnrichmentCache: coordinator).GetPremieresAsync(date, date, CancellationToken.None, filters: filters));
            Assert.Equal(externalFails ? 7.1 : 8.4, result.ImdbScore);
            Assert.Equal(externalFails ? 94 : 83, result.RottenTomatoesScore);
            clock.Advance(TimeSpan.FromMinutes(6));
        }
        Assert.Equal(externalFails ? 2 : 1, omdb.Calls.Count);
        Assert.Equal(externalFails ? 1 : 2, tomatoes.Calls);
    }

    private sealed class ComponentTomatoesClient : IRottenTomatoesClient
    {
        public RottenTomatoesScores Result = RottenTomatoesScores.Empty;
        public bool Successful = true;
        public int Calls;
        public Task<RottenTomatoesScores> GetScoresAsync(PremiereMediaType mediaType, string title, int? year,
            string? wikidataId, CancellationToken cancellationToken, bool forceRefresh = false)
            => Task.FromResult(Result);
        public Task<ProviderRefreshResult<RottenTomatoesScores>> GetScoresWithStatusAsync(PremiereMediaType mediaType,
            string title, int? year, string? wikidataId, CancellationToken cancellationToken, bool forceRefresh = false)
        {
            Calls++;
            return Task.FromResult(new ProviderRefreshResult<RottenTomatoesScores>(Result, Successful));
        }
    }

    private sealed class ForceRecordingArtwork(string url) : IArtworkProvider
    {
        public List<bool> Forced { get; } = [];
        public Task<ArtworkCandidate?> GetArtworkAsync(ArtworkRequest request, CancellationToken cancellationToken, bool forceRefresh = false)
        {
            Forced.Add(forceRefresh);
            return Task.FromResult<ArtworkCandidate?>(new(url, "Test artwork"));
        }
    }
}
