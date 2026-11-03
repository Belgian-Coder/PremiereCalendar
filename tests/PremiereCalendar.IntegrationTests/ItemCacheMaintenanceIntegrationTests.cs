using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed class ItemCacheMaintenanceIntegrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "premiere-maintenance-item-cache", Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScoreMaintenancePersistsNewScoresWithoutRegressingGlobalMetadataOrRenewingClocks(bool priorRatings)
    {
        var now = DateTimeOffset.UtcNow;
        var environment = new Environment(_root);
        var database = Microsoft.Extensions.Options.Options.Create(new AppDatabaseOptions { Path = "test.db" });
        TestSqliteDatabase.Initialize(_root, "test.db");
        var store = new SqliteItemEnrichmentStore(database, environment);
        var coordinator = new ItemEnrichmentCacheCoordinator(store,
            Microsoft.Extensions.Options.Options.Create(new ItemEnrichmentCacheOptions()),
            Microsoft.Extensions.Options.Options.Create(new TmdbOptions()), NullLogger<ItemEnrichmentCacheCoordinator>.Instance);
        var start = new DateOnly(2026, 11, 2);
        var old = new PremiereItem { CanonicalId = "movie:42", TmdbId = 42, MediaType = PremiereMediaType.Movie,
            Type = PremiereItemType.MovieFirstRelease, Title = "Older week title", PremiereDate = start,
            ImdbId = "tt0000042", PosterUrl = "https://example.test/old.jpg", LastUpdatedUtc = now.AddDays(-2) };
        var key = new ItemEnrichmentIdentity(PremiereMediaType.Movie, 42);
        var global = old with { Title = "Newer global title", PosterUrl = "https://example.test/new.jpg", ImdbScore = 7.4 };
        var metadataClock = now.AddHours(-3);
        var ratingClock = now.AddHours(-4);
        await store.ReadManyAsync(coordinator.Partition, [key], true, default);
        await store.WriteManyAsync(coordinator.Partition, [new(key, 0,
            JsonSerializer.Serialize(ItemEnrichmentMetadata.FromItem(global)), metadataClock,
            priorRatings ? JsonSerializer.Serialize(ItemEnrichmentRatings.FromItem(global)) : null,
            priorRatings ? ratingClock : null)], default);
        var cache = new FileCalendarCache(Microsoft.Extensions.Options.Options.Create(
            new CalendarCacheOptions { Enabled = true, Directory = "cache" }), environment, NullLogger<FileCalendarCache>.Instance);
        await cache.SetWeekAsync(start, start.AddDays(6), "default", [old], default);
        var maintenance = new CalendarDataMaintenanceService(new WeekStore(start), cache,
            new ScoreBackfillService(null, new Scores(), new RatingMapper(), null, NullLogger<ScoreBackfillService>.Instance),
            new MissingExternalIdRepairService(null!, NullLogger<MissingExternalIdRepairService>.Instance),
            NullLogger<CalendarDataMaintenanceService>.Instance, coordinator);

        var result = await maintenance.BackfillRecentScoresAsync(default);

        Assert.Equal(1, result.ChangedCount);
        var record = Assert.Single(await store.ReadManyAsync(coordinator.Partition, [key], false, default)).Value;
        var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(record.MetadataJson!)!;
        var ratings = JsonSerializer.Deserialize<ItemEnrichmentRatings>(record.RatingsJson!)!;
        Assert.Equal("Newer global title", metadata.Title);
        Assert.Equal("https://example.test/new.jpg", metadata.PosterUrl);
        Assert.Equal(8.8, ratings.ImdbScore);
        Assert.Equal(metadataClock, record.MetadataFetchedUtc);
        Assert.Equal(priorRatings ? ratingClock : DateTimeOffset.MinValue, record.RatingsFetchedUtc);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    private sealed class Scores : IOmdbClient
    {
        public Task<OmdbItem?> GetByImdbIdAsync(string imdbId, CancellationToken token, bool forceRefresh = false)
            => Task.FromResult<OmdbItem?>(new() { Response = "True", ImdbRating = "8.8" });
    }

    private sealed class WeekStore(DateOnly start) : IProviderCacheStateStore
    {
        public Task<ProviderCacheState?> GetAsync(string provider, ProviderCacheScope scope, string key, CancellationToken token) => Task.FromResult<ProviderCacheState?>(null);
        public Task SaveAsync(ProviderCacheState value, CancellationToken token) => Task.CompletedTask;
        public Task<IReadOnlyList<ProviderCacheState>> GetRecentAsync(int count, CancellationToken token) => GetByProviderAsync("calendar", count, token);
        public Task<IReadOnlyList<ProviderCacheState>> GetByProviderAsync(string provider, int count, CancellationToken token)
            => Task.FromResult<IReadOnlyList<ProviderCacheState>>([new("calendar", ProviderCacheScope.Week, $"{start:yyyyMMdd}:default", DateTimeOffset.UtcNow, null, null, 1, null)]);
    }

    private sealed class Environment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Tests";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = root;
        public string WebRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
