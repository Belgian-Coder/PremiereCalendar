using System.Collections;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed class FileCalendarCachePartialWeekTests : IDisposable
{
    private static readonly DateOnly Start = new(2026, 8, 31);
    private static readonly DateOnly End = Start.AddDays(6);
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"premiere-partial-cache-{Guid.NewGuid():N}");
    private string CacheDirectory => Path.Combine(_root, "cache");
    private string CachePath => Path.Combine(CacheDirectory, "20260831-20260906-default.json");

    [Fact]
    public async Task PartialWeek_PersistsRowsAndMetadataWithoutBecomingComplete()
    {
        var cache = CreateCache();
        var before = DateTimeOffset.UtcNow;

        await cache.SetPartialWeekAsync(Start, End, "default", [Item("movie:1", "Partial discovery")], CancellationToken.None);

        var snapshot = Assert.IsType<CalendarCacheSnapshot>(await cache.GetWeekSnapshotAsync(Start, End, "default", CancellationToken.None));
        var metadata = Assert.IsType<CalendarCacheMetadata>(await cache.GetWeekMetadataAsync(Start, End, "default", CancellationToken.None));
        Assert.Equal(CalendarCacheCompleteness.Partial, snapshot.Metadata.Completeness);
        Assert.Equal(snapshot.Metadata, metadata);
        Assert.Equal(8, metadata.SchemaVersion);
        Assert.Equal(1, metadata.ItemCount);
        Assert.InRange(metadata.CachedAtUtc, before, DateTimeOffset.UtcNow);
        Assert.NotNull(snapshot.Generation);
        Assert.False(snapshot.IsComplete);
        Assert.Null(await cache.GetWeekAsync(Start, End, "default", CancellationToken.None));
        Assert.Equal("Partial discovery", Assert.Single((await CreateCache().GetWeekAsync(
            Start, End, "default", CancellationToken.None, allowExpired: true))!).Title);
    }

    [Theory]
    [InlineData(0, 8)]
    [InlineData(-48, 8)]
    [InlineData(-48, 7)]
    [InlineData(0, 999)]
    public async Task PartialWeek_PreservesCompleteEnvelopeByteForByte(int hoursAgo, int schemaVersion)
    {
        await WriteEnvelopeAsync([Item("movie:1", "Complete discovery")], CalendarCacheCompleteness.Complete,
            DateTimeOffset.UtcNow.AddHours(hoursAgo), schemaVersion);
        var before = await File.ReadAllBytesAsync(CachePath);

        await CreateCache().SetPartialWeekAsync(Start, End, "default", [Item("movie:2", "Failed refresh")], CancellationToken.None);

        Assert.Equal(before, await File.ReadAllBytesAsync(CachePath));
        Assert.Empty(Directory.GetFiles(CacheDirectory, "*.tmp"));
    }

    [Fact]
    public async Task CompleteWeek_ReplacesPartialWeekNormally()
    {
        var cache = CreateCache();
        await cache.SetPartialWeekAsync(Start, End, "default", [Item("movie:1", "Partial")], CancellationToken.None);

        await cache.SetWeekAsync(Start, End, "default", [Item("movie:2", "Complete")], CancellationToken.None);

        var snapshot = (await cache.GetWeekSnapshotAsync(Start, End, "default", CancellationToken.None))!;
        Assert.True(snapshot.IsComplete);
        Assert.Equal(CalendarCacheCompleteness.Complete, snapshot.Metadata.Completeness);
        Assert.Equal("Complete", Assert.Single((await cache.GetWeekAsync(Start, End, "default", CancellationToken.None))!).Title);
    }

    [Fact]
    public async Task PartialWeek_RetainsMissingRowsAndNewerPublishedMetadata()
    {
        var cache = CreateCache();
        var timestamp = DateTimeOffset.UtcNow.AddMinutes(-5);
        var latest = Item("movie:1", "Latest metadata") with { LastUpdatedUtc = timestamp, ImdbScore = 8.4 };
        var missing = Item("movie:2", "Concurrent discovery");
        var old = Item("movie:3", "Old discovery") with { LastUpdatedUtc = timestamp.AddMinutes(-1) };
        await cache.SetPartialWeekAsync(Start, End, "default", [latest, missing, old], CancellationToken.None);

        await cache.SetPartialWeekAsync(Start, End, "default", [
            latest with { Title = "Stale metadata", ImdbScore = null },
            old with { Title = "New discovery", LastUpdatedUtc = timestamp.AddMinutes(1) },
            Item("movie:4", "Added discovery")
        ], CancellationToken.None);

        var rows = (await cache.GetWeekSnapshotAsync(Start, End, "default", CancellationToken.None))!.Items;
        Assert.Equal(4, rows.Count);
        Assert.Equal("Latest metadata", rows.Single(item => item.CanonicalId == "movie:1").Title);
        Assert.Equal(8.4, rows.Single(item => item.CanonicalId == "movie:1").ImdbScore);
        Assert.Equal("Concurrent discovery", rows.Single(item => item.CanonicalId == "movie:2").Title);
        Assert.Equal("New discovery", rows.Single(item => item.CanonicalId == "movie:3").Title);
        Assert.Equal("Added discovery", rows.Single(item => item.CanonicalId == "movie:4").Title);
    }

    [Fact]
    public async Task PartialWeek_DoesNotCollapseAmbiguousCanonicalIdentities()
    {
        var cache = CreateCache();
        await cache.SetPartialWeekAsync(Start, End, "default", [
            Item("movie:1", "First existing row"), Item("movie:1", "Second existing row"), Item("", "Unidentified row")
        ], CancellationToken.None);

        await cache.SetPartialWeekAsync(Start, End, "default", [Item("movie:1", "Incoming row")], CancellationToken.None);

        var rows = (await cache.GetWeekSnapshotAsync(Start, End, "default", CancellationToken.None))!.Items;
        Assert.Equal(4, rows.Count);
        Assert.Contains(rows, row => row.Title == "First existing row");
        Assert.Contains(rows, row => row.Title == "Second existing row");
        Assert.Contains(rows, row => row.Title == "Unidentified row");
        Assert.Contains(rows, row => row.Title == "Incoming row");
    }

    [Fact]
    public async Task PartialWeek_ChecksForCompletePublicationAfterAcquiringLock()
    {
        Directory.CreateDirectory(CacheDirectory);
        Task pending;
        byte[] complete;
        await using (var heldLock = new FileStream(Path.Combine(CacheDirectory, ".publish.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            pending = CreateCache().SetPartialWeekAsync(Start, End, "default", [Item("movie:1", "Partial")], CancellationToken.None);
            Assert.False(pending.IsCompleted);
            await WriteEnvelopeAsync([Item("movie:2", "Concurrent complete")], CalendarCacheCompleteness.Complete, DateTimeOffset.UtcNow);
            complete = await File.ReadAllBytesAsync(CachePath);
        }

        await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(complete, await File.ReadAllBytesAsync(CachePath));
    }

    [Fact]
    public async Task ConcurrentPartialWeeks_RetainEveryPublishedDiscovery()
    {
        await Task.WhenAll(Enumerable.Range(1, 12).Select(index => CreateCache().SetPartialWeekAsync(
            Start, End, "default", [Item($"movie:{index}", $"Discovery {index}")], CancellationToken.None)));

        var snapshot = (await CreateCache().GetWeekSnapshotAsync(Start, End, "default", CancellationToken.None))!;
        Assert.Equal(CalendarCacheCompleteness.Partial, snapshot.Metadata.Completeness);
        Assert.Equal(12, snapshot.Metadata.ItemCount);
        Assert.Equal(12, snapshot.Items.Select(item => item.CanonicalId).Distinct().Count());
        Assert.Empty(Directory.GetFiles(CacheDirectory, "*.tmp"));
    }

    [Fact]
    public async Task PartialWeek_CanceledWhileWaitingForLockPreservesExistingFile()
    {
        var cache = CreateCache();
        await cache.SetPartialWeekAsync(Start, End, "default", [Item("movie:1", "Existing")], CancellationToken.None);
        var before = await File.ReadAllBytesAsync(CachePath);
        using var cancellation = new CancellationTokenSource();
        await using var heldLock = new FileStream(Path.Combine(CacheDirectory, ".publish.lock"),
            FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        var pending = cache.SetPartialWeekAsync(Start, End, "default", [Item("movie:2", "Canceled")], cancellation.Token);
        Assert.False(pending.IsCompleted);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        Assert.Equal(before, await File.ReadAllBytesAsync(CachePath));
        Assert.Empty(Directory.GetFiles(CacheDirectory, "*.tmp"));
    }

    [Fact]
    public async Task PartialWeek_CanceledDuringWritePreparationPreservesExistingFileAndRemovesTemporaryFile()
    {
        var cache = CreateCache();
        await cache.SetPartialWeekAsync(Start, End, "default", [Item("movie:1", "Existing")], CancellationToken.None);
        var before = await File.ReadAllBytesAsync(CachePath);
        using var cancellation = new CancellationTokenSource();
        var items = new CancelOnEnumeration([Item("movie:2", "Canceled")], cancellation);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.SetPartialWeekAsync(
            Start, End, "default", items, cancellation.Token));

        Assert.Equal(before, await File.ReadAllBytesAsync(CachePath));
        Assert.Empty(Directory.GetFiles(CacheDirectory, "*.tmp"));
        Assert.Equal("Existing", Assert.Single((await cache.GetWeekAsync(
            Start, End, "default", CancellationToken.None, allowExpired: true))!).Title);
    }

    [Fact]
    public async Task PartialWeek_DefaultAdapterDoesNotPublishPartialRowsAsComplete()
    {
        var adapter = new LegacyCacheAdapter();

        await ((ICalendarCache)adapter).SetPartialWeekAsync(Start, End, "default", [Item("movie:1", "Partial")], CancellationToken.None);

        Assert.False(adapter.CompleteWriteCalled);
    }

    private async Task WriteEnvelopeAsync(IReadOnlyList<PremiereItem> items, CalendarCacheCompleteness completeness,
        DateTimeOffset cachedAtUtc, int schemaVersion = 8)
    {
        Directory.CreateDirectory(CacheDirectory);
        await File.WriteAllTextAsync(CachePath, JsonSerializer.Serialize(new
        {
            schemaVersion,
            generation = "existing-generation",
            cachedAtUtc,
            completeness,
            itemCount = items.Count,
            items
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private static PremiereItem Item(string id, string title) => new()
    {
        CanonicalId = id,
        Type = PremiereItemType.MovieFirstRelease,
        MediaType = PremiereMediaType.Movie,
        TmdbId = 1,
        Title = title,
        PremiereDate = Start
    };

    private FileCalendarCache CreateCache() => new(
        Microsoft.Extensions.Options.Options.Create(new CalendarCacheOptions { Enabled = true, Directory = "cache", WeekCacheHours = 1 }),
        new TestEnvironment(_root), NullLogger<FileCalendarCache>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "PremiereCalendar.Tests";
        public string WebRootPath { get; set; } = Path.Combine(root, "wwwroot");
        public IFileProvider WebRootFileProvider { get; set; } = default!;
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = default!;
    }

    private sealed class CancelOnEnumeration(IReadOnlyList<PremiereItem> items, CancellationTokenSource cancellation) : IReadOnlyList<PremiereItem>
    {
        public int Count => items.Count;
        public PremiereItem this[int index] => items[index];
        public IEnumerator<PremiereItem> GetEnumerator()
        {
            cancellation.Cancel();
            return items.GetEnumerator();
        }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class LegacyCacheAdapter : ICalendarCache
    {
        public bool CompleteWriteCalled { get; private set; }
        public Task<IReadOnlyList<PremiereItem>?> GetWeekAsync(DateOnly start, DateOnly end, string cacheKey,
            CancellationToken cancellationToken, bool allowExpired = false) => Task.FromResult<IReadOnlyList<PremiereItem>?>(null);
        public Task SetWeekAsync(DateOnly start, DateOnly end, string cacheKey, IReadOnlyList<PremiereItem> items,
            CancellationToken cancellationToken)
        {
            CompleteWriteCalled = true;
            return Task.CompletedTask;
        }
    }
}
