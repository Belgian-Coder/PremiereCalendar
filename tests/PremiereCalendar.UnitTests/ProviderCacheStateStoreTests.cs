using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class ProviderCacheStateStoreTests
{
    [Fact]
    public async Task ProviderCacheStateStore_RoundTripsGlobalWeekAndItemStates()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var store = new ProviderCacheStateStore(database.Options);
        var checkedAt = DateTimeOffset.Parse("2026-05-09T10:00:00Z");
        var changedAt = DateTimeOffset.Parse("2026-05-09T09:00:00Z");

        await store.SaveAsync(
            new ProviderCacheState(
                "tmdb",
                ProviderCacheScope.Global,
                "movie-changes",
                checkedAt,
                changedAt,
                "2026-05-09",
                2,
                "{\"source\":\"test\"}"),
            CancellationToken.None);
        await store.SaveAsync(
            new ProviderCacheState("tmdb", ProviderCacheScope.Item, "movie:10", checkedAt, changedAt, null, null, null),
            CancellationToken.None);
        await store.SaveAsync(
            new ProviderCacheState("calendar", ProviderCacheScope.Week, "20260504:movies", checkedAt, null, null, 42, null),
            CancellationToken.None);

        var global = await store.GetAsync("tmdb", ProviderCacheScope.Global, "movie-changes", CancellationToken.None);
        var item = await store.GetAsync("tmdb", ProviderCacheScope.Item, "movie:10", CancellationToken.None);
        var week = await store.GetAsync("calendar", ProviderCacheScope.Week, "20260504:movies", CancellationToken.None);

        Assert.NotNull(global);
        Assert.Equal(changedAt, global.LastChangedUtc);
        Assert.Equal("2026-05-09", global.Watermark);
        Assert.Equal(2, global.ItemCount);
        Assert.NotNull(item);
        Assert.Equal(changedAt, item.LastChangedUtc);
        Assert.NotNull(week);
        Assert.Equal(42, week.ItemCount);
    }
}
