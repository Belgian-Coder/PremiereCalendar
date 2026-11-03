using System.Text.Json;
using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class PremiereServiceTests
{
    [Fact]
    public async Task QueuedDegradedReuseAppliesUnkeyedRequestedSort()
    {
        using var files = new DegradedCoverageFiles();
        var date = new DateOnly(2026, 11, 2);
        var trakt = new DegradedTraktClient();
        var first = await DegradedFinalAsync(CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"),
            calendarCache: files.CreateCache(), discoveryProviders: [new TraktDiscoveryProvider(trakt)]), date);
        var snapshot = await files.ReadAsync(date);
        var extra = Assert.Single(first.Items) with { CanonicalId = "movie:902", TmdbId = 902, Title = "Zebra" };
        Assert.True(await files.CreateCache().SetDegradedWeekAsync(date, date, DegradedCacheKey(),
            [.. first.Items, extra], first.UnavailableSources, DateTimeOffset.UtcNow, CancellationToken.None, snapshot.Generation));
        var tmdb = ItemCacheMovie(date);
        var filters = DegradedFilters();
        filters.SortMode = PremiereSortMode.Title;
        filters.SortDirection = SortDirection.Descending;
        Assert.Equal(DegradedCacheKey(), DegradedCacheKey(filters));

        var result = await DegradedFinalAsync(CreateService(tmdb, calendarCache: files.CreateCache(),
            discoveryProviders: [new TraktDiscoveryProvider(trakt)]), date, filters: filters);

        Assert.True(result.FromCache);
        Assert.True(result.IsReusableDegraded);
        Assert.Equal(new[] { 902, 901 }, result.Items.Select(item => item.TmdbId));
        Assert.Equal(new[] { 902, 901 }, result.SourceItems.Select(item => item.TmdbId));
        Assert.Empty(tmdb.MovieCalls);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task WatchmodeReleaseAccessDenialFinishesWithReusableHonestPartialCoverage(int status)
    {
        using var files = new DegradedCoverageFiles();
        var date = new DateOnly(2026, 11, 2);
        var watchmode = new ReviewDeniedWatchmode(status);
        var first = await DegradedFinalAsync(CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"),
            calendarCache: files.CreateCache(), discoveryProviders: [new WatchmodeReleaseDiscoveryProvider(watchmode)]), date);
        Assert.True(first.HasSourceErrors);
        Assert.True(first.IsDegradedResult);
        Assert.True(first.IsReusableDegraded);
        Assert.Equal("watchmode", Assert.Single(first.UnavailableSources).Provider);
        Assert.Single(first.Items);
        var calls = watchmode.ReleaseCalls;
        var second = await DegradedFinalAsync(CreateService(new FakeTmdbClient(), calendarCache: files.CreateCache(),
            discoveryProviders: [new WatchmodeReleaseDiscoveryProvider(watchmode)]), date);
        Assert.True(second.FromCache);
        Assert.True(second.IsReusableDegraded);
        Assert.Equal(calls, watchmode.ReleaseCalls);
    }

    [Theory]
    [InlineData("not_applicable")]
    [InlineData("disabled")]
    [InlineData("not_configured")]
    public async Task VerifiedEmptyArtworkClearsOldPosterEvenWhenAnotherProviderSkips(string skipReason)
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var filters = DegradedFilters();
        await CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"),
            artworkProviders: [new ForceRecordingArtwork("https://art.example/old.jpg")], itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: filters);
        clock.Advance(TimeSpan.FromHours(1));
        await store.InvalidateTmdbAsync(PremiereMediaType.Movie, [901], clock.GetUtcNow(), CancellationToken.None);
        var result = Assert.Single(await CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"),
            artworkProviders: [new ReviewArtworkOutcome(null), new ReviewArtworkOutcome(skipReason)],
            itemEnrichmentCache: coordinator).GetPremieresAsync(date, date, CancellationToken.None, filters: filters));

        Assert.Null(result.PosterUrl);
        var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!)!;
        Assert.True(metadata.ArtworkComplete);
        Assert.Equal(clock.GetUtcNow(), metadata.ArtworkFetchedUtc);
    }

    [Fact]
    public async Task OptionalTvmazeRecoveryRetainsTmdbNetworkIdentity()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        FakeTmdbClient Tmdb()
        {
            var tmdb = DegradedSeriesTmdb(date, discover: true);
            tmdb.TvDetailsById[901] = tmdb.TvDetailsById[901] with
            {
                ExternalIds = new TmdbExternalIds { ImdbId = "tt0000901" },
                Networks = [new TmdbNetwork { Id = 123, Name = "Shared network" }]
            };
            return tmdb;
        }
        var filters = new CalendarFilters { ShowMovies = false };
        await CreateService(Tmdb(), omdb: ItemCacheOmdb("7.1"),
            tvmaze: new FakeTvmazeClient { ThrowOnLookup = true, ThrowOnSearch = true }, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, filters: filters);
        // Make the last-good TVmaze name equal to the independent TMDb source.
        var entry = Assert.Single(store.Records);
        var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(entry.Value.MetadataJson!)!;
        store.Records[entry.Key] = entry.Value with { MetadataJson = JsonSerializer.Serialize(metadata with { NetworkName = "Shared network" }) };
        clock.Advance(TimeSpan.FromMinutes(6));
        var result = Assert.Single(await CreateService(Tmdb(), omdb: ItemCacheOmdb("7.1"), tvmaze: new FakeTvmazeClient(),
            itemEnrichmentCache: coordinator).GetPremieresAsync(date, date, CancellationToken.None, filters: filters));
        Assert.Contains(result.Sources, source => source.Kind == "network" && source.Name == "Shared network" && source.Id == 123);
    }

    private sealed class ReviewArtworkOutcome(string? skipReason) : IArtworkProvider
    {
        public string? OwnedSource => skipReason is null ? "Test artwork" : null;
        public Task<ArtworkCandidate?> GetArtworkAsync(ArtworkRequest request, CancellationToken cancellationToken, bool forceRefresh = false)
            => Task.FromResult<ArtworkCandidate?>(null);
        public Task<ProviderRefreshResult<ArtworkCandidate?>> GetArtworkWithStatusAsync(ArtworkRequest request, CancellationToken cancellationToken, bool forceRefresh = false)
            => Task.FromResult(new ProviderRefreshResult<ArtworkCandidate?>(null, true,
                skipReason is null ? null : new ProviderFailure(skipReason)));
    }

    private sealed class ReviewDeniedWatchmode(int status) : IWatchmodeClient
    {
        public int ReleaseCalls;
        public Task<IReadOnlyList<PremiereSource>> GetTitleSourcesAsync(PremiereMediaType mediaType, int tmdbId,
            string? imdbId, IReadOnlyList<string> regions, CancellationToken cancellationToken, bool forceRefresh = false)
            => Task.FromResult<IReadOnlyList<PremiereSource>>([]);
        public Task<IReadOnlyList<ExternalPremiereCandidate>> GetReleaseCandidatesAsync(DateOnly start, DateOnly end,
            CancellationToken cancellationToken, bool forceRefresh = false)
        {
            ReleaseCalls++;
            return Task.FromException<IReadOnlyList<ExternalPremiereCandidate>>(new ProviderRequestException("watchmode",
                new ProviderFailure("authentication_denied", status, DateTimeOffset.UtcNow.AddMinutes(15))));
        }
    }
}
