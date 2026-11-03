using System.Text.Json;
using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class PremiereServiceTests
{
    [Fact]
    public async Task UnrelatedOptionalPassCannotGiveWatchmodeOwnershipOfLegacyAvailability()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var old = ItemCacheLegacyItem(date, clock) with
        {
            Sources = [new() { Id = 8, Name = "Legacy provider", Kind = "flatrate" }]
        };
        await coordinator.ImportAsync([old], CancellationToken.None);
        var before = Assert.Single(store.Records.Values);
        SetOptionalOwnershipMetadata(store, metadata => metadata with
        {
            AvailabilityComplete = false, OptionalMetadataComplete = false, ArtworkComplete = false,
            AvailabilityOwnedByWatchmode = false, OptionalFreshnessTracked = false
        });
        // An earlier availability failure defers only that component. Artwork
        // can recover during its cooldown without establishing source ownership.
        var identity = new ItemEnrichmentIdentity(old.MediaType, old.TmdbId);
        var failed = await coordinator.CreateSessionAsync(CancellationToken.None);
        await failed.PrepareAsync([identity], null, CancellationToken.None);
        await failed.GetAsync(identity, true, (_, _, _, _) => Task.FromResult(new ItemEnrichmentRefresh(null, null,
            RefreshMetadataClock: false, AvailabilityFailed: true, RefreshRatingsClock: false)), CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(1));
        var watchmode = new FakeWatchmodeClient();
        var filters = new CalendarFilters { ShowSeries = false };
        await CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"), watchmode: watchmode,
            itemEnrichmentCache: coordinator).GetPremieresAsync(date, date, CancellationToken.None, filters: filters);
        var optional = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!)!;
        Assert.True(optional.OptionalFreshnessTracked);
        Assert.True(optional.ArtworkComplete);
        Assert.False(optional.AvailabilityComplete);
        Assert.False(optional.AvailabilityOwnedByWatchmode);
        Assert.Equal(before.MetadataFetchedUtc, optional.AvailabilityFetchedUtc);
        Assert.Empty(watchmode.SourceCalls);
        clock.Advance(TimeSpan.FromMinutes(6));

        var result = Assert.Single(await CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"), watchmode: watchmode,
            itemEnrichmentCache: coordinator).GetPremieresAsync(date, date, CancellationToken.None, filters: filters));

        Assert.Contains(result.Sources, source => source.Id == 8);
        Assert.Empty(watchmode.SourceCalls);
        var after = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!)!;
        Assert.True(after.AvailabilityComplete);
        Assert.False(after.AvailabilityOwnedByWatchmode);
        Assert.Equal(before.MetadataFetchedUtc, after.AvailabilityFetchedUtc);
    }

    [Fact]
    public async Task AuthoritativeTmdbAvailabilityIsNeverRemovedByAnOptionalWatchmodeRetry()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var tmdb = ItemCacheMovie(date);
        tmdb.MovieDetailsById[901] = tmdb.MovieDetailsById[901] with
        {
            WatchProviders = new TmdbWatchProviders { Results = new() { ["BE"] = new TmdbWatchProviderRegion
            {
                Flatrate = [new TmdbWatchProvider { ProviderId = 8, ProviderName = "TMDb provider" }]
            } } }
        };
        var watchmode = new FakeWatchmodeClient();
        var filters = new CalendarFilters { ShowSeries = false };
        await CreateService(tmdb, omdb: ItemCacheOmdb("7.1"), watchmode: watchmode, itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, CancellationToken.None, forceRefresh: true, filters: filters);
        var before = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!)!;
        Assert.True(before.OptionalFreshnessTracked);
        Assert.False(before.AvailabilityOwnedByWatchmode);
        SetOptionalOwnershipMetadata(store, metadata => metadata with { AvailabilityComplete = false });
        clock.Advance(TimeSpan.FromMinutes(1));

        var result = Assert.Single(await CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"), watchmode: watchmode,
            itemEnrichmentCache: coordinator).GetPremieresAsync(date, date, CancellationToken.None, filters: filters));

        Assert.Contains(result.Sources, source => source.Id == 8);
        Assert.Empty(watchmode.SourceCalls);
        var after = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!)!;
        Assert.False(after.AvailabilityOwnedByWatchmode);
        Assert.Equal(before.AvailabilityFetchedUtc, after.AvailabilityFetchedUtc);
    }

    [Fact]
    public async Task FailedFullAvailabilityRefreshKeepsWatchmodeOwnershipUntilVerifiedReplacement()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var filters = new CalendarFilters { ShowSeries = false };
        await CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"), watchmode: new FakeWatchmodeClient
        {
            Sources = [new() { Id = 8, Name = "Watchmode provider", Kind = "flatrate" }]
        }, itemEnrichmentCache: coordinator).GetPremieresAsync(date, date, CancellationToken.None, forceRefresh: true, filters: filters);
        var before = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!)!;
        Assert.True(before.AvailabilityOwnedByWatchmode);
        clock.Advance(TimeSpan.FromDays(8));
        var failedWatchmode = new FakeWatchmodeClient { SourcesOutcomeSuccessful = false };
        var preserved = Assert.Single(await CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"), watchmode: failedWatchmode,
            itemEnrichmentCache: coordinator).GetPremieresAsync(date, date, CancellationToken.None, filters: filters));
        Assert.Contains(preserved.Sources, source => source.Id == 8);
        var failed = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!)!;
        Assert.True(failed.AvailabilityOwnedByWatchmode);
        Assert.False(failed.AvailabilityComplete);
        Assert.Equal(before.AvailabilityFetchedUtc, failed.AvailabilityFetchedUtc);
        clock.Advance(TimeSpan.FromMinutes(6));
        var emptyWatchmode = new FakeWatchmodeClient();

        var cleared = Assert.Single(await CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"), watchmode: emptyWatchmode,
            itemEnrichmentCache: coordinator).GetPremieresAsync(date, date, CancellationToken.None, filters: filters));

        Assert.DoesNotContain(cleared.Sources, source => source.Id == 8);
        Assert.Single(emptyWatchmode.SourceCalls);
        var after = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!)!;
        Assert.True(after.AvailabilityOwnedByWatchmode);
        Assert.True(after.AvailabilityComplete);
        Assert.Equal(clock.GetUtcNow(), after.AvailabilityFetchedUtc);
    }
}
