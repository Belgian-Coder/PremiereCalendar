using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class PremiereServiceTests
{
    [Fact]
    public async Task ForeignVoteOnlyRevisionPreservesFreshDiscoveryFieldsAcrossCachedFileSessions()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var old = ItemCacheLegacyItem(date, clock) with
        {
            Title = "Old global title", Overview = "Old global overview", PosterUrl = "https://old.example/poster.jpg",
            TmdbScore = 6.5, TmdbVoteCount = 100
        };
        await coordinator.ImportAsync([old], CancellationToken.None);
        var metadataClock = Assert.Single(store.Records.Values).MetadataFetchedUtc;
        var identity = new ItemEnrichmentIdentity(old.MediaType, old.TmdbId);
        var sessionA = await coordinator.CreateSessionAsync(CancellationToken.None);
        await sessionA.PrepareAsync([identity], null, CancellationToken.None);
        var fresh = old with { Title = "Fresh discovery title", Overview = "Fresh discovery overview",
            PosterUrl = "https://fresh.example/poster.jpg", TmdbScore = 7, TmdbVoteCount = 150 };
        var first = await sessionA.GetAsync(identity, false, (_, _, _, _) => Task.FromResult(new ItemEnrichmentRefresh(null, null)),
            CancellationToken.None, ratingsInputs: fresh);
        await sessionA.FlushAsync(CancellationToken.None);
        var firstReconciliation = await sessionA.ReconcileAsync([first.Apply(fresh)], true, CancellationToken.None);
        var cachedFile = JsonSerializer.Deserialize<PremiereItem>(JsonSerializer.Serialize(Assert.Single(firstReconciliation.Items)))!;
        clock.Advance(TimeSpan.FromHours(1));
        var sessionB = await coordinator.CreateSessionAsync(CancellationToken.None);
        await sessionB.PrepareAsync([identity], null, CancellationToken.None);
        await sessionB.GetAsync(identity, false, (_, _, _, _) => Task.FromResult(new ItemEnrichmentRefresh(null, null)),
            CancellationToken.None, ratingsInputs: old with { TmdbScore = 8, TmdbVoteCount = 200 });
        await sessionB.FlushAsync(CancellationToken.None);
        var sessionC = await coordinator.CreateSessionAsync(CancellationToken.None);
        await sessionC.PrepareAsync([identity], null, CancellationToken.None);

        var final = await sessionC.ReconcileAsync([cachedFile], false, CancellationToken.None);

        var item = Assert.Single(final.Items);
        Assert.Empty(final.DirtyMedia);
        Assert.Equal("Fresh discovery title", item.Title);
        Assert.Equal("Fresh discovery overview", item.Overview);
        Assert.Equal("https://fresh.example/poster.jpg", item.PosterUrl);
        Assert.Equal(8, item.TmdbScore);
        Assert.Equal(200, item.TmdbVoteCount);
        Assert.Equal(metadataClock, item.EnrichmentVersion?.MetadataFetchedUtc);
        Assert.True(item.EnrichmentVersion?.MetadataRevision > cachedFile.EnrichmentVersion?.MetadataRevision);
        Assert.Equal("Old global title", JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!)?.Title);
    }

    [Fact]
    public async Task MaintenanceMergesDifferentScoreDeltasAcrossOccurrencesAndHidesUnknownUiClock()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var first = ItemCacheLegacyItem(date, clock) with
        {
            CanonicalId = PremiereIdentity.SeriesEpisodeCanonicalId(901, date, 1, 1), MediaType = PremiereMediaType.Series,
            Type = PremiereItemType.SeriesEpisode, ImdbScore = null, SeasonNumber = 1, EpisodeNumber = 1
        };
        var second = first with { CanonicalId = PremiereIdentity.SeriesEpisodeCanonicalId(901, date.AddDays(7), 1, 2), PremiereDate = date.AddDays(7), EpisodeNumber = 2 };
        await coordinator.ImportAsync([first, second], CancellationToken.None);
        var metadataClock = Assert.Single(store.Records.Values).MetadataFetchedUtc;

        await coordinator.SynchronizeMaintainedProjectionAsync([first, second],
            [first with { ImdbScore = 8.8 }, second with { MetacriticScore = 88 }], CalendarCacheMaintenanceKind.Scores, CancellationToken.None);

        var record = Assert.Single(store.Records.Values);
        var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(record.MetadataJson!);
        var ratings = JsonSerializer.Deserialize<ItemEnrichmentRatings>(record.RatingsJson!);
        Assert.Equal(8.8, ratings?.ImdbScore);
        Assert.Equal(88, ratings?.MetacriticScore);
        Assert.Equal(DateTimeOffset.MinValue, record.RatingsFetchedUtc);
        Assert.False(ratings?.FreshnessVerified);
        var snapshot = new ItemEnrichmentSnapshot(record, metadata, ratings);
        Assert.Equal(metadataClock, snapshot.Apply(first).LastUpdatedUtc);
        Assert.Equal(metadataClock, snapshot.Rebase(first, metadataChanged: true).LastUpdatedUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MaintenanceRetriesUnrelatedRevisionRaceButPreservesAChangedTargetField(bool targetChanged)
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var original = ItemCacheLegacyItem(date, clock) with { ImdbId = null, ImdbScore = null };
        await coordinator.ImportAsync([original], CancellationToken.None);
        var before = Assert.Single(store.Records.Values);
        store.BeforeWrite = () =>
        {
            var pair = Assert.Single(store.Records);
            var concurrent = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(pair.Value.MetadataJson!)! with
            {
                Title = "Concurrent title", PosterUrl = "https://concurrent.example/poster.jpg",
                ImdbId = targetChanged ? "tt0000999" : null,
                ImdbUrl = targetChanged ? "https://www.imdb.com/title/tt0000999/" : null
            };
            store.Records[pair.Key] = pair.Value with { MetadataJson = JsonSerializer.Serialize(concurrent), MetadataRevision = pair.Value.MetadataRevision + 1 };
        };
        var published = original with { ImdbId = "tt0000901", ImdbUrl = "https://www.imdb.com/title/tt0000901/" };

        await coordinator.SynchronizeMaintainedProjectionAsync([original], [published], CalendarCacheMaintenanceKind.ExternalIds, CancellationToken.None);

        var record = Assert.Single(store.Records.Values);
        var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(record.MetadataJson!);
        Assert.Equal("Concurrent title", metadata?.Title);
        Assert.Equal("https://concurrent.example/poster.jpg", metadata?.PosterUrl);
        Assert.Equal(targetChanged ? "tt0000999" : "tt0000901", metadata?.ImdbId);
        Assert.Equal(targetChanged ? "https://www.imdb.com/title/tt0000999/" : published.ImdbUrl, metadata?.ImdbUrl);
        Assert.Equal(before.MetadataFetchedUtc, record.MetadataFetchedUtc);
        Assert.Equal(before.RatingsFetchedUtc, record.RatingsFetchedUtc);
    }

    [Fact]
    public async Task ExternalIdMaintenancePreservesCurrentMetadataAndClearsOldIdentityRatings()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var original = ItemCacheLegacyItem(date, clock) with { PosterUrl = "https://old.example/poster.jpg" };
        await coordinator.ImportAsync([original], CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(1));
        var pair = Assert.Single(store.Records);
        var currentMetadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(pair.Value.MetadataJson!)! with
            { Title = "Current database title", PosterUrl = "https://current.example/poster.jpg" };
        store.Records[pair.Key] = pair.Value with { MetadataJson = JsonSerializer.Serialize(currentMetadata),
            MetadataFetchedUtc = clock.GetUtcNow(), MetadataRevision = pair.Value.MetadataRevision + 1 };
        var published = original with { ImdbId = "tt0000902", ImdbUrl = "https://www.imdb.com/title/tt0000902/", TvdbId = 92 };

        await coordinator.SynchronizeMaintainedProjectionAsync([original], [published], CalendarCacheMaintenanceKind.ExternalIds, CancellationToken.None);

        var updated = Assert.Single(store.Records.Values);
        var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(updated.MetadataJson!);
        var ratings = JsonSerializer.Deserialize<ItemEnrichmentRatings>(updated.RatingsJson!);
        Assert.Equal("Current database title", metadata?.Title);
        Assert.Equal("https://current.example/poster.jpg", metadata?.PosterUrl);
        Assert.Equal("tt0000902", metadata?.ImdbId);
        Assert.Equal(92, metadata?.TvdbId);
        Assert.Equal(clock.GetUtcNow(), updated.MetadataFetchedUtc);
        Assert.Equal(pair.Value.RatingsFetchedUtc, updated.RatingsFetchedUtc);
        Assert.Null(ratings?.ImdbScore);
        Assert.Equal("tt0000902", ratings?.ImdbId);
        Assert.False(ratings?.FreshnessVerified);
    }

    [Fact]
    public async Task FailedRatingsPreserveGoodScoresAndRetryOnlyAfterSeparateCooldown()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var cache = new FakeCalendarCache();
        await CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"), calendarCache: cache, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: new CalendarFilters { ShowSeries = false });
        var before = Assert.Single(store.Records.Values);
        clock.Advance(TimeSpan.FromHours(25));
        var failed = new FakeOmdbClient { ThrowOnGet = true };
        for (var visit = 0; visit < 2; visit++)
        {
            var result = Assert.Single(await CreateService(new FakeTmdbClient(), omdb: failed, calendarCache: cache, itemEnrichmentCache: coordinator)
                .GetPremieresAsync(date, date, CancellationToken.None, filters: new CalendarFilters { ShowSeries = false }));
            Assert.Equal(7.1, result.ImdbScore);
        }
        Assert.Single(failed.Calls);
        clock.Advance(TimeSpan.FromMinutes(6));
        await CreateService(new FakeTmdbClient(), omdb: failed, calendarCache: cache, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: new CalendarFilters { ShowSeries = false });
        Assert.Equal(2, failed.Calls.Count);
        Assert.Equal(before.RatingsFetchedUtc, Assert.Single(store.Records.Values).RatingsFetchedUtc);
        Assert.Equal(before.RatingsJson, Assert.Single(store.Records.Values).RatingsJson);
    }

    [Fact]
    public async Task ColdOptionalMetadataFailureKeepsDiscoveryCompleteAndRetriesOnlyAfterCooldown()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var tmdb = ItemCacheMovie(date);
        var watchmode = new FakeWatchmodeClient { SourcesOutcomeSuccessful = false };
        var cache = new FakeCalendarCache();
        var omdb = ItemCacheOmdb("7.1");
        var updates = new List<PremiereLoadProgress>();
        await foreach (var update in CreateService(tmdb, omdb: omdb, watchmode: watchmode, calendarCache: cache, itemEnrichmentCache: coordinator)
            .StreamCoreAsync(date, date, filters: new CalendarFilters { ShowSeries = false })) updates.Add(update);

        Assert.False(Assert.Single(updates, update => update.IsFinal).HasSourceErrors);
        Assert.Equal(112, Assert.Single(updates.Last().Items).RuntimeMinutes);
        Assert.Single(cache.SetCalls);
        Assert.Null(Assert.Single(store.Records.Values).MetadataFetchedUtc);
        Assert.Single(tmdb.MovieDetailCalls);
        Assert.Single(watchmode.SourceCalls);
        await CreateService(tmdb, omdb: omdb, watchmode: watchmode, calendarCache: cache, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: new CalendarFilters { ShowSeries = false });
        Assert.Single(tmdb.MovieDetailCalls);
        Assert.Single(watchmode.SourceCalls);
        clock.Advance(TimeSpan.FromMinutes(6));
        var retried = await CreateService(tmdb, omdb: omdb, watchmode: watchmode, calendarCache: cache, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: new CalendarFilters { ShowSeries = false });
        Assert.Single(retried);
        Assert.Equal(2, tmdb.MovieDetailCalls.Count);
        Assert.Equal(2, watchmode.SourceCalls.Count);
        Assert.Single(tmdb.MovieCalls);
        Assert.Null(Assert.Single(store.Records.Values).MetadataFetchedUtc);
    }

    [Fact]
    public async Task OptionalFailureCannotAssociateNewIdentityRatingsWithRetainedOldMetadata()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        await CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("8.0"), itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: new CalendarFilters { ShowSeries = false });
        var before = Assert.Single(store.Records.Values);
        clock.Advance(TimeSpan.FromDays(8));
        var tmdb = ItemCacheMovie(date.AddDays(7));
        tmdb.MovieDetailsById[901] = tmdb.MovieDetailsById[901] with { ExternalIds = new TmdbExternalIds { ImdbId = "tt0000902" } };
        var omdb = new FakeOmdbClient();
        omdb.ItemsByImdbId["tt0000902"] = new OmdbItem { Response = "True", ImdbRating = "9.5" };

        var result = Assert.Single(await CreateService(tmdb, omdb: omdb,
            watchmode: new FakeWatchmodeClient { SourcesOutcomeSuccessful = false }, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date.AddDays(7), date.AddDays(7), CancellationToken.None, filters: new CalendarFilters { ShowSeries = false }));

        Assert.Equal("tt0000901", result.ImdbId);
        Assert.Equal(8.0, result.ImdbScore);
        Assert.Equal("tt0000902", Assert.Single(omdb.Calls));
        var after = Assert.Single(store.Records.Values);
        Assert.Equal(before.MetadataJson, after.MetadataJson);
        Assert.Equal(before.RatingsJson, after.RatingsJson);
        Assert.Equal(before.MetadataFetchedUtc, after.MetadataFetchedUtc);
        Assert.Equal(before.RatingsFetchedUtc, after.RatingsFetchedUtc);
    }

    [Fact]
    public async Task PendingLegacySeedThenObservationWritesMatchingJsonAndColumnClocks()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var item = ItemCacheLegacyItem(date, clock) with { TmdbScore = 6.5, TmdbVoteCount = 100 };
        var identity = new ItemEnrichmentIdentity(item.MediaType, item.TmdbId);
        var session = await coordinator.CreateSessionAsync(CancellationToken.None);
        await session.PrepareAsync([identity], [item], CancellationToken.None);
        await session.GetAsync(identity, false, (_, _, _, _) => Task.FromResult(new ItemEnrichmentRefresh(null, null)),
            CancellationToken.None, ratingsInputs: item with { TmdbScore = 9.2, TmdbVoteCount = 250 });
        await session.FlushAsync(CancellationToken.None);

        var record = Assert.Single(store.Records.Values);
        var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(record.MetadataJson!);
        Assert.Equal(clock.GetUtcNow(), metadata?.TmdbVotes?.TmdbObservedUtc);
        Assert.Equal(metadata?.TmdbVotes?.TmdbObservedUtc, record.TmdbObservedUtc);
        Assert.Equal(9.2, metadata?.TmdbVotes?.TmdbScore);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OptionalTvmazeFailurePreservesFieldsAndClockWhileVerifiedAbsenceCanRefresh(bool fail)
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        FakeTmdbClient Tmdb()
        {
            var client = new FakeTmdbClient { TvItems = [new TmdbTvDiscoverItem { Id = 901, Name = "Optional TVmaze", FirstAirDate = date.ToString("yyyy-MM-dd") }] };
            client.TvDetailsById[901] = new TmdbDetailsWithExtras { Id = 901, ExternalIds = new TmdbExternalIds { ImdbId = "tt0000901" } };
            client.SeasonOneEpisodeOneDatesById[901] = date;
            return client;
        }
        var healthy = new FakeTvmazeClient { LookupResult = new TvmazeShow
        {
            Id = 91, Network = new TvmazeChannel { Name = "Previous network" }, AverageRuntime = 47,
            OfficialSite = "https://previous.example/", Url = "https://www.tvmaze.com/shows/91/optional",
            Image = new TvmazeImage { Original = "https://previous.example/poster.jpg" }
        } };
        await CreateService(Tmdb(), tvmaze: healthy, omdb: ItemCacheOmdb("7.1"), itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: new CalendarFilters { ShowMovies = false });
        var before = Assert.Single(store.Records.Values);
        clock.Advance(TimeSpan.FromDays(8));

        var result = Assert.Single(await CreateService(Tmdb(), tvmaze: new FakeTvmazeClient { ThrowOnLookup = fail, ThrowOnSearch = fail },
            omdb: ItemCacheOmdb("8.4"), itemEnrichmentCache: coordinator).GetPremieresAsync(date, date, CancellationToken.None,
            filters: new CalendarFilters { ShowMovies = false }));

        var after = Assert.Single(store.Records.Values);
        Assert.Equal(fail ? before.MetadataFetchedUtc : clock.GetUtcNow(), after.MetadataFetchedUtc);
        if (fail)
        {
            Assert.Equal("Previous network", result.NetworkName);
            Assert.Equal(47, result.RuntimeMinutes);
            Assert.Equal("https://previous.example/", result.OfficialSiteUrl);
            Assert.Equal("https://previous.example/poster.jpg", result.PosterUrl);
            Assert.Contains("Previous network", result.SourceNames);
            Assert.Equal(before.MetadataJson, after.MetadataJson);
        }
        else Assert.Null(result.NetworkName);
    }

    [Fact]
    public async Task OptionalWatchmodeFailurePreservesPriorAvailabilityAndMetadataClock()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var watchmode = new FakeWatchmodeClient { Sources = [new PremiereSource { Name = "Prior streaming", Kind = "flatrate", Id = 91 }] };
        await CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"), watchmode: watchmode, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: new CalendarFilters { ShowSeries = false });
        var before = Assert.Single(store.Records.Values);
        clock.Advance(TimeSpan.FromDays(8));
        var tmdb = ItemCacheMovie(date.AddDays(7));
        tmdb.MovieDetailsById[901] = tmdb.MovieDetailsById[901] with { Runtime = 144 };

        var result = Assert.Single(await CreateService(tmdb, omdb: ItemCacheOmdb("8.4"),
            watchmode: new FakeWatchmodeClient { ThrowOnSources = true }, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date.AddDays(7), date.AddDays(7), CancellationToken.None, filters: new CalendarFilters { ShowSeries = false }));

        Assert.Contains("Prior streaming", result.SourceNames);
        Assert.Equal(112, result.RuntimeMinutes);
        Assert.Equal(before.MetadataFetchedUtc, Assert.Single(store.Records.Values).MetadataFetchedUtc);
        Assert.Equal(before.MetadataJson, Assert.Single(store.Records.Values).MetadataJson);
    }

    [Fact]
    public async Task ExternalFirstGlobalMetadataHasTheSameAvailabilityCoverageAsTmdbDiscovery()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var tmdb = new FakeTmdbClient();
        tmdb.MovieDetailsById[901] = ItemCacheMovie(date).MovieDetailsById[901];
        var watchmode = new FakeWatchmodeClient { Sources = [new PremiereSource { Name = "Uniform streaming", Kind = "flatrate", Id = 91 }] };
        var external = new FakeDiscoveryProvider { Candidates = [new ExternalPremiereCandidate(PremiereMediaType.Movie, date, "Cached movie", 901, "tt0000901", null, "External schedule")] };
        await CreateService(tmdb, omdb: ItemCacheOmdb("7.1"), watchmode: watchmode, discoveryProviders: [external], itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: new CalendarFilters { ShowSeries = false });
        var reusedWatchmode = new FakeWatchmodeClient();

        var result = Assert.Single(await CreateService(ItemCacheMovie(date.AddDays(7)), watchmode: reusedWatchmode, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date.AddDays(7), date.AddDays(7), CancellationToken.None, filters: new CalendarFilters { ShowSeries = false }));

        Assert.Single(watchmode.SourceCalls);
        Assert.Empty(reusedWatchmode.SourceCalls);
        Assert.Contains("Uniform streaming", result.SourceNames);
        Assert.True(JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!)?.AvailabilityComplete);
    }

    [Fact]
    public async Task InvalidationAfterSuccessfulBatchFlushPreventsCompleteWeekPublication()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var cache = new FakeCalendarCache();
        var service = CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"), calendarCache: cache, itemEnrichmentCache: ItemCacheCoordinator(store, clock));
        PremiereLoadProgress? final = null;
        var invalidated = false;
        await foreach (var update in service.StreamCoreAsync(date, date, filters: new CalendarFilters { ShowSeries = false }))
        {
            if (!invalidated && !update.IsFinal && update.Items.Any(item => item.RuntimeMinutes == 112))
            {
                invalidated = true;
                clock.Advance(TimeSpan.FromMinutes(1));
                await store.InvalidateTmdbAsync(PremiereMediaType.Movie, [901], clock.GetUtcNow(), CancellationToken.None);
            }
            if (update.IsFinal) final = update;
        }
        Assert.True(invalidated);
        Assert.True(final?.HasSourceErrors);
        Assert.Empty(cache.SetCalls);
    }

    [Fact]
    public async Task InvalidatedCompleteWeekRediscoveryRemovesMovedItemsAndBypassesDiscoveryCache()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var cache = new FakeCalendarCache();
        await CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"), calendarCache: cache, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: new CalendarFilters { ShowSeries = false });
        clock.Advance(TimeSpan.FromHours(1));
        await store.InvalidateTmdbAsync(PremiereMediaType.Movie, [901], clock.GetUtcNow(), CancellationToken.None);
        var tmdb = new FakeTmdbClient();

        var result = await CreateService(tmdb, calendarCache: cache, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: new CalendarFilters { ShowSeries = false });

        Assert.Empty(result);
        Assert.True(Assert.Single(tmdb.MovieCalls).ForceRefresh);
        Assert.Empty(cache.SetCalls.Last().Items);
    }

    [Fact]
    public async Task CompleteWeekReadOverlaysNewerAcceptedMetadataAndRatingsWithoutHttp()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var cache = new FakeCalendarCache();
        await CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"), calendarCache: cache, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: new CalendarFilters { ShowSeries = false });
        clock.Advance(TimeSpan.FromHours(1));
        var pair = Assert.Single(store.Records);
        var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(pair.Value.MetadataJson!)! with { RuntimeMinutes = 144 };
        var ratings = JsonSerializer.Deserialize<ItemEnrichmentRatings>(pair.Value.RatingsJson!)! with { ImdbScore = 8.8 };
        store.Records[pair.Key] = pair.Value with { MetadataJson = JsonSerializer.Serialize(metadata), RatingsJson = JsonSerializer.Serialize(ratings),
            MetadataFetchedUtc = clock.GetUtcNow(), RatingsFetchedUtc = clock.GetUtcNow(), MetadataRevision = pair.Value.MetadataRevision + 1 };
        var tmdb = new FakeTmdbClient();

        var result = Assert.Single(await CreateService(tmdb, calendarCache: cache, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: new CalendarFilters { ShowSeries = false }));

        Assert.Equal(144, result.RuntimeMinutes);
        Assert.Equal(8.8, result.ImdbScore);
        Assert.Empty(tmdb.MovieCalls);
        Assert.Empty(tmdb.MovieDetailCalls);
    }

    [Fact]
    public async Task OlderSameGenerationRefreshRebasesToAcceptedNewerMetadataBeforePublication()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var item = ItemCacheLegacyItem(date, clock);
        await coordinator.ImportAsync([item], CancellationToken.None);
        var identity = new ItemEnrichmentIdentity(item.MediaType, item.TmdbId);
        clock.Advance(TimeSpan.FromHours(1));
        var older = await coordinator.CreateSessionAsync(CancellationToken.None);
        await older.PrepareAsync([identity], null, CancellationToken.None);
        var oldResult = await older.GetAsync(identity, true, (previous, _, _, _) => Task.FromResult(new ItemEnrichmentRefresh(previous.Metadata! with { RuntimeMinutes = 111 }, null)), CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(1));
        var newer = await coordinator.CreateSessionAsync(CancellationToken.None);
        await newer.PrepareAsync([identity], null, CancellationToken.None);
        await newer.GetAsync(identity, true, (previous, _, _, _) => Task.FromResult(new ItemEnrichmentRefresh(previous.Metadata! with { RuntimeMinutes = 222 }, null)), CancellationToken.None);
        await newer.FlushAsync(CancellationToken.None);
        await older.FlushAsync(CancellationToken.None);

        var final = await older.ReconcileAsync([oldResult.Apply(item)], true, CancellationToken.None);

        Assert.Empty(final.DirtyMedia);
        Assert.Equal(222, Assert.Single(final.Items).RuntimeMinutes);
        Assert.Equal(222, JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!)?.RuntimeMinutes);
    }

    [Fact]
    public async Task OlderVoteObservationCannotRegressNewerSameClockMetadata()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var item = ItemCacheLegacyItem(date, clock) with { TmdbScore = 6.5, TmdbVoteCount = 100 };
        await coordinator.ImportAsync([item], CancellationToken.None);
        var identity = new ItemEnrichmentIdentity(item.MediaType, item.TmdbId);
        var originalClock = Assert.Single(store.Records.Values).MetadataFetchedUtc;
        clock.Advance(TimeSpan.FromHours(1));
        var older = await coordinator.CreateSessionAsync(CancellationToken.None);
        await older.PrepareAsync([identity], null, CancellationToken.None);
        var oldResult = await older.GetAsync(identity, false, (_, _, _, _) => Task.FromResult(new ItemEnrichmentRefresh(null, null)),
            CancellationToken.None, ratingsInputs: item with { TmdbScore = 7, TmdbVoteCount = 150 });
        clock.Advance(TimeSpan.FromHours(1));
        var newer = await coordinator.CreateSessionAsync(CancellationToken.None);
        await newer.PrepareAsync([identity], null, CancellationToken.None);
        await newer.GetAsync(identity, false, (_, _, _, _) => Task.FromResult(new ItemEnrichmentRefresh(null, null)),
            CancellationToken.None, ratingsInputs: item with { TmdbScore = 8, TmdbVoteCount = 200 });
        await newer.FlushAsync(CancellationToken.None);
        await older.FlushAsync(CancellationToken.None);

        var final = await older.ReconcileAsync([oldResult.Apply(item)], true, CancellationToken.None);
        var accepted = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!);
        Assert.Equal(8, accepted?.TmdbVotes?.TmdbScore);
        Assert.Equal(200, accepted?.TmdbVotes?.TmdbVoteCount);
        Assert.Equal(clock.GetUtcNow(), accepted?.TmdbVotes?.TmdbObservedUtc);
        Assert.Equal(originalClock, Assert.Single(store.Records.Values).MetadataFetchedUtc);
        Assert.Equal(8, Assert.Single(final.Items).TmdbScore);
    }

    [Fact]
    public async Task GlobalItemCacheReusesMetadataAcrossWeeksCriteriaAndCoordinatorRestart()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var omdb = ItemCacheOmdb("7.1");
        var firstTmdb = ItemCacheMovie(date);
        var first = Assert.Single(await CreateService(firstTmdb, omdb: omdb,
            itemEnrichmentCache: ItemCacheCoordinator(store, clock)).GetPremieresAsync(date, date,
            CancellationToken.None, filters: new CalendarFilters { ShowSeries = false }));
        var originalRecord = Assert.Single(store.Records.Values);
        clock.Advance(TimeSpan.FromHours(4));
        var secondTmdb = ItemCacheMovie(date.AddDays(8));
        var second = Assert.Single(await CreateService(secondTmdb, omdb: omdb,
            itemEnrichmentCache: ItemCacheCoordinator(store, clock)).GetPremieresAsync(date.AddDays(8), date.AddDays(8),
            CancellationToken.None, filters: new CalendarFilters { ShowSeries = false, MovieFilters = { OriginalLanguages = ["en"] } }));

        Assert.Single(firstTmdb.MovieDetailCalls);
        Assert.Empty(secondTmdb.MovieDetailCalls);
        Assert.Single(omdb.Calls);
        Assert.Equal(first.RuntimeMinutes, second.RuntimeMinutes);
        Assert.Equal(first.ImdbScore, second.ImdbScore);
        Assert.Equal(date.AddDays(8), second.PremiereDate);
        Assert.Equal(second.PremiereDate, second.DateSemantics?.ChosenDate);
        Assert.Equal(originalRecord.MetadataFetchedUtc, Assert.Single(store.Records.Values).MetadataFetchedUtc);
        Assert.Equal(originalRecord.RatingsFetchedUtc, Assert.Single(store.Records.Values).RatingsFetchedUtc);
        Assert.Equal(clock.GetUtcNow(), Assert.Single(store.Records.Values).LastSeenUtc);
    }

    [Fact]
    public async Task GlobalItemCacheRefreshesOnlyScoresAfterTwentyFourHours()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var omdb = ItemCacheOmdb("7.1");
        var artwork = new RecordingArtworkProvider();
        await CreateService(ItemCacheMovie(date), omdb: omdb, artworkProviders: [artwork], itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: new CalendarFilters { ShowSeries = false });
        var original = Assert.Single(store.Records.Values);
        var artCalls = artwork.Calls.Count;
        clock.Advance(TimeSpan.FromHours(25));
        omdb.ItemsByImdbId["tt0000901"] = new OmdbItem { Response = "True", ImdbRating = "8.4", Poster = "https://changed.example/new.jpg", Plot = "Ratings must not change metadata" };
        var tmdb = ItemCacheMovie(date.AddDays(7));

        var result = Assert.Single(await CreateService(tmdb, omdb: omdb, artworkProviders: [artwork], itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date.AddDays(7), date.AddDays(7), CancellationToken.None, filters: new CalendarFilters { ShowSeries = false }));

        Assert.Empty(tmdb.MovieDetailCalls);
        Assert.Equal(artCalls, artwork.Calls.Count);
        Assert.Equal(2, omdb.Calls.Count);
        Assert.Equal(8.4, result.ImdbScore);
        Assert.DoesNotContain("Ratings must not", result.Overview ?? "");
        var refreshed = Assert.Single(store.Records.Values);
        Assert.Equal(original.MetadataFetchedUtc, refreshed.MetadataFetchedUtc);
        Assert.Equal(clock.GetUtcNow(), refreshed.RatingsFetchedUtc);
    }

    [Fact]
    public async Task InvalidatedMetadataBypassesOnlyMetadataAndKeepsRatingsSuccessClock()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var omdb = ItemCacheOmdb("7.1");
        await CreateService(ItemCacheMovie(date), omdb: omdb, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: new CalendarFilters { ShowSeries = false });
        var before = Assert.Single(store.Records.Values);
        clock.Advance(TimeSpan.FromHours(1));
        await store.InvalidateTmdbAsync(PremiereMediaType.Movie, [901], clock.GetUtcNow(), CancellationToken.None);
        var tmdb = ItemCacheMovie(date.AddDays(1));
        tmdb.MovieDetailsById[901] = tmdb.MovieDetailsById[901] with { Runtime = 144 };

        var result = Assert.Single(await CreateService(tmdb, omdb: omdb, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date.AddDays(1), date.AddDays(1), CancellationToken.None, filters: new CalendarFilters { ShowSeries = false }));

        Assert.True(Assert.Single(tmdb.MovieDetailCalls).ForceRefresh);
        Assert.Single(omdb.Calls);
        Assert.Equal(144, result.RuntimeMinutes);
        Assert.Equal(before.RatingsFetchedUtc, Assert.Single(store.Records.Values).RatingsFetchedUtc);
        Assert.Equal(clock.GetUtcNow(), Assert.Single(store.Records.Values).MetadataFetchedUtc);
    }

    [Fact]
    public async Task FailedMetadataAndRatingsKeepLastGoodPayloadsAndSuccessClocks()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        await CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"), itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: new CalendarFilters { ShowSeries = false });
        var before = Assert.Single(store.Records.Values);
        clock.Advance(TimeSpan.FromDays(8));
        var failedTmdb = new FakeTmdbClient
        {
            MovieItems = [new TmdbMovieDiscoverItem { Id = 901, Title = "Cached movie", ReleaseDate = date.AddDays(9).ToString("yyyy-MM-dd") }],
            MovieDetailExceptions = { [901] = new ExternalApiException("Provider unavailable") }
        };
        var result = Assert.Single(await CreateService(failedTmdb, omdb: new FakeOmdbClient { ThrowOnGet = true }, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date.AddDays(9), date.AddDays(9), CancellationToken.None, filters: new CalendarFilters { ShowSeries = false }));

        Assert.Equal(112, result.RuntimeMinutes);
        Assert.Equal(7.1, result.ImdbScore);
        var after = Assert.Single(store.Records.Values);
        Assert.Equal(before.MetadataJson, after.MetadataJson);
        Assert.Equal(before.RatingsJson, after.RatingsJson);
        Assert.Equal(before.MetadataFetchedUtc, after.MetadataFetchedUtc);
        Assert.Equal(before.RatingsFetchedUtc, after.RatingsFetchedUtc);
    }

    [Fact]
    public async Task GlobalItemCacheNeverCopiesEpisodeIdentityOrOldScheduleProvenance()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var firstTmdb = new FakeTmdbClient();
        firstTmdb.TvDetailsById[901] = new TmdbDetailsWithExtras { Id = 901, EpisodeRunTime = [45], ExternalIds = new TmdbExternalIds { ImdbId = "tt0000901" } };
        var firstFeed = new FakeDiscoveryProvider
        {
            Candidates = [new ExternalPremiereCandidate(PremiereMediaType.Series, date, "Cached show", 901, "tt0000901", null, "Old schedule")
                { SeasonNumber = 2, EpisodeNumber = 3, EpisodeTitle = "Old episode", ExternalProviderId = "old-id", ExternalUrl = "https://old.example/episode" }]
        };
        var filters = new CalendarFilters { ShowMovies = false, SeriesFilters = { SeriesDateMode = SeriesDateMode.AllEpisodes } };
        await CreateService(firstTmdb, omdb: ItemCacheOmdb("7.1"), discoveryProviders: [firstFeed], itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: filters);
        var tmdb = new FakeTmdbClient();
        var newFeed = new FakeDiscoveryProvider
        {
            Candidates = [new ExternalPremiereCandidate(PremiereMediaType.Series, date.AddDays(7), "Cached show", 901, "tt0000901", null, "New schedule")]
        };

        var result = Assert.Single(await CreateService(tmdb, omdb: ItemCacheOmdb("7.1"), discoveryProviders: [newFeed], itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date.AddDays(7), date.AddDays(7), CancellationToken.None, filters: filters));

        Assert.Empty(tmdb.TvDetailCalls);
        Assert.Equal(PremiereIdentity.SeriesEpisodeCanonicalId(901, date.AddDays(7), null, null), result.CanonicalId);
        Assert.Null(result.SeasonNumber);
        Assert.Null(result.EpisodeNumber);
        Assert.Null(result.EpisodeTitle);
        Assert.DoesNotContain(result.Sources, source => source.Name == "Old schedule");
        Assert.DoesNotContain(result.MergeContributions, source => source.Source == "Old schedule");
        Assert.Contains(result.Sources, source => source.Name == "New schedule");
        Assert.Equal(date.AddDays(7), result.PremiereDate);
    }

    [Fact]
    public async Task GlobalItemCacheReusesCanonicalDebutDateButInvalidationRevalidatesIt()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var item = new TmdbTvDiscoverItem { Id = 901, Name = "First debut", FirstAirDate = date.ToString("yyyy-MM-dd") };
        var firstTmdb = new FakeTmdbClient { TvItems = [item] };
        firstTmdb.SeasonOneEpisodeOneDatesById[901] = date;
        await CreateService(firstTmdb, omdb: ItemCacheOmdb("7.1"), itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: new CalendarFilters { ShowMovies = false });
        var secondTmdb = new FakeTmdbClient { TvItems = [item with { FirstAirDate = date.AddDays(1).ToString("yyyy-MM-dd") }] };
        var reused = Assert.Single(await CreateService(secondTmdb, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date.AddDays(2), CancellationToken.None, filters: new CalendarFilters { ShowMovies = false }));
        Assert.Empty(secondTmdb.TvSeasonDetailCalls);
        Assert.Equal(date, reused.PremiereDate);
        clock.Advance(TimeSpan.FromMinutes(1));
        await store.InvalidateTmdbAsync(PremiereMediaType.Series, [901], clock.GetUtcNow(), CancellationToken.None);
        secondTmdb.SeasonOneEpisodeOneDatesById[901] = date.AddYears(-3);

        var removed = await CreateService(secondTmdb, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date.AddDays(2), CancellationToken.None, filters: new CalendarFilters { ShowMovies = false });

        Assert.Empty(removed);
        Assert.True(Assert.Single(secondTmdb.TvSeasonDetailCalls).ForceRefresh);
    }

    [Fact]
    public async Task RepeatedDiscoveryCohortsAndExternalTmdbResolutionShareOneEnrichment()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var tmdb = ItemCacheMovie(date, duplicate: true);
        tmdb.FindResults[(PremiereMediaType.Movie, "imdb_id", "tt0000901")] = 901;
        var external = new FakeDiscoveryProvider
        {
            Candidates = [new ExternalPremiereCandidate(PremiereMediaType.Movie, date, "Cached movie", null, "tt0000901", null, "External schedule")]
        };
        var omdb = ItemCacheOmdb("7.1");

        var result = Assert.Single(await CreateService(tmdb, omdb: omdb, discoveryProviders: [external],
            itemEnrichmentCache: ItemCacheCoordinator(store, clock)).GetPremieresAsync(date, date,
            CancellationToken.None, filters: new CalendarFilters { ShowSeries = false }));

        Assert.Single(tmdb.MovieDetailCalls);
        Assert.Single(omdb.Calls);
        Assert.Single(store.Records);
        Assert.All(store.ReadBatches, batch => Assert.Single(batch));
        Assert.Contains(result.MergeContributions, contribution => contribution.Source == "External schedule");
    }

    [Fact]
    public async Task ConcurrentWeeksShareTheSameConfiguredItemRefresh()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var item = new TmdbMovieDiscoverItem { Id = 901, Title = "Shared flight", ReleaseDate = date.ToString("yyyy-MM-dd") };
        var firstTmdb = new FakeTmdbClient { MovieItems = [item], MovieDetailDelay = TimeSpan.FromMilliseconds(150) };
        var secondTmdb = new FakeTmdbClient { MovieItems = [item with { ReleaseDate = date.AddDays(7).ToString("yyyy-MM-dd") }], MovieDetailDelay = TimeSpan.FromMilliseconds(150) };
        foreach (var tmdb in new[] { firstTmdb, secondTmdb })
            tmdb.MovieDetailsById[901] = new TmdbDetailsWithExtras { Id = 901, Runtime = 112, ExternalIds = new TmdbExternalIds { ImdbId = "tt0000901" } };
        var omdb = ItemCacheOmdb("7.1");

        var loads = await Task.WhenAll(
            CreateService(firstTmdb, omdb: omdb, itemEnrichmentCache: coordinator).GetPremieresAsync(date, date, CancellationToken.None,
                filters: new CalendarFilters { ShowSeries = false }),
            CreateService(secondTmdb, omdb: omdb, itemEnrichmentCache: coordinator).GetPremieresAsync(date.AddDays(7), date.AddDays(7), CancellationToken.None,
                filters: new CalendarFilters { ShowSeries = false }));

        Assert.Equal(1, firstTmdb.MovieDetailCalls.Count + secondTmdb.MovieDetailCalls.Count);
        Assert.Single(omdb.Calls);
        Assert.Equal(date, Assert.Single(loads[0]).PremiereDate);
        Assert.Equal(date.AddDays(7), Assert.Single(loads[1]).PremiereDate);
    }

    [Fact]
    public async Task DiscoveryCohortReservesAllItemsWithOneDatabaseRead()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var raw = Enumerable.Range(1, 30).Select(id => new TmdbMovieDiscoverItem
            { Id = id, Title = $"Movie {id}", ReleaseDate = date.ToString("yyyy-MM-dd") }).ToArray();
        var tmdb = new FakeTmdbClient { MovieItems = raw };
        foreach (var item in raw) tmdb.MovieDetailsById[item.Id] = new TmdbDetailsWithExtras { Id = item.Id, Runtime = 90 };

        var items = await CreateService(tmdb, itemEnrichmentCache: ItemCacheCoordinator(store, clock))
            .GetPremieresAsync(date, date, CancellationToken.None, filters: new CalendarFilters { ShowSeries = false });

        Assert.Equal(30, items.Count);
        Assert.Equal(30, Assert.Single(store.ReservedBatches).Length);
        Assert.Equal(30, tmdb.MovieDetailCalls.Count);
    }

    [Fact]
    public async Task LegacyWeekImportUsesOriginalTimestampWithoutExternalCalls()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var seededAt = clock.GetUtcNow().AddHours(-20);
        var cached = new PremiereItem
        {
            CanonicalId = "movie:901", MediaType = PremiereMediaType.Movie, TmdbId = 901, Title = "Legacy",
            PremiereDate = date, RuntimeMinutes = 112, ImdbId = "tt0000901", ImdbScore = 7.1, LastUpdatedUtc = seededAt
        };
        var tmdb = new FakeTmdbClient();
        var omdb = ItemCacheOmdb("9.9");

        var result = Assert.Single(await CreateService(tmdb, omdb: omdb, calendarCache: new FakeCalendarCache { Items = [cached] },
            itemEnrichmentCache: ItemCacheCoordinator(store, clock)).GetPremieresAsync(date, date, CancellationToken.None,
            filters: new CalendarFilters { ShowSeries = false }));

        Assert.Empty(tmdb.MovieCalls);
        Assert.Empty(tmdb.MovieDetailCalls);
        Assert.Empty(omdb.Calls);
        var imported = Assert.Single(store.Records.Values);
        Assert.Equal(seededAt, imported.MetadataFetchedUtc);
        Assert.Equal(seededAt, imported.RatingsFetchedUtc);
        Assert.Equal(7.1, result.ImdbScore);
    }

    [Fact]
    public async Task ConcurrentInvalidationRejectsProviderResultAndNeverPublishesCompleteWeek()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var cache = new FakeCalendarCache();
        store.BeforeWrite = () => store.InvalidateTmdbAsync(PremiereMediaType.Movie, [901], clock.GetUtcNow(), CancellationToken.None).GetAwaiter().GetResult();
        var updates = new List<PremiereLoadProgress>();
        var service = CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"), calendarCache: cache,
            itemEnrichmentCache: ItemCacheCoordinator(store, clock));

        await foreach (var update in service.StreamCoreAsync(date, date, filters: new CalendarFilters { ShowSeries = false })) updates.Add(update);

        Assert.True(Assert.Single(updates, update => update.IsFinal).HasSourceErrors);
        Assert.Empty(cache.SetCalls);
        Assert.Null(Assert.Single(store.Records.Values).MetadataFetchedUtc);
        Assert.Null(Assert.Single(store.Records.Values).RatingsFetchedUtc);
    }

    [Fact]
    public async Task LegacyTmdbVotesRemainAvailableAcrossExternalOnlyRatingsRefresh()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var seededAt = clock.GetUtcNow().AddHours(-2);
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var legacy = new PremiereItem
        {
            CanonicalId = "movie:901", MediaType = PremiereMediaType.Movie, TmdbId = 901, Title = "Cached movie",
            PremiereDate = date, RuntimeMinutes = 112, ImdbId = "tt0000901", ImdbScore = 7.1,
            TmdbScore = 6.5, TmdbVoteCount = 100, LastUpdatedUtc = seededAt
        };
        await coordinator.ImportAsync([legacy], CancellationToken.None);
        var imported = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!);
        Assert.Equal(6.5, imported?.TmdbVotes?.TmdbScore);
        Assert.Equal(100, imported?.TmdbVotes?.TmdbVoteCount);
        Assert.Equal(seededAt, imported?.TmdbVotes?.TmdbObservedUtc);
        clock.Advance(TimeSpan.FromHours(25));
        var tmdb = new FakeTmdbClient();
        var feed = new FakeDiscoveryProvider
        {
            Candidates = [new ExternalPremiereCandidate(PremiereMediaType.Movie, date.AddDays(7), legacy.Title, 901, legacy.ImdbId, null, "External schedule")]
        };

        var result = Assert.Single(await CreateService(tmdb, omdb: ItemCacheOmdb("8.4"), discoveryProviders: [feed], itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date.AddDays(7), date.AddDays(7), CancellationToken.None, filters: new CalendarFilters { ShowSeries = false }));

        Assert.Empty(tmdb.MovieDetailCalls);
        Assert.Equal(8.4, result.ImdbScore);
        Assert.Equal(6.5, result.TmdbScore);
        Assert.Equal(100, result.TmdbVoteCount);
        var refreshed = Assert.Single(store.Records.Values);
        var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(refreshed.MetadataJson!);
        Assert.Equal(seededAt, metadata?.TmdbVotes?.TmdbObservedUtc);
        Assert.Equal(seededAt, refreshed.MetadataFetchedUtc);
        Assert.Equal(clock.GetUtcNow(), refreshed.RatingsFetchedUtc);
    }

    [Fact]
    public async Task FreshDiscoveryVotesOverrideStoredFallbackAndPersistTheirOwnObservationClock()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var seededAt = clock.GetUtcNow().AddHours(-2);
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        await coordinator.ImportAsync([new PremiereItem
        {
            CanonicalId = "movie:901", MediaType = PremiereMediaType.Movie, TmdbId = 901, Title = "Cached movie",
            PremiereDate = date, RuntimeMinutes = 112, ImdbId = "tt0000901", ImdbScore = 7.1,
            TmdbScore = 6.5, TmdbVoteCount = 100, LastUpdatedUtc = seededAt
        }], CancellationToken.None);
        clock.Advance(TimeSpan.FromHours(1));
        var tmdb = new FakeTmdbClient
        {
            MovieItems = [new TmdbMovieDiscoverItem
                { Id = 901, Title = "Fresh discovery title", PosterPath = "/fresh-discovery.jpg", ReleaseDate = date.AddDays(7).ToString("yyyy-MM-dd"), VoteAverage = 9.2, VoteCount = 250 }]
        };

        var result = Assert.Single(await CreateService(tmdb, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date.AddDays(7), date.AddDays(7), CancellationToken.None, filters: new CalendarFilters { ShowSeries = false }));

        Assert.Empty(tmdb.MovieDetailCalls);
        Assert.Equal(9.2, result.TmdbScore);
        Assert.Equal(250, result.TmdbVoteCount);
        Assert.Equal("Fresh discovery title", result.Title);
        Assert.EndsWith("/fresh-discovery.jpg", result.PosterUrl);
        var record = Assert.Single(store.Records.Values);
        var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(record.MetadataJson!);
        Assert.Equal(9.2, metadata?.TmdbVotes?.TmdbScore);
        Assert.Equal(250, metadata?.TmdbVotes?.TmdbVoteCount);
        Assert.Equal(clock.GetUtcNow(), metadata?.TmdbVotes?.TmdbObservedUtc);
        Assert.Equal(metadata?.TmdbVotes?.TmdbObservedUtc, record.TmdbObservedUtc);
        Assert.Equal(seededAt, record.MetadataFetchedUtc);
        Assert.Equal(seededAt, record.RatingsFetchedUtc);
    }

    [Fact]
    public async Task LegacyImportDoesNotInventSuccessfulRatingsForMetadataOnlyRows()
    {
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var item = new PremiereItem
        {
            CanonicalId = "movie:901", MediaType = PremiereMediaType.Movie, TmdbId = 901, Title = "Metadata only",
            PremiereDate = new DateOnly(2026, 11, 2), RuntimeMinutes = 112, LastUpdatedUtc = clock.GetUtcNow().AddDays(-2),
            TmdbScore = 6.5, TmdbVoteCount = 100
        };

        await ItemCacheCoordinator(store, clock).ImportAsync([item], CancellationToken.None);

        var record = Assert.Single(store.Records.Values);
        Assert.Equal(item.LastUpdatedUtc, record.MetadataFetchedUtc);
        Assert.Null(record.RatingsJson);
        Assert.Null(record.RatingsFetchedUtc);
        var observation = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(record.MetadataJson!)?.TmdbVotes;
        Assert.Equal(6.5, observation?.TmdbScore);
        Assert.Equal(100, observation?.TmdbVoteCount);
        Assert.Equal(item.LastUpdatedUtc, observation?.TmdbObservedUtc);
    }

    [Fact]
    public async Task RestartCannotImportOldWeekDataIntoChangedProviderSettingsPartition()
    {
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var settings = new ItemCacheSettingsStore();
        var state = new ItemCacheAppStateStore();
        var item = new PremiereItem
        {
            CanonicalId = "movie:901", MediaType = PremiereMediaType.Movie, TmdbId = 901, Title = "Legacy configuration",
            PremiereDate = new DateOnly(2026, 11, 2), RuntimeMinutes = 112, ImdbScore = 7.1, ImdbId = "tt0000901",
            LastUpdatedUtc = clock.GetUtcNow().AddHours(-2)
        };
        await ItemCacheCoordinator(store, clock, settings, state).ImportAsync([item], CancellationToken.None);
        settings.Settings.Sources.Wikimedia.Enabled = false;

        await ItemCacheCoordinator(store, clock, settings, state).ImportAsync([item], CancellationToken.None);

        Assert.Equal(2, store.Records.Count);
        Assert.Single(store.Records.Values, row => row.MetadataJson is not null);
        var newPartition = Assert.Single(store.Records.Values, row => row.MetadataJson is null);
        Assert.Null(newPartition.MetadataFetchedUtc);
        Assert.Null(newPartition.RatingsFetchedUtc);
    }

    [Fact]
    public async Task FinalBackfillProjectionUpdatesGlobalPayloadWithoutResettingSuccessClocks()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var started = clock.GetUtcNow();
        var store = new MemoryItemEnrichmentStore(clock);
        var omdb = ItemCacheOmdb("N/A");
        var backfill = new ScoreBackfillService(null, omdb, new RatingMapper(), null, NullLogger<ScoreBackfillService>.Instance);
        var service = CreateService(ItemCacheMovie(date), omdb: omdb, scoreBackfill: backfill,
            itemEnrichmentCache: ItemCacheCoordinator(store, clock));
        PremiereItem? final = null;
        var changed = false;

        await foreach (var update in service.StreamCoreAsync(date, date, forceRefresh: true,
            filters: new CalendarFilters { ShowSeries = false }))
        {
            if (!changed && !update.IsFinal && update.Items.Any(item => item.RuntimeMinutes == 112 && item.ImdbId is not null))
            {
                changed = true;
                clock.Advance(TimeSpan.FromHours(1));
                omdb.ItemsByImdbId["tt0000901"] = new OmdbItem { Response = "True", ImdbRating = "8.8", ImdbVotes = "1,000" };
            }
            if (update.IsFinal) final = Assert.Single(update.Items);
        }

        Assert.Equal(8.8, final?.ImdbScore);
        var record = Assert.Single(store.Records.Values);
        Assert.Equal(8.8, JsonSerializer.Deserialize<ItemEnrichmentRatings>(record.RatingsJson!)?.ImdbScore);
        Assert.Equal(started, record.MetadataFetchedUtc);
        Assert.Equal(started, record.RatingsFetchedUtc);
    }

    [Fact]
    public async Task ChangedMetadataIdsCannotReuseOrFreshenScoresFromPreviousIdentity()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        await CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"), itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: new CalendarFilters { ShowSeries = false });
        var before = Assert.Single(store.Records.Values);
        clock.Advance(TimeSpan.FromHours(1));
        await store.InvalidateTmdbAsync(PremiereMediaType.Movie, [901], clock.GetUtcNow(), CancellationToken.None);
        var tmdb = ItemCacheMovie(date.AddDays(1));
        tmdb.MovieDetailsById[901] = tmdb.MovieDetailsById[901] with
            { ExternalIds = new TmdbExternalIds { ImdbId = "tt0000902", WikidataId = "Q902" } };
        var failedOmdb = new FakeOmdbClient { ThrowOnGet = true };

        var result = Assert.Single(await CreateService(tmdb, omdb: failedOmdb, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date.AddDays(1), date.AddDays(1), CancellationToken.None, filters: new CalendarFilters { ShowSeries = false }));

        Assert.Equal("tt0000902", result.ImdbId);
        Assert.Null(result.ImdbScore);
        Assert.Single(failedOmdb.Calls);
        Assert.Equal("tt0000902", Assert.Single(failedOmdb.Calls));
        Assert.Equal(before.RatingsFetchedUtc, Assert.Single(store.Records.Values).RatingsFetchedUtc);
        Assert.Equal(before.RatingsJson, Assert.Single(store.Records.Values).RatingsJson);
    }

    [Fact]
    public async Task SuccessClocksReflectReservationBeforeProviderWorkRatherThanCompletion()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var started = clock.GetUtcNow();
        var store = new MemoryItemEnrichmentStore(clock) { BeforeWrite = () => clock.Advance(TimeSpan.FromHours(2)) };

        await CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"), itemEnrichmentCache: ItemCacheCoordinator(store, clock))
            .GetPremieresAsync(date, date, CancellationToken.None, filters: new CalendarFilters { ShowSeries = false });

        var record = Assert.Single(store.Records.Values);
        Assert.Equal(started, record.MetadataFetchedUtc);
        Assert.Equal(started, record.RatingsFetchedUtc);
        Assert.True(clock.GetUtcNow() > record.MetadataFetchedUtc);
    }

    [Fact]
    public async Task EffectiveProviderSettingsPartitionGlobalReuse()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var settings = new ItemCacheSettingsStore();
        var coordinator = ItemCacheCoordinator(store, clock, settings);
        await CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"), itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: new CalendarFilters { ShowSeries = false });
        settings.Settings.Sources.Wikimedia.Enabled = false;
        var tmdb = ItemCacheMovie(date.AddDays(7));

        await CreateService(tmdb, omdb: ItemCacheOmdb("7.1"), itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date.AddDays(7), date.AddDays(7), CancellationToken.None, filters: new CalendarFilters { ShowSeries = false });

        Assert.Single(tmdb.MovieDetailCalls);
        Assert.Equal(2, store.Records.Count);
        Assert.Equal(2, settings.Reads);
    }

    private static FakeTmdbClient ItemCacheMovie(DateOnly date, bool duplicate = false)
    {
        var item = new TmdbMovieDiscoverItem { Id = 901, Title = "Cached movie", ReleaseDate = date.ToString("yyyy-MM-dd"), OriginalLanguage = "en" };
        var client = new FakeTmdbClient
        {
            MovieItems = [item],
            MovieStreamBatches = duplicate ? [new(1, 1, 2, 2, [item]), new(2, 2, 2, 2, [item])] : null
        };
        client.MovieDetailsById[901] = new TmdbDetailsWithExtras
        {
            Id = 901, Title = item.Title, Runtime = 112, Overview = "Stable metadata overview", OriginalLanguage = "en",
            ExternalIds = new TmdbExternalIds { ImdbId = "tt0000901", WikidataId = "Q901" },
            Genres = [new TmdbGenre { Id = 18, Name = "Drama" }]
        };
        return client;
    }

    private static PremiereItem ItemCacheLegacyItem(DateOnly date, ItemCacheClock clock) => new()
    {
        CanonicalId = "movie:901", MediaType = PremiereMediaType.Movie, TmdbId = 901, Title = "Cached movie", PremiereDate = date,
        RuntimeMinutes = 112, ImdbId = "tt0000901", ImdbScore = 7.1, LastUpdatedUtc = clock.GetUtcNow().AddHours(-2)
    };

    private static FakeOmdbClient ItemCacheOmdb(string score)
    {
        var client = new FakeOmdbClient();
        client.ItemsByImdbId["tt0000901"] = new OmdbItem { Response = "True", ImdbRating = score, ImdbVotes = "1,000" };
        return client;
    }

    private static ItemEnrichmentCacheCoordinator ItemCacheCoordinator(MemoryItemEnrichmentStore store, ItemCacheClock clock,
        IIntegrationSettingsStore? settings = null, IAppStateStore? state = null) => new(store, Microsoft.Extensions.Options.Options.Create(new ItemEnrichmentCacheOptions()),
        Microsoft.Extensions.Options.Options.Create(new TmdbOptions()), NullLogger<ItemEnrichmentCacheCoordinator>.Instance, timeProvider: clock,
        integrationSettingsStore: settings, appStateStore: state);

    private sealed class ItemCacheClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class ItemCacheSettingsStore : IIntegrationSettingsStore
    {
        public IntegrationSettings Settings { get; } = new();
        public int Reads;
        public Task<IntegrationSettings> GetAsync(CancellationToken cancellationToken = default) { Reads++; return Task.FromResult(Settings); }
        public Task SaveAsync(IntegrationSettings settings, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ItemCacheAppStateStore : IAppStateStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken) => Task.FromResult(_values.GetValueOrDefault(key));
        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken) { _values[key] = value; return Task.CompletedTask; }
        public Task DeleteValueAsync(string key, CancellationToken cancellationToken) { _values.Remove(key); return Task.CompletedTask; }
        public Task<IReadOnlyDictionary<string, string>> GetValuesByPrefixAsync(string prefix, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(_values.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal)).ToDictionary());
    }

    private sealed class MemoryItemEnrichmentStore(ItemCacheClock clock) : IItemEnrichmentStore
    {
        public ConcurrentDictionary<(string Partition, ItemEnrichmentIdentity Identity), ItemEnrichmentRecord> Records { get; } = new();
        public ConcurrentBag<ItemEnrichmentIdentity[]> ReadBatches { get; } = [];
        public ConcurrentBag<ItemEnrichmentIdentity[]> ReservedBatches { get; } = [];
        public Action? BeforeWrite;
        private readonly object _gate = new();

        public Task<IReadOnlyDictionary<ItemEnrichmentIdentity, ItemEnrichmentRecord>> ReadManyAsync(string partition,
            IReadOnlyCollection<ItemEnrichmentIdentity> identities, bool reserveMissing, CancellationToken cancellationToken)
        {
            ReadBatches.Add(identities.ToArray());
            if (reserveMissing) ReservedBatches.Add(identities.ToArray());
            lock (_gate)
            {
                var results = new Dictionary<ItemEnrichmentIdentity, ItemEnrichmentRecord>();
                foreach (var identity in identities)
                {
                    var key = (partition, identity);
                    if (!Records.TryGetValue(key, out var record) && reserveMissing)
                        record = new(identity, null, null, null, null, clock.GetUtcNow(), 0, null, null);
                    if (record is null) continue;
                    Records[key] = record = record with { LastSeenUtc = clock.GetUtcNow() };
                    results[identity] = record;
                }
                return Task.FromResult<IReadOnlyDictionary<ItemEnrichmentIdentity, ItemEnrichmentRecord>>(results);
            }
        }

        public Task<int> WriteManyAsync(string partition, IReadOnlyList<ItemEnrichmentWrite> writes, CancellationToken cancellationToken)
        {
            Interlocked.Exchange(ref BeforeWrite, null)?.Invoke();
            var updated = 0;
            lock (_gate)
            {
                foreach (var write in writes)
                {
                    var key = (partition, write.Identity);
                    if (!Records.TryGetValue(key, out var record) || record.Generation != write.ExpectedGeneration) continue;
                    var metadata = write.MetadataJson is not null && write.MetadataFetchedUtc is { } metadataUtc
                        && (record.MetadataFetchedUtc is null || metadataUtc >= record.MetadataFetchedUtc)
                        && (record.TmdbObservedUtc is null || write.TmdbObservedUtc >= record.TmdbObservedUtc)
                        && (write.ExpectedMetadataRevision is { } revision ? revision == record.MetadataRevision
                            : record.MetadataFetchedUtc is null || metadataUtc > record.MetadataFetchedUtc);
                    var ratings = write.RatingsJson is not null && write.RatingsFetchedUtc is { } ratingsUtc
                        && (record.RatingsFetchedUtc is null || ratingsUtc >= record.RatingsFetchedUtc)
                        && (write.ExpectedMetadataRevision is null || write.ExpectedMetadataRevision == record.MetadataRevision);
                    if (!metadata && !ratings) continue;
                    Records[key] = record with
                    {
                        MetadataJson = metadata ? write.MetadataJson : record.MetadataJson,
                        RatingsJson = ratings ? write.RatingsJson : record.RatingsJson,
                        MetadataFetchedUtc = metadata ? write.MetadataFetchedUtc : record.MetadataFetchedUtc,
                        RatingsFetchedUtc = ratings ? write.RatingsFetchedUtc : record.RatingsFetchedUtc,
                        TvmazeShowId = metadata ? write.TvmazeShowId ?? record.TvmazeShowId : record.TvmazeShowId,
                        TmdbObservedUtc = metadata ? write.TmdbObservedUtc : record.TmdbObservedUtc,
                        MetadataRevision = metadata ? record.MetadataRevision + 1 : record.MetadataRevision
                    };
                    updated++;
                }
            }
            return Task.FromResult(updated);
        }

        public Task<int> InvalidateTmdbAsync(PremiereMediaType mediaType, IReadOnlyCollection<int> tmdbIds,
            DateTimeOffset changedAtUtc, CancellationToken cancellationToken)
        {
            var count = 0;
            lock (_gate)
            {
                foreach (var key in Records.Keys.Where(key => key.Identity.MediaType == mediaType && tmdbIds.Contains(key.Identity.TmdbId)))
                {
                    var row = Records[key];
                    Records[key] = row with { Generation = row.Generation + 1, MetadataInvalidatedUtc = changedAtUtc };
                    count++;
                }
            }
            return Task.FromResult(count);
        }
        public Task<int> InvalidateTvmazeAsync(IReadOnlyCollection<int> showIds, DateTimeOffset changedAtUtc, CancellationToken cancellationToken) => Task.FromResult(0);
        public Task<int> CleanupAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken) => Task.FromResult(0);
    }
}
