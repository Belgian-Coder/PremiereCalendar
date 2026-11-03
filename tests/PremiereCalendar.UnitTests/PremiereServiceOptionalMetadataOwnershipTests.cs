using System.Text.Json;
using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class PremiereServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AvailabilityRetryPreservesLegacySourcesButCanClearTrackedWatchmodeSources(bool tracked)
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var old = ItemCacheLegacyItem(date, clock) with
        {
            Sources = [new() { Id = 8, Name = "Saved provider", Kind = "flatrate" },
                new() { Id = 9, Name = "TMDb network", Kind = "network" }]
        };
        await coordinator.ImportAsync([old], CancellationToken.None);
        var before = Assert.Single(store.Records.Values);
        SetOptionalOwnershipMetadata(store, metadata => metadata with
        {
            OptionalFreshnessTracked = tracked, AvailabilityComplete = false,
            AvailabilityOwnedByWatchmode = tracked,
            OptionalMetadataComplete = true, TvmazeComplete = true, ArtworkComplete = true,
            AvailabilityFetchedUtc = before.MetadataFetchedUtc
        });
        var watchmode = new FakeWatchmodeClient();
        var tmdb = ItemCacheMovie(date);

        var result = Assert.Single(await CreateService(tmdb, omdb: ItemCacheOmdb("7.1"), watchmode: watchmode,
            itemEnrichmentCache: coordinator).GetPremieresAsync(date, date, CancellationToken.None,
                filters: new CalendarFilters { ShowSeries = false }));

        Assert.Equal(!tracked, result.Sources.Any(source => source.Id == 8));
        Assert.Contains(result.Sources, source => source.Id == 9 && source.Kind == "network");
        Assert.Equal(tracked ? 1 : 0, watchmode.SourceCalls.Count);
        Assert.Empty(tmdb.MovieDetailCalls);
        var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!)!;
        Assert.True(metadata.AvailabilityComplete);
        Assert.Equal(tracked, metadata.AvailabilityOwnedByWatchmode);
        Assert.Equal(tracked ? clock.GetUtcNow() : before.MetadataFetchedUtc, metadata.AvailabilityFetchedUtc);
        Assert.Equal(before.MetadataFetchedUtc, Assert.Single(store.Records.Values).MetadataFetchedUtc);
    }

    [Theory]
    [InlineData("TMDb poster", false)]
    [InlineData("TMDb poster", true)]
    [InlineData("OMDb poster", false)]
    [InlineData("OMDb poster", true)]
    [InlineData(ArtworkSources.TvmazeImage, false)]
    [InlineData(ArtworkSources.TvmazeImage, true)]
    public async Task ConfirmedRemovedKnownCoverRetriesArtworkAndIsRetainedOnlyWhileProviderFails(string source, bool replacement)
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var series = source == ArtworkSources.TvmazeImage;
        var filters = new CalendarFilters { ShowSeries = series, ShowMovies = !series };
        var old = ItemCacheLegacyItem(date, clock) with
        {
            CanonicalId = series ? "series:901" : "movie:901", MediaType = series ? PremiereMediaType.Series : PremiereMediaType.Movie,
            Type = series ? PremiereItemType.SeriesPremiere : PremiereItemType.MovieFirstRelease,
            PosterUrl = "https://old.example/poster.jpg", ImageSource = source
        };
        await coordinator.ImportAsync([old], CancellationToken.None);
        var before = Assert.Single(store.Records.Values);
        SetOptionalOwnershipMetadata(store, metadata => metadata with
        {
            OptionalFreshnessTracked = true, AvailabilityComplete = true,
            OptionalMetadataComplete = false, TvmazeComplete = true, ArtworkComplete = false,
            PosterOwnerVerifiedEmpty = true,
            ArtworkFetchedUtc = before.MetadataFetchedUtc
        });
        var failed = new OptionalOwnershipArtwork { Successful = false };
        var first = Assert.Single(await CreateService(OptionalOwnershipTmdb(date, series, null), omdb: ItemCacheOmdb("7.1"),
            artworkProviders: [failed], itemEnrichmentCache: coordinator).GetPremieresAsync(date, date, CancellationToken.None,
                filters: filters));
        Assert.Equal(1, failed.Calls);
        Assert.Equal(old.PosterUrl, first.PosterUrl);
        var preserved = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!)!;
        Assert.False(preserved.ArtworkComplete);
        Assert.True(preserved.PosterOwnerVerifiedEmpty);
        Assert.Equal(before.MetadataFetchedUtc, preserved.ArtworkFetchedUtc);
        clock.Advance(TimeSpan.FromMinutes(6));
        var recovered = new OptionalOwnershipArtwork
        {
            Candidate = replacement ? new("https://fresh.example/poster.jpg", ArtworkSources.Fanart) : null
        };

        var result = Assert.Single(await CreateService(OptionalOwnershipTmdb(date, series, null), omdb: ItemCacheOmdb("7.1"),
            artworkProviders: [recovered], itemEnrichmentCache: coordinator).GetPremieresAsync(date, date, CancellationToken.None,
                filters: filters));

        Assert.Equal(1, recovered.Calls);
        Assert.Equal(recovered.Candidate?.Url, result.PosterUrl);
        Assert.Equal(recovered.Candidate?.Source, result.ImageSource);
        var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!)!;
        Assert.True(metadata.ArtworkComplete);
        Assert.False(metadata.PosterOwnerVerifiedEmpty);
        Assert.Equal(clock.GetUtcNow(), metadata.ArtworkFetchedUtc);
        Assert.Equal(before.MetadataFetchedUtc, Assert.Single(store.Records.Values).MetadataFetchedUtc);
    }

    [Theory]
    [InlineData("TMDb poster")]
    [InlineData("OMDb poster")]
    [InlineData(ArtworkSources.TvmazeImage)]
    public async Task NonOwnerEmptyPreservesUncontactedPrimaryPosterAndItsClock(string source)
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var series = source == ArtworkSources.TvmazeImage;
        var old = ItemCacheLegacyItem(date, clock) with
        {
            CanonicalId = series ? "series:901" : "movie:901", MediaType = series ? PremiereMediaType.Series : PremiereMediaType.Movie,
            Type = series ? PremiereItemType.SeriesPremiere : PremiereItemType.MovieFirstRelease,
            PosterUrl = "https://owner.example/saved.jpg", ImageSource = source
        };
        await coordinator.ImportAsync([old], CancellationToken.None);
        var before = Assert.Single(store.Records.Values);
        SetOptionalOwnershipMetadata(store, metadata => metadata with
        {
            OptionalFreshnessTracked = true, AvailabilityComplete = true, TvmazeComplete = true,
            OptionalMetadataComplete = false, ArtworkComplete = false, ArtworkFetchedUtc = before.MetadataFetchedUtc
        });
        var tmdb = OptionalOwnershipTmdb(date, series, null);
        var nonOwner = new OptionalOwnershipArtwork { OwnedSource = ArtworkSources.Wikimedia };

        var result = Assert.Single(await CreateService(tmdb, omdb: ItemCacheOmdb("7.1"),
            artworkProviders: [nonOwner], itemEnrichmentCache: coordinator).GetPremieresAsync(date, date, CancellationToken.None,
                filters: new CalendarFilters { ShowSeries = series, ShowMovies = !series }));

        Assert.Equal(1, nonOwner.Calls);
        Assert.Equal(old.PosterUrl, result.PosterUrl);
        Assert.Equal(source, result.ImageSource);
        Assert.Empty(tmdb.MovieDetailCalls);
        Assert.Empty(tmdb.TvDetailCalls);
        var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!)!;
        Assert.False(metadata.PosterOwnerVerifiedEmpty);
        Assert.Equal(before.MetadataFetchedUtc, metadata.ArtworkFetchedUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReusedOrDisabledOmdbOwnerIsNotClearedByAnotherProvidersEmptyResult(bool disabled)
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var old = ItemCacheLegacyItem(date, clock) with
            { WikidataId = "Q901", PosterUrl = "https://owner.example/omdb.jpg", ImageSource = "OMDb poster" };
        await coordinator.ImportAsync([old], CancellationToken.None);
        var before = Assert.Single(store.Records.Values);
        SetOptionalOwnershipMetadata(store, metadata => metadata with
        {
            OptionalFreshnessTracked = true, AvailabilityComplete = true, ArtworkComplete = false,
            OptionalMetadataComplete = false, ArtworkFetchedUtc = before.MetadataFetchedUtc, OmdbPosterUrl = old.PosterUrl
        });
        clock.Advance(TimeSpan.FromDays(8));
        var pair = Assert.Single(store.Records);
        var ratings = new ItemEnrichmentRatings(7.1, 1000, null, null, null, old.ImdbId, old.WikidataId, old.Title, date.Year)
        {
            ProviderFreshnessTracked = true, ExternalFetchedUtc = clock.GetUtcNow(), ExternalRequired = true,
            TomatoesRequired = false, ExternalValues = new(ImdbScore: 7.1, ImdbVoteCount: 1000)
        };
        store.Records[pair.Key] = pair.Value with { RatingsJson = JsonSerializer.Serialize(ratings), RatingsFetchedUtc = clock.GetUtcNow() };
        var omdb = new OptionalSkippedOmdb(disabled);
        var artwork = new OptionalOwnershipArtwork { OwnedSource = ArtworkSources.Wikimedia };

        var result = Assert.Single(await CreateService(ItemCacheMovie(date), omdb: omdb, artworkProviders: [artwork], itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, forceRefresh: disabled,
                filters: new CalendarFilters { ShowSeries = false }));

        Assert.Equal(disabled ? 1 : 0, omdb.Calls);
        Assert.Equal(1, artwork.Calls);
        Assert.Equal(old.PosterUrl, result.PosterUrl);
        var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!)!;
        Assert.False(metadata.PosterOwnerVerifiedEmpty);
        Assert.Equal(before.MetadataFetchedUtc, metadata.ArtworkFetchedUtc);
    }

    [Fact]
    public async Task DisabledArtworkOwnerIsRetainedWhenAnotherProviderVerifiesEmpty()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var old = ItemCacheLegacyItem(date, clock) with { PosterUrl = "https://owner.example/fanart.jpg", ImageSource = ArtworkSources.Fanart };
        await coordinator.ImportAsync([old], CancellationToken.None);
        var before = Assert.Single(store.Records.Values);
        SetOptionalOwnershipMetadata(store, metadata => metadata with
        {
            OptionalFreshnessTracked = true, AvailabilityComplete = true, TvmazeComplete = true,
            OptionalMetadataComplete = false, ArtworkComplete = false, ArtworkFetchedUtc = before.MetadataFetchedUtc
        });
        var owner = new OptionalOwnershipArtwork { OwnedSource = ArtworkSources.Fanart, SkipReason = "disabled" };
        var other = new OptionalOwnershipArtwork { OwnedSource = ArtworkSources.Wikimedia };

        var result = Assert.Single(await CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"),
            artworkProviders: [owner, other], itemEnrichmentCache: coordinator).GetPremieresAsync(date, date, CancellationToken.None,
                filters: new CalendarFilters { ShowSeries = false }));

        Assert.Equal(1, owner.Calls);
        Assert.Equal(1, other.Calls);
        Assert.Equal(old.PosterUrl, result.PosterUrl);
        var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!)!;
        Assert.False(metadata.PosterOwnerVerifiedEmpty);
        Assert.Equal(before.MetadataFetchedUtc, metadata.ArtworkFetchedUtc);
    }

    [Fact]
    public async Task VerifiedEmptyArtworkOwnerRemainsAbsentWhenSkippedDuringLaterFallbackRecovery()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var original = ItemCacheLegacyItem(date, clock) with { PosterUrl = "https://owner.example/fanart.jpg", ImageSource = ArtworkSources.Fanart };
        await coordinator.ImportAsync([original], CancellationToken.None);
        var before = Assert.Single(store.Records.Values);
        SetOptionalOwnershipMetadata(store, metadata => metadata with
        {
            OptionalFreshnessTracked = true, AvailabilityComplete = true, TvmazeComplete = true,
            OptionalMetadataComplete = false, ArtworkComplete = false, ArtworkFetchedUtc = before.MetadataFetchedUtc
        });
        await CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"), artworkProviders:
            [new OptionalOwnershipArtwork { OwnedSource = ArtworkSources.Fanart },
                new OptionalOwnershipArtwork { OwnedSource = ArtworkSources.Wikimedia, Successful = false }], itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: new CalendarFilters { ShowSeries = false });
        var failed = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!)!;
        Assert.True(failed.PosterOwnerVerifiedEmpty);
        Assert.Equal(original.PosterUrl, failed.PosterUrl);
        Assert.Equal(before.MetadataFetchedUtc, failed.ArtworkFetchedUtc);
        clock.Advance(TimeSpan.FromMinutes(6));

        var result = Assert.Single(await CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"), artworkProviders:
            [new OptionalOwnershipArtwork { OwnedSource = ArtworkSources.Fanart, SkipReason = "disabled" },
                new OptionalOwnershipArtwork { OwnedSource = ArtworkSources.Wikimedia }], itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: new CalendarFilters { ShowSeries = false }));

        Assert.Null(result.PosterUrl);
        var cleared = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!)!;
        Assert.False(cleared.PosterOwnerVerifiedEmpty);
        Assert.Equal(clock.GetUtcNow(), cleared.ArtworkFetchedUtc);
    }

    [Theory]
    [InlineData("TMDb poster")]
    [InlineData("TMDb backdrop")]
    [InlineData("OMDb poster")]
    [InlineData(ArtworkSources.TvmazeImage)]
    public async Task VerifiedOwnerAbsenceSurvivesFailedFallbackAndLaterClearsWithoutCoreRefetch(string source)
    {
        var date = new DateOnly(2026, 11, 2);
        var series = source == ArtworkSources.TvmazeImage;
        var filters = new CalendarFilters { ShowSeries = series, ShowMovies = !series };
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var firstTmdb = OptionalOwnershipTmdb(date, series, source == "TMDb backdrop" ? "/old-backdrop.jpg" : null);
        if (source == "TMDb poster") firstTmdb.MovieDetailsById[901] = firstTmdb.MovieDetailsById[901] with { PosterPath = "/old-poster.jpg" };
        var firstOmdb = ItemCacheOmdb("7.1");
        if (source == "OMDb poster") firstOmdb.ItemsByImdbId["tt0000901"] = firstOmdb.ItemsByImdbId["tt0000901"] with { Poster = "https://owner.example/omdb.jpg" };
        var firstTvmaze = new FakeTvmazeClient { LookupResult = source == ArtworkSources.TvmazeImage
            ? new TvmazeShow { Id = 91, Image = new TvmazeImage { Original = "https://owner.example/tvmaze.jpg" } } : null };
        var original = Assert.Single(await CreateService(firstTmdb, tvmaze: firstTvmaze, omdb: firstOmdb,
            artworkProviders: [new OptionalOwnershipArtwork()], itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: filters));
        Assert.Equal(source, original.ImageSource);
        var before = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!)!;
        clock.Advance(TimeSpan.FromDays(8));

        var retained = Assert.Single(await CreateService(OptionalOwnershipTmdb(date, series, null), omdb: ItemCacheOmdb("8.4"),
            artworkProviders: [new OptionalOwnershipArtwork { Successful = false }], itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: filters));
        var failed = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!)!;
        Assert.Equal(original.PosterUrl, retained.PosterUrl);
        Assert.True(failed.PosterOwnerVerifiedEmpty);
        Assert.False(failed.ArtworkComplete);
        Assert.Equal(before.ArtworkFetchedUtc, failed.ArtworkFetchedUtc);
        clock.Advance(TimeSpan.FromMinutes(6));
        var noCore = OptionalOwnershipTmdb(date, series, null);
        var recovered = new OptionalOwnershipArtwork { OwnedSource = ArtworkSources.Wikimedia };

        var cleared = Assert.Single(await CreateService(noCore, omdb: ItemCacheOmdb("8.4"),
            artworkProviders: [recovered], itemEnrichmentCache: coordinator).GetPremieresAsync(date, date, CancellationToken.None, filters: filters));

        Assert.Equal(1, recovered.Calls);
        Assert.Empty(noCore.MovieDetailCalls);
        Assert.Empty(noCore.TvDetailCalls);
        Assert.Null(cleared.PosterUrl);
        var complete = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!)!;
        Assert.False(complete.PosterOwnerVerifiedEmpty);
        Assert.Equal(clock.GetUtcNow(), complete.ArtworkFetchedUtc);
    }

    [Fact]
    public async Task NonForcedSevenDayArtworkRetryBypassesRetainedPosterAndCallsProvider()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        await coordinator.ImportAsync([ItemCacheLegacyItem(date, clock) with
            { PosterUrl = "https://owner.example/old.jpg", ImageSource = ArtworkSources.Fanart }], CancellationToken.None);
        SetOptionalOwnershipMetadata(store, metadata => metadata with { ArtworkComplete = false, OptionalMetadataComplete = false });
        clock.Advance(TimeSpan.FromDays(8));
        var provider = new OptionalOwnershipArtwork
            { OwnedSource = ArtworkSources.Fanart, Candidate = new("https://owner.example/new.jpg", ArtworkSources.Fanart) };

        var result = Assert.Single(await CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("8.4"),
            artworkProviders: [provider], itemEnrichmentCache: coordinator).GetPremieresAsync(date, date, CancellationToken.None,
                filters: new CalendarFilters { ShowSeries = false }));

        Assert.Equal(1, provider.Calls);
        Assert.All(provider.Forced, forced => Assert.True(forced));
        Assert.Equal(provider.Candidate!.Url, result.PosterUrl);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProjectionMaintenanceRetainsAbsenceProofOnlyForUnchangedPosterAndOwner(bool replacement)
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var original = ItemCacheLegacyItem(date, clock) with { PosterUrl = "https://owner.example/old.jpg", ImageSource = "TMDb poster" };
        await coordinator.ImportAsync([original], CancellationToken.None);
        SetOptionalOwnershipMetadata(store, metadata => metadata with { PosterOwnerVerifiedEmpty = true });
        var session = await coordinator.CreateSessionAsync(CancellationToken.None);
        await session.PrepareAsync([new(PremiereMediaType.Movie, 901)], null, CancellationToken.None);
        session.SynchronizeMaintainedProjection([replacement ? original with
            { PosterUrl = "https://owner.example/new.jpg", ImageSource = ArtworkSources.Fanart } : original]);
        await session.FlushAsync(CancellationToken.None);

        var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!)!;
        Assert.Equal(!replacement, metadata.PosterOwnerVerifiedEmpty);
    }

    [Fact]
    public async Task NewlyFetchedOmdbPosterStillShortCircuitsIncompleteArtworkFallback()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        await coordinator.ImportAsync([ItemCacheLegacyItem(date, clock) with
            { PosterUrl = "https://old.example/poster.jpg", ImageSource = "OMDb poster" }], CancellationToken.None);
        SetOptionalOwnershipMetadata(store, metadata => metadata with { ArtworkComplete = false, OptionalMetadataComplete = false });
        clock.Advance(TimeSpan.FromDays(8));
        var omdb = ItemCacheOmdb("8.4");
        omdb.ItemsByImdbId["tt0000901"] = omdb.ItemsByImdbId["tt0000901"] with { Poster = "https://fresh.example/omdb.jpg" };
        var artwork = new OptionalOwnershipArtwork { Successful = false };

        var result = Assert.Single(await CreateService(ItemCacheMovie(date), omdb: omdb,
            artworkProviders: [artwork], itemEnrichmentCache: coordinator).GetPremieresAsync(date, date, CancellationToken.None,
                filters: new CalendarFilters { ShowSeries = false }));

        Assert.Equal("https://fresh.example/omdb.jpg", result.PosterUrl);
        Assert.Equal("OMDb poster", result.ImageSource);
        Assert.Equal(0, artwork.Calls);
        Assert.Single(omdb.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SuccessfulTmdbVerifiedEmptyBackdropClearsOldFallbackAndAdvancesArtworkClock(bool series)
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var filters = new CalendarFilters { ShowSeries = series, ShowMovies = !series };
        var first = Assert.Single(await CreateService(OptionalOwnershipTmdb(date, series, "/old-backdrop.jpg"),
            omdb: ItemCacheOmdb("7.1"), artworkProviders: [new OptionalOwnershipArtwork()], itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: filters));
        Assert.NotNull(first.BackdropUrl);
        Assert.Equal(first.BackdropUrl, first.PosterUrl);
        var before = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!)!;
        clock.Advance(TimeSpan.FromDays(8));
        var empty = new OptionalOwnershipArtwork();

        var result = Assert.Single(await CreateService(OptionalOwnershipTmdb(date, series, null), omdb: ItemCacheOmdb("8.4"),
            artworkProviders: [empty], itemEnrichmentCache: coordinator).GetPremieresAsync(date, date, CancellationToken.None, filters: filters));

        Assert.Null(result.BackdropUrl);
        Assert.Null(result.PosterUrl);
        Assert.Null(result.ImageSource);
        Assert.Equal(1, empty.Calls);
        var after = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!)!;
        Assert.Null(after.BackdropUrl);
        Assert.Equal(clock.GetUtcNow(), after.ArtworkFetchedUtc);
        Assert.NotEqual(before.ArtworkFetchedUtc, after.ArtworkFetchedUtc);
        Assert.Equal(clock.GetUtcNow(), Assert.Single(store.Records.Values).MetadataFetchedUtc);
    }

    [Theory]
    [InlineData(false, false, "empty")]
    [InlineData(false, true, "empty")]
    [InlineData(true, false, "empty")]
    [InlineData(true, true, "empty")]
    [InlineData(false, false, "skipped")]
    [InlineData(false, true, "skipped")]
    [InlineData(true, false, "skipped")]
    [InlineData(true, true, "skipped")]
    [InlineData(false, false, "failed")]
    [InlineData(false, true, "failed")]
    [InlineData(true, false, "failed")]
    [InlineData(true, true, "failed")]
    public async Task CurrentVerifiedBackdropRefreshesPosterFallbackWithoutReusingOlderImageOrClock(bool force, bool changed, string fallback)
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var original = Assert.Single(await CreateService(OptionalOwnershipTmdb(date, false, "/backdrop-1.jpg"),
            omdb: ItemCacheOmdb("7.1"), artworkProviders: [new OptionalOwnershipArtwork()], itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: new CalendarFilters { ShowSeries = false }));
        var before = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!)!;
        clock.Advance(TimeSpan.FromDays(8));
        var provider = new OptionalOwnershipArtwork
        {
            Successful = fallback != "failed", SkipReason = fallback == "skipped" ? "not_configured" : null
        };
        var path = changed ? "/backdrop-2.jpg" : "/backdrop-1.jpg";

        var result = Assert.Single(await CreateService(OptionalOwnershipTmdb(date, false, path), omdb: ItemCacheOmdb("8.4"),
            artworkProviders: [provider], itemEnrichmentCache: coordinator).GetPremieresAsync(date, date, CancellationToken.None,
                forceRefresh: force, filters: new CalendarFilters { ShowSeries = false }));

        Assert.Equal(1, provider.Calls);
        Assert.EndsWith(path, result.PosterUrl!);
        Assert.Equal(result.BackdropUrl, result.PosterUrl);
        Assert.Equal("TMDb backdrop", result.ImageSource);
        Assert.Equal(!changed, result.PosterUrl == original.PosterUrl);
        var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!)!;
        Assert.Equal(fallback == "failed" ? before.ArtworkFetchedUtc : clock.GetUtcNow(), metadata.ArtworkFetchedUtc);
        Assert.Equal(fallback != "failed", metadata.ArtworkComplete);
        Assert.False(metadata.PosterOwnerVerifiedEmpty);
    }

    [Fact]
    public async Task FirstVerifiedBackdropAdvancesArtworkClockWhenEveryFallbackProviderSkips()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var skipped = new OptionalOwnershipArtwork { SkipReason = "not_configured" };

        var result = Assert.Single(await CreateService(OptionalOwnershipTmdb(date, false, "/first-backdrop.jpg"),
            omdb: ItemCacheOmdb("7.1"), artworkProviders: [skipped], itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: new CalendarFilters { ShowSeries = false }));

        Assert.Equal(1, skipped.Calls);
        Assert.EndsWith("/first-backdrop.jpg", result.PosterUrl!);
        Assert.Equal("TMDb backdrop", result.ImageSource);
        var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!)!;
        Assert.True(metadata.ArtworkComplete);
        Assert.Equal(clock.GetUtcNow(), metadata.ArtworkFetchedUtc);
    }

    [Fact]
    public async Task NewlyWithdrawnTmdbPosterUsesCurrentVerifiedBackdropWhenEveryFallbackProviderSkips()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var first = OptionalOwnershipTmdb(date, false, null);
        first.MovieDetailsById[901] = first.MovieDetailsById[901] with { PosterPath = "/old-poster.jpg" };
        var old = Assert.Single(await CreateService(first, omdb: ItemCacheOmdb("7.1"), itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: new CalendarFilters { ShowSeries = false }));
        Assert.Equal("TMDb poster", old.ImageSource);
        clock.Advance(TimeSpan.FromDays(8));
        var skipped = new OptionalOwnershipArtwork { SkipReason = "not_configured" };

        var result = Assert.Single(await CreateService(OptionalOwnershipTmdb(date, false, "/replacement-backdrop.jpg"),
            omdb: ItemCacheOmdb("8.4"), artworkProviders: [skipped], itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: new CalendarFilters { ShowSeries = false }));

        Assert.Equal(1, skipped.Calls);
        Assert.EndsWith("/replacement-backdrop.jpg", result.PosterUrl!);
        Assert.NotEqual(old.PosterUrl, result.PosterUrl);
        Assert.Equal("TMDb backdrop", result.ImageSource);
        var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!)!;
        Assert.False(metadata.PosterOwnerVerifiedEmpty);
        Assert.True(metadata.ArtworkComplete);
        Assert.Equal(clock.GetUtcNow(), metadata.ArtworkFetchedUtc);
    }

    [Fact]
    public async Task SkippedOptionalTvmazeLookupPreservesKnownShowIdPosterAndSuccessfulClocks()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var old = ItemCacheLegacyItem(date, clock) with
        {
            CanonicalId = "series:901", MediaType = PremiereMediaType.Series, Type = PremiereItemType.SeriesPremiere,
            TvmazeUrl = "https://www.tvmaze.com/shows/91/fixture", PosterUrl = "https://owner.example/tvmaze.jpg",
            ImageSource = ArtworkSources.TvmazeImage
        };
        await coordinator.ImportAsync([old], CancellationToken.None);
        var before = Assert.Single(store.Records.Values);
        SetOptionalOwnershipMetadata(store, metadata => metadata with
        {
            OptionalFreshnessTracked = true, AvailabilityComplete = true, TvmazeComplete = false,
            ArtworkComplete = true, OptionalMetadataComplete = false, TvmazeShowId = 91,
            TvmazeFetchedUtc = before.MetadataFetchedUtc, ArtworkFetchedUtc = before.MetadataFetchedUtc
        });
        var skipped = new OptionalSkippedTvmaze();
        var tmdb = OptionalOwnershipTmdb(date, true, null);
        var service = new PremiereService(tmdb, ItemCacheOmdb("7.1"), skipped, new FakeWatchmodeClient(), new NullCalendarCache(),
            new TrailerSelector(), new RatingMapper(), [], [], Microsoft.Extensions.Options.Options.Create(new PremiereCalendar.Options.TmdbOptions { SourceRegions = [] }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<PremiereService>.Instance, itemEnrichmentCache: coordinator);

        var result = Assert.Single(await service.GetPremieresAsync(date, date, CancellationToken.None,
            filters: new CalendarFilters { ShowMovies = false }));

        Assert.True(skipped.Calls > 0);
        Assert.Empty(tmdb.TvDetailCalls);
        Assert.Equal(old.PosterUrl, result.PosterUrl);
        var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!)!;
        Assert.Equal(91, metadata.TvmazeShowId);
        Assert.Equal(before.MetadataFetchedUtc, metadata.TvmazeFetchedUtc);
        Assert.Equal(before.MetadataFetchedUtc, metadata.ArtworkFetchedUtc);
    }

    [Fact]
    public async Task FailedTmdbDetailsPreserveExistingBackdropAndItsClock()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var first = Assert.Single(await CreateService(OptionalOwnershipTmdb(date, false, "/old-backdrop.jpg"),
            omdb: ItemCacheOmdb("7.1"), itemEnrichmentCache: coordinator).GetPremieresAsync(date, date,
                CancellationToken.None, filters: new CalendarFilters { ShowSeries = false }));
        var before = Assert.Single(store.Records.Values);
        clock.Advance(TimeSpan.FromDays(8));
        var template = ItemCacheMovie(date);
        var failed = new FakeTmdbClient { MovieItems = template.MovieItems,
            MovieDetailExceptions = new() { [901] = new ExternalApiException("Fixture TMDb failure") } };

        var result = Assert.Single(await CreateService(failed, omdb: ItemCacheOmdb("8.4"), itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: new CalendarFilters { ShowSeries = false }));

        Assert.Equal(first.BackdropUrl, result.BackdropUrl);
        Assert.Equal(first.PosterUrl, result.PosterUrl);
        var after = Assert.Single(store.Records.Values);
        Assert.Equal(before.MetadataFetchedUtc, after.MetadataFetchedUtc);
        Assert.Equal(JsonSerializer.Deserialize<ItemEnrichmentMetadata>(before.MetadataJson!)?.ArtworkFetchedUtc,
            JsonSerializer.Deserialize<ItemEnrichmentMetadata>(after.MetadataJson!)?.ArtworkFetchedUtc);
    }

    private static void SetOptionalOwnershipMetadata(MemoryItemEnrichmentStore store, Func<ItemEnrichmentMetadata, ItemEnrichmentMetadata> configure)
    {
        var pair = Assert.Single(store.Records);
        var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(pair.Value.MetadataJson!)!;
        store.Records[pair.Key] = pair.Value with { MetadataJson = JsonSerializer.Serialize(configure(metadata)) };
    }

    private static FakeTmdbClient OptionalOwnershipTmdb(DateOnly date, bool series, string? backdrop)
    {
        var tmdb = series ? new FakeTmdbClient { TvItems = [new TmdbTvDiscoverItem
            { Id = 901, Name = "Ownership fixture", FirstAirDate = date.ToString("yyyy-MM-dd") }] } : ItemCacheMovie(date);
        var details = new TmdbDetailsWithExtras
        {
            Id = 901, BackdropPath = backdrop, Runtime = 112, EpisodeRunTime = [47],
            ExternalIds = new TmdbExternalIds { ImdbId = "tt0000901", WikidataId = "Q901" }
        };
        if (series)
        {
            tmdb.TvDetailsById[901] = details;
            tmdb.SeasonOneEpisodeOneDatesById[901] = date;
        }
        else tmdb.MovieDetailsById[901] = details;
        return tmdb;
    }

    private sealed class OptionalOwnershipArtwork : IArtworkProvider
    {
        public string? OwnedSource { get; set; }
        public bool Successful = true;
        public ArtworkCandidate? Candidate;
        public string? SkipReason;
        public int Calls;
        public List<bool> Forced { get; } = [];
        public Task<ArtworkCandidate?> GetArtworkAsync(ArtworkRequest request, CancellationToken cancellationToken, bool forceRefresh = false)
            => Task.FromResult(Candidate);
        public Task<ProviderRefreshResult<ArtworkCandidate?>> GetArtworkWithStatusAsync(ArtworkRequest request,
            CancellationToken cancellationToken, bool forceRefresh = false)
        {
            Calls++;
            Forced.Add(forceRefresh);
            return Task.FromResult(new ProviderRefreshResult<ArtworkCandidate?>(Candidate, Successful,
                SkipReason is null ? null : new ProviderFailure(SkipReason)));
        }
    }

    private sealed class OptionalSkippedOmdb(bool disabled) : IOmdbClient
    {
        public int Calls;
        public Task<OmdbItem?> GetByImdbIdAsync(string imdbId, CancellationToken cancellationToken, bool forceRefresh = false)
            => Task.FromResult<OmdbItem?>(null);
        public Task<ProviderRefreshResult<OmdbItem?>> GetByImdbIdWithStatusAsync(string imdbId,
            CancellationToken cancellationToken, bool forceRefresh = false)
        {
            Calls++;
            return Task.FromResult(new ProviderRefreshResult<OmdbItem?>(null, true, disabled ? new ProviderFailure("disabled") : null));
        }
    }

    private sealed class OptionalSkippedTvmaze : ITvmazeClient
    {
        public int Calls;
        private Task<ProviderRefreshResult<TvmazeShow?>> Skipped()
        {
            Calls++;
            return Task.FromResult(new ProviderRefreshResult<TvmazeShow?>(null, true, new ProviderFailure("disabled")));
        }
        public Task<ProviderRefreshResult<TvmazeShow?>> LookupShowWithStatusAsync(int? tvdbId, string? imdbId,
            CancellationToken cancellationToken, bool forceRefresh = false) => Skipped();
        public Task<ProviderRefreshResult<TvmazeShow?>> SearchShowByNameWithStatusAsync(string title,
            CancellationToken cancellationToken, bool forceRefresh = false) => Skipped();
        public Task<TvmazeShow?> LookupShowAsync(int? tvdbId, string? imdbId, CancellationToken cancellationToken, bool forceRefresh = false)
            => Task.FromResult<TvmazeShow?>(null);
        public Task<TvmazeShow?> SearchShowByNameAsync(string title, CancellationToken cancellationToken, bool forceRefresh = false)
            => Task.FromResult<TvmazeShow?>(null);
        public Task<IReadOnlyList<TvmazeShowImage>> GetShowImagesAsync(int showId, CancellationToken cancellationToken, bool forceRefresh = false)
            => Task.FromResult<IReadOnlyList<TvmazeShowImage>>([]);
        public Task<IReadOnlyList<TvmazeScheduleEpisode>> GetScheduleAsync(DateOnly date, string? country, bool webSchedule,
            CancellationToken cancellationToken, bool forceRefresh = false) => Task.FromResult<IReadOnlyList<TvmazeScheduleEpisode>>([]);
        public Task<IReadOnlyList<TvmazeShowUpdate>> GetShowUpdatesAsync(TvmazeUpdateWindow since, CancellationToken cancellationToken, bool forceRefresh = false)
            => Task.FromResult<IReadOnlyList<TvmazeShowUpdate>>([]);
    }
}
