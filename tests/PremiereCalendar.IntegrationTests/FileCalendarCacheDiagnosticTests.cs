using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed class FileCalendarCacheDiagnosticTests : IDisposable
{
    private static readonly DateOnly Start = new(2026, 8, 31);
    private static readonly DateOnly End = Start.AddDays(6);
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"premiere-cache-diagnostics-{Guid.NewGuid():N}");
    private readonly PerformanceDiagnosticStore _store;
    private readonly PremiereTelemetry _telemetry;
    private string CachePath => Path.Combine(_root, "cache", "20260831-20260906-default.json");

    public FileCalendarCacheDiagnosticTests()
    {
        _store = new PerformanceDiagnosticStore(new TelemetryOptions { PerformanceDiagnosticsDirectory = "diagnostics" }, _root);
        _telemetry = new PremiereTelemetry(_store);
    }

    [Fact]
    public async Task PersistedDiagnosticsDistinguishMissingPartialCompleteAndProtectedPublication()
    {
        var cache = CreateCache();
        var context = _telemetry.CreateDiagnosticContext() with { JobId = Guid.NewGuid().ToString("N"), SourceKey = "simkl" };
        using (_telemetry.UseDiagnosticContext(context))
        {
            Assert.Null(await cache.GetWeekSnapshotAsync(Start, End, "default", CancellationToken.None));
            Assert.Null(await cache.GetWeekMetadataAsync(Start, End, "default", CancellationToken.None));
            await cache.SetPartialWeekAsync(Start, End, "default", [Item()], CancellationToken.None);
            Assert.False((await cache.GetWeekSnapshotAsync(Start, End, "default", CancellationToken.None))!.IsComplete);
            Assert.Equal(CalendarCacheCompleteness.Partial, (await cache.GetWeekMetadataAsync(Start, End, "default", CancellationToken.None))!.Completeness);
            await cache.SetWeekAsync(Start, End, "default", [Item()], CancellationToken.None);
            Assert.True((await cache.GetWeekSnapshotAsync(Start, End, "default", CancellationToken.None))!.IsComplete);
            await cache.GetWeekMetadataAsync(Start, End, "default", CancellationToken.None);
            var complete = await File.ReadAllBytesAsync(CachePath);
            await cache.SetPartialWeekAsync(Start, End, "default", [], CancellationToken.None);
            Assert.Equal(complete, await File.ReadAllBytesAsync(CachePath));
        }

        var events = ReadEvents();
        Assert.Equal(new[]
        {
            "snapshot:miss:missing", "metadata:miss:missing", "partial:saved:partial",
            "snapshot:hit:partial", "metadata:hit:partial", "complete:saved:complete",
            "snapshot:hit:fresh", "metadata:hit:fresh", "partial:complete_preserved:complete"
        }, events.Select(Decision));
        Assert.All(events, entry =>
        {
            Assert.Equal(context.LoadId, entry.GetProperty("loadId").GetString());
            Assert.Equal(context.JobId, entry.GetProperty("jobId").GetString());
            Assert.Equal("simkl", entry.GetProperty("sourceKey").GetString());
            var details = entry.GetProperty("details");
            Assert.Equal("file_week", details.GetProperty("cache").GetString());
            Assert.Equal("default", details.GetProperty("cacheKey").GetString());
            Assert.Equal(Start.ToString("yyyy-MM-dd"), details.GetProperty("weekStart").GetString());
            Assert.Equal(End.ToString("yyyy-MM-dd"), details.GetProperty("weekEnd").GetString());
            Assert.True(details.GetProperty("durationMilliseconds").GetDouble() >= 0);
        });
        Assert.All(events.Skip(2), entry => Assert.Equal(1, entry.GetProperty("details").GetProperty("count").GetInt32()));

        await cache.GetWeekMetadataAsync(Start, End, "default", CancellationToken.None);
        var uncorrelated = ReadEvents()[^1];
        Assert.False(uncorrelated.TryGetProperty("loadId", out _));
        Assert.Equal("default", uncorrelated.GetProperty("details").GetProperty("cacheKey").GetString());
        AssertNoSensitiveData(ReadRawDiagnostics());
    }

    [Fact]
    public async Task PersistedDiagnosticsDistinguishSchemaInvalidEnvelopeAndMalformedJson()
    {
        var cache = CreateCache();
        await WriteEnvelopeAsync(schemaVersion: 999, CalendarCacheCompleteness.Partial);
        Assert.Null(await cache.GetWeekSnapshotAsync(Start, End, "default", CancellationToken.None));
        Assert.Null(await cache.GetWeekMetadataAsync(Start, End, "default", CancellationToken.None));
        await cache.SetPartialWeekAsync(Start, End, "default", [Item()], CancellationToken.None);

        const string malformed = "{\"private_title\":\"private-movie-title\",\"credential\":\"super-secret-token\"";
        await File.WriteAllTextAsync(CachePath, malformed);
        Assert.Null(await cache.GetWeekSnapshotAsync(Start, End, "default", CancellationToken.None));
        Assert.Null(await cache.GetWeekMetadataAsync(Start, End, "default", CancellationToken.None));
        await cache.SetPartialWeekAsync(Start, End, "default", [Item()], CancellationToken.None);
        Assert.Equal(malformed, await File.ReadAllTextAsync(CachePath));

        await WriteEnvelopeAsync(schemaVersion: 8, (CalendarCacheCompleteness)99);
        Assert.Null(await cache.GetWeekSnapshotAsync(Start, End, "default", CancellationToken.None));
        Assert.Null(await cache.GetWeekMetadataAsync(Start, End, "default", CancellationToken.None));
        await cache.SetPartialWeekAsync(Start, End, "default", [Item()], CancellationToken.None);

        Assert.Equal(new[]
        {
            "snapshot:miss:schema", "metadata:miss:schema", "partial:schema_preserved:schema",
            "snapshot:malformed:JsonException", "metadata:malformed:JsonException", "partial:malformed:JsonException",
            "snapshot:miss:invalid", "metadata:miss:invalid", "partial:invalid_preserved:invalid"
        }, ReadEvents().Select(Decision));
        AssertNoSensitiveData(ReadRawDiagnostics());
    }

    [Fact]
    public async Task PersistedDiagnosticsDistinguishExpiredAndDisabledCache()
    {
        await WriteEnvelopeAsync(8, CalendarCacheCompleteness.Complete, DateTimeOffset.UtcNow.AddHours(-3));
        var cache = CreateCache();
        Assert.False((await cache.GetWeekSnapshotAsync(Start, End, "default", CancellationToken.None))!.IsFresh);
        await cache.GetWeekMetadataAsync(Start, End, "default", CancellationToken.None);
        var disabled = CreateCache(enabled: false);
        await disabled.GetWeekSnapshotAsync(Start, End, "default", CancellationToken.None);
        await disabled.GetWeekMetadataAsync(Start, End, "default", CancellationToken.None);
        await disabled.SetWeekAsync(Start, End, "default", [Item()], CancellationToken.None);
        await disabled.SetPartialWeekAsync(Start, End, "default", [Item()], CancellationToken.None);

        Assert.Equal(new[]
        {
            "snapshot:hit:expired", "metadata:hit:expired", "snapshot:miss:disabled", "metadata:miss:disabled",
            "complete:skipped:disabled", "partial:skipped:disabled"
        }, ReadEvents().Select(Decision));
    }

    [Fact]
    public async Task PersistedDiagnosticsRecordCaughtCancellationAndPreserveExceptionBehavior()
    {
        await WriteEnvelopeAsync(8, CalendarCacheCompleteness.Partial);
        var cache = CreateCache();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetWeekSnapshotAsync(Start, End, "default", cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.GetWeekMetadataAsync(Start, End, "default", cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.SetWeekAsync(Start, End, "default", [Item()], cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cache.SetPartialWeekAsync(Start, End, "default", [Item()], cancellation.Token));

        var events = ReadEvents();
        Assert.Equal(4, events.Count);
        Assert.All(events, entry =>
        {
            Assert.Equal("canceled", entry.GetProperty("details").GetProperty("outcome").GetString());
            Assert.Contains(entry.GetProperty("details").GetProperty("reason").GetString(), new[] { "OperationCanceledException", "TaskCanceledException" });
        });

        File.Delete(CachePath);
        Assert.Null(await cache.GetWeekSnapshotAsync(Start, End, "default", cancellation.Token));
        Assert.Equal("snapshot:miss:missing", Decision(ReadEvents()[^1]));
    }

    [Fact]
    public async Task PersistedDiagnosticsRecordIoWithoutExceptionTextOrPath()
    {
        await WriteEnvelopeAsync(8, CalendarCacheCompleteness.Partial);
        var cache = CreateCache();
        await using (var blocked = new FileStream(CachePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Null(await cache.GetWeekSnapshotAsync(Start, End, "default", CancellationToken.None));
            Assert.Null(await cache.GetWeekMetadataAsync(Start, End, "default", CancellationToken.None));
            await cache.SetPartialWeekAsync(Start, End, "default", [Item()], CancellationToken.None);
        }

        Assert.Equal(new[] { "snapshot:io:IOException", "metadata:io:IOException", "partial:io:IOException" }, ReadEvents().Select(Decision));
        AssertNoSensitiveData(ReadRawDiagnostics());
    }

    private static string Decision(JsonElement entry)
    {
        var details = entry.GetProperty("details");
        return $"{details.GetProperty("stage").GetString()}:{details.GetProperty("outcome").GetString()}:{details.GetProperty("reason").GetString()}";
    }

    private void AssertNoSensitiveData(string raw)
    {
        foreach (var value in new[] { _root, "private-movie-title", "private-overview", "private.example", "super-secret-token", "items", "exceptionMessage" })
            Assert.DoesNotContain(value, raw, StringComparison.OrdinalIgnoreCase);
    }

    private List<JsonElement> ReadEvents() => ReadRawDiagnostics().Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToList();

    private string ReadRawDiagnostics()
    {
        _telemetry.FlushDiagnostics();
        return string.Concat(Directory.GetFiles(Path.Combine(_root, "diagnostics"), "performance-*.ndjson")
            .OrderBy(path => path, StringComparer.Ordinal).Select(path =>
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }));
    }

    private async Task WriteEnvelopeAsync(int schemaVersion, CalendarCacheCompleteness completeness, DateTimeOffset? cachedAtUtc = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
        await File.WriteAllTextAsync(CachePath, JsonSerializer.Serialize(new
        {
            schemaVersion,
            cachedAtUtc = cachedAtUtc ?? DateTimeOffset.UtcNow,
            completeness,
            itemCount = 1,
            items = new[] { Item() }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    private static PremiereItem Item() => new()
    {
        CanonicalId = "movie:1", MediaType = PremiereMediaType.Movie, TmdbId = 1,
        Title = "private-movie-title", Overview = "private-overview", PremiereDate = Start,
        ExternalUrl = "https://private.example/movie?token=super-secret-token"
    };

    private FileCalendarCache CreateCache(bool enabled = true) => new(
        Microsoft.Extensions.Options.Options.Create(new CalendarCacheOptions { Enabled = enabled, Directory = "cache", WeekCacheHours = 1 }),
        new TestEnvironment(_root), NullLogger<FileCalendarCache>.Instance, _telemetry);

    public void Dispose()
    {
        _store.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
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
}
