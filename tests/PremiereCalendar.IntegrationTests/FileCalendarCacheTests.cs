using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using System.Text.Json.Nodes;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed class FileCalendarCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"premiere-cache-{Guid.NewGuid():N}");

    [Fact]
    public async Task SetAndGetWeekAsync_RoundTripsCachedPremieres()
    {
        var cache = CreateCache();
        var start = new DateOnly(2026, 5, 4);
        var end = new DateOnly(2026, 5, 10);
        var items = new[]
        {
            new PremiereItem
            {
                CanonicalId = "movie:1",
                Type = PremiereItemType.MovieFirstRelease,
                MediaType = PremiereMediaType.Movie,
                TmdbId = 1,
                Title = "Cached Movie",
                PremiereDate = start
            }
        };

        await cache.SetWeekAsync(start, end, "default", items, CancellationToken.None);
        var cached = await cache.GetWeekAsync(start, end, "default", CancellationToken.None);

        var item = Assert.Single(cached!);
        Assert.Equal("Cached Movie", item.Title);
        Assert.Equal("movie:1", item.CanonicalId);
    }

    [Fact]
    public async Task SetWeekAsync_PersistsCacheAcrossCacheInstances()
    {
        var writer = CreateCache();
        var reader = CreateCache();
        var start = new DateOnly(2026, 5, 4);
        var end = new DateOnly(2026, 5, 10);

        await writer.SetWeekAsync(
            start,
            end,
            "default",
            [
                new PremiereItem
                {
                    CanonicalId = "movie:1",
                    Type = PremiereItemType.MovieFirstRelease,
                    MediaType = PremiereMediaType.Movie,
                    TmdbId = 1,
                    Title = "Persisted Movie",
                    PremiereDate = start
                }
            ],
            CancellationToken.None);

        var cached = await reader.GetWeekAsync(start, end, "default", CancellationToken.None);

        Assert.Equal("Persisted Movie", Assert.Single(cached!).Title);
    }

    [Fact]
    public async Task GetWeekAsync_IgnoresLegacyUnversionedCacheFiles()
    {
        var cache = CreateCache();
        var start = new DateOnly(2026, 5, 4);
        var end = new DateOnly(2026, 5, 10);
        Directory.CreateDirectory(Path.Combine(_root, "cache"));
        await File.WriteAllTextAsync(
            Path.Combine(_root, "cache", "20260504-20260510-default.json"),
            JsonSerializer.Serialize(new[]
            {
                new PremiereItem
                {
                    CanonicalId = "tv:2",
                    Type = PremiereItemType.SeriesPremiere,
                    MediaType = PremiereMediaType.Series,
                    TmdbId = 2,
                    Title = "Legacy Series",
                    PremiereDate = start
                }
            }),
            CancellationToken.None);

        var cached = await cache.GetWeekAsync(start, end, "default", CancellationToken.None, allowExpired: true);

        Assert.Null(cached);
    }

    [Fact]
    public async Task GetWeekAsync_UsesEnvelopeCachedAtUtcInsteadOfFileMtime()
    {
        var cache = CreateCache(weekCacheHours: 1);
        var start = new DateOnly(2026, 5, 4);
        var end = new DateOnly(2026, 5, 10);
        var items = new[]
        {
            new PremiereItem
            {
                CanonicalId = "tv:2",
                Type = PremiereItemType.SeriesPremiere,
                MediaType = PremiereMediaType.Series,
                TmdbId = 2,
                Title = "Expired Series",
                PremiereDate = start
            }
        };

        await cache.SetWeekAsync(start, end, "default", items, CancellationToken.None);
        File.SetLastWriteTimeUtc(
            Path.Combine(_root, "cache", "20260504-20260510-default.json"),
            DateTime.UtcNow.AddHours(-3));

        var freshOnly = await cache.GetWeekAsync(start, end, "default", CancellationToken.None);
        var allowExpired = await cache.GetWeekAsync(start, end, "default", CancellationToken.None, allowExpired: true);

        Assert.Equal("Expired Series", Assert.Single(freshOnly!).Title);
        Assert.Equal("Expired Series", Assert.Single(allowExpired!).Title);
    }

    [Fact]
    public async Task GetWeekAsync_ReturnsExpiredEnvelopeOnlyWhenAllowed()
    {
        var cache = CreateCache(weekCacheHours: 1);
        var start = new DateOnly(2026, 5, 4);
        var end = new DateOnly(2026, 5, 10);

        await cache.SetWeekAsync(
            start,
            end,
            "default",
            [
                new PremiereItem
                {
                    CanonicalId = "tv:2",
                    Type = PremiereItemType.SeriesPremiere,
                    MediaType = PremiereMediaType.Series,
                    TmdbId = 2,
                    Title = "Expired Envelope Series",
                    PremiereDate = start
                }
            ],
            CancellationToken.None);

        var path = Path.Combine(_root, "cache", "20260504-20260510-default.json");
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path, CancellationToken.None))!;
        json["cachedAtUtc"] = DateTimeOffset.UtcNow.AddHours(-3);
        await File.WriteAllTextAsync(path, json.ToJsonString(), CancellationToken.None);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow);

        var freshOnly = await cache.GetWeekAsync(start, end, "default", CancellationToken.None);
        var allowExpired = await cache.GetWeekAsync(start, end, "default", CancellationToken.None, allowExpired: true);

        Assert.Null(freshOnly);
        Assert.Equal("Expired Envelope Series", Assert.Single(allowExpired!).Title);
    }

    [Fact]
    public async Task GetWeekMetadataAsync_ReturnsCachedAtItemCountSchemaAndCompleteness()
    {
        var cache = CreateCache();
        var start = new DateOnly(2026, 5, 4);
        var end = new DateOnly(2026, 5, 10);

        await cache.SetWeekAsync(
            start,
            end,
            "default",
            [
                new PremiereItem
                {
                    CanonicalId = "movie:1",
                    Type = PremiereItemType.MovieFirstRelease,
                    MediaType = PremiereMediaType.Movie,
                    TmdbId = 1,
                    Title = "Metadata Movie",
                    PremiereDate = start
                }
            ],
            CancellationToken.None);

        var metadata = await ((ICalendarCacheMaintenance)cache).GetWeekMetadataAsync(
            start,
            end,
            "default",
            CancellationToken.None);

        Assert.NotNull(metadata);
        Assert.Equal(1, metadata.ItemCount);
        Assert.True(metadata.SchemaVersion > 0);
        Assert.Equal(CalendarCacheCompleteness.Complete, metadata.Completeness);
        Assert.True(DateTimeOffset.UtcNow - metadata.CachedAtUtc < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task GetWeekAsync_SeparatesCacheFilesByFilterKey()
    {
        var cache = CreateCache();
        var start = new DateOnly(2026, 5, 4);
        var end = new DateOnly(2026, 5, 10);

        await cache.SetWeekAsync(
            start,
            end,
            "default",
            [new PremiereItem { CanonicalId = "movie:1", Type = PremiereItemType.MovieFirstRelease, MediaType = PremiereMediaType.Movie, TmdbId = 1, Title = "Unfiltered", PremiereDate = start }],
            CancellationToken.None);
        await cache.SetWeekAsync(
            start,
            end,
            "filtered",
            [new PremiereItem { CanonicalId = "movie:2", Type = PremiereItemType.MovieFirstRelease, MediaType = PremiereMediaType.Movie, TmdbId = 2, Title = "Filtered", PremiereDate = start }],
            CancellationToken.None);

        var unfiltered = await cache.GetWeekAsync(start, end, "default", CancellationToken.None);
        var filtered = await cache.GetWeekAsync(start, end, "filtered", CancellationToken.None);

        Assert.Equal("Unfiltered", Assert.Single(unfiltered!).Title);
        Assert.Equal("Filtered", Assert.Single(filtered!).Title);
    }

    [Fact]
    public async Task SetWeekAsync_DoesNotLeaveFinalCacheFileWhenCanceled()
    {
        var cache = CreateCache();
        var start = new DateOnly(2026, 5, 4);
        var end = new DateOnly(2026, 5, 10);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.SetWeekAsync(
            start,
            end,
            "default",
            [new PremiereItem { CanonicalId = "movie:1", Type = PremiereItemType.MovieFirstRelease, MediaType = PremiereMediaType.Movie, TmdbId = 1, Title = "Canceled", PremiereDate = start }],
            cts.Token));

        Assert.False(File.Exists(Path.Combine(_root, "cache", "20260504-20260510-default.json")));
    }

    [Fact]
    public async Task CleanupAsync_RemovesCalendarWeeksOlderThanRetention()
    {
        var cache = CreateCache();
        var oldStart = new DateOnly(2026, 2, 23);
        var oldEnd = new DateOnly(2026, 3, 1);
        var currentStart = new DateOnly(2026, 5, 4);
        var currentEnd = new DateOnly(2026, 5, 10);

        await cache.SetWeekAsync(
            oldStart,
            oldEnd,
            "default",
            [new PremiereItem { CanonicalId = "movie:1", Type = PremiereItemType.MovieFirstRelease, MediaType = PremiereMediaType.Movie, TmdbId = 1, Title = "Old", PremiereDate = oldStart }],
            CancellationToken.None);
        await cache.SetWeekAsync(
            currentStart,
            currentEnd,
            "default",
            [new PremiereItem { CanonicalId = "movie:2", Type = PremiereItemType.MovieFirstRelease, MediaType = PremiereMediaType.Movie, TmdbId = 2, Title = "Current", PremiereDate = currentStart }],
            CancellationToken.None);

        var removed = await ((ICalendarCacheMaintenance)cache).CleanupAsync(
            new DateTimeOffset(2026, 5, 8, 10, 0, 0, TimeSpan.Zero),
            TimeSpan.FromDays(60),
            CancellationToken.None);

        Assert.Equal(1, removed);
        Assert.Null(await cache.GetWeekAsync(oldStart, oldEnd, "default", CancellationToken.None, allowExpired: true));
        Assert.Equal("Current", Assert.Single((await cache.GetWeekAsync(currentStart, currentEnd, "default", CancellationToken.None))!).Title);
    }

    [Fact]
    public async Task CleanupAsync_RemovesOldTemporaryCacheFiles()
    {
        var cache = CreateCache();
        var cacheDirectory = Path.Combine(_root, "cache");
        Directory.CreateDirectory(cacheDirectory);
        var tempPath = Path.Combine(cacheDirectory, "20260504-20260510-default.json.abandoned.tmp");
        await File.WriteAllTextAsync(tempPath, "partial", CancellationToken.None);
        File.SetLastWriteTimeUtc(tempPath, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));

        var removed = await ((ICalendarCacheMaintenance)cache).CleanupAsync(
            new DateTimeOffset(2026, 5, 8, 10, 0, 0, TimeSpan.Zero),
            TimeSpan.FromDays(60),
            CancellationToken.None);

        Assert.Equal(1, removed);
        Assert.False(File.Exists(tempPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private FileCalendarCache CreateCache(int weekCacheHours = 6)
    {
        return new FileCalendarCache(
            Microsoft.Extensions.Options.Options.Create(new CalendarCacheOptions
            {
                Enabled = true,
                Directory = "cache",
                WeekCacheHours = weekCacheHours
            }),
            new FakeWebHostEnvironment(_root),
            NullLogger<FileCalendarCache>.Instance);
    }

    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public FakeWebHostEnvironment(string contentRootPath)
        {
            ContentRootPath = contentRootPath;
            WebRootPath = Path.Combine(contentRootPath, "wwwroot");
        }

        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "PremiereCalendar.Tests";
        public string WebRootPath { get; set; }
        public IFileProvider WebRootFileProvider { get; set; } = default!;
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; } = default!;
    }
}
