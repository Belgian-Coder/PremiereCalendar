using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Text.Json;
using PremiereCalendar.Models;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class ProviderDeltaSyncService : BackgroundService
{
    private readonly ITmdbClient _tmdbClient;
    private readonly ITvmazeClient _tvmazeClient;
    private readonly IProviderCacheStateStore _stateStore;
    private readonly IOptionsMonitor<ProviderDeltaSyncOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ProviderDeltaSyncService> _logger;
    private readonly BackgroundJobTimelineService? _timeline;
    private readonly IProviderWorkScheduler? _workScheduler;
    private readonly IItemEnrichmentStore? _itemEnrichmentStore;
    private readonly PremiereTelemetry? _telemetry;

    public ProviderDeltaSyncService(
        ITmdbClient tmdbClient,
        ITvmazeClient tvmazeClient,
        IProviderCacheStateStore stateStore,
        IOptionsMonitor<ProviderDeltaSyncOptions> options,
        TimeProvider timeProvider,
        ILogger<ProviderDeltaSyncService> logger,
        BackgroundJobTimelineService? timeline = null,
        IProviderWorkScheduler? workScheduler = null,
        IItemEnrichmentStore? itemEnrichmentStore = null,
        PremiereTelemetry? telemetry = null)
    {
        _tmdbClient = tmdbClient;
        _tvmazeClient = tvmazeClient;
        _stateStore = stateStore;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _timeline = timeline;
        _workScheduler = workScheduler;
        _itemEnrichmentStore = itemEnrichmentStore;
        _telemetry = telemetry;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.CurrentValue.Enabled)
        {
            return;
        }

        try
        {
            if (_options.CurrentValue.RunOnStartup)
            {
                var delay = TimeSpan.FromSeconds(Math.Max(0, _options.CurrentValue.StartupDelaySeconds));
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, stoppingToken);
                }

                await QueueOrRunOnceAsync(stoppingToken);
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(Math.Max(15, _options.CurrentValue.WakeIntervalMinutes)), stoppingToken);
                await QueueOrRunOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task QueueOrRunOnceAsync(CancellationToken cancellationToken)
    {
        if (_workScheduler is null)
        {
            await RunOnceSafelyAsync(cancellationToken);
            return;
        }

        await _workScheduler.EnqueueAsync(new ProviderWorkRequest(
            ProviderWorkKind.ProviderDeltaSync,
            "provider-delta-sync",
            ProviderWorkPriority.Maintenance,
            "{}"), cancellationToken);
    }

    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var result = await RunOnceCoreAsync(cancellationToken);
        if (result.Failures.Count > 0)
            throw new ExternalApiException("One or more provider change feeds could not be synchronized.");
    }

    private async Task<ProviderDeltaSyncResult> RunOnceCoreAsync(CancellationToken cancellationToken)
    {
        var nowUtc = _timeProvider.GetUtcNow();
        var failures = new List<ProviderDeltaFailure>();
        if (_options.CurrentValue.UseTmdbChanges)
        {
            if (await SyncTmdbChangesAsync(nowUtc, cancellationToken) is { } failure)
            {
                failures.Add(failure);
            }
        }

        if (_options.CurrentValue.UseTvmazeUpdates)
        {
            if (await SyncTvmazeUpdatesAsync(nowUtc, cancellationToken) is { } failure)
            {
                failures.Add(failure);
            }
        }

        return new ProviderDeltaSyncResult(failures);
    }

    internal async Task RunOnceSafelyAsync(CancellationToken cancellationToken)
    {
        var startedUtc = _timeProvider.GetUtcNow();
        var startedTimestamp = _timeProvider.GetTimestamp();
        try
        {
            await RecordTimelineAsync(
                BackgroundJobStatus.Started,
                "Started provider delta sync.",
                startedUtc,
                null,
                cancellationToken);
            var result = await RunOnceCoreAsync(cancellationToken);
            if (result.Failures.Count > 0)
            {
                await RecordTimelineAsync(
                    BackgroundJobStatus.Failed,
                    $"Provider delta sync failed: {string.Join("; ", result.Failures.Select(failure => failure.Reason))}",
                    _timeProvider.GetUtcNow(),
                    _timeProvider.GetElapsedTime(startedTimestamp),
                    cancellationToken);
                return;
            }

            await RecordTimelineAsync(
                BackgroundJobStatus.Succeeded,
                "Finished provider delta sync.",
                _timeProvider.GetUtcNow(),
                _timeProvider.GetElapsedTime(startedTimestamp),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Provider delta sync cycle failed.");
            await RecordTimelineAsync(
                BackgroundJobStatus.Failed,
                ex.Message,
                _timeProvider.GetUtcNow(),
                _timeProvider.GetElapsedTime(startedTimestamp),
                CancellationToken.None);
        }
    }

    private async Task RecordTimelineAsync(
        BackgroundJobStatus status,
        string message,
        DateTimeOffset occurredUtc,
        TimeSpan? duration,
        CancellationToken cancellationToken)
    {
        if (_timeline is null)
        {
            return;
        }

        try
        {
            await _timeline.RecordAsync("Provider delta sync", status, message, occurredUtc, duration, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not record provider delta sync timeline event.");
        }
    }

    private async Task<ProviderDeltaFailure?> SyncTmdbChangesAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        try
        {
            var end = DateOnly.FromDateTime(nowUtc.UtcDateTime);
            await SyncTmdbMediaChangesAsync(PremiereMediaType.Movie, end, nowUtc, cancellationToken);
            await SyncTmdbMediaChangesAsync(PremiereMediaType.Series, end, nowUtc, cancellationToken);
            return null;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "TMDb change tracking sync timed out or was canceled by an external dependency.");
            return new ProviderDeltaFailure("TMDb", "TMDb change tracking timed out or was canceled by an external dependency.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TMDb change tracking sync failed.");
            return new ProviderDeltaFailure("TMDb", $"TMDb change tracking failed: {SafeFailureMessage(ex)}");
        }
    }

    private async Task SyncTmdbMediaChangesAsync(
        PremiereMediaType mediaType,
        DateOnly end,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var keyPrefix = mediaType == PremiereMediaType.Movie ? "movie" : "tv";
        var globalKey = $"{keyPrefix}-changes";
        var prior = await _stateStore.GetAsync("tmdb", ProviderCacheScope.Global, globalKey, cancellationToken);
        var maximumDays = Math.Clamp(_options.CurrentValue.TmdbLookbackDays, 1, 14);
        var minimum = end.AddDays(-(maximumDays - 1));
        var start = prior is null
            ? end.AddDays(-(Math.Clamp(_options.CurrentValue.InitialLookbackDays, 1, maximumDays) - 1))
            : DateOnly.FromDateTime(prior.LastCheckedUtc.UtcDateTime);
        var gap = start < minimum;
        if (gap) start = minimum;
        if (start > end) start = end;
        var changes = mediaType == PremiereMediaType.Movie
            ? await _tmdbClient.GetChangedMovieIdsAsync(start, end, cancellationToken)
            : await _tmdbClient.GetChangedTvIdsAsync(start, end, cancellationToken);
        var watermark = end.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var ids = changes.Where(change => change.Id > 0).Select(change => change.Id).Distinct().ToArray();
        var known = _itemEnrichmentStore is null ? new HashSet<int>()
            : await _itemEnrichmentStore.FindKnownTmdbIdsAsync(mediaType, ids, cancellationToken);
        var histories = new System.Collections.Concurrent.ConcurrentDictionary<int, DateTimeOffset?>();
        await Parallel.ForEachAsync(known, new ParallelOptions
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = Math.Clamp(_options.CurrentValue.ChangeHistoryConcurrency, 1, 8)
        }, async (id, token) =>
        {
            var timestamp = await _tmdbClient.GetLastMetadataChangeUtcAsync(mediaType, id, start, end, token);
            histories[id] = timestamp;
        });
        var invalidations = new Dictionary<int, DateTimeOffset>();
        foreach (var (id, timestamp) in histories)
        {
            if (timestamp == DateTimeOffset.MinValue) continue;
            // Unknown/deleted history is conservative; precise histories make
            // overlapping change-list windows idempotent for unchanged items.
            var changedAt = timestamp is null || timestamp > nowUtc ? nowUtc : timestamp.Value;
            invalidations[id] = changedAt;
        }
        var invalidated = _itemEnrichmentStore is null ? 0
            : await _itemEnrichmentStore.InvalidateTmdbManyAsync(mediaType, invalidations, cancellationToken);
        // Unrelated worldwide IDs need no per-item marker writes. The global
        // coverage record retains the full feed count and successful watermark.
        var states = known.Select(id => new ProviderCacheState("tmdb", ProviderCacheScope.Item,
            $"{keyPrefix}:{id}", nowUtc,
            histories.TryGetValue(id, out var actual) && actual != DateTimeOffset.MinValue ? actual : null,
            watermark, null, null)).ToList();
        // Advance the global watermark only after complete paging, history checks
        // and every matching generation invalidation have succeeded.
        await _stateStore.SaveManyAsync(states, cancellationToken);
        await _stateStore.SaveAsync(new ProviderCacheState("tmdb", ProviderCacheScope.Global, globalKey,
            nowUtc, null, watermark, changes.Count,
            JsonSerializer.Serialize(new { start, end, coverageGap = gap, known = known.Count, invalidated })), cancellationToken);
        if (gap)
            _logger.LogWarning("TMDb {MediaType} change recovery exceeded {MaximumDays} days; bounded metadata refresh remains required.", mediaType, maximumDays);
        _logger.LogInformation("TMDb {MediaType} delta checked {ChangedIds} IDs, inspected {KnownIds} cached histories and invalidated {InvalidatedItems} records in {DurationMilliseconds} ms.",
            mediaType, ids.Length, known.Count, invalidated, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        _telemetry?.RecordDiagnostic("itemcache.delta.completed", details: new()
        {
            Provider = "tmdb", Outcome = "success", Reason = gap ? "coverage_gap" : "targeted_changes",
            Count = invalidated, Total = ids.Length, DurationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds
        });
    }

    private async Task<ProviderDeltaFailure?> SyncTvmazeUpdatesAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        try
        {
            var started = Stopwatch.GetTimestamp();
            var prior = await _stateStore.GetAsync("tvmaze", ProviderCacheScope.Global, "show-updates", cancellationToken);
            var age = prior is null ? TimeSpan.Zero : nowUtc - prior.LastCheckedUtc;
            var window = age <= TimeSpan.FromDays(1) ? TvmazeUpdateWindow.Day
                : age <= TimeSpan.FromDays(7) ? TvmazeUpdateWindow.Week : TvmazeUpdateWindow.Month;
            var updates = await _tvmazeClient.GetShowUpdatesCheckedAsync(window, cancellationToken);
            var invalidated = 0;
            if (_itemEnrichmentStore is not null)
            {
                if (updates.Count > 0)
                    invalidated += await _itemEnrichmentStore.FenceUnmappedTvmazeReservationsAsync(
                        _timeProvider.GetUtcNow(), cancellationToken);
                var changes = updates.GroupBy(update => update.ShowId).ToDictionary(group => group.Key,
                    group => group.Max(update => update.UpdatedAtUtc) > nowUtc ? nowUtc
                        : group.Max(update => update.UpdatedAtUtc));
                invalidated += await _itemEnrichmentStore.InvalidateTvmazeManyAsync(changes, cancellationToken);
            }
            var states = new List<ProviderCacheState>();

            foreach (var update in updates)
            {
                states.Add(new ProviderCacheState(
                        "tvmaze",
                        ProviderCacheScope.Item,
                        $"show:{update.ShowId}",
                        nowUtc,
                        update.UpdatedAtUtc,
                        window.ToString().ToLowerInvariant(),
                        null,
                        null));
            }

            await _stateStore.SaveManyAsync(states, cancellationToken);
            await _stateStore.SaveAsync(new ProviderCacheState("tvmaze", ProviderCacheScope.Global, "show-updates",
                nowUtc, updates.Count > 0 ? updates.Max(update => update.UpdatedAtUtc) : null,
                window.ToString().ToLowerInvariant(), updates.Count,
                JsonSerializer.Serialize(new { coverageGap = age > TimeSpan.FromDays(30), invalidated })), cancellationToken);
            _telemetry?.RecordDiagnostic("itemcache.delta.completed", details: new()
            {
                Provider = "tvmaze", Outcome = "success", Reason = age > TimeSpan.FromDays(30) ? "coverage_gap" : "targeted_changes",
                Count = invalidated, Total = updates.Count, DurationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds
            });
            return null;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "TVmaze update tracking sync timed out or was canceled by an external dependency.");
            return new ProviderDeltaFailure("TVmaze", "TVmaze update tracking timed out or was canceled by an external dependency.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TVmaze update tracking sync failed.");
            return new ProviderDeltaFailure("TVmaze", $"TVmaze update tracking failed: {SafeFailureMessage(ex)}");
        }
    }

    private static string SafeFailureMessage(Exception exception)
    {
        return string.IsNullOrWhiteSpace(exception.Message)
            ? exception.GetType().Name
            : exception.Message;
    }

    private sealed record ProviderDeltaSyncResult(IReadOnlyList<ProviderDeltaFailure> Failures);

    private sealed record ProviderDeltaFailure(string Provider, string Reason);
}
