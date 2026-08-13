using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class ProviderCacheStateStoreTests
{
    [Fact]
    public async Task SqliteProviderCacheStateStore_RoundTripsGlobalWeekAndItemStates()
    {
        var root = CreateRoot();
        try
        {
            TestSqliteDatabase.Initialize(root, "data/app.db");
            var store = new SqliteProviderCacheStateStore(
                Microsoft.Extensions.Options.Options.Create(new AppDatabaseOptions { Path = "data/app.db" }),
                new FakeWebHostEnvironment(root));
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
        finally
        {
            TryDelete(root);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"premiere-calendar-provider-state-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private sealed class FakeWebHostEnvironment(string contentRootPath) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PremiereCalendar.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRootPath;
        public string EnvironmentName { get; set; } = "Development";
        public string WebRootPath { get; set; } = contentRootPath;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
