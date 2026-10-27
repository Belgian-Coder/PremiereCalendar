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
    public async Task SetWeekAsync_ReplacesFileWhileAnotherReaderKeepsOldSnapshotOpen()
    {
        var cache = CreateCache();
        var start = new DateOnly(2026, 5, 4);
        var end = new DateOnly(2026, 5, 10);
        var path = Path.Combine(_root, "cache", "20260504-20260510-default.json");
        await cache.SetWeekAsync(start, end, "default", [new PremiereItem
        {
            CanonicalId = "movie:1",
            Type = PremiereItemType.MovieFirstRelease,
            MediaType = PremiereMediaType.Movie,
            TmdbId = 1,
            Title = "First",
            PremiereDate = start
        }], CancellationToken.None);
        await using var oldReader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);

        await cache.SetWeekAsync(start, end, "default", [new PremiereItem
        {
            CanonicalId = "movie:2",
            Type = PremiereItemType.MovieFirstRelease,
            MediaType = PremiereMediaType.Movie,
            TmdbId = 2,
            Title = "Second",
            PremiereDate = start
        }], CancellationToken.None);

        Assert.Equal("Second", Assert.Single((await cache.GetWeekAsync(start, end, "default", CancellationToken.None))!).Title);
        using var oldDocument = await JsonDocument.ParseAsync(oldReader, cancellationToken: CancellationToken.None);
        Assert.Equal("First", oldDocument.RootElement.GetProperty("items")[0].GetProperty("title").GetString());
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
    public async Task GetWeekMetadataAsync_UsesBoundedHeaderBeforeLargeItemsPayload()
    {
        var cache = CreateCache();
        var start = new DateOnly(2026, 5, 4);
        var end = new DateOnly(2026, 5, 10);
        await cache.SetWeekAsync(
            start,
            end,
            "default",
            [new PremiereItem
            {
                CanonicalId = "movie:large",
                Type = PremiereItemType.MovieFirstRelease,
                MediaType = PremiereMediaType.Movie,
                TmdbId = 1,
                Title = new string('x', 2 * 1024 * 1024),
                PremiereDate = start
            }],
            CancellationToken.None);

        var before = GC.GetAllocatedBytesForCurrentThread();
        var metadata = await ((ICalendarCacheMaintenance)cache).GetWeekMetadataAsync(
            start,
            end,
            "default",
            CancellationToken.None);
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.NotNull(metadata);
        Assert.Equal(1, metadata.ItemCount);
        Assert.True(allocatedBytes < 1024 * 1024, $"Header read allocated {allocatedBytes} bytes.");
    }

    [Fact]
    public async Task GetWeekMetadataAsync_FallsBackToFullParseWhenItemCountIsAbsent()
    {
        var cache = CreateCache();
        var start = new DateOnly(2026, 5, 4);
        var end = new DateOnly(2026, 5, 10);
        await cache.SetWeekAsync(
            start,
            end,
            "default",
            [new PremiereItem
            {
                CanonicalId = "movie:legacy-header",
                Type = PremiereItemType.MovieFirstRelease,
                MediaType = PremiereMediaType.Movie,
                TmdbId = 1,
                Title = "Legacy header",
                PremiereDate = start
            }],
            CancellationToken.None);

        var path = Path.Combine(_root, "cache", "20260504-20260510-default.json");
        var envelope = JsonNode.Parse(await File.ReadAllTextAsync(path, CancellationToken.None))!;
        envelope.AsObject().Remove("itemCount");
        await File.WriteAllTextAsync(path, envelope.ToJsonString(), CancellationToken.None);

        var metadata = await ((ICalendarCacheMaintenance)cache).GetWeekMetadataAsync(
            start,
            end,
            "default",
            CancellationToken.None);

        Assert.NotNull(metadata);
        Assert.Equal(1, metadata.ItemCount);
    }

    [Fact]
    public async Task GetWeekMetadataAsync_SkipsUnknownNestedMetadataAcrossReadChunks()
    {
        var cache = CreateCache();
        var start = new DateOnly(2026, 5, 4);
        var end = new DateOnly(2026, 5, 10);
        var path = Path.Combine(_root, "cache", "20260504-20260510-default.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var largeUnknown = new string('x', 5000);
        var json = JsonSerializer.Serialize(new
        {
            schemaVersion = 8,
            cachedAtUtc = new DateTimeOffset(2026, 5, 8, 10, 0, 0, TimeSpan.Zero),
            itemCount = 1,
            future = new { nested = new { padding = largeUnknown } },
            items = Array.Empty<PremiereItem>()
        });
        await File.WriteAllTextAsync(path, json);

        var metadata = await ((ICalendarCacheMaintenance)cache).GetWeekMetadataAsync(
            start,
            end,
            "default",
            CancellationToken.None);

        Assert.NotNull(metadata);
        Assert.Equal(1, metadata.ItemCount);
    }

    [Fact]
    public async Task GetWeekMetadataAsync_MalformedNestedCompletenessFallsBackToCacheMiss()
    {
        var cache = CreateCache();
        var start = new DateOnly(2026, 5, 4);
        var end = new DateOnly(2026, 5, 10);
        var path = Path.Combine(_root, "cache", "20260504-20260510-default.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path,
            """{"schemaVersion":8,"cachedAtUtc":"2026-05-08T10:00:00+00:00","itemCount":1,"completeness":{"unexpected":true},"items":[]}""");

        var metadata = await ((ICalendarCacheMaintenance)cache).GetWeekMetadataAsync(
            start,
            end,
            "default",
            CancellationToken.None);
        var items = await cache.GetWeekAsync(start, end, "default", CancellationToken.None, allowExpired: true);

        Assert.Null(metadata);
        Assert.Null(items);
    }

    [Fact]
    public async Task GetWeekAsync_FreshReadRejectsPartialCacheButAllowExpiredCanReadIt()
    {
        var cache = CreateCache();
        var start = new DateOnly(2026, 5, 4);
        var end = new DateOnly(2026, 5, 10);
        await cache.SetWeekAsync(
            start,
            end,
            "default",
            [new PremiereItem
            {
                CanonicalId = "movie:partial",
                Type = PremiereItemType.MovieFirstRelease,
                MediaType = PremiereMediaType.Movie,
                TmdbId = 1,
                Title = "Partial",
                PremiereDate = start
            }],
            CancellationToken.None);

        var path = Path.Combine(_root, "cache", "20260504-20260510-default.json");
        var envelope = JsonNode.Parse(await File.ReadAllTextAsync(path, CancellationToken.None))!;
        envelope["completeness"] = (int)CalendarCacheCompleteness.Partial;
        await File.WriteAllTextAsync(path, envelope.ToJsonString(), CancellationToken.None);

        Assert.Null(await cache.GetWeekAsync(start, end, "default", CancellationToken.None));
        Assert.Equal("Partial", Assert.Single((await cache.GetWeekAsync(
            start,
            end,
            "default",
            CancellationToken.None,
            allowExpired: true))!).Title);
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

    [Theory]
    [InlineData(false, CalendarCacheCompleteness.Complete)]
    [InlineData(true, CalendarCacheCompleteness.Complete)]
    [InlineData(false, CalendarCacheCompleteness.Partial)]
    [InlineData(true, CalendarCacheCompleteness.Partial)]
    public async Task GetWeekSnapshotAsync_ReturnsItemsAndEnvelopeStateTogether(
        bool expired, CalendarCacheCompleteness completeness)
    {
        var cache = CreateCache(weekCacheHours: 1);
        var start = new DateOnly(2026, 5, 4);
        var cachedAt = DateTimeOffset.UtcNow.AddHours(expired ? -3 : -0.25);
        await WriteEnvelopeAsync([Item("movie:1", "Snapshot")], cachedAt, completeness);

        var snapshot = await cache.GetWeekSnapshotAsync(start, start.AddDays(6), "default", CancellationToken.None);

        Assert.NotNull(snapshot);
        Assert.Equal("Snapshot", Assert.Single(snapshot.Items).Title);
        Assert.Equal(cachedAt, snapshot.Metadata.CachedAtUtc);
        Assert.Equal(1, snapshot.Metadata.ItemCount);
        Assert.Equal(8, snapshot.Metadata.SchemaVersion);
        Assert.Equal(completeness, snapshot.Metadata.Completeness);
        Assert.Equal(!expired, snapshot.IsFresh);
        Assert.Equal(!expired && completeness == CalendarCacheCompleteness.Complete, snapshot.IsComplete);
        Assert.NotNull(snapshot.Generation); // Existing v8 envelopes need no migration.
    }

    [Theory]
    [InlineData(CalendarCacheMaintenanceKind.Scores)]
    [InlineData(CalendarCacheMaintenanceKind.ExternalIds)]
    public async Task UpdateWeekMetadataAsync_MergesDeltasIntoNewerCanonicalRows(
        CalendarCacheMaintenanceKind kind)
    {
        var cache = CreateCache();
        var start = new DateOnly(2026, 5, 4);
        var end = start.AddDays(6);
        var originalRow = Item("movie:1", "Old title") with
        {
            ImdbId = kind == CalendarCacheMaintenanceKind.Scores ? "tt0000001" : null
        };
        var removedRow = Item("movie:removed", "Removed");
        await cache.SetWeekAsync(start, end, "default", [originalRow, removedRow], CancellationToken.None);
        var original = (await cache.GetWeekSnapshotAsync(start, end, "default", CancellationToken.None))!;
        var maintained = original.Items.Select(item => item with
        {
            Title = "Must not overwrite discovery",
            ImdbScore = 7.4,
            ImdbVoteCount = 123,
            RottenTomatoesScore = 86,
            RottenTomatoesAudienceScore = 90,
            MetacriticScore = 75,
            ImdbId = "tt0000001",
            ImdbUrl = "https://www.imdb.com/title/tt0000001/",
            TvdbId = 101,
            WikidataId = "Q101"
        }).ToArray();

        var latestRow = originalRow with
        {
            Title = "New discovery title",
            Overview = "New discovery overview",
            PremiereDate = end,
            SourceNames = ["new source"],
            ImdbScore = 9.1, // A newer enrichment wins over a delayed score write.
            WikidataId = "Q202", // The same conflict rule applies to identifiers.
            LastUpdatedUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        var addedRow = Item("movie:added", "Added during remote work");
        await cache.SetWeekAsync(start, end, "default", [latestRow, addedRow], CancellationToken.None);
        var latest = (await cache.GetWeekSnapshotAsync(start, end, "default", CancellationToken.None))!;

        var merged = await cache.UpdateWeekMetadataAsync(
            start, end, "default", original, maintained, kind, CancellationToken.None);
        var final = (await cache.GetWeekSnapshotAsync(start, end, "default", CancellationToken.None))!;

        Assert.Equal(2, merged!.Count);
        Assert.DoesNotContain(final.Items, item => item.CanonicalId == removedRow.CanonicalId);
        Assert.Equal(JsonSerializer.Serialize(addedRow), JsonSerializer.Serialize(final.Items[1]));
        var updated = final.Items[0];
        Assert.Equal(latestRow.Title, updated.Title);
        Assert.Equal(latestRow.Overview, updated.Overview);
        Assert.Equal(latestRow.PremiereDate, updated.PremiereDate);
        Assert.Equal(latestRow.SourceNames, updated.SourceNames);
        Assert.Equal(latestRow.LastUpdatedUtc, updated.LastUpdatedUtc);
        Assert.Equal(9.1, updated.ImdbScore);
        Assert.Equal("Q202", updated.WikidataId);
        Assert.Equal(latest.Metadata.CachedAtUtc, final.Metadata.CachedAtUtc);
        Assert.Equal(latest.Metadata.Completeness, final.Metadata.Completeness);
        Assert.NotEqual(latest.Generation, final.Generation);
        if (kind == CalendarCacheMaintenanceKind.Scores)
        {
            Assert.Equal(123, updated.ImdbVoteCount);
            Assert.Null(updated.RottenTomatoesScore); // Title/Wikidata identity changed during the remote lookup.
            Assert.Null(updated.RottenTomatoesAudienceScore);
            Assert.Equal(75, updated.MetacriticScore);
            Assert.Equal("tt0000001", updated.ImdbId);
            Assert.Null(updated.ImdbUrl);
            Assert.Null(updated.TvdbId);
        }
        else
        {
            Assert.Equal("tt0000001", updated.ImdbId);
            Assert.Equal("https://www.imdb.com/title/tt0000001/", updated.ImdbUrl);
            Assert.Equal(101, updated.TvdbId);
            Assert.Null(updated.ImdbVoteCount);
            Assert.Null(updated.RottenTomatoesScore);
        }
    }

    [Theory]
    [InlineData("imdb", false)]
    [InlineData("title", true)]
    [InlineData("wikidata", true)]
    [InlineData("year", true)]
    [InlineData("tmdb", false)]
    public async Task UpdateWeekMetadataAsync_DoesNotAttachScoresToChangedProviderIdentity(
        string changedIdentity, bool imdbStillMatches)
    {
        var cache = CreateCache();
        var start = new DateOnly(2026, 5, 4);
        var end = start.AddDays(6);
        var row = Item("movie:1", "Original identity");
        await cache.SetWeekAsync(start, end, "default", [row], CancellationToken.None);
        var original = (await cache.GetWeekSnapshotAsync(start, end, "default", CancellationToken.None))!;
        var latest = changedIdentity switch
        {
            "imdb" => row with { ImdbId = "tt0000002" },
            "title" => row with { Title = "Corrected title" },
            "wikidata" => row with { WikidataId = "Q202" },
            "year" => row with { PremiereDate = row.PremiereDate.AddYears(1) },
            _ => row with { TmdbId = 2 }
        };
        await cache.SetWeekAsync(start, end, "default", [latest], CancellationToken.None);

        await cache.UpdateWeekMetadataAsync(start, end, "default", original,
            [row with { ImdbScore = 7.4, ImdbVoteCount = 123, MetacriticScore = 75,
                RottenTomatoesScore = 86, RottenTomatoesAudienceScore = 90 }],
            CalendarCacheMaintenanceKind.Scores, CancellationToken.None);
        var final = Assert.Single((await cache.GetWeekSnapshotAsync(start, end, "default", CancellationToken.None))!.Items);

        Assert.Equal(latest.ImdbId, final.ImdbId);
        Assert.Equal(latest.Title, final.Title);
        Assert.Equal(latest.WikidataId, final.WikidataId);
        Assert.Equal(latest.PremiereDate, final.PremiereDate);
        Assert.Equal(imdbStillMatches ? 7.4 : (double?)null, final.ImdbScore);
        Assert.Equal(imdbStillMatches ? 123 : (int?)null, final.ImdbVoteCount);
        Assert.Equal(imdbStillMatches ? 75 : (int?)null, final.MetacriticScore);
        Assert.Null(final.RottenTomatoesScore);
        Assert.Null(final.RottenTomatoesAudienceScore);
    }

    [Fact]
    public async Task UpdateWeekMetadataAsync_DoesNotPairRepairedUrlWithNewerImdbId()
    {
        var cache = CreateCache();
        var start = new DateOnly(2026, 5, 4);
        var end = start.AddDays(6);
        var row = Item("movie:1", "Missing ID") with { ImdbId = null };
        await cache.SetWeekAsync(start, end, "default", [row], CancellationToken.None);
        var original = (await cache.GetWeekSnapshotAsync(start, end, "default", CancellationToken.None))!;
        await cache.SetWeekAsync(start, end, "default", [row with { ImdbId = "tt0000002" }], CancellationToken.None);

        await cache.UpdateWeekMetadataAsync(start, end, "default", original,
            [row with { ImdbId = "tt0000001", ImdbUrl = "https://www.imdb.com/title/tt0000001/", TvdbId = 101 }],
            CalendarCacheMaintenanceKind.ExternalIds, CancellationToken.None);
        var final = Assert.Single((await cache.GetWeekSnapshotAsync(start, end, "default", CancellationToken.None))!.Items);

        Assert.Equal("tt0000002", final.ImdbId);
        Assert.Null(final.ImdbUrl);
        Assert.Equal(101, final.TvdbId);
    }

    [Theory]
    [InlineData(CalendarCacheCompleteness.Complete)]
    [InlineData(CalendarCacheCompleteness.Partial)]
    public async Task UpdateWeekMetadataAsync_PreservesExpiredDiscoveryState(CalendarCacheCompleteness completeness)
    {
        var cache = CreateCache(weekCacheHours: 1);
        var start = new DateOnly(2026, 5, 4);
        var end = start.AddDays(6);
        var cachedAt = DateTimeOffset.UtcNow.AddHours(-3);
        await WriteEnvelopeAsync([Item("movie:1", "Expired")], cachedAt, completeness);
        var original = (await cache.GetWeekSnapshotAsync(start, end, "default", CancellationToken.None))!;

        await cache.UpdateWeekMetadataAsync(start, end, "default", original,
            [original.Items[0] with { ImdbScore = 7.4, ImdbVoteCount = 123, RottenTomatoesScore = 86,
                RottenTomatoesAudienceScore = 90, MetacriticScore = 75 }], CalendarCacheMaintenanceKind.Scores, CancellationToken.None);
        var final = (await cache.GetWeekSnapshotAsync(start, end, "default", CancellationToken.None))!;

        var updated = Assert.Single(final.Items);
        Assert.Equal(7.4, updated.ImdbScore);
        Assert.Equal(123, updated.ImdbVoteCount);
        Assert.Equal(86, updated.RottenTomatoesScore);
        Assert.Equal(90, updated.RottenTomatoesAudienceScore);
        Assert.Equal(75, updated.MetacriticScore);
        Assert.Equal(cachedAt, final.Metadata.CachedAtUtc);
        Assert.Equal(completeness, final.Metadata.Completeness);
        Assert.False(final.IsFresh);
        Assert.False(final.IsComplete);
        Assert.Null(await cache.GetWeekAsync(start, end, "default", CancellationToken.None));
    }

    [Fact]
    public async Task UpdateWeekMetadataAsync_NoDeltaDoesNotPublishAnotherGeneration()
    {
        var cache = CreateCache();
        var start = new DateOnly(2026, 5, 4);
        var end = start.AddDays(6);
        await cache.SetWeekAsync(start, end, "default", [Item("movie:1", "Unchanged")], CancellationToken.None);
        var original = (await cache.GetWeekSnapshotAsync(start, end, "default", CancellationToken.None))!;
        var path = Path.Combine(_root, "cache", "20260504-20260510-default.json");
        var before = await File.ReadAllBytesAsync(path);

        await cache.UpdateWeekMetadataAsync(start, end, "default", original, original.Items,
            CalendarCacheMaintenanceKind.Scores, CancellationToken.None);

        Assert.Equal(before, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task UpdateWeekMetadataAsync_RejectsReplacedSchemaAndDoesNotResurrectDeletedWeek()
    {
        var cache = CreateCache();
        var start = new DateOnly(2026, 5, 4);
        var end = start.AddDays(6);
        await cache.SetWeekAsync(start, end, "default", [Item("movie:1", "Old")], CancellationToken.None);
        var original = (await cache.GetWeekSnapshotAsync(start, end, "default", CancellationToken.None))!;
        var maintained = new[] { original.Items[0] with { ImdbScore = 7.4 } };
        var path = Path.Combine(_root, "cache", "20260504-20260510-default.json");
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        json["schemaVersion"] = 999;
        await File.WriteAllTextAsync(path, json.ToJsonString());
        var incompatible = await File.ReadAllBytesAsync(path);

        Assert.Null(await cache.GetWeekSnapshotAsync(start, end, "default", CancellationToken.None));
        Assert.Null(await cache.UpdateWeekMetadataAsync(start, end, "default", original, maintained,
            CalendarCacheMaintenanceKind.Scores, CancellationToken.None));
        Assert.Equal(incompatible, await File.ReadAllBytesAsync(path));

        File.Delete(path);
        Assert.Null(await cache.UpdateWeekMetadataAsync(start, end, "default", original, maintained,
            CalendarCacheMaintenanceKind.Scores, CancellationToken.None));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task ScoreMaintenance_AllowsDiscoveryDuringRemoteWorkAndMergesAfterwards()
    {
        var cache = CreateCache();
        var start = new DateOnly(2026, 5, 4);
        var end = start.AddDays(6);
        var old = Item("movie:1", "Before maintenance");
        await cache.SetWeekAsync(start, end, "default", [old], CancellationToken.None);
        var omdb = new PausedOmdbClient();
        var service = new CalendarDataMaintenanceService(
            new WeekStateStore(start), cache,
            new ScoreBackfillService(null, omdb, new RatingMapper(), null, NullLogger<ScoreBackfillService>.Instance),
            new MissingExternalIdRepairService(null!, NullLogger<MissingExternalIdRepairService>.Instance),
            NullLogger<CalendarDataMaintenanceService>.Instance);

        var maintenance = service.BackfillRecentScoresAsync(CancellationToken.None);
        await omdb.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(maintenance.IsCompleted);
        try
        {
            await cache.SetWeekAsync(start, end, "default",
                [old with { Title = "New discovery", PremiereDate = end }, Item("movie:2", "New row")],
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            omdb.Release.TrySetResult();
        }

        await maintenance.WaitAsync(TimeSpan.FromSeconds(5));
        var final = (await cache.GetWeekSnapshotAsync(start, end, "default", CancellationToken.None))!;
        Assert.Equal(2, final.Items.Count);
        Assert.Equal("New discovery", final.Items[0].Title);
        Assert.Equal(end, final.Items[0].PremiereDate);
        Assert.Equal(7.4, final.Items[0].ImdbScore);
        Assert.Equal("New row", final.Items[1].Title);
        Assert.Null(final.Items[1].ImdbScore);
    }

    private async Task WriteEnvelopeAsync(
        IReadOnlyList<PremiereItem> items,
        DateTimeOffset cachedAt,
        CalendarCacheCompleteness completeness)
    {
        var path = Path.Combine(_root, "cache", "20260504-20260510-default.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new
        {
            schemaVersion = 8,
            cachedAtUtc = cachedAt,
            completeness,
            items
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private static PremiereItem Item(string canonicalId, string title) => new()
    {
        CanonicalId = canonicalId,
        Type = PremiereItemType.MovieFirstRelease,
        MediaType = PremiereMediaType.Movie,
        TmdbId = 1,
        Title = title,
        PremiereDate = new DateOnly(2026, 5, 4),
        ImdbId = "tt0000001"
    };

    private sealed class PausedOmdbClient : IOmdbClient
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<OmdbItem?> GetByImdbIdAsync(
            string imdbId, CancellationToken cancellationToken, bool forceRefresh = false)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new OmdbItem { Response = "True", ImdbRating = "7.4" };
        }
    }

    private sealed class WeekStateStore(DateOnly weekStart) : IProviderCacheStateStore
    {
        public Task<ProviderCacheState?> GetAsync(string provider, ProviderCacheScope scope, string key, CancellationToken cancellationToken)
            => Task.FromResult<ProviderCacheState?>(null);
        public Task SaveAsync(ProviderCacheState state, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<ProviderCacheState>> GetRecentAsync(int take, CancellationToken cancellationToken)
            => GetByProviderAsync("calendar", take, cancellationToken);
        public Task<IReadOnlyList<ProviderCacheState>> GetByProviderAsync(string provider, int take, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ProviderCacheState>>([
                new("calendar", ProviderCacheScope.Week, $"{weekStart:yyyyMMdd}:default", DateTimeOffset.UtcNow,
                    null, null, 1, null)
            ]);
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
