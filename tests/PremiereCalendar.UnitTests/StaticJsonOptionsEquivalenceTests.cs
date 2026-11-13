using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

/// <summary>
/// Shared options and cheaper hex formatting must not change any emitted byte.
/// The reference values are produced by the former per-call code paths.
/// </summary>
public sealed class StaticJsonOptionsEquivalenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "premiere-static-json-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(75)]
    public void ExportJsonIsByteIdenticalToFreshWebOptionsOutput(int count)
    {
        var items = PerformanceGoldenSupport.Items(count);
        var expected = JsonSerializer.Serialize<IEnumerable<PremiereCalendar.Models.PremiereItem>>(items, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(expected, CalendarExportService.ToJson(items));
        Assert.Equal(expected, CalendarExportService.ToJson(items)); // The shared instance stays usable across calls.
    }

    [Fact]
    public void ExportJsonStillHonoursCallerSuppliedOptions()
    {
        var items = PerformanceGoldenSupport.Items(3);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true, PropertyNamingPolicy = null };
        Assert.Equal(JsonSerializer.Serialize<IEnumerable<PremiereCalendar.Models.PremiereItem>>(items, options), CalendarExportService.ToJson(items, options));
    }

    [Fact]
    public void WebOptionsSingletonMatchesPerCallWebOptionsForTheSameGraph()
    {
        var graph = new { Version = 1, RawBytes = 5L, Sha256 = "ab", Min = (DateTimeOffset?)new DateTimeOffset(2026, 11, 5, 18, 0, 0, TimeSpan.Zero), Missing = (string?)null,
            Ratio = 0.1 + 0.2, Items = new[] { new { PascalCase = "x", Date = new DateOnly(2026, 11, 2) } } };
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(graph, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            JsonSerializer.SerializeToUtf8Bytes(graph, JsonSerializerOptions.Web));
    }

    [Fact]
    public void ArchivedTraceIndexBytesEqualFreshWebOptionsSerializationAndHashIsLowercaseHex()
    {
        Directory.CreateDirectory(_root);
        var instance = Guid.NewGuid().ToString("N");
        var path = Path.Combine(_root, $"performance-20261105T1800000000000Z-{instance}-000001.ndjson");
        var text = new StringBuilder();
        for (var row = 0; row < 100; row++)
            text.AppendLine(JsonSerializer.Serialize(new { timestampUtc = new DateTimeOffset(2026, 11, 5, 18, 0, 0, TimeSpan.Zero).AddSeconds(row),
                instanceId = instance, processElapsedMilliseconds = row, eventSequence = row + 1, @event = "source.item.completed" }));
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
        var rawBytes = File.ReadAllBytes(path);

        Assert.True(PerformanceTraceArchive.Archive(path, 1024 * 1024));

        var archiveBytes = File.ReadAllBytes(path + ".gz");
        var indexBytes = File.ReadAllBytes(path + ".gz.index.json");
        using var index = JsonDocument.Parse(indexBytes);
        var hash = index.RootElement.GetProperty("sha256").GetString()!;
        // The hash is over the gzip input (the raw trace) and was formerly Convert.ToHexString(..).ToLowerInvariant().
        Assert.Equal(Convert.ToHexString(SHA256.HashData(rawBytes)).ToLowerInvariant(), hash);
        Assert.Equal(archiveBytes.Length, index.RootElement.GetProperty("compressedBytes").GetInt64());

        var traceIndexType = typeof(PerformanceTraceArchive).GetNestedType("TraceIndex", System.Reflection.BindingFlags.NonPublic)!;
        var roundTripped = JsonSerializer.Deserialize(indexBytes, traceIndexType, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(roundTripped, traceIndexType, new JsonSerializerOptions(JsonSerializerDefaults.Web)), indexBytes);
    }

    [Fact]
    public void LowercaseHexHelperEqualsTheFormerUppercaseThenLowerPattern()
    {
        foreach (var length in new[] { 0, 1, 16, 20, 32, 33 })
        {
            var bytes = SHA256.HashData(Encoding.UTF8.GetBytes("sample-" + length));
            var data = bytes.AsSpan(0, Math.Min(length, bytes.Length)).ToArray();
            Assert.Equal(Convert.ToHexString(data).ToLowerInvariant(), Convert.ToHexStringLower(data));
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
