using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

/// <summary>
/// Calendar cache files are a persisted format with a downgrade path. Buffer sizes and file options
/// must not change a byte, and files written by the previous code path must still read back.
/// </summary>
public sealed class FileCalendarCacheFormatGoldenTests : IDisposable
{
    private static readonly JsonSerializerOptions ReferenceOptions = new(JsonSerializerDefaults.Web) { WriteIndented = false };
    private readonly string _root = Path.Combine(Path.GetTempPath(), "premiere-cache-golden-" + Guid.NewGuid().ToString("N"));
    private static readonly DateOnly Start = new(2026, 11, 2);
    private static readonly DateOnly End = new(2026, 11, 8);

    private FileCalendarCache CreateCache() => new(
        Microsoft.Extensions.Options.Options.Create(new CalendarCacheOptions { Directory = "cache", WeekCacheHours = 6 }),
        new Env(_root), NullLogger<FileCalendarCache>.Instance);

    private string SingleCacheFile() => Assert.Single(Directory.GetFiles(Path.Combine(_root, "cache"), "*.json", SearchOption.AllDirectories));

    [Fact]
    public async Task CompleteWeekFileHasTheFormerPropertyOrderAndByteIdenticalItems()
    {
        var items = PerformanceGoldenSupport.Items(500);
        await CreateCache().SetWeekAsync(Start, End, "golden", items, default);
        var bytes = File.ReadAllBytes(SingleCacheFile());

        using var document = JsonDocument.Parse(bytes);
        Assert.Equal(["schemaVersion", "generation", "cachedAtUtc", "completeness", "itemCount", "unavailableSources", "items"],
            document.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(500, document.RootElement.GetProperty("itemCount").GetInt32());
        Assert.Equal(JsonSerializer.Serialize(items, ReferenceOptions), document.RootElement.GetProperty("items").GetRawText());
        // Compact output: no indentation anywhere, and the file is exactly one JSON value with no trailing bytes.
        Assert.Equal((byte)'{', bytes[0]);
        Assert.Equal((byte)'}', bytes[^1]);
        Assert.DoesNotContain((byte)'\n', bytes);
    }

    [Fact]
    public async Task WholeEnvelopeEqualsReferenceSerializationApartFromGenerationAndTimestamp()
    {
        var items = PerformanceGoldenSupport.Items(120);
        await CreateCache().SetWeekAsync(Start, End, "golden", items, default);
        var text = File.ReadAllText(SingleCacheFile());
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        var reference = JsonSerializer.Serialize(new
        {
            SchemaVersion = root.GetProperty("schemaVersion").GetInt32(),
            Generation = root.GetProperty("generation").GetString(),
            CachedAtUtc = root.GetProperty("cachedAtUtc").GetDateTimeOffset(),
            Completeness = root.GetProperty("completeness").GetInt32() is var completeness ? (CalendarCacheCompleteness)completeness : default,
            ItemCount = items.Count,
            UnavailableSources = (object?)null,
            Items = items
        }, ReferenceOptions);
        Assert.Equal(reference.Length, text.Length);
        Assert.Equal(reference, text);
    }

    [Fact]
    public async Task FileWrittenByTheFormerCodePathReadsBackAndRepeatedWritesAreStable()
    {
        var items = PerformanceGoldenSupport.Items(200);
        var cache = CreateCache();
        await cache.SetWeekAsync(Start, End, "golden", items, default);
        var path = SingleCacheFile();
        using (var first = JsonDocument.Parse(File.ReadAllBytes(path)))
        {
            var legacy = JsonSerializer.Serialize(new
            {
                SchemaVersion = first.RootElement.GetProperty("schemaVersion").GetInt32(),
                Generation = "legacy-generation",
                CachedAtUtc = first.RootElement.GetProperty("cachedAtUtc").GetDateTimeOffset(),
                Completeness = CalendarCacheCompleteness.Complete,
                ItemCount = items.Count,
                UnavailableSources = (object?)null,
                Items = items
            }, ReferenceOptions);
            // Former writer: File.Create + JsonSerializer.SerializeAsync.
            await using (var stream = File.Create(path))
                await JsonSerializer.SerializeAsync(stream, JsonDocument.Parse(legacy).RootElement, ReferenceOptions);
        }
        var read = await cache.GetWeekAsync(Start, End, "golden", default, allowExpired: true);
        Assert.NotNull(read);
        Assert.Equal(items.Count, read.Count);
        Assert.Equal(JsonSerializer.Serialize(items, ReferenceOptions), JsonSerializer.Serialize(read, ReferenceOptions));

        for (var round = 0; round < 3; round++) await cache.SetWeekAsync(Start, End, "golden", items, default);
        using var again = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.Equal(JsonSerializer.Serialize(items, ReferenceOptions), again.RootElement.GetProperty("items").GetRawText());
    }

    [Fact]
    public async Task PartialWeekFileKeepsTheFormerLayoutAndRoundTrips()
    {
        var items = PerformanceGoldenSupport.Items(60);
        var cache = CreateCache();
        await cache.SetPartialWeekAsync(Start, End, "golden", items, default);
        var path = SingleCacheFile();
        using var document = JsonDocument.Parse(File.ReadAllBytes(path));
        Assert.Equal(["schemaVersion", "generation", "cachedAtUtc", "completeness", "itemCount", "unavailableSources", "items"],
            document.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.Equal(JsonSerializer.Serialize(items, ReferenceOptions), document.RootElement.GetProperty("items").GetRawText());
        var snapshot = await cache.GetWeekSnapshotAsync(Start, End, "golden", default);
        Assert.NotNull(snapshot);
        Assert.Equal(items.Count, snapshot.Items.Count);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "cache"), "*.tmp", SearchOption.AllDirectories));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class Env(string root) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "PremiereCalendar.Tests";
        public string WebRootPath { get; set; } = Path.Combine(root, "wwwroot");
        public IFileProvider WebRootFileProvider { get; set; } = default!;
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = default!;
    }
}
