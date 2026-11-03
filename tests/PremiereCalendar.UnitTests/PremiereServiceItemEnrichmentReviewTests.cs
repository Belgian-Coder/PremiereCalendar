using System.Text.Json;
using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class PremiereServiceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DisabledOmdbDoesNotFreezeTheEnabledImdbDataset(bool initiallyAvailable)
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var cache = new FakeCalendarCache();
        var dataset = new FakeImdbRatingsStore();
        if (initiallyAvailable) dataset.Items["tt0000901"] = new("tt0000901", 7.1, 1000, clock.GetUtcNow());
        var disabled = new DisabledExternalClient();
        var filters = new CalendarFilters { ShowSeries = false };
        await CreateService(ItemCacheMovie(date), omdb: disabled, imdbRatingsStore: dataset,
            calendarCache: cache, itemEnrichmentCache: coordinator).GetPremieresAsync(date, date, CancellationToken.None, filters: filters);
        var before = JsonSerializer.Deserialize<ItemEnrichmentRatings>(Assert.Single(store.Records.Values).RatingsJson!)!;
        Assert.False(before.ExternalRequired);
        Assert.True(before.DatasetRequired);
        Assert.Equal(clock.GetUtcNow(), before.Dataset?.FetchedUtc);
        Assert.Equal(initiallyAvailable ? 7.1 : (double?)null, before.Dataset?.ImdbScore);
        clock.Advance(TimeSpan.FromHours(25));
        dataset.Items["tt0000901"] = new("tt0000901", 8.4, 2000, clock.GetUtcNow());
        var result = Assert.Single(await CreateService(new FakeTmdbClient(), omdb: disabled, imdbRatingsStore: dataset,
            calendarCache: cache, itemEnrichmentCache: coordinator).GetPremieresAsync(date, date, CancellationToken.None, filters: filters));
        Assert.Equal(8.4, result.ImdbScore);
        Assert.Equal(2000, result.ImdbVoteCount);
        var after = JsonSerializer.Deserialize<ItemEnrichmentRatings>(Assert.Single(store.Records.Values).RatingsJson!);
        Assert.False(after?.ExternalRequired);
        Assert.Null(after?.ExternalFetchedUtc);
        Assert.True(after?.DatasetRequired);
        Assert.Equal(clock.GetUtcNow(), after?.Dataset?.FetchedUtc);
        Assert.Equal(1, disabled.Calls);
    }

    [Fact]
    public async Task DatasetRefreshSurvivesOmdbFailureWithoutFresheningItsPayloadOrClock()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var cache = new FakeCalendarCache();
        var dataset = new FakeImdbRatingsStore();
        dataset.Items["tt0000901"] = new("tt0000901", 7.1, 1000, clock.GetUtcNow());
        var filters = new CalendarFilters { ShowSeries = false };
        await CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("6.8"), imdbRatingsStore: dataset,
            calendarCache: cache, itemEnrichmentCache: coordinator).GetPremieresAsync(date, date, CancellationToken.None, filters: filters);
        var before = JsonSerializer.Deserialize<ItemEnrichmentRatings>(Assert.Single(store.Records.Values).RatingsJson!)!;
        clock.Advance(TimeSpan.FromHours(25));
        dataset.Items["tt0000901"] = new("tt0000901", 8.4, 2000, clock.GetUtcNow());
        var failed = new FakeOmdbClient { ThrowOnGet = true };
        var result = Assert.Single(await CreateService(new FakeTmdbClient(), omdb: failed, imdbRatingsStore: dataset,
            calendarCache: cache, itemEnrichmentCache: coordinator).GetPremieresAsync(date, date, CancellationToken.None, filters: filters));
        Assert.Equal(8.4, result.ImdbScore);
        var after = JsonSerializer.Deserialize<ItemEnrichmentRatings>(Assert.Single(store.Records.Values).RatingsJson!)!;
        Assert.Equal(6.8, after.ExternalValues?.ImdbScore);
        Assert.Equal(before.ExternalFetchedUtc, after.ExternalFetchedUtc);
        Assert.Equal(clock.GetUtcNow(), after.Dataset?.FetchedUtc);
        clock.Advance(TimeSpan.FromMinutes(6));
        await CreateService(new FakeTmdbClient(), omdb: failed, imdbRatingsStore: dataset,
            calendarCache: cache, itemEnrichmentCache: coordinator).GetPremieresAsync(date, date, CancellationToken.None, filters: filters);
        Assert.Equal(2, failed.Calls.Count);
        Assert.Equal(after.Dataset, JsonSerializer.Deserialize<ItemEnrichmentRatings>(Assert.Single(store.Records.Values).RatingsJson!)?.Dataset);
    }

    [Fact]
    public async Task OldNotRequiredTrackedRowsDiscoverTheDatasetWithoutNetworkWork()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var item = ItemCacheLegacyItem(date, clock);
        await coordinator.ImportAsync([item], CancellationToken.None);
        var pair = Assert.Single(store.Records);
        var old = ItemEnrichmentRatings.FromItem(item) with
        {
            ProviderFreshnessTracked = true, ExternalRequired = false, TomatoesRequired = false,
            ExternalValues = new(ImdbScore: 7.1), ExternalFetchedUtc = item.LastUpdatedUtc
        };
        store.Records[pair.Key] = pair.Value with { RatingsJson = JsonSerializer.Serialize(old) };
        var dataset = new FakeImdbRatingsStore();
        dataset.Items[item.ImdbId!] = new(item.ImdbId!, 9.1, 3000, clock.GetUtcNow());
        var disabled = new DisabledExternalClient();
        var result = Assert.Single(await CreateService(new FakeTmdbClient(), omdb: disabled, imdbRatingsStore: dataset,
            calendarCache: new FakeCalendarCache { Items = [item] }, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: new CalendarFilters { ShowSeries = false }));
        Assert.Equal(9.1, result.ImdbScore);
        Assert.Equal(0, disabled.Calls);
        Assert.True(JsonSerializer.Deserialize<ItemEnrichmentRatings>(Assert.Single(store.Records.Values).RatingsJson!)?.DatasetRequired);
    }

    [Fact]
    public async Task OptionalOnlyRetryCannotBypassLongerRatingsCooldownWithoutMatchingRatings()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock, cacheOptions: new PremiereCalendar.Options.ItemEnrichmentCacheOptions
            { MetadataFailureRetryMinutes = 1, RatingsFailureRetryMinutes = 10 });
        var external = new FakeOmdbClient { ThrowOnGet = true };
        var tomatoes = new ComponentTomatoesClient { Successful = false };
        var watchmode = new FakeWatchmodeClient { SourcesOutcomeSuccessful = false };
        var filters = new CalendarFilters { ShowSeries = false };
        await CreateService(ItemCacheMovie(date), omdb: external, rottenTomatoes: tomatoes, watchmode: watchmode, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: filters);
        Assert.Single(external.Calls);
        Assert.Equal(1, tomatoes.Calls);
        clock.Advance(TimeSpan.FromMinutes(2));
        await CreateService(ItemCacheMovie(date.AddDays(1)), omdb: external, rottenTomatoes: tomatoes, watchmode: watchmode, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date.AddDays(1), date.AddDays(1), CancellationToken.None, filters: filters);
        Assert.Equal(2, watchmode.SourceCalls.Count);
        Assert.Single(external.Calls);
        Assert.Equal(1, tomatoes.Calls);
        clock.Advance(TimeSpan.FromMinutes(9));
        await CreateService(ItemCacheMovie(date.AddDays(2)), omdb: external, rottenTomatoes: tomatoes, watchmode: watchmode, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date.AddDays(2), date.AddDays(2), CancellationToken.None, filters: filters);
        Assert.Equal(2, external.Calls.Count);
        Assert.Equal(2, tomatoes.Calls);
    }

    private sealed class DisabledExternalClient : IOmdbClient
    {
        public int Calls;
        public Task<OmdbItem?> GetByImdbIdAsync(string imdbId, CancellationToken cancellationToken, bool forceRefresh = false)
            => Task.FromResult<OmdbItem?>(null);
        public Task<ProviderRefreshResult<OmdbItem?>> GetByImdbIdWithStatusAsync(string imdbId, CancellationToken cancellationToken, bool forceRefresh = false)
        {
            Calls++;
            return Task.FromResult(new ProviderRefreshResult<OmdbItem?>(null, true, new("disabled")));
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NewerDedicatedTomatoesWithdrawalDoesNotResurrectOlderExternalProviderScore(bool verifiedEmpty)
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var cache = new FakeCalendarCache();
        var filters = new CalendarFilters { ShowSeries = false };
        var external = ItemCacheOmdb("7.1");
        external.ItemsByImdbId["tt0000901"] = external.ItemsByImdbId["tt0000901"] with
            { Ratings = [new OmdbRating { Source = "Rotten Tomatoes", Value = "80%" }] };
        await CreateService(ItemCacheMovie(date), omdb: external,
            rottenTomatoes: new ComponentTomatoesClient { Result = new(90, 91) }, calendarCache: cache, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: filters);
        clock.Advance(TimeSpan.FromHours(25));
        var result = Assert.Single(await CreateService(new FakeTmdbClient(), omdb: new FakeOmdbClient { ThrowOnGet = true },
            rottenTomatoes: new ComponentTomatoesClient { Successful = verifiedEmpty }, calendarCache: cache, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: filters));
        Assert.Equal(verifiedEmpty ? (int?)null : 80, result.RottenTomatoesScore);
        var ratings = JsonSerializer.Deserialize<ItemEnrichmentRatings>(Assert.Single(store.Records.Values).RatingsJson!);
        Assert.Equal(80, ratings?.ExternalValues?.RottenTomatoesScore);
        if (verifiedEmpty)
        {
            Assert.NotNull(ratings?.TomatoesValues);
            Assert.Null(ratings.TomatoesValues.RottenTomatoesScore);
            Assert.Equal(clock.GetUtcNow(), ratings.TomatoesFetchedUtc);
        }
    }

    [Fact]
    public async Task SkippedTomatoesProviderKeepsItsOwnClockWhenExternalRatingsRefresh()
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
        var before = JsonSerializer.Deserialize<ItemEnrichmentRatings>(Assert.Single(store.Records.Values).RatingsJson!)!;
        clock.Advance(TimeSpan.FromHours(25));
        var result = Assert.Single(await CreateService(new FakeTmdbClient(), omdb: ItemCacheOmdb("8.4"),
            rottenTomatoes: new NotApplicableTomatoesClient(), calendarCache: cache, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: filters));
        var after = JsonSerializer.Deserialize<ItemEnrichmentRatings>(Assert.Single(store.Records.Values).RatingsJson!);
        Assert.Equal(8.4, result.ImdbScore);
        Assert.Equal(83, result.RottenTomatoesScore);
        Assert.Equal(clock.GetUtcNow(), after?.ExternalFetchedUtc);
        Assert.Equal(before.TomatoesFetchedUtc, after?.TomatoesFetchedUtc);
        Assert.False(after?.TomatoesRequired);
    }

    [Fact]
    public async Task NotApplicableTomatoesDecisionIsRetainedWhileExternalRatingsAreReused()
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
        var changed = new FakeTmdbClient { MovieItems = [original.MovieItems.Single() with { Title = "電影" }] };
        changed.MovieDetailsById[901] = original.MovieDetailsById[901] with { Title = "電影" };
        var tomatoes = new NotApplicableTomatoesClient();
        await CreateService(changed, rottenTomatoes: tomatoes, calendarCache: cache, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: filters);
        var before = tomatoes.Calls;
        var omdb = new FakeOmdbClient { ThrowOnGet = true };
        await CreateService(new FakeTmdbClient(), omdb: omdb, rottenTomatoes: tomatoes, calendarCache: cache, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: filters);
        Assert.Equal(before, tomatoes.Calls);
        Assert.Empty(omdb.Calls);
        Assert.False(JsonSerializer.Deserialize<ItemEnrichmentRatings>(Assert.Single(store.Records.Values).RatingsJson!)?.TomatoesRequired);
    }

    private sealed class NotApplicableTomatoesClient : IRottenTomatoesClient
    {
        public int Calls;
        public Task<RottenTomatoesScores> GetScoresAsync(PremiereMediaType mediaType, string title, int? year, string? wikidataId,
            CancellationToken cancellationToken, bool forceRefresh = false) => Task.FromResult(RottenTomatoesScores.Empty);
        public Task<ProviderRefreshResult<RottenTomatoesScores>> GetScoresWithStatusAsync(PremiereMediaType mediaType, string title,
            int? year, string? wikidataId, CancellationToken cancellationToken, bool forceRefresh = false)
        {
            Calls++;
            return Task.FromResult(new ProviderRefreshResult<RottenTomatoesScores>(RottenTomatoesScores.Empty, true, new("not_applicable")));
        }
    }

    [Fact]
    public async Task OptionalRecoveryCannotClearUnrelatedCoreFailureCooldown()
    {
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var item = ItemCacheLegacyItem(new(2026, 11, 2), clock);
        await coordinator.ImportAsync([item], CancellationToken.None);
        clock.Advance(TimeSpan.FromDays(8));
        var identity = new ItemEnrichmentIdentity(item.MediaType, item.TmdbId);
        var failed = await coordinator.CreateSessionAsync(CancellationToken.None);
        await failed.PrepareAsync([identity], null, CancellationToken.None);
        await failed.GetAsync(identity, false, (_, metadataDue, _, _) =>
        {
            Assert.True(metadataDue);
            return Task.FromResult(new ItemEnrichmentRefresh(null, null, MetadataFailed: true));
        }, CancellationToken.None);
        var recovered = await coordinator.CreateSessionAsync(CancellationToken.None);
        await recovered.PrepareAsync([identity], null, CancellationToken.None);
        await recovered.GetAsync(identity, false, (previous, metadataDue, _, _) =>
        {
            Assert.False(metadataDue);
            return Task.FromResult(new ItemEnrichmentRefresh(previous.Metadata! with { AvailabilityComplete = true }, null,
                RefreshMetadataClock: false));
        }, CancellationToken.None);
        await recovered.FlushAsync(CancellationToken.None);
        var check = await coordinator.CreateSessionAsync(CancellationToken.None);
        await check.PrepareAsync([identity], null, CancellationToken.None);
        Assert.False(check.MetadataRefreshNeeded(identity));
        clock.Advance(TimeSpan.FromMinutes(6));
        Assert.True(check.MetadataRefreshNeeded(identity));
    }

    [Fact]
    public async Task FailedArtworkRetainsItsSuccessClockWhileCoreMetadataCorrectionIsAccepted()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var filters = new CalendarFilters { ShowSeries = false };
        await CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"),
            artworkProviders: [new ForceRecordingArtwork("https://art.example/last-good.jpg")], itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: filters);
        var before = Assert.Single(store.Records.Values);
        var oldMetadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(before.MetadataJson!)!;
        clock.Advance(TimeSpan.FromDays(8));
        var changed = ItemCacheMovie(date);
        changed.MovieDetailsById[901] = changed.MovieDetailsById[901] with { Runtime = 144 };
        var result = Assert.Single(await CreateService(changed, omdb: ItemCacheOmdb("8.4"),
            artworkProviders: [new ThrowingArtworkProvider()], itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: filters));

        var after = Assert.Single(store.Records.Values);
        var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(after.MetadataJson!);
        Assert.Equal(144, result.RuntimeMinutes);
        Assert.Equal("https://art.example/last-good.jpg", result.PosterUrl);
        Assert.Equal(clock.GetUtcNow(), after.MetadataFetchedUtc);
        Assert.Equal(oldMetadata.ArtworkFetchedUtc, metadata?.ArtworkFetchedUtc);
        Assert.False(metadata?.ArtworkComplete);
        Assert.Equal(before.MetadataFetchedUtc, result.LastUpdatedUtc);
    }

    [Fact]
    public async Task AcceptedClearingDiagnosticsComeFromRealWritesAndDisambiguateMovieAndSeriesIds()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PremiereCalendar-clearing-audit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var diagnostics = new PerformanceDiagnosticStore(new(), directory);
            var telemetry = new PremiereTelemetry(diagnostics);
            var clock = new ItemCacheClock();
            var store = new MemoryItemEnrichmentStore(clock);
            var coordinator = ItemCacheCoordinator(store, clock, telemetry: telemetry);
            var movie = ItemCacheLegacyItem(new(2026, 11, 2), clock) with { RottenTomatoesScore = 83 };
            var series = movie with { CanonicalId = "tv:901", MediaType = PremiereMediaType.Series, Type = PremiereItemType.SeriesPremiere };
            await coordinator.ImportAsync([movie, series], CancellationToken.None);
            clock.Advance(TimeSpan.FromHours(1));
            var session = await coordinator.CreateSessionAsync(CancellationToken.None);
            var identities = new[] { movie, series }.Select(item => new ItemEnrichmentIdentity(item.MediaType, item.TmdbId)).ToArray();
            await session.PrepareAsync(identities, null, CancellationToken.None);
            foreach (var identity in identities)
                await session.GetAsync(identity, true, (previous, _, _, _) => Task.FromResult(new ItemEnrichmentRefresh(null,
                    previous.Ratings! with { ImdbScore = null, RottenTomatoesScore = null }, RefreshMetadataClock: false)), CancellationToken.None);
            await session.FlushAsync(CancellationToken.None);
            telemetry.FlushDiagnostics();
            var events = new List<JsonElement>();
            foreach (var file in Directory.GetFiles(Path.Combine(directory, "App_Data", "logs", "performance"), "*.ndjson"))
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                events.AddRange(reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonSerializer.Deserialize<JsonElement>(line)));
            }
            var cleared = events.Where(item => item.GetProperty("event").GetString() == "item_cache.result"
                && item.GetProperty("details").GetProperty("outcome").GetString() == "cleared")
                .Select(item => item.GetProperty("details")).ToArray();
            Assert.Equal(2, cleared.Length);
            Assert.Equal(new[] { "Movie", "Series" }, cleared.Select(item => item.GetProperty("mediaType").GetString()).OrderBy(value => value));
            Assert.All(cleared, item =>
            {
                Assert.Equal("accepted_projection", item.GetProperty("reason").GetString());
                Assert.Equal(2, item.GetProperty("count").GetInt32());
                Assert.True(item.GetProperty("durationMilliseconds").GetDouble() >= 0);
            });
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task MetadataOnlyInvalidationKeepsCachedOmdbPosterAndPlotWithoutRatingsRequest()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        FakeTmdbClient Tmdb()
        {
            var client = ItemCacheMovie(date);
            client.MovieDetailsById[901] = client.MovieDetailsById[901] with { Overview = null };
            return client;
        }
        var omdb = ItemCacheOmdb("7.1");
        omdb.ItemsByImdbId["tt0000901"] = omdb.ItemsByImdbId["tt0000901"] with
            { Poster = "https://omdb.example/poster.jpg", Plot = "Plot from OMDb." };
        var filters = new CalendarFilters { ShowSeries = false };
        await CreateService(Tmdb(), omdb: omdb, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: filters);
        clock.Advance(TimeSpan.FromHours(1));
        await store.InvalidateTmdbAsync(PremiereMediaType.Movie, [901], clock.GetUtcNow(), CancellationToken.None);

        var result = Assert.Single(await CreateService(Tmdb(), omdb: omdb, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: filters));

        Assert.Equal("https://omdb.example/poster.jpg", result.PosterUrl);
        Assert.Equal("Plot from OMDb.", result.Overview);
        Assert.Equal("OMDb poster", result.ImageSource);
        Assert.Single(omdb.Calls);
        var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!);
        Assert.Equal(result.PosterUrl, metadata?.OmdbPosterUrl);
        Assert.Equal(result.Overview, metadata?.OmdbPlot);
    }

    [Fact]
    public async Task ForcedProjectionSynchronizationKeepsFailedAndSuccessfulProviderClocksSeparate()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var filters = new CalendarFilters { ShowSeries = false };
        await CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"),
            rottenTomatoes: new ComponentTomatoesClient { Result = new(83, 91) }, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: filters);
        var before = JsonSerializer.Deserialize<ItemEnrichmentRatings>(Assert.Single(store.Records.Values).RatingsJson!)!;
        clock.Advance(TimeSpan.FromHours(25));
        var failed = new FakeOmdbClient { ThrowOnGet = true };
        var tomatoes = new ComponentTomatoesClient { Result = new(94, 96) };

        var result = Assert.Single(await CreateService(ItemCacheMovie(date), omdb: failed, rottenTomatoes: tomatoes, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, forceRefresh: true, filters: filters));

        var ratings = JsonSerializer.Deserialize<ItemEnrichmentRatings>(Assert.Single(store.Records.Values).RatingsJson!);
        Assert.True(ratings?.ProviderFreshnessTracked);
        Assert.Equal(before.ExternalFetchedUtc, ratings?.ExternalFetchedUtc);
        Assert.Equal(clock.GetUtcNow(), ratings?.TomatoesFetchedUtc);
        Assert.Equal(94, ratings?.TomatoesValues?.RottenTomatoesScore);
        Assert.Equal(before.ExternalFetchedUtc, result.LastUpdatedUtc);
        clock.Advance(TimeSpan.FromMinutes(6));
        await CreateService(ItemCacheMovie(date), omdb: failed, rottenTomatoes: tomatoes, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: filters);
        Assert.Equal(2, failed.Calls.Count);
        Assert.Equal(1, tomatoes.Calls);
    }

    [Fact]
    public async Task UnverifiedMaintenanceRatingsAreActuallyRefetchedDespiteRecentProviderClocks()
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
        clock.Advance(TimeSpan.FromMinutes(1));
        var pair = Assert.Single(store.Records);
        var unverified = JsonSerializer.Deserialize<ItemEnrichmentRatings>(pair.Value.RatingsJson!)! with { FreshnessVerified = false };
        store.Records[pair.Key] = pair.Value with { RatingsJson = JsonSerializer.Serialize(unverified) };
        var omdb = ItemCacheOmdb("8.4");
        var tomatoes = new ComponentTomatoesClient { Result = new(94, 96) };

        var result = Assert.Single(await CreateService(new FakeTmdbClient(), omdb: omdb, rottenTomatoes: tomatoes,
            calendarCache: cache, itemEnrichmentCache: coordinator).GetPremieresAsync(date, date, CancellationToken.None, filters: filters));

        Assert.Equal(8.4, result.ImdbScore);
        Assert.Equal(94, result.RottenTomatoesScore);
        Assert.Single(omdb.Calls);
        Assert.Equal(1, tomatoes.Calls);
        Assert.True(JsonSerializer.Deserialize<ItemEnrichmentRatings>(Assert.Single(store.Records.Values).RatingsJson!)?.FreshnessVerified);
    }

    [Fact]
    public async Task RatingsDueCannotBypassAvailabilityFailureCooldown()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        await coordinator.ImportAsync([ItemCacheLegacyItem(date, clock)], CancellationToken.None);
        var pair = Assert.Single(store.Records);
        store.Records[pair.Key] = pair.Value with { RatingsFetchedUtc = clock.GetUtcNow().AddHours(-24).AddMinutes(1) };
        var watchmode = new FakeWatchmodeClient { SourcesOutcomeSuccessful = false };
        var omdb = ItemCacheOmdb("8.4");
        var filters = new CalendarFilters { ShowSeries = false };
        await CreateService(ItemCacheMovie(date), omdb: omdb, watchmode: watchmode, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: filters);
        clock.Advance(TimeSpan.FromMinutes(2));

        await CreateService(ItemCacheMovie(date.AddDays(1)), omdb: omdb, watchmode: watchmode, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date.AddDays(1), date.AddDays(1), CancellationToken.None, filters: filters);

        Assert.Single(watchmode.SourceCalls);
        Assert.Single(omdb.Calls);
        Assert.False(JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!)?.AvailabilityComplete);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewInvalidationRefreshesCoreMetadataDuringPreviousFailureCooldown(bool coreFailed)
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var filters = new CalendarFilters { ShowSeries = false };
        await CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"), itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: filters);
        clock.Advance(TimeSpan.FromDays(8));
        var failed = ItemCacheMovie(date);
        if (coreFailed) failed.MovieDetailExceptions[901] = new ExternalApiException("TMDb unavailable.");
        await CreateService(failed, omdb: ItemCacheOmdb("7.1"),
            watchmode: new FakeWatchmodeClient { SourcesOutcomeSuccessful = false }, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: filters);
        clock.Advance(TimeSpan.FromMinutes(1));
        await store.InvalidateTmdbAsync(PremiereMediaType.Movie, [901], clock.GetUtcNow(), CancellationToken.None);
        var corrected = ItemCacheMovie(date.AddDays(1));
        corrected.MovieDetailsById[901] = corrected.MovieDetailsById[901] with { Runtime = 144 };

        var result = Assert.Single(await CreateService(corrected, omdb: ItemCacheOmdb("7.1"), itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date.AddDays(1), date.AddDays(1), CancellationToken.None, filters: filters));

        Assert.Equal(144, result.RuntimeMinutes);
        Assert.True(Assert.Single(corrected.MovieDetailCalls).ForceRefresh);
        Assert.Equal(clock.GetUtcNow(), Assert.Single(store.Records.Values).MetadataFetchedUtc);
    }

    [Fact]
    public async Task ForcedRefreshDoesNotJoinNormalRefreshWithDifferentProviderPolicy()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var item = ItemCacheLegacyItem(date, clock);
        await coordinator.ImportAsync([item], CancellationToken.None);
        clock.Advance(TimeSpan.FromDays(8));
        var identity = new ItemEnrichmentIdentity(item.MediaType, item.TmdbId);
        var normal = await coordinator.CreateSessionAsync(CancellationToken.None);
        var forced = await coordinator.CreateSessionAsync(CancellationToken.None);
        await normal.PrepareAsync([identity], null, CancellationToken.None);
        await forced.PrepareAsync([identity], null, CancellationToken.None);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var forcedEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var normalTask = normal.GetAsync(identity, false, async (previous, _, _, _) =>
        {
            entered.SetResult();
            await release.Task;
            return new(previous.Metadata, previous.Ratings);
        }, CancellationToken.None);
        await entered.Task;
        var forcedTask = forced.GetAsync(identity, true, (previous, _, _, _) =>
        {
            forcedEntered.SetResult();
            return Task.FromResult(new ItemEnrichmentRefresh(previous.Metadata, previous.Ratings));
        }, CancellationToken.None);
        try { await forcedEntered.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
        finally { release.TrySetResult(); }
        await Task.WhenAll(normalTask, forcedTask);
    }

    [Fact]
    public async Task VerifiedEmptyTomatoesClearsLegacyAggregateWhileExternalProviderFails()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        await coordinator.ImportAsync([ItemCacheLegacyItem(date, clock) with { RottenTomatoesScore = 83 }], CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(25));
        var result = Assert.Single(await CreateService(ItemCacheMovie(date), omdb: new FakeOmdbClient { ThrowOnGet = true },
            rottenTomatoes: new ComponentTomatoesClient(), itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: new CalendarFilters { ShowSeries = false }));
        Assert.Equal(7.1, result.ImdbScore);
        Assert.Null(result.RottenTomatoesScore);
    }
}
