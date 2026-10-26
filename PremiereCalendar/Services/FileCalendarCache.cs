using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class FileCalendarCache : ICalendarCache, ICalendarCacheMaintenance
{
    private const int CurrentSchemaVersion = 8;
    private const int MetadataHeaderReadLimit = 16 * 1024;

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
            await using var stream = OpenCacheReader(path);
            var cached = await JsonSerializer.DeserializeAsync<CalendarCacheEnvelope>(stream, JsonOptions, cancellationToken);
            if (cached is null || !IsCurrentSchema(path, cached))
            {
                return null;
            }

            if (!IsValidCompleteness(cached.Completeness)
                || !allowExpired && cached.Completeness != CalendarCacheCompleteness.Complete)
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
            cancellationToken.ThrowIfCancellationRequested();
            if (TryReadMetadataHeader(path, out var header))
            {
                if (header.SchemaVersion != CurrentSchemaVersion)
                {
                    LogAndRejectSchema(path, header.SchemaVersion);
                    return null;
                }
                if (!IsValidCompleteness(header.Completeness))
                {
                    return null;
                }

                return new CalendarCacheMetadata(
                    header.CachedAtUtc == default
                        ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero)
                        : header.CachedAtUtc,
                    header.ItemCount,
                    header.SchemaVersion,
                    header.Completeness);
            }

            // Older envelopes do not include itemCount, so retain the compatible full parse fallback.
            await using var stream = OpenCacheReader(path);
            var cached = await JsonSerializer.DeserializeAsync<CalendarCacheEnvelope>(stream, JsonOptions, cancellationToken);
            if (cached is null || !IsCurrentSchema(path, cached) || !IsValidCompleteness(cached.Completeness))
            {
                return null;
            }

            return new CalendarCacheMetadata(EffectiveCachedAtUtc(path, cached), cached.Items.Count, cached.SchemaVersion, cached.Completeness);
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
            ItemCount = items.Count,
            Items = [.. items]
        };

        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await JsonSerializer.SerializeAsync(stream, envelope, JsonOptions, cancellationToken);
            }

            await CalendarCachePublicationLock.RunAsync(
                CacheDirectory(),
                token =>
                {
                    token.ThrowIfCancellationRequested();
                    ReplaceAtomically(tempPath, path);
                    return Task.CompletedTask;
                },
                cancellationToken);
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

    private void LogAndRejectSchema(string path, int schemaVersion)
    {
        _logger.LogInformation(
            "Ignoring calendar cache {CachePath} because schema version {SchemaVersion} does not match current schema version {CurrentSchemaVersion}.",
            path,
            schemaVersion,
            CurrentSchemaVersion);
    }

    private static FileStream OpenCacheReader(string path)
    {
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
    }

    private static void ReplaceAtomically(string temporaryPath, string destinationPath)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (!File.Exists(destinationPath))
            {
                try
                {
                    File.Move(temporaryPath, destinationPath);
                    return;
                }
                catch (IOException) when (File.Exists(destinationPath))
                {
                    continue;
                }
            }

            try
            {
                File.Replace(temporaryPath, destinationPath, destinationBackupFileName: null);
                return;
            }
            catch (FileNotFoundException) when (attempt < 2)
            {
                // Another writer removed the old entry between the existence check and replace.
            }
        }

        throw new IOException($"Could not atomically replace cache file '{destinationPath}'.");
    }

    private static bool TryReadMetadataHeader(string path, out CalendarCacheHeader header)
    {
        header = default;
        var prefix = new byte[MetadataHeaderReadLimit];

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.SequentialScan);
            var length = 0;
            while (length < prefix.Length)
            {
                var read = stream.Read(prefix, length, Math.Min(4096, prefix.Length - length));
                if (read == 0)
                {
                    return false;
                }

                length += read;
                if (TryParseMetadataHeader(prefix.AsSpan(0, length), out header, out var foundItemsProperty))
                {
                    return foundItemsProperty;
                }

                if (foundItemsProperty)
                {
                    return false;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }

    private static bool TryParseMetadataHeader(
        ReadOnlySpan<byte> prefix,
        out CalendarCacheHeader header,
        out bool foundItemsProperty)
    {
        header = default;
        foundItemsProperty = false;
        var schemaVersion = 0;
        var itemCount = -1;
        var completeness = CalendarCacheCompleteness.Complete;
        var cachedAtUtc = default(DateTimeOffset);

        try
        {
            var reader = new Utf8JsonReader(prefix, isFinalBlock: false, state: default);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                return false;
            }

            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndObject)
                {
                    return false;
                }

                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    return false;
                }

                var propertyName = reader.GetString();
                if (!reader.Read())
                {
                    return false;
                }

                if (string.Equals(propertyName, "items", StringComparison.OrdinalIgnoreCase))
                {
                    foundItemsProperty = true;
                    if (itemCount < 0)
                    {
                        return false;
                    }

                    header = new CalendarCacheHeader(schemaVersion, cachedAtUtc, itemCount, completeness);
                    return true;
                }

                switch (propertyName?.ToLowerInvariant())
                {
                    case "schemaversion":
                        if (reader.TokenType != JsonTokenType.Number) return false;
                        if (!reader.TryGetInt32(out schemaVersion)) return false;
                        break;
                    case "cachedatutc":
                        if (reader.TokenType != JsonTokenType.String) return false;
                        if (!reader.TryGetDateTimeOffset(out cachedAtUtc)) return false;
                        break;
                    case "completeness":
                        if (reader.TokenType != JsonTokenType.Number) return false;
                        if (!reader.TryGetInt32(out var completenessValue)) return false;
                        completeness = (CalendarCacheCompleteness)completenessValue;
                        if (!IsValidCompleteness(completeness)) return false;
                        break;
                    case "itemcount":
                        if (reader.TokenType != JsonTokenType.Number) return false;
                        if (!reader.TryGetInt32(out itemCount) || itemCount < 0) return false;
                        break;
                    default:
                        if (reader.TokenType is JsonTokenType.StartArray or JsonTokenType.StartObject)
                        {
                            if (!reader.TrySkip()) return false;
                        }
                        break;
                }
            }
        }
        catch (JsonException)
        {
            return false;
        }

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

    private static bool IsValidCompleteness(CalendarCacheCompleteness completeness)
    {
        return completeness is CalendarCacheCompleteness.Complete or CalendarCacheCompleteness.Partial;
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
        public int ItemCount { get; init; } = -1;
        public List<PremiereItem> Items { get; init; } = [];
    }

    private readonly record struct CalendarCacheHeader(
        int SchemaVersion,
        DateTimeOffset CachedAtUtc,
        int ItemCount,
        CalendarCacheCompleteness Completeness);
}
