using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class FileCalendarCache : ICalendarCache, ICalendarCacheMaintenance
{
    private const int CurrentSchemaVersion = 7;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly CalendarCacheOptions _options;
    private readonly IWebHostEnvironment _environment;
    private readonly ILogger<FileCalendarCache> _logger;

    public FileCalendarCache(
        IOptions<CalendarCacheOptions> options,
        IWebHostEnvironment environment,
        ILogger<FileCalendarCache> logger)
    {
        _options = options.Value;
        _environment = environment;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PremiereItem>?> GetWeekAsync(
        DateOnly start,
        DateOnly end,
        string cacheKey,
        CancellationToken cancellationToken,
        bool allowExpired = false)
    {
        if (!_options.Enabled)
        {
            return null;
        }

        var path = CachePath(start, end, cacheKey);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var cached = await JsonSerializer.DeserializeAsync<CalendarCacheEnvelope>(stream, JsonOptions, cancellationToken);
            if (cached is null || !IsCurrentSchema(path, cached))
            {
                return null;
            }

            var maxAge = TimeSpan.FromHours(Math.Max(1, _options.WeekCacheHours));
            if (!allowExpired && IsExpired(path, cached, maxAge))
            {
                return null;
            }

            return cached.Items;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read calendar cache from {CachePath}.", path);
            return null;
        }
    }

    public async Task<CalendarCacheMetadata?> GetWeekMetadataAsync(
        DateOnly start,
        DateOnly end,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return null;
        }

        var path = CachePath(start, end, cacheKey);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var cached = await JsonSerializer.DeserializeAsync<CalendarCacheEnvelope>(stream, JsonOptions, cancellationToken);
            if (cached is null || !IsCurrentSchema(path, cached))
            {
                return null;
            }

            return new CalendarCacheMetadata(
                EffectiveCachedAtUtc(path, cached),
                cached.Items.Count,
                cached.SchemaVersion,
                cached.Completeness);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not read calendar cache metadata from {CachePath}.", path);
            return null;
        }
    }

    public async Task SetWeekAsync(
        DateOnly start,
        DateOnly end,
        string cacheKey,
        IReadOnlyList<PremiereItem> items,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return;
        }

        var path = CachePath(start, end, cacheKey);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var envelope = new CalendarCacheEnvelope
        {
            SchemaVersion = CurrentSchemaVersion,
            CachedAtUtc = DateTimeOffset.UtcNow,
            Completeness = CalendarCacheCompleteness.Complete,
            Items = [.. items]
        };

        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, envelope, JsonOptions, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(tempPath, path, overwrite: true);
        }
        catch
        {
            TryDeleteFile(tempPath);
            throw;
        }
    }

    public Task<int> CleanupAsync(
        DateTimeOffset nowUtc,
        TimeSpan retention,
        CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return Task.FromResult(0);
        }

        var directory = CacheDirectory();
        if (!Directory.Exists(directory))
        {
            return Task.FromResult(0);
        }

        var cutoffUtc = nowUtc - retention;
        var cutoffDate = DateOnly.FromDateTime(nowUtc.UtcDateTime).AddDays(-Math.Max(1, (int)Math.Ceiling(retention.TotalDays)));
        var removed = 0;

        foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!TryGetWeekEndFromFileName(path, out var weekEnd) || weekEnd >= cutoffDate)
            {
                continue;
            }

            try
            {
                File.Delete(path);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not delete old calendar cache file {CachePath}.", path);
            }
        }

        foreach (var path in Directory.EnumerateFiles(directory, "*.tmp"))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (File.GetLastWriteTimeUtc(path) >= cutoffUtc.UtcDateTime)
            {
                continue;
            }

            try
            {
                File.Delete(path);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not delete old temporary calendar cache file {CachePath}.", path);
            }
        }

        return Task.FromResult(removed);
    }

    private string CachePath(DateOnly start, DateOnly end, string cacheKey)
    {
        var directory = CacheDirectory();

        var safeCacheKey = string.IsNullOrWhiteSpace(cacheKey)
            ? "default"
            : string.Concat(cacheKey.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_'));
        if (string.IsNullOrWhiteSpace(safeCacheKey))
        {
            safeCacheKey = "default";
        }

        var fileName = string.Create(
            CultureInfo.InvariantCulture,
            $"{start:yyyyMMdd}-{end:yyyyMMdd}-{safeCacheKey}.json");

        return Path.Combine(directory, fileName);
    }

    private string CacheDirectory()
    {
        return Path.IsPathRooted(_options.Directory)
            ? _options.Directory
            : Path.Combine(_environment.ContentRootPath, _options.Directory);
    }

    private static bool TryGetWeekEndFromFileName(string path, out DateOnly weekEnd)
    {
        weekEnd = default;
        var fileName = Path.GetFileNameWithoutExtension(path);
        var parts = fileName.Split('-', 3, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length >= 2
            && DateOnly.TryParseExact(
                parts[1],
                "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out weekEnd);
    }

    private bool IsCurrentSchema(string path, CalendarCacheEnvelope? cached)
    {
        if (cached?.SchemaVersion == CurrentSchemaVersion)
        {
            return true;
        }

        _logger.LogInformation(
            "Ignoring calendar cache {CachePath} because schema version {SchemaVersion} does not match current schema version {CurrentSchemaVersion}.",
            path,
            cached?.SchemaVersion,
            CurrentSchemaVersion);

        return false;
    }

    private static bool IsExpired(string path, CalendarCacheEnvelope cached, TimeSpan maxAge)
    {
        return DateTimeOffset.UtcNow - EffectiveCachedAtUtc(path, cached) > maxAge;
    }

    private static DateTimeOffset EffectiveCachedAtUtc(string path, CalendarCacheEnvelope cached)
    {
        if (cached.CachedAtUtc != default)
        {
            return cached.CachedAtUtc;
        }

        return new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record CalendarCacheEnvelope
    {
        public int SchemaVersion { get; init; }
        public DateTimeOffset CachedAtUtc { get; init; }
        public CalendarCacheCompleteness Completeness { get; init; } = CalendarCacheCompleteness.Complete;
        public List<PremiereItem> Items { get; init; } = [];
    }
}
