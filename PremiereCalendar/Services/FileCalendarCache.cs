using System.Diagnostics;
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
    private readonly PremiereTelemetry? _telemetry;

    public FileCalendarCache(
        IOptions<CalendarCacheOptions> options,
        IWebHostEnvironment environment,
        ILogger<FileCalendarCache> logger,
        PremiereTelemetry? telemetry = null)
    {
        _options = options.Value;
        _environment = environment;
        _logger = logger;
        _telemetry = telemetry;
    }

    public async Task<IReadOnlyList<PremiereItem>?> GetWeekAsync(
        DateOnly start,
        DateOnly end,
        string cacheKey,
        CancellationToken cancellationToken,
        bool allowExpired = false)
    {
        var snapshot = await GetWeekSnapshotAsync(start, end, cacheKey, cancellationToken);
        return snapshot is not null && (allowExpired || snapshot.IsComplete) ? snapshot.Items : null;
    }

    public async Task<CalendarCacheSnapshot?> GetWeekSnapshotAsync(
        DateOnly start,
        DateOnly end,
        string cacheKey,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        if (!_options.Enabled)
        {
            RecordCacheDiagnostic("calendar.cache.file.read", "snapshot", start, end, cacheKey, started, "miss", "disabled");
            return null;
        }

        var path = CachePath(start, end, cacheKey);
        if (!File.Exists(path))
        {
            RecordCacheDiagnostic("calendar.cache.file.read", "snapshot", start, end, cacheKey, started, "miss", "missing");
            return null;
        }

        try
        {
            await using var stream = OpenCacheReader(path);
            var cached = await JsonSerializer.DeserializeAsync<CalendarCacheEnvelope>(stream, JsonOptions, cancellationToken);
            if (cached is null || cached.Items is null)
            {
                RecordCacheDiagnostic("calendar.cache.file.read", "snapshot", start, end, cacheKey, started, "miss", "invalid");
                return null;
            }
            if (!IsCurrentSchema(path, cached))
            {
                RecordCacheDiagnostic("calendar.cache.file.read", "snapshot", start, end, cacheKey, started, "miss", "schema");
                return null;
            }

            if (!IsValidCompleteness(cached.Completeness))
            {
                RecordCacheDiagnostic("calendar.cache.file.read", "snapshot", start, end, cacheKey, started, "miss", "invalid");
                return null;
            }

            var maxAge = TimeSpan.FromHours(Math.Max(1, _options.WeekCacheHours));
            var cachedAtUtc = EffectiveCachedAtUtc(path, cached);
            RecordCacheDiagnostic("calendar.cache.file.read", "snapshot", start, end, cacheKey, started, "hit",
                CacheReadReason(cachedAtUtc, cached.Completeness), cached.Items.Count);
            return new CalendarCacheSnapshot(
                cached.Items,
                new CalendarCacheMetadata(cachedAtUtc, cached.Items.Count, cached.SchemaVersion, cached.Completeness),
                DateTimeOffset.UtcNow - cachedAtUtc <= maxAge,
                cached.Generation ?? $"{cached.SchemaVersion}:{cachedAtUtc:O}");
        }
        catch (OperationCanceledException ex)
        {
            RecordCacheDiagnostic("calendar.cache.file.read", "snapshot", start, end, cacheKey, started, "canceled", ex.GetType().Name);
            throw;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            RecordCacheDiagnostic("calendar.cache.file.read", "snapshot", start, end, cacheKey, started,
                ex is JsonException ? "malformed" : "io", ex.GetType().Name);
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
        var started = Stopwatch.GetTimestamp();
        if (!_options.Enabled)
        {
            RecordCacheDiagnostic("calendar.cache.file.metadata", "metadata", start, end, cacheKey, started, "miss", "disabled");
            return null;
        }

        var path = CachePath(start, end, cacheKey);
        if (!File.Exists(path))
        {
            RecordCacheDiagnostic("calendar.cache.file.metadata", "metadata", start, end, cacheKey, started, "miss", "missing");
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
                    RecordCacheDiagnostic("calendar.cache.file.metadata", "metadata", start, end, cacheKey, started, "miss", "schema");
                    return null;
                }
                if (!IsValidCompleteness(header.Completeness))
                {
                    RecordCacheDiagnostic("calendar.cache.file.metadata", "metadata", start, end, cacheKey, started, "miss", "invalid");
                    return null;
                }

                var metadata = new CalendarCacheMetadata(
                    header.CachedAtUtc == default
                        ? new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero)
                        : header.CachedAtUtc,
                    header.ItemCount,
                    header.SchemaVersion,
                    header.Completeness);
                RecordCacheDiagnostic("calendar.cache.file.metadata", "metadata", start, end, cacheKey, started, "hit",
                    CacheReadReason(metadata.CachedAtUtc, metadata.Completeness), metadata.ItemCount);
                return metadata;
            }

            // Older envelopes do not include itemCount, so retain the compatible full parse fallback.
            await using var stream = OpenCacheReader(path);
            var cached = await JsonSerializer.DeserializeAsync<CalendarCacheEnvelope>(stream, JsonOptions, cancellationToken);
            if (cached is null)
            {
                RecordCacheDiagnostic("calendar.cache.file.metadata", "metadata", start, end, cacheKey, started, "miss", "invalid");
                return null;
            }
            if (!IsCurrentSchema(path, cached))
            {
                RecordCacheDiagnostic("calendar.cache.file.metadata", "metadata", start, end, cacheKey, started, "miss", "schema");
                return null;
            }
            if (!IsValidCompleteness(cached.Completeness))
            {
                RecordCacheDiagnostic("calendar.cache.file.metadata", "metadata", start, end, cacheKey, started, "miss", "invalid");
                return null;
            }

            var fallback = new CalendarCacheMetadata(EffectiveCachedAtUtc(path, cached), cached.Items.Count, cached.SchemaVersion, cached.Completeness);
            RecordCacheDiagnostic("calendar.cache.file.metadata", "metadata", start, end, cacheKey, started, "hit",
                CacheReadReason(fallback.CachedAtUtc, fallback.Completeness), fallback.ItemCount);
            return fallback;
        }
        catch (OperationCanceledException ex)
        {
            RecordCacheDiagnostic("calendar.cache.file.metadata", "metadata", start, end, cacheKey, started, "canceled", ex.GetType().Name);
            throw;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            RecordCacheDiagnostic("calendar.cache.file.metadata", "metadata", start, end, cacheKey, started,
                ex is JsonException ? "malformed" : "io", ex.GetType().Name);
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
        var started = Stopwatch.GetTimestamp();
        if (!_options.Enabled)
        {
            RecordCacheDiagnostic("calendar.cache.file.write", "complete", start, end, cacheKey, started, "skipped", "disabled");
            return;
        }

        string? tempPath = null;
        try
        {
            var path = CachePath(start, end, cacheKey);
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var envelope = new CalendarCacheEnvelope
            {
                SchemaVersion = CurrentSchemaVersion,
                Generation = Guid.NewGuid().ToString("N"),
                CachedAtUtc = DateTimeOffset.UtcNow,
                Completeness = CalendarCacheCompleteness.Complete,
                ItemCount = items.Count,
                Items = [.. items]
            };
            tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
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
            RecordCacheDiagnostic("calendar.cache.file.write", "complete", start, end, cacheKey, started, "saved", "complete", items.Count);
        }
        catch (Exception ex)
        {
            if (tempPath is not null) TryDeleteFile(tempPath);
            RecordCacheDiagnostic("calendar.cache.file.write", "complete", start, end, cacheKey, started,
                CacheWriteFailureOutcome(ex), ex.GetType().Name);
            throw;
        }
    }

    public async Task SetPartialWeekAsync(
        DateOnly start,
        DateOnly end,
        string cacheKey,
        IReadOnlyList<PremiereItem> items,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        if (!_options.Enabled)
        {
            RecordCacheDiagnostic("calendar.cache.file.write", "partial", start, end, cacheKey, started, "skipped", "disabled");
            return;
        }

        try
        {
            var path = CachePath(start, end, cacheKey);
            await CalendarCachePublicationLock.RunAsync(CacheDirectory(), async token =>
            {
                CalendarCacheEnvelope? latest = null;
                if (File.Exists(path))
                {
                    try
                    {
                        await using var stream = OpenCacheReader(path);
                        latest = await JsonSerializer.DeserializeAsync<CalendarCacheEnvelope>(stream, JsonOptions, token);
                    }
                    catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
                    {
                        // Preserve an existing entry when we cannot safely establish its state.
                        _logger.LogWarning(ex, "Could not inspect calendar cache before partial publication to {CachePath}.", path);
                        RecordCacheDiagnostic("calendar.cache.file.write", "partial", start, end, cacheKey, started,
                            CacheWriteFailureOutcome(ex), ex.GetType().Name);
                        return;
                    }

                    // A failed refresh must never downgrade a complete discovery, even
                    // when it is expired or a newer publisher used another schema.
                    if (latest is null || latest.Items is null || !IsValidCompleteness(latest.Completeness))
                    {
                        RecordCacheDiagnostic("calendar.cache.file.write", "partial", start, end, cacheKey, started, "invalid_preserved", "invalid");
                        return;
                    }
                    if (latest.Completeness == CalendarCacheCompleteness.Complete)
                    {
                        RecordCacheDiagnostic("calendar.cache.file.write", "partial", start, end, cacheKey, started,
                            "complete_preserved", "complete", latest.Items.Count);
                        return;
                    }
                    if (latest.SchemaVersion != CurrentSchemaVersion)
                    {
                        RecordCacheDiagnostic("calendar.cache.file.write", "partial", start, end, cacheKey, started, "schema_preserved", "schema");
                        return;
                    }
                }

                var merged = latest is null ? [.. items] : MergePartialRows(latest.Items, items);
                var envelope = new CalendarCacheEnvelope
                {
                    SchemaVersion = CurrentSchemaVersion,
                    Generation = Guid.NewGuid().ToString("N"),
                    CachedAtUtc = DateTimeOffset.UtcNow,
                    Completeness = CalendarCacheCompleteness.Partial,
                    ItemCount = merged.Count,
                    Items = merged
                };
                var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
                try
                {
                    await using (var stream = File.Create(tempPath))
                    {
                        await JsonSerializer.SerializeAsync(stream, envelope, JsonOptions, token);
                    }

                    token.ThrowIfCancellationRequested();
                    ReplaceAtomically(tempPath, path);
                    RecordCacheDiagnostic("calendar.cache.file.write", "partial", start, end, cacheKey, started, "saved", "partial", merged.Count);
                }
                catch
                {
                    TryDeleteFile(tempPath);
                    throw;
                }
            }, cancellationToken);
        }
        catch (Exception ex)
        {
            RecordCacheDiagnostic("calendar.cache.file.write", "partial", start, end, cacheKey, started,
                CacheWriteFailureOutcome(ex), ex.GetType().Name);
            throw;
        }
    }

    private static List<PremiereItem> MergePartialRows(
        IReadOnlyList<PremiereItem> latest,
        IReadOnlyList<PremiereItem> incoming)
    {
        var result = incoming.ToList();
        var incomingIndexes = incoming.Select((item, index) => (item, index))
            .Where(row => !string.IsNullOrWhiteSpace(row.item.CanonicalId))
            .GroupBy(row => row.item.CanonicalId, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single().index, StringComparer.Ordinal);
        var latestById = UniqueCanonicalRows(latest);

        foreach (var item in latest)
        {
            if (!string.IsNullOrWhiteSpace(item.CanonicalId)
                && incomingIndexes.TryGetValue(item.CanonicalId, out var index)
                && latestById.ContainsKey(item.CanonicalId))
            {
                // Metadata maintenance preserves row timestamps. Prefer the
                // currently published row on ties so its enrichment survives.
                if (item.LastUpdatedUtc >= incoming[index].LastUpdatedUtc)
                {
                    result[index] = item;
                }
            }
            else
            {
                // Keep discoveries absent from this attempt. Ambiguous or blank
                // identities cannot safely be collapsed into another row.
                result.Add(item);
            }
        }

        return result;
    }

    public async Task<IReadOnlyList<PremiereItem>?> UpdateWeekMetadataAsync(
        DateOnly start,
        DateOnly end,
        string cacheKey,
        CalendarCacheSnapshot original,
        IReadOnlyList<PremiereItem> maintainedItems,
        CalendarCacheMaintenanceKind kind,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var stage = kind == CalendarCacheMaintenanceKind.Scores ? "maintenance_scores" : "maintenance_external_ids";
        if (!_options.Enabled || original.Metadata.SchemaVersion != CurrentSchemaVersion)
        {
            RecordCacheDiagnostic("calendar.cache.file.write", stage, start, end, cacheKey, started, "skipped",
                !_options.Enabled ? "disabled" : "schema");
            return null;
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        try
        {
            var originalById = UniqueCanonicalRows(original.Items);
            var maintainedById = UniqueCanonicalRows(maintainedItems);
            var path = CachePath(start, end, cacheKey);
            IReadOnlyList<PremiereItem>? result = null;
            await CalendarCachePublicationLock.RunAsync(CacheDirectory(), async token =>
            {
                // Only the local read/merge/publication is locked. Provider calls have
                // already finished, so discovery can publish while maintenance waits.
                var latest = await GetWeekSnapshotAsync(start, end, cacheKey, token);
                if (latest is null)
                {
                    RecordCacheDiagnostic("calendar.cache.file.write", stage, start, end, cacheKey, started, "skipped", "snapshot_unavailable");
                    return;
                }

                var sameGeneration = string.Equals(original.Generation, latest.Generation, StringComparison.Ordinal);
                var changed = false;
                var merged = new List<PremiereItem>(latest.Items.Count);
                foreach (var item in latest.Items)
                {
                    var updated = item;
                    if (!string.IsNullOrWhiteSpace(item.CanonicalId)
                        && originalById.TryGetValue(item.CanonicalId, out var before)
                        && maintainedById.TryGetValue(item.CanonicalId, out var after))
                    {
                        updated = MergeMetadata(item, before, after, kind, sameGeneration);
                    }

                    if (!Equals(updated, item))
                    {
                        updated = PremiereDiagnosticsFactory.ApplyMissingDataIssues(updated);
                        changed = true;
                    }

                    merged.Add(updated);
                }

                result = merged;
                if (!changed)
                {
                    RecordCacheDiagnostic("calendar.cache.file.write", stage, start, end, cacheKey, started, "unchanged", "no_delta", merged.Count);
                    return;
                }

                var envelope = new CalendarCacheEnvelope
                {
                    SchemaVersion = latest.Metadata.SchemaVersion,
                    Generation = Guid.NewGuid().ToString("N"),
                    CachedAtUtc = latest.Metadata.CachedAtUtc,
                    Completeness = latest.Metadata.Completeness,
                    ItemCount = merged.Count,
                    Items = merged
                };
                var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
                try
                {
                    await using (var stream = File.Create(tempPath))
                    {
                        await JsonSerializer.SerializeAsync(stream, envelope, JsonOptions, token);
                    }

                    token.ThrowIfCancellationRequested();
                    ReplaceAtomically(tempPath, path);
                    RecordCacheDiagnostic("calendar.cache.file.write", stage, start, end, cacheKey, started, "saved", "metadata", merged.Count);
                }
                catch
                {
                    TryDeleteFile(tempPath);
                    throw;
                }
            }, cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            RecordCacheDiagnostic("calendar.cache.file.write", stage, start, end, cacheKey, started,
                CacheWriteFailureOutcome(ex), ex.GetType().Name);
            throw;
        }
    }

    private string CacheReadReason(DateTimeOffset cachedAtUtc, CalendarCacheCompleteness completeness)
        => completeness == CalendarCacheCompleteness.Partial ? "partial"
            : DateTimeOffset.UtcNow - cachedAtUtc > TimeSpan.FromHours(Math.Max(1, _options.WeekCacheHours)) ? "expired" : "fresh";

    private static string CacheWriteFailureOutcome(Exception exception) => exception switch
    {
        OperationCanceledException => "canceled",
        JsonException => "malformed",
        IOException or UnauthorizedAccessException => "io",
        _ => "failed"
    };

    private void RecordCacheDiagnostic(string eventName, string stage, DateOnly start, DateOnly end, string cacheKey,
        long started, string outcome, string reason, int? count = null)
        => _telemetry?.RecordDiagnostic(eventName, details: new()
        {
            Stage = stage,
            Cache = "file_week",
            WeekStart = start,
            WeekEnd = end,
            CacheKey = cacheKey,
            Count = count,
            DurationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            Outcome = outcome,
            Reason = reason
        });

    private static Dictionary<string, PremiereItem> UniqueCanonicalRows(IReadOnlyList<PremiereItem> items)
        => items.Where(item => !string.IsNullOrWhiteSpace(item.CanonicalId))
            .GroupBy(item => item.CanonicalId, StringComparer.Ordinal)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.Ordinal);

    private static PremiereItem MergeMetadata(
        PremiereItem latest,
        PremiereItem before,
        PremiereItem after,
        CalendarCacheMaintenanceKind kind,
        bool sameGeneration)
    {
        if (latest.MediaType != before.MediaType || latest.TmdbId != before.TmdbId)
        {
            return latest;
        }

        // A newer generation may have enriched the same field. Only apply our
        // delta when that field still matches the value used for the remote work.
        T Merge<T>(T current, T previous, T maintained)
            => !EqualityComparer<T>.Default.Equals(previous, maintained)
                && (sameGeneration || EqualityComparer<T>.Default.Equals(current, previous))
                    ? maintained : current;

        var sameImdbIdentity = string.Equals(latest.ImdbId, before.ImdbId, StringComparison.OrdinalIgnoreCase);
        var sameRottenTomatoesIdentity = sameImdbIdentity
            && string.Equals(latest.Title, before.Title, StringComparison.Ordinal)
            && string.Equals(latest.WikidataId, before.WikidataId, StringComparison.OrdinalIgnoreCase)
            && latest.PremiereDate.Year == before.PremiereDate.Year;
        var imdbId = Merge(latest.ImdbId, before.ImdbId, after.ImdbId);
        return kind switch
        {
            CalendarCacheMaintenanceKind.Scores => latest with
            {
                ImdbScore = sameImdbIdentity ? Merge(latest.ImdbScore, before.ImdbScore, after.ImdbScore) : latest.ImdbScore,
                ImdbVoteCount = sameImdbIdentity ? Merge(latest.ImdbVoteCount, before.ImdbVoteCount, after.ImdbVoteCount) : latest.ImdbVoteCount,
                RottenTomatoesScore = sameRottenTomatoesIdentity
                    ? Merge(latest.RottenTomatoesScore, before.RottenTomatoesScore, after.RottenTomatoesScore) : latest.RottenTomatoesScore,
                RottenTomatoesAudienceScore = sameRottenTomatoesIdentity
                    ? Merge(latest.RottenTomatoesAudienceScore, before.RottenTomatoesAudienceScore, after.RottenTomatoesAudienceScore) : latest.RottenTomatoesAudienceScore,
                MetacriticScore = sameImdbIdentity ? Merge(latest.MetacriticScore, before.MetacriticScore, after.MetacriticScore) : latest.MetacriticScore
            },
            CalendarCacheMaintenanceKind.ExternalIds => latest with
            {
                ImdbId = imdbId,
                ImdbUrl = string.Equals(imdbId, after.ImdbId, StringComparison.OrdinalIgnoreCase)
                    ? Merge(latest.ImdbUrl, before.ImdbUrl, after.ImdbUrl) : latest.ImdbUrl,
                TvdbId = Merge(latest.TvdbId, before.TvdbId, after.TvdbId),
                WikidataId = Merge(latest.WikidataId, before.WikidataId, after.WikidataId)
            },
            _ => latest
        };
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
        public string? Generation { get; init; }
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
