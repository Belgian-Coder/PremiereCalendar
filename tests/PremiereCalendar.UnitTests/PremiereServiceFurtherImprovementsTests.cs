using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class PremiereServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CurrentTmdbPosterWinsCoreRefreshWithoutCallingArtworkFallback(bool previouslyCached)
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var filters = new CalendarFilters { ShowSeries = false };
        if (previouslyCached)
        {
            var old = ItemCacheMovie(date);
            old.MovieDetailsById[901] = old.MovieDetailsById[901] with { PosterPath = "/old-poster.jpg" };
            await CreateService(old, omdb: ItemCacheOmdb("7.1"), itemEnrichmentCache: coordinator)
                .GetPremieresAsync(date, date, default, filters: filters);
            clock.Advance(TimeSpan.FromHours(1));
            await store.InvalidateTmdbAsync(PremiereMediaType.Movie, [901], clock.GetUtcNow(), default);
        }
        var current = ItemCacheMovie(date);
        current.MovieDetailsById[901] = current.MovieDetailsById[901] with { PosterPath = "/current-poster.jpg" };
        var artwork = new RecordingArtworkProvider();
        var result = Assert.Single(await CreateService(current, omdb: ItemCacheOmdb("7.1"),
            artworkProviders: [artwork], itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, default, filters: filters));
        Assert.EndsWith("/current-poster.jpg", result.PosterUrl);
        Assert.Equal("TMDb poster", result.ImageSource);
        Assert.Empty(artwork.Calls);
        var record = Assert.Single(store.Records.Values);
        var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(record.MetadataJson!)!;
        Assert.True(metadata.ArtworkComplete);
        Assert.Equal(clock.GetUtcNow(), metadata.ArtworkFetchedUtc);
        Assert.Equal(clock.GetUtcNow(), record.MetadataFetchedUtc);
        Assert.False(metadata.PosterOwnerVerifiedEmpty);
    }

    [Fact]
    public async Task ConfirmedRemovedTmdbPosterIsClearedAfterSuccessfulEmptyFallback()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var filters = new CalendarFilters { ShowSeries = false };
        var old = ItemCacheMovie(date);
        old.MovieDetailsById[901] = old.MovieDetailsById[901] with { PosterPath = "/removed-poster.jpg" };
        await CreateService(old, omdb: ItemCacheOmdb("7.1"), itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, default, filters: filters);
        clock.Advance(TimeSpan.FromHours(1));
        await store.InvalidateTmdbAsync(PremiereMediaType.Movie, [901], clock.GetUtcNow(), default);
        var artwork = new RecordingArtworkProvider(returnCandidate: false);
        var result = Assert.Single(await CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"),
            artworkProviders: [artwork], itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, default, filters: filters));
        Assert.Single(artwork.Calls);
        Assert.Null(result.PosterUrl);
        Assert.Null(result.ImageSource);
        var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!)!;
        Assert.True(metadata.ArtworkComplete);
        Assert.False(metadata.PosterOwnerVerifiedEmpty);
        Assert.Equal(clock.GetUtcNow(), metadata.ArtworkFetchedUtc);
    }

    [Fact]
    public async Task FailedWatchmodeAfterCoreCorrectionRetainsOwnedSourcesAndTheirSuccessClock()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var filters = new CalendarFilters { ShowSeries = false };
        await CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"),
            rottenTomatoes: new ComponentTomatoesClient { Result = new(83, 91) },
            watchmode: new FakeWatchmodeClient { Sources = [new() { Id = 8, Name = "Saved availability", Kind = "flatrate" }] },
            itemEnrichmentCache: coordinator).GetPremieresAsync(date, date, default, filters: filters);
        var before = Assert.Single(store.Records.Values);
        var beforeMetadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(before.MetadataJson!)!;
        var beforeRatings = JsonSerializer.Deserialize<ItemEnrichmentRatings>(before.RatingsJson!)!;
        Assert.True(beforeMetadata.AvailabilityOwnedByWatchmode);
        clock.Advance(TimeSpan.FromHours(1));
        await store.InvalidateTmdbAsync(PremiereMediaType.Movie, [901], clock.GetUtcNow(), default);
        var current = ItemCacheMovie(date);
        current.MovieDetailsById[901] = current.MovieDetailsById[901] with { Runtime = 120, Overview = "Corrected core overview" };
        var unavailable = new FakeWatchmodeClient { ThrowOnSources = true };
        var result = Assert.Single(await CreateService(current, omdb: new FakeOmdbClient { ThrowOnGet = true },
            watchmode: unavailable, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, default, filters: filters));
        Assert.Equal(120, result.RuntimeMinutes);
        Assert.Equal("Corrected core overview", result.Overview);
        Assert.Contains(result.Sources, source => source.Id == 8 && source.Name == "Saved availability");
        Assert.Single(unavailable.SourceCalls);
        var after = Assert.Single(store.Records.Values);
        var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(after.MetadataJson!)!;
        var ratings = JsonSerializer.Deserialize<ItemEnrichmentRatings>(after.RatingsJson!)!;
        Assert.False(metadata.AvailabilityComplete);
        Assert.True(metadata.AvailabilityOwnedByWatchmode);
        Assert.Equal(beforeMetadata.AvailabilityFetchedUtc, metadata.AvailabilityFetchedUtc);
        Assert.Equal(clock.GetUtcNow(), after.MetadataFetchedUtc);
        Assert.Equal(before.RatingsFetchedUtc, after.RatingsFetchedUtc);
        Assert.Equal(beforeRatings.ExternalFetchedUtc, ratings.ExternalFetchedUtc);
        Assert.Equal(beforeRatings.TomatoesFetchedUtc, ratings.TomatoesFetchedUtc);
        Assert.Equal(beforeRatings.ExternalValues, ratings.ExternalValues);
        Assert.Equal(beforeRatings.TomatoesValues, ratings.TomatoesValues);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IneligibleMovieStoresCoreWithoutOptionalCallsAndEligibleProfileCompletesIt(bool languageMismatch)
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var tmdb = ItemCacheMovie(date);
        var omdb = ItemCacheOmdb("7.8");
        var tomatoes = new ComponentTomatoesClient { Result = new(83, 91) };
        var artwork = new RecordingArtworkProvider();
        var watchmode = new FakeWatchmodeClient();
        var rejected = new CalendarFilters { ShowSeries = false };
        if (languageMismatch) rejected.MovieFilters.OriginalLanguages = ["nl"];
        else rejected.MovieFilters.RuntimeMaxMinutes = 60;

        var service = CreateService(tmdb, omdb: omdb, rottenTomatoes: tomatoes,
            artworkProviders: [artwork], watchmode: watchmode, itemEnrichmentCache: coordinator);
        Assert.Empty(await service.GetPremieresAsync(date, date, default, filters: rejected));

        var core = Assert.Single(store.Records.Values);
        var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(core.MetadataJson!)!;
        Assert.Equal("tt0000901", metadata.ImdbId);
        Assert.Equal(112, metadata.RuntimeMinutes);
        Assert.NotNull(core.MetadataFetchedUtc);
        Assert.Null(core.RatingsJson);
        Assert.Single(tmdb.MovieDetailCalls);
        Assert.Empty(omdb.Calls);
        Assert.Equal(0, tomatoes.Calls);
        Assert.Empty(artwork.Calls);
        Assert.Empty(watchmode.SourceCalls);

        clock.Advance(TimeSpan.FromMinutes(1));
        var eligible = new CalendarFilters { ShowSeries = false };
        eligible.MovieFilters.OriginalLanguages = ["en"];
        eligible.MovieFilters.RuntimeMinMinutes = 90;
        var result = Assert.Single(await service.GetPremieresAsync(date, date, default, filters: eligible));
        Assert.Equal("movie:901", result.CanonicalId);
        Assert.Equal("tt0000901", result.ImdbId);
        Assert.Equal(7.8, result.ImdbScore);
        Assert.Equal(83, result.RottenTomatoesScore);
        Assert.Single(tmdb.MovieDetailCalls);
        Assert.Single(omdb.Calls);
        Assert.Equal(1, tomatoes.Calls);
        Assert.Single(artwork.Calls);
        Assert.Single(watchmode.SourceCalls);
        Assert.Equal(core.MetadataFetchedUtc, Assert.Single(store.Records.Values).MetadataFetchedUtc);
    }

    [Fact]
    public async Task OmdbCooldownDoesNotDelayDueDatasetOrTomatoesOrRenewRetainedScores()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock, cacheOptions: new ItemEnrichmentCacheOptions
            { RatingsRefreshHours = 1, RatingsFailureRetryMinutes = 180 });
        var cache = new FakeCalendarCache();
        var filters = new CalendarFilters { ShowSeries = false };
        var initial = ItemCacheOmdb("7.1");
        initial.ItemsByImdbId["tt0000901"] = initial.ItemsByImdbId["tt0000901"] with { Metascore = "78" };
        var dataset = new FakeImdbRatingsStore();
        dataset.Items["tt0000901"] = new("tt0000901", 7.2, 1200, clock.GetUtcNow());
        var tomatoes = new ComponentTomatoesClient { Result = new(83, 91) };
        await CreateService(ItemCacheMovie(date), omdb: initial, rottenTomatoes: tomatoes,
            imdbRatingsStore: dataset, calendarCache: cache, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, default, filters: filters);
        var originalRecord = Assert.Single(store.Records.Values);
        var original = JsonSerializer.Deserialize<ItemEnrichmentRatings>(originalRecord.RatingsJson!)!;

        clock.Advance(TimeSpan.FromHours(2));
        var failed = new FakeOmdbClient { ThrowOnGet = true };
        dataset.Items["tt0000901"] = new("tt0000901", 8.4, 2000, clock.GetUtcNow());
        tomatoes.Result = new(94, 96);
        await CreateService(new FakeTmdbClient(), omdb: failed, rottenTomatoes: tomatoes,
            imdbRatingsStore: dataset, calendarCache: cache, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, default, filters: filters);
        Assert.Single(failed.Calls);

        clock.Advance(TimeSpan.FromMinutes(61));
        dataset.Items["tt0000901"] = new("tt0000901", 8.5, 2500, clock.GetUtcNow());
        tomatoes.Result = new(95, 97);
        var result = Assert.Single(await CreateService(new FakeTmdbClient(), omdb: failed, rottenTomatoes: tomatoes,
            imdbRatingsStore: dataset, calendarCache: cache, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, default, filters: filters));

        var record = Assert.Single(store.Records.Values);
        var ratings = JsonSerializer.Deserialize<ItemEnrichmentRatings>(record.RatingsJson!)!;
        Assert.Single(failed.Calls);
        Assert.Equal(3, tomatoes.Calls);
        Assert.Equal(8.5, result.ImdbScore);
        Assert.Equal(2500, result.ImdbVoteCount);
        Assert.Equal(95, result.RottenTomatoesScore);
        Assert.Equal(97, result.RottenTomatoesAudienceScore);
        Assert.Equal(78, result.MetacriticScore);
        Assert.Equal(original.ExternalValues, ratings.ExternalValues);
        Assert.Equal(original.ExternalFetchedUtc, ratings.ExternalFetchedUtc);
        Assert.Equal(clock.GetUtcNow(), ratings.TomatoesFetchedUtc);
        Assert.Equal(clock.GetUtcNow(), ratings.Dataset?.FetchedUtc);
        Assert.Equal(originalRecord.MetadataFetchedUtc, record.MetadataFetchedUtc);
        Assert.Equal(original.ImdbId, ratings.ImdbId);
        Assert.Equal(original.WikidataId, ratings.WikidataId);
        Assert.Equal(original.Title, ratings.Title);
        Assert.Equal(original.Year, ratings.Year);
    }

    [Fact]
    public async Task TvmazeCooldownDoesNotSuppressArtworkRecoveryOrRenewCoreMetadata()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var legacy = ItemCacheLegacyItem(date, clock) with
        {
            CanonicalId = "series:901", MediaType = PremiereMediaType.Series,
            Type = PremiereItemType.SeriesPremiere, Title = "Ownership fixture"
        };
        await coordinator.ImportAsync([legacy], default);
        var before = Assert.Single(store.Records.Values);
        SetOptionalOwnershipMetadata(store, metadata => metadata with
        {
            OptionalFreshnessTracked = true, AvailabilityComplete = true,
            OptionalMetadataComplete = false, TvmazeComplete = false, ArtworkComplete = false,
            TvmazeFetchedUtc = before.MetadataFetchedUtc, ArtworkFetchedUtc = before.MetadataFetchedUtc
        });
        var identity = new ItemEnrichmentIdentity(PremiereMediaType.Series, 901);
        var session = await coordinator.CreateSessionAsync(default);
        await session.PrepareAsync([identity], null, default);
        session.RecordComponentOutcome(identity, "tvmaze", false, clock.GetUtcNow().AddMinutes(20));
        session.RecordComponentOutcome(identity, "artwork", false, clock.GetUtcNow().AddMinutes(2));
        clock.Advance(TimeSpan.FromMinutes(3));
        var artwork = new RecordingArtworkProvider();
        var tvmaze = new FakeTvmazeClient();
        var result = Assert.Single(await CreateService(OptionalOwnershipTmdb(date, true, null),
            tvmaze: tvmaze, omdb: ItemCacheOmdb("7.1"), artworkProviders: [artwork], itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, default, filters: new CalendarFilters { ShowMovies = false }));
        Assert.Equal("https://assets.fanart.tv/poster.jpg", result.PosterUrl);
        Assert.Single(artwork.Calls);
        Assert.Empty(tvmaze.SearchCalls);
        var record = Assert.Single(store.Records.Values);
        var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(record.MetadataJson!)!;
        Assert.False(metadata.TvmazeComplete);
        Assert.True(metadata.ArtworkComplete);
        Assert.Equal(before.MetadataFetchedUtc, metadata.TvmazeFetchedUtc);
        Assert.Equal(clock.GetUtcNow(), metadata.ArtworkFetchedUtc);
        Assert.Equal(before.MetadataFetchedUtc, record.MetadataFetchedUtc);
    }

    [Fact]
    public async Task CachedAvailabilityAndRatingsStartBeforeEitherProviderCompletes()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock, cacheOptions: new() { RatingsRefreshHours = 1 });
        var cache = new FakeCalendarCache();
        var filters = new CalendarFilters { ShowSeries = false };
        await CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"),
            calendarCache: cache, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, default, filters: filters);
        SetOptionalOwnershipMetadata(store, metadata => metadata with
            { OptionalFreshnessTracked = true, AvailabilityComplete = false, AvailabilityOwnedByWatchmode = true });
        clock.Advance(TimeSpan.FromHours(2));
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var omdb = new OverlappingOmdb(release.Task);
        var watchmode = new OverlappingWatchmode(release.Task);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var load = CreateService(new FakeTmdbClient(), omdb: omdb, watchmode: watchmode,
            calendarCache: cache, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, timeout.Token, filters: filters);
        try
        {
            await Task.WhenAll(omdb.Entered.Task, watchmode.Entered.Task).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(load.IsCompleted);
        }
        finally { release.TrySetResult(); }
        var result = Assert.Single(await load.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(8.4, result.ImdbScore);
        Assert.Contains(result.Sources, source => source.Id == 19);
    }

    [Fact]
    public async Task ItemStoreOutageCircuitDefersProvidersAndPreservesRowsUntilProbeRecovers()
    {
        var clock = new ItemCacheClock();
        var memory = new MemoryItemEnrichmentStore(clock);
        var legacy = ItemCacheLegacyItem(new(2026, 11, 2), clock);
        await ItemCacheCoordinator(memory, clock).ImportAsync([legacy], default);
        var original = Assert.Single(memory.Records.Values);
        var store = new OutageItemStore(memory) { Unavailable = true };
        var coordinator = new ItemEnrichmentCacheCoordinator(store,
            Microsoft.Extensions.Options.Options.Create(new ItemEnrichmentCacheOptions()),
            Microsoft.Extensions.Options.Options.Create(new TmdbOptions()),
            NullLogger<ItemEnrichmentCacheCoordinator>.Instance, timeProvider: clock);
        var identity = original.Identity;
        var providers = 0;
        async Task AttemptAsync()
        {
            var session = await coordinator.CreateSessionAsync(default);
            await session.PrepareAsync([identity], null, default);
            await session.GetAsync(identity, true, (previous, _, _, _) =>
            {
                providers++;
                return Task.FromResult(new ItemEnrichmentRefresh(previous.Metadata, previous.Ratings,
                    RefreshMetadataClock: false, RefreshRatingsClock: false));
            }, default);
        }
        await Assert.ThrowsAsync<ProviderRetryAfterException>(AttemptAsync);
        clock.Advance(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<ProviderRetryAfterException>(AttemptAsync);
        Assert.Equal(1, store.Reads);
        Assert.Equal(0, providers);
        Assert.Equal(original, Assert.Single(memory.Records.Values));
        clock.Advance(TimeSpan.FromSeconds(21));
        store.Unavailable = false;
        await AttemptAsync();
        Assert.Equal(2, store.Reads);
        Assert.Equal(1, providers);
        var after = Assert.Single(memory.Records.Values);
        Assert.Equal(original.MetadataJson, after.MetadataJson);
        Assert.Equal(original.RatingsJson, after.RatingsJson);
        Assert.Equal(original.MetadataFetchedUtc, after.MetadataFetchedUtc);
        Assert.Equal(original.RatingsFetchedUtc, after.RatingsFetchedUtc);
        Assert.Equal(original.Generation, after.Generation);
    }

    private sealed class OverlappingOmdb(Task release) : IOmdbClient
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<OmdbItem?> GetByImdbIdAsync(string imdbId, CancellationToken cancellationToken, bool forceRefresh = false)
        {
            Entered.TrySetResult();
            await release.WaitAsync(cancellationToken);
            return new() { Response = "True", ImdbRating = "8.4", ImdbVotes = "2,000" };
        }
    }

    private sealed class OverlappingWatchmode(Task release) : IWatchmodeClient
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IReadOnlyList<PremiereSource>> GetTitleSourcesAsync(PremiereMediaType mediaType, int tmdbId,
            string? imdbId, IReadOnlyList<string> regions, CancellationToken cancellationToken, bool forceRefresh = false)
        {
            Entered.TrySetResult();
            await release.WaitAsync(cancellationToken);
            return [new() { Id = 19, Name = "Available", Kind = "flatrate" }];
        }
        public Task<IReadOnlyList<ExternalPremiereCandidate>> GetReleaseCandidatesAsync(DateOnly start, DateOnly end,
            CancellationToken cancellationToken, bool forceRefresh = false)
            => Task.FromResult<IReadOnlyList<ExternalPremiereCandidate>>([]);
    }

    private sealed class OutageItemStore(IItemEnrichmentStore inner) : IItemEnrichmentStore
    {
        public bool Unavailable;
        public int Reads;
        public Task<IReadOnlyDictionary<ItemEnrichmentIdentity, ItemEnrichmentRecord>> ReadManyAsync(string partition,
            IReadOnlyCollection<ItemEnrichmentIdentity> identities, bool reserveMissing, CancellationToken cancellationToken)
        {
            Reads++;
            if (Unavailable) throw new IOException("temporary storage outage");
            return inner.ReadManyAsync(partition, identities, reserveMissing, cancellationToken);
        }
        public Task<int> WriteManyAsync(string partition, IReadOnlyList<ItemEnrichmentWrite> writes, CancellationToken cancellationToken)
            => inner.WriteManyAsync(partition, writes, cancellationToken);
        public Task<int> InvalidateTmdbAsync(PremiereMediaType mediaType, IReadOnlyCollection<int> tmdbIds,
            DateTimeOffset changedAtUtc, CancellationToken cancellationToken)
            => inner.InvalidateTmdbAsync(mediaType, tmdbIds, changedAtUtc, cancellationToken);
        public Task<int> InvalidateTvmazeAsync(IReadOnlyCollection<int> showIds, DateTimeOffset changedAtUtc, CancellationToken cancellationToken)
            => inner.InvalidateTvmazeAsync(showIds, changedAtUtc, cancellationToken);
        public Task<int> CleanupAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken)
            => inner.CleanupAsync(cutoffUtc, cancellationToken);
    }
}
