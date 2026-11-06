using System.Text.Json;
using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class PremiereServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForcedFilteredMovieRunsFullEnrichmentEvenWhenCachedCoreAndRatingsAreFresh(bool knownCoreCoverAndSources)
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var tmdb = CoreCompleteMovie(date, knownCoreCoverAndSources);
        var omdb = ItemCacheOmdb("7.1");
        omdb.ItemsByImdbId["tt0000901"] = omdb.ItemsByImdbId["tt0000901"] with { Plot = "Original OMDb plot" };
        var tomatoes = new ComponentTomatoesClient { Result = new(83, 91) };
        var dataset = new CoreCompletionDataset { Rating = new("tt0000901", 7.2, 1200, clock.GetUtcNow()) };
        var artwork = new OptionalOwnershipArtwork { Candidate = new("https://cover.example/old.jpg", ArtworkSources.Fanart) };
        var watchmode = new FakeWatchmodeClient { Sources = [new() { Id = 19, Name = "Available", Kind = "flatrate" }] };
        var service = CreateService(tmdb, omdb: omdb, rottenTomatoes: tomatoes, imdbRatingsStore: dataset,
            artworkProviders: [artwork], watchmode: watchmode, sourceRegions: ["US"], itemEnrichmentCache: coordinator);
        await service.GetPremieresAsync(date, date, default, filters: new CalendarFilters { ShowSeries = false });
        var seededForcedDetailCalls = tmdb.MovieDetailCalls.Count(call => call.ForceRefresh);
        clock.Advance(TimeSpan.FromMinutes(1));
        omdb.ItemsByImdbId["tt0000901"] = omdb.ItemsByImdbId["tt0000901"] with { ImdbRating = "8.4", Plot = "Forced OMDb plot" };
        tomatoes.Result = new(94, 96);
        dataset.Rating = new("tt0000901", 8.7, 2000, clock.GetUtcNow());
        artwork.Candidate = new("https://cover.example/fresh.jpg", ArtworkSources.Fanart);

        var result = Assert.Single(await service.GetPremieresAsync(date, date, default, forceRefresh: true,
            filters: new CalendarFilters { ShowSeries = false, MovieFilters = { RuntimeMinMinutes = 90, OriginalLanguages = ["en"] } }));

        Assert.Equal(2, tmdb.MovieDetailCalls.Count);
        Assert.Equal(seededForcedDetailCalls + 1, tmdb.MovieDetailCalls.Count(call => call.ForceRefresh));
        Assert.Equal(2, omdb.Calls.Count);
        Assert.Equal(2, tomatoes.Calls);
        Assert.Equal(2, dataset.Calls);
        Assert.Equal(knownCoreCoverAndSources ? 0 : 2, artwork.Calls);
        Assert.Equal(knownCoreCoverAndSources ? 0 : 2, watchmode.SourceCalls.Count);
        Assert.Equal(8.7, result.ImdbScore);
        Assert.Equal(94, result.RottenTomatoesScore);
        Assert.Equal(96, result.RottenTomatoesAudienceScore);
        Assert.Equal("Forced OMDb plot", result.Overview);
        var record = Assert.Single(store.Records.Values);
        var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(record.MetadataJson!)!;
        Assert.False(metadata.CoreMetadataOnly);
        Assert.Equal(clock.GetUtcNow(), record.MetadataFetchedUtc);
        Assert.Equal(clock.GetUtcNow(), record.RatingsFetchedUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForcedFilteredKnownMismatchSavesFreshCoreAndSkipsOptionalProviders(bool previouslyCached)
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        if (previouslyCached)
            await CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"), itemEnrichmentCache: coordinator)
                .GetPremieresAsync(date, date, default, filters: new CalendarFilters { ShowSeries = false });
        var before = store.Records.Values.SingleOrDefault();
        clock.Advance(TimeSpan.FromMinutes(1));
        var tmdb = ItemCacheMovie(date);
        tmdb.MovieDetailsById[901] = tmdb.MovieDetailsById[901] with { Runtime = 20 };
        var omdb = ItemCacheOmdb("8.4");
        var tomatoes = new ComponentTomatoesClient { Result = new(94, 96) };
        var artwork = new OptionalOwnershipArtwork();
        var watchmode = new FakeWatchmodeClient();
        var service = CreateService(tmdb, omdb: omdb, rottenTomatoes: tomatoes, artworkProviders: [artwork],
            watchmode: watchmode, itemEnrichmentCache: coordinator);
        var result = await service
            .GetPremieresAsync(date, date, default, forceRefresh: true,
                filters: new CalendarFilters { ShowSeries = false, MovieFilters = { RuntimeMinMinutes = 45 } });

        Assert.Empty(result);
        Assert.True(Assert.Single(tmdb.MovieDetailCalls).ForceRefresh);
        Assert.Empty(omdb.Calls);
        Assert.Equal(0, tomatoes.Calls);
        Assert.Equal(0, artwork.Calls);
        Assert.Empty(watchmode.SourceCalls);
        var after = Assert.Single(store.Records.Values);
        Assert.Equal(clock.GetUtcNow(), after.MetadataFetchedUtc);
        Assert.Equal(before?.RatingsFetchedUtc, after.RatingsFetchedUtc);
        Assert.Equal(before?.RatingsJson, after.RatingsJson);
        var currentMetadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(after.MetadataJson!)!;
        var oldMetadata = before?.MetadataJson is { } oldJson ? JsonSerializer.Deserialize<ItemEnrichmentMetadata>(oldJson) : null;
        Assert.Equal(20, currentMetadata.RuntimeMinutes);
        Assert.True(currentMetadata.CoreMetadataOnly);
        Assert.Equal(oldMetadata?.ArtworkFetchedUtc, currentMetadata.ArtworkFetchedUtc);
        Assert.Equal(oldMetadata?.AvailabilityFetchedUtc, currentMetadata.AvailabilityFetchedUtc);
        Assert.Equal(oldMetadata?.PosterUrl, currentMetadata.PosterUrl);

        var suitable = Assert.Single(await service.GetPremieresAsync(date, date, default,
            filters: new CalendarFilters { ShowSeries = false, MovieFilters = { RuntimeMaxMinutes = 60 } }));
        Assert.Equal(20, suitable.RuntimeMinutes);
        Assert.Single(tmdb.MovieDetailCalls);
        Assert.Equal(previouslyCached ? 0 : 1, omdb.Calls.Count);
        Assert.Equal(previouslyCached ? 0 : 1, tomatoes.Calls);
    }

    [Fact]
    public async Task CoreOnlyCompleteMetadataFinalizesCachedOmdbPlotWhileRatingsStayFresh()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var omdb = ItemCacheOmdb("7.1");
        omdb.ItemsByImdbId["tt0000901"] = omdb.ItemsByImdbId["tt0000901"] with { Plot = "Cached matching OMDb plot" };
        await CreateService(CoreCompleteMovie(date, true), omdb: omdb, sourceRegions: ["US"], itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, default, filters: new CalendarFilters { ShowSeries = false });
        var before = Assert.Single(store.Records.Values);
        clock.Advance(TimeSpan.FromHours(1));
        await store.InvalidateTmdbAsync(PremiereMediaType.Movie, [901], clock.GetUtcNow(), default);
        var current = CoreCompleteMovie(date, true);
        var result = Assert.Single(await CreateService(current, omdb: omdb, sourceRegions: ["US"], itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, default, filters: new CalendarFilters { ShowSeries = false,
                MovieFilters = { RuntimeMinMinutes = 90 } }));

        Assert.Equal("Cached matching OMDb plot", result.Overview);
        Assert.Single(omdb.Calls);
        Assert.Single(current.MovieDetailCalls);
        var after = Assert.Single(store.Records.Values);
        var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(after.MetadataJson!)!;
        Assert.False(metadata.CoreMetadataOnly);
        Assert.Equal(result.Overview, metadata.OmdbPlot);
        Assert.Equal(before.RatingsFetchedUtc, after.RatingsFetchedUtc);
        Assert.Equal(before.RatingsJson, after.RatingsJson);
        Assert.Equal(clock.GetUtcNow(), after.MetadataFetchedUtc);
    }

    private static FakeTmdbClient CoreCompleteMovie(DateOnly date, bool knownCoreCoverAndSources)
    {
        var tmdb = ItemCacheMovie(date);
        tmdb.MovieDetailsById[901] = tmdb.MovieDetailsById[901] with { Overview = null,
            PosterPath = knownCoreCoverAndSources ? "/core-cover.jpg" : null,
            WatchProviders = knownCoreCoverAndSources ? new TmdbWatchProviders { Results = new()
            {
                ["US"] = new TmdbWatchProviderRegion { Flatrate = [new TmdbWatchProvider { ProviderId = 9, ProviderName = "Core provider" }] }
            } } : null };
        return tmdb;
    }

    [Fact]
    public async Task ConcurrentForcedMovieCriteriaCannotShareAnIneligibleCoreOnlyOutcome()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var template = ItemCacheMovie(date);
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseEligible = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var detailCalls = 0;
        var tmdb = new FakeTmdbClient { MovieItems = template.MovieItems, MovieDetailOverride = async (_, token) =>
        {
            var call = Interlocked.Increment(ref detailCalls);
            if (call == 1) firstEntered.TrySetResult();
            else secondEntered.TrySetResult();
            await (call == 1 ? release.Task : releaseEligible.Task).WaitAsync(token);
            return template.MovieDetailsById[901] with { Runtime = 20 };
        } };
        var omdb = ItemCacheOmdb("8.4");
        omdb.ItemsByImdbId["tt0000901"] = omdb.ItemsByImdbId["tt0000901"] with
        { Plot = "Eligible OMDb plot" };
        var tomatoes = new ComponentTomatoesClient { Result = new(94, 96) };
        var artwork = new OptionalOwnershipArtwork { Candidate = new("https://cover.example/eligible.jpg", ArtworkSources.Fanart) };
        var watchmode = new FakeWatchmodeClient { Sources = [new() { Id = 19, Name = "Available", Kind = "flatrate" }] };
        PremiereService Service() => CreateService(tmdb, omdb: omdb, rottenTomatoes: tomatoes,
            artworkProviders: [artwork], watchmode: watchmode, itemEnrichmentCache: coordinator);
        var rejectedFilters = new CalendarFilters { ShowSeries = false, MovieFilters = { RuntimeMinMinutes = 45 } };
        var first = Service().GetPremieresAsync(date, date, default, forceRefresh: true, filters: rejectedFilters);
        Task<IReadOnlyList<PremiereItem>>? sameCriteria = null;
        Task<IReadOnlyList<PremiereItem>>? eligible = null;
        try
        {
            await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            sameCriteria = Service().GetPremieresAsync(date, date, default, forceRefresh: true, filters: rejectedFilters);
            eligible = Service().GetPremieresAsync(date, date, default, forceRefresh: true,
                filters: new CalendarFilters { ShowSeries = false, MovieFilters = { RuntimeMaxMinutes = 60 } });
            await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            release.SetResult();
            Assert.Empty(await first.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Empty(await sameCriteria.WaitAsync(TimeSpan.FromSeconds(5)));
            var preflight = Assert.Single(store.Records.Values);
            Assert.True(JsonSerializer.Deserialize<ItemEnrichmentMetadata>(preflight.MetadataJson!)!.CoreMetadataOnly);
            Assert.Null(preflight.RatingsJson);
            Assert.Equal(1, preflight.MetadataRevision);
            releaseEligible.SetResult();
            var result = Assert.Single(await eligible.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(20, result.RuntimeMinutes);
            Assert.Equal(94, result.RottenTomatoesScore);
            Assert.Equal(8.4, result.ImdbScore);
            Assert.Equal("https://cover.example/eligible.jpg", result.PosterUrl);
            Assert.Equal(19, Assert.Single(result.Sources).Id);
            var persisted = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!)!;
            Assert.False(persisted.CoreMetadataOnly);
            Assert.True(persisted.AvailabilityComplete);
            Assert.True(persisted.AvailabilityOwnedByWatchmode);
            Assert.True(persisted.ArtworkComplete);
            Assert.True(persisted.OptionalMetadataComplete);
            Assert.Equal(clock.GetUtcNow(), persisted.AvailabilityFetchedUtc);
            Assert.Equal(clock.GetUtcNow(), persisted.ArtworkFetchedUtc);
            Assert.Equal(result.PosterUrl, persisted.PosterUrl);
            Assert.Equal(19, Assert.Single(persisted.Sources).Id);
            Assert.Equal("Eligible OMDb plot", persisted.OmdbPlot);
            Assert.Null(persisted.OmdbPosterUrl);
            var following = Assert.Single(await Service().GetPremieresAsync(date, date, default,
                filters: new CalendarFilters { ShowSeries = false, MovieFilters = { RuntimeMaxMinutes = 60 } }));
            Assert.Equal(result.PosterUrl, following.PosterUrl);
            Assert.Equal(19, Assert.Single(following.Sources).Id);
            Assert.Equal(2, detailCalls);
            Assert.Single(omdb.Calls);
            Assert.Equal(1, tomatoes.Calls);
            Assert.Equal(1, artwork.Calls);
            Assert.Single(watchmode.SourceCalls);
        }
        finally
        {
            release.TrySetResult();
            releaseEligible.TrySetResult();
            await first.WaitAsync(TimeSpan.FromSeconds(10));
            if (sameCriteria is not null) await sameCriteria.WaitAsync(TimeSpan.FromSeconds(10));
            if (eligible is not null) await eligible.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Fact]
    public async Task CompletedCoreLocalMergeDoesNotBypassFailedAvailabilityCooldown()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var tmdb = CoreCompleteMovie(date, false);
        tmdb.MovieDetailsById[901] = tmdb.MovieDetailsById[901] with { PosterPath = "/known-core.jpg" };
        var omdb = ItemCacheOmdb("7.1");
        omdb.ItemsByImdbId["tt0000901"] = omdb.ItemsByImdbId["tt0000901"] with { Plot = "Locally merged OMDb plot" };
        var unavailable = new FakeWatchmodeClient { SourcesOutcomeSuccessful = false };
        var filters = new CalendarFilters { ShowSeries = false, MovieFilters = { RuntimeMinMinutes = 90 } };
        var service = CreateService(tmdb, omdb: omdb, watchmode: unavailable, itemEnrichmentCache: coordinator);
        var first = Assert.Single(await service.GetPremieresAsync(date, date, default, filters: filters));
        var before = Assert.Single(store.Records.Values);
        var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(before.MetadataJson!)!;
        Assert.Equal("Locally merged OMDb plot", first.Overview);
        Assert.False(metadata.CoreMetadataOnly);
        Assert.False(metadata.AvailabilityComplete);
        clock.Advance(TimeSpan.FromMinutes(1));
        await using var session = await coordinator.CreateSessionAsync(default);
        var identity = new ItemEnrichmentIdentity(PremiereMediaType.Movie, 901);
        await session.PrepareAsync([identity], null, default);
        Assert.False(session.MetadataRefreshNeeded(identity));
        var retained = Assert.Single(await service.GetPremieresAsync(date, date, default, filters: filters));
        Assert.Equal(first.Overview, retained.Overview);
        Assert.Single(unavailable.SourceCalls);
        Assert.Single(omdb.Calls);
        Assert.Single(tmdb.MovieDetailCalls);
        var after = Assert.Single(store.Records.Values);
        Assert.Equal(before.MetadataFetchedUtc, after.MetadataFetchedUtc);
        Assert.Equal(before.RatingsFetchedUtc, after.RatingsFetchedUtc);
        Assert.Equal(metadata.AvailabilityFetchedUtc,
            JsonSerializer.Deserialize<ItemEnrichmentMetadata>(after.MetadataJson!)?.AvailabilityFetchedUtc);
    }

    private sealed class CoreCompletionDataset : IImdbRatingsStore
    {
        public ImdbRatingRecord? Rating;
        public int Calls;
        public Task<ImdbRatingRecord?> GetByImdbIdAsync(string imdbId, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(Rating); }
        public Task ReplaceAllAsync(IEnumerable<ImdbRatingRecord> ratings, DateTimeOffset importedAtUtc, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ImdbDatasetState> GetStateAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task SaveStateAsync(ImdbDatasetState state, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoreKnownCoverAndAvailabilityStillMergeOmdbPlotAndKeepLaterRatingsOnlyMetadataUnchanged(bool filteredPreflight)
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var tmdb = ItemCacheMovie(date);
        tmdb.MovieDetailsById[901] = tmdb.MovieDetailsById[901] with
        {
            Overview = null, PosterPath = "/current-poster.jpg",
            WatchProviders = new TmdbWatchProviders { Results = new()
            {
                ["US"] = new TmdbWatchProviderRegion { Flatrate = [new TmdbWatchProvider { ProviderId = 9, ProviderName = "Core provider" }] }
            } }
        };
        var omdb = ItemCacheOmdb("7.1");
        omdb.ItemsByImdbId["tt0000901"] = omdb.ItemsByImdbId["tt0000901"] with
            { Plot = "Initial OMDb plot", Poster = "https://omdb.example/lower-priority.jpg" };
        var artwork = new OptionalOwnershipArtwork { Successful = false };
        var watchmode = new FakeWatchmodeClient();
        var filters = new CalendarFilters { ShowSeries = false };
        if (filteredPreflight) filters.MovieFilters.RuntimeMinMinutes = 90;
        var service = CreateService(tmdb, omdb: omdb, artworkProviders: [artwork], watchmode: watchmode,
            sourceRegions: ["US"], itemEnrichmentCache: coordinator);

        var result = Assert.Single(await service.GetPremieresAsync(date, date, default, filters: filters));

        Assert.Equal("Initial OMDb plot", result.Overview);
        Assert.EndsWith("/current-poster.jpg", result.PosterUrl!);
        Assert.Equal("TMDb poster", result.ImageSource);
        Assert.Contains("Core provider", result.SourceNames);
        Assert.Empty(watchmode.SourceCalls);
        Assert.Equal(0, artwork.Calls);
        Assert.Single(tmdb.MovieDetailCalls);
        Assert.Single(omdb.Calls);
        var before = Assert.Single(store.Records.Values);
        var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(before.MetadataJson!)!;
        Assert.Equal(result.Overview, metadata.OmdbPlot);
        Assert.False(metadata.CoreMetadataOnly);

        clock.Advance(TimeSpan.FromHours(25));
        omdb.ItemsByImdbId["tt0000901"] = omdb.ItemsByImdbId["tt0000901"] with { Plot = "Ratings-only plot", ImdbRating = "8.6" };
        var ratingsOnly = Assert.Single(await service.GetPremieresAsync(date, date, default, filters: filters));
        Assert.Equal(8.6, ratingsOnly.ImdbScore);
        Assert.Equal(result.Overview, ratingsOnly.Overview);
        var after = Assert.Single(store.Records.Values);
        var unchanged = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(after.MetadataJson!)!;
        Assert.Equal(before.MetadataFetchedUtc, after.MetadataFetchedUtc);
        Assert.Equal(metadata.ArtworkFetchedUtc, unchanged.ArtworkFetchedUtc);
        Assert.Equal(metadata.OmdbPlot, unchanged.OmdbPlot);
        Assert.Single(tmdb.MovieDetailCalls);
        Assert.Equal(2, omdb.Calls.Count);
    }

    [Theory]
    [InlineData("poster")]
    [InlineData("backdrop")]
    [InlineData("withdrawn-poster")]
    [InlineData("empty")]
    public async Task FilteredCoreProofPreservesArtworkOwnershipAndItsClockWhenAnotherProfileCompletesIt(string change)
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var originalTmdb = OptionalOwnershipTmdb(date, false, change is "poster" or "withdrawn-poster" ? null : "/old-backdrop.jpg");
        if (change is "poster" or "withdrawn-poster")
            originalTmdb.MovieDetailsById[901] = originalTmdb.MovieDetailsById[901] with { PosterPath = "/old-poster.jpg" };
        var original = Assert.Single(await CreateService(originalTmdb, omdb: ItemCacheOmdb("7.1"),
            artworkProviders: [new OptionalOwnershipArtwork()], itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, default, filters: new CalendarFilters { ShowSeries = false }));
        var before = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!)!;
        clock.Advance(TimeSpan.FromDays(8));
        var coreClock = clock.GetUtcNow();
        var current = OptionalOwnershipTmdb(date, false, change is "backdrop" or "withdrawn-poster" ? "/new-backdrop.jpg" : null);
        if (change == "poster") current.MovieDetailsById[901] = current.MovieDetailsById[901] with { PosterPath = "/new-poster.jpg" };
        var omdb = ItemCacheOmdb("8.4");
        var provider = new OptionalOwnershipArtwork();
        var service = CreateService(current, omdb: omdb, artworkProviders: [provider], itemEnrichmentCache: coordinator);

        Assert.Empty(await service.GetPremieresAsync(date, date, default, filters: new CalendarFilters
            { ShowSeries = false, MovieFilters = { RuntimeMaxMinutes = 60 } }));
        Assert.Single(current.MovieDetailCalls);
        Assert.Empty(omdb.Calls);
        Assert.Equal(0, provider.Calls);
        var partial = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!)!;
        Assert.True(partial.CoreMetadataOnly);
        Assert.Equal(change == "poster" ? coreClock : before.ArtworkFetchedUtc, partial.ArtworkFetchedUtc);
        clock.Advance(TimeSpan.FromMinutes(1));

        var completed = Assert.Single(await service.GetPremieresAsync(date, date, default, filters: new CalendarFilters
            { ShowSeries = false, MovieFilters = { RuntimeMinMinutes = 90 } }));

        Assert.Single(current.MovieDetailCalls);
        Assert.Single(omdb.Calls);
        Assert.Equal(change == "poster" ? 0 : 1, provider.Calls);
        if (change == "empty")
        {
            Assert.Null(completed.PosterUrl);
            Assert.Null(completed.BackdropUrl);
            Assert.Null(completed.ImageSource);
        }
        else
        {
            Assert.EndsWith(change == "poster" ? "/new-poster.jpg" : "/new-backdrop.jpg", completed.PosterUrl!);
            Assert.Equal(change == "poster" ? "TMDb poster" : "TMDb backdrop", completed.ImageSource);
            Assert.NotEqual(original.PosterUrl, completed.PosterUrl);
        }
        var after = Assert.Single(store.Records.Values);
        var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(after.MetadataJson!)!;
        Assert.Equal(coreClock, after.MetadataFetchedUtc);
        Assert.Equal(coreClock, metadata.ArtworkFetchedUtc);
        Assert.True(metadata.ArtworkComplete);
        Assert.False(metadata.PosterOwnerVerifiedEmpty);
        Assert.False(metadata.CoreMetadataOnly);
    }

    [Fact]
    public async Task FilteredCoreMetadataInvalidationReusesMatchedOmdbPosterAndPlotWithoutRenewingTheirClock()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var original = OptionalOwnershipTmdb(date, false, null);
        var omdb = ItemCacheOmdb("7.1");
        omdb.ItemsByImdbId["tt0000901"] = omdb.ItemsByImdbId["tt0000901"] with
            { Poster = "https://omdb.example/cached.jpg", Plot = "Matched cached plot" };
        await CreateService(original, omdb: omdb, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, default, filters: new CalendarFilters { ShowSeries = false });
        var before = Assert.Single(store.Records.Values);
        var oldMetadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(before.MetadataJson!)!;
        clock.Advance(TimeSpan.FromHours(1));
        await store.InvalidateTmdbAsync(PremiereMediaType.Movie, [901], clock.GetUtcNow(), default);
        var current = OptionalOwnershipTmdb(date, false, null);
        var service = CreateService(current, omdb: omdb, itemEnrichmentCache: coordinator);

        Assert.Empty(await service.GetPremieresAsync(date, date, default, filters: new CalendarFilters
            { ShowSeries = false, MovieFilters = { RuntimeMaxMinutes = 60 } }));
        var result = Assert.Single(await service.GetPremieresAsync(date, date, default, filters: new CalendarFilters
            { ShowSeries = false, MovieFilters = { RuntimeMinMinutes = 90 } }));

        Assert.Single(current.MovieDetailCalls);
        Assert.Single(omdb.Calls);
        Assert.Equal("https://omdb.example/cached.jpg", result.PosterUrl);
        Assert.Equal("Matched cached plot", result.Overview);
        var after = Assert.Single(store.Records.Values);
        var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(after.MetadataJson!)!;
        Assert.Equal(oldMetadata.ArtworkFetchedUtc, metadata.ArtworkFetchedUtc);
        Assert.Equal(before.RatingsFetchedUtc, after.RatingsFetchedUtc);
        Assert.Equal(result.PosterUrl, metadata.OmdbPosterUrl);
        Assert.Equal(result.Overview, metadata.OmdbPlot);
    }

    [Fact]
    public async Task LaterEmptyArtworkProviderDoesNotClearAnEarlierFailureCooldown()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var failing = new OptionalOwnershipArtwork { Successful = false };
        var empty = new OptionalOwnershipArtwork();
        var service = CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"), artworkProviders: [failing, empty],
            itemEnrichmentCache: coordinator);
        await service.GetPremieresAsync(date, date, default, filters: new CalendarFilters { ShowSeries = false });
        Assert.Equal(1, failing.Calls);
        Assert.Equal(1, empty.Calls);

        clock.Advance(TimeSpan.FromMinutes(1));
        await service.GetPremieresAsync(date, date, default, filters: new CalendarFilters { ShowSeries = false,
            MovieFilters = { OriginalLanguages = ["en"] } });

        Assert.Equal(1, failing.Calls);
        Assert.Equal(1, empty.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FilteredPreflightDoesNotTurnAnIncompleteOrWithdrawnOmdbCoverIntoVerifiedReuse(bool ownerVerifiedAbsent)
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var old = ItemCacheLegacyItem(date, clock) with { PosterUrl = "https://omdb.example/retained.jpg", ImageSource = "OMDb poster" };
        await coordinator.ImportAsync([old], default);
        SetOptionalOwnershipMetadata(store, metadata => metadata with
        {
            ArtworkComplete = false, OptionalMetadataComplete = false, AvailabilityComplete = true,
            PosterOwnerVerifiedEmpty = ownerVerifiedAbsent, OmdbPosterUrl = old.PosterUrl
        });
        clock.Advance(TimeSpan.FromHours(1));
        await store.InvalidateTmdbAsync(PremiereMediaType.Movie, [901], clock.GetUtcNow(), default);
        var omdb = ItemCacheOmdb("7.1");
        var fallback = new OptionalOwnershipArtwork { OwnedSource = ArtworkSources.Wikimedia };
        var service = CreateService(ItemCacheMovie(date), omdb: omdb, artworkProviders: [fallback], itemEnrichmentCache: coordinator);
        Assert.Empty(await service.GetPremieresAsync(date, date, default, filters: new CalendarFilters
            { ShowSeries = false, MovieFilters = { RuntimeMaxMinutes = 60 } }));
        var core = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!)!;
        Assert.True(core.CoreMetadataOnly);
        Assert.False(core.CoreOmdbCoverReusable);

        var result = Assert.Single(await service.GetPremieresAsync(date, date, default, filters: new CalendarFilters
            { ShowSeries = false, MovieFilters = { RuntimeMinMinutes = 90 } }));

        Assert.Empty(omdb.Calls);
        Assert.Equal(1, fallback.Calls);
        Assert.Equal(ownerVerifiedAbsent ? null : old.PosterUrl, result.PosterUrl);
    }
}
