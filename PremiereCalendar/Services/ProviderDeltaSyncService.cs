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
    private readonly DatabaseRecoveryState? _databaseState;
    private DateTimeOffset? _nextContinuationUtc;

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
        PremiereTelemetry? telemetry = null,
        DatabaseRecoveryState? databaseState = null)
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
        _databaseState = databaseState;
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

                await QueueOrRunSupervisedAsync(stoppingToken);
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                var delay = TimeSpan.FromMinutes(Math.Max(15, _options.CurrentValue.WakeIntervalMinutes));
                if (_workScheduler is null && _nextContinuationUtc is { } continuation)
                {
                    var untilContinuation = continuation - _timeProvider.GetUtcNow();
                    if (untilContinuation < delay) delay = untilContinuation > TimeSpan.Zero ? untilContinuation : TimeSpan.Zero;
                }
                await Task.Delay(delay, stoppingToken);
                await QueueOrRunSupervisedAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task QueueOrRunSupervisedAsync(CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromSeconds(5);
        while (true)
        {
            try { await QueueOrRunOnceAsync(cancellationToken); return; }
            catch (Exception exception) when (DatabaseConnectionFactory.IsTransient(exception))
            {
                _logger.LogWarning(exception, "Provider delta scheduling will retry after temporary database unavailability.");
                await Task.Delay(delay, cancellationToken);
                delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
            }
        }
    }

    private async Task QueueOrRunOnceAsync(CancellationToken cancellationToken)
    {
        if (_databaseState is not null) await _databaseState.WaitUntilHealthyAsync(cancellationToken);
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
        if (result.Failures.Any(failure => failure.ContinueAtUtc is null))
            throw new ExternalApiException("One or more provider change feeds could not be synchronized.");
        if (result.Failures.Where(failure => failure.ContinueAtUtc is not null).MinBy(failure => failure.ContinueAtUtc)
            ?.ContinueAtUtc is { } continueAt)
            throw new ProviderWorkContinuationException(continueAt);
    }

    private async Task<ProviderDeltaSyncResult> RunOnceCoreAsync(CancellationToken cancellationToken)
    {
        var nowUtc = _timeProvider.GetUtcNow();
        var failures = new List<ProviderDeltaFailure>();
        if (_options.CurrentValue.UseTmdbChanges)
        {
            failures.AddRange(await SyncTmdbChangesAsync(nowUtc, cancellationToken));
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
            _nextContinuationUtc = result.Failures.Where(failure => failure.ContinueAtUtc is not null)
                .MinBy(failure => failure.ContinueAtUtc)?.ContinueAtUtc;
            if (result.Failures.Any(failure => failure.ContinueAtUtc is null))
            {
                await RecordTimelineAsync(
                    BackgroundJobStatus.Failed,
                    $"Provider delta sync failed: {string.Join("; ", result.Failures.Select(failure => failure.Reason))}",
                    _timeProvider.GetUtcNow(),
                    _timeProvider.GetElapsedTime(startedTimestamp),
                    cancellationToken);
                return;
            }
            if (result.Failures.Count > 0)
            {
                await RecordTimelineAsync(BackgroundJobStatus.Skipped,
                    "Provider delta sync saved progress; remaining history checks are deferred to a later bounded cycle.",
                    _timeProvider.GetUtcNow(), _timeProvider.GetElapsedTime(startedTimestamp), cancellationToken);
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
        catch (Exception ex) when (IsExpectedBackgroundFailure(ex))
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

    private async Task<IReadOnlyList<ProviderDeltaFailure>> SyncTmdbChangesAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var failures = new List<ProviderDeltaFailure>();
        foreach (var mediaType in new[] { PremiereMediaType.Movie, PremiereMediaType.Series })
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.CurrentValue.ProviderBudgetSeconds, 1, 900)));
            try
            {
                await SyncTmdbMediaChangesAsync(mediaType, DateOnly.FromDateTime(nowUtc.UtcDateTime), nowUtc, budget.Token, cancellationToken);
            }
            catch (ProviderWorkContinuationException continuation)
            {
                failures.Add(new("TMDb", "TMDb history checks saved progress for continuation.", continuation.RetryAtUtc));
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "TMDb {MediaType} change tracking exceeded its cycle budget or timed out.", mediaType);
                failures.Add(new("TMDb", "TMDb change tracking timed out or was canceled by an external dependency."));
            }
            catch (Exception ex) when (IsExpectedBackgroundFailure(ex))
            {
                _logger.LogWarning(ex, "TMDb {MediaType} change tracking sync failed.", mediaType);
                failures.Add(new("TMDb", $"TMDb change tracking failed: {SafeFailureMessage(ex)}"));
            }
        }
        return failures;
    }

    private static bool IsExpectedBackgroundFailure(Exception exception)
        => exception is HttpRequestException or ExternalApiException or ProviderRequestException
            or ProviderCircuitOpenException or ProviderRetryAfterException or IOException or TimeoutException
            || DatabaseConnectionFactory.IsTransient(exception);

    private async Task SyncTmdbMediaChangesAsync(
        PremiereMediaType mediaType,
        DateOnly end,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken,
        CancellationToken checkpointCancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var keyPrefix = mediaType == PremiereMediaType.Movie ? "movie" : "tv";
        var globalKey = $"{keyPrefix}-changes";
        var prior = await _stateStore.GetAsync("tmdb", ProviderCacheScope.Global, globalKey, cancellationToken);
        if (prior is not null && nowUtc >= prior.LastCheckedUtc
            && nowUtc - prior.LastCheckedUtc < TimeSpan.FromMinutes(Math.Clamp(_options.CurrentValue.TmdbIntervalMinutes, 1, 1_440))) return;
        var maximumDays = Math.Clamp(_options.CurrentValue.TmdbLookbackDays, 1, 14);
        var minimum = end.AddDays(-(maximumDays - 1));
        var start = prior is null
            ? end.AddDays(-(Math.Clamp(_options.CurrentValue.InitialLookbackDays, 1, maximumDays) - 1))
            : DateOnly.FromDateTime(prior.LastCheckedUtc.UtcDateTime);
        var gap = start < minimum;
        if (gap) start = minimum;
        if (start > end) start = end;
        var sessionKey = $"{keyPrefix}-history-session";
        var savedSession = await _stateStore.GetAsync("tmdb", ProviderCacheScope.Global, sessionKey, cancellationToken);
        var session = ReadHistorySession(savedSession?.MetadataJson, minimum, end, nowUtc);
        if (session is null || prior is not null && session.StartedUtc <= prior.LastCheckedUtc)
        {
            var changes = mediaType == PremiereMediaType.Movie
                ? await _tmdbClient.GetChangedMovieIdsAsync(start, end, cancellationToken)
                : await _tmdbClient.GetChangedTvIdsAsync(start, end, cancellationToken);
            var candidates = changes.Where(change => change.Id > 0).Select(change => change.Id).Distinct().ToArray();
            var knownIds = _itemEnrichmentStore is null ? new HashSet<int>()
                : await _itemEnrichmentStore.FindKnownTmdbIdsAsync(mediaType, candidates, cancellationToken);
            session = new(start, end, nowUtc, gap, candidates.Length, knownIds.Order().ToArray(), []);
            await SaveHistorySessionAsync(sessionKey, session, cancellationToken);
        }
        if (session.ContinueAtUtc > nowUtc) throw new ProviderWorkContinuationException(session.ContinueAtUtc.Value);
        start = session.Start;
        end = session.End;
        gap = session.CoverageGap;
        var watermark = end.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var histories = new System.Collections.Concurrent.ConcurrentDictionary<int, DateTimeOffset?>(session.Histories);
        var pending = session.KnownIds.Where(id => !histories.ContainsKey(id)).ToArray();
        var dirty = _itemEnrichmentStore is null ? new HashSet<int>()
            : await _itemEnrichmentStore.FindDirtyTmdbIdsAsync(mediaType, pending, cancellationToken);
        // Re-fence dirty records at this feed observation before skipping history;
        // a repair that started since the prior invalidation cannot erase this change.
        foreach (var id in dirty) histories[id] = nowUtc;
        var work = pending.Where(id => !dirty.Contains(id))
            .Take(Math.Clamp(_options.CurrentValue.HistoryMaximumItemsPerCycle, 1, 10_000));
        try
        {
            foreach (var batch in work.Chunk(Math.Clamp(_options.CurrentValue.HistoryBatchSize, 1, 500)))
            {
                try
                {
                    await Parallel.ForEachAsync(batch, new ParallelOptions
                    {
                        CancellationToken = cancellationToken,
                        MaxDegreeOfParallelism = Math.Clamp(_options.CurrentValue.ChangeHistoryConcurrency, 1, 8)
                    }, async (id, token) =>
                    {
                        histories[id] = await _tmdbClient.GetLastMetadataChangeUtcAsync(mediaType, id, start, end, token);
                    });
                }
                finally
                {
                    using var checkpointTimeout = CancellationTokenSource.CreateLinkedTokenSource(checkpointCancellationToken);
                    checkpointTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                    await SaveHistorySessionAsync(sessionKey, session with { Histories = new(histories) }, checkpointTimeout.Token);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested
            && !checkpointCancellationToken.IsCancellationRequested && histories.Count > session.Histories.Count)
        {
            var continueAt = ContinuationDeadline();
            using var checkpointTimeout = CancellationTokenSource.CreateLinkedTokenSource(checkpointCancellationToken);
            checkpointTimeout.CancelAfter(TimeSpan.FromSeconds(5));
            await SaveHistorySessionAsync(sessionKey, session with { Histories = new(histories), ContinueAtUtc = continueAt }, checkpointTimeout.Token);
            throw new ProviderWorkContinuationException(continueAt);
        }
        await SaveHistorySessionAsync(sessionKey, session with { Histories = new(histories) }, cancellationToken);
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
        var states = histories.Keys.Select(id => new ProviderCacheState("tmdb", ProviderCacheScope.Item,
            $"{keyPrefix}:{id}", nowUtc,
            histories.TryGetValue(id, out var actual) && actual != DateTimeOffset.MinValue ? actual : null,
            watermark, null, null)).ToList();
        // Advance the global watermark only after complete paging, history checks
        // and every matching generation invalidation have succeeded.
        await _stateStore.SaveManyAsync(states, cancellationToken);
        if (histories.Count < session.KnownIds.Length)
        {
            var continueAt = ContinuationDeadline();
            await SaveHistorySessionAsync(sessionKey, session with { Histories = new(histories), ContinueAtUtc = continueAt }, cancellationToken);
            throw new ProviderWorkContinuationException(continueAt);
        }
        await _stateStore.SaveAsync(new ProviderCacheState("tmdb", ProviderCacheScope.Global, globalKey,
            session.StartedUtc, null, watermark, session.ChangedCount,
            JsonSerializer.Serialize(new { start, end, coverageGap = gap, known = session.KnownIds.Length, invalidated })), cancellationToken);
        if (gap)
            _logger.LogWarning("TMDb {MediaType} change recovery exceeded {MaximumDays} days; bounded metadata refresh remains required.", mediaType, maximumDays);
        _logger.LogInformation("TMDb {MediaType} delta checked {ChangedIds} IDs, inspected {KnownIds} cached histories and invalidated {InvalidatedItems} records in {DurationMilliseconds} ms.",
            mediaType, session.ChangedCount, session.KnownIds.Length, invalidated, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        _telemetry?.RecordDiagnostic("itemcache.delta.completed", details: new()
        {
            Provider = "tmdb", Outcome = "success", Reason = gap ? "coverage_gap" : "targeted_changes",
            Count = invalidated, Total = session.ChangedCount, DurationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds
        });
    }

    private Task SaveHistorySessionAsync(string key, TmdbHistorySession session, CancellationToken cancellationToken)
        => _stateStore.SaveAsync(new ProviderCacheState("tmdb", ProviderCacheScope.Global, key,
            session.StartedUtc, null, null, session.Histories.Count, JsonSerializer.Serialize(session)), cancellationToken);

    private DateTimeOffset ContinuationDeadline() => _timeProvider.GetUtcNow().AddSeconds(
        Math.Clamp(_options.CurrentValue.ContinuationDelaySeconds, 15, 3_600));

    private TmdbHistorySession? ReadHistorySession(string? json, DateOnly minimum, DateOnly end, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var session = JsonSerializer.Deserialize<TmdbHistorySession>(json);
            var known = session?.KnownIds?.ToHashSet();
            if (session is not null && session.Version == 1 && session.Start >= minimum && session.Start <= session.End
                && session.End <= end && session.StartedUtc != default && session.StartedUtc <= now
                && session.KnownIds is not null && session.Histories is not null && session.ChangedCount >= session.KnownIds.Length
                && session.KnownIds.All(id => id > 0) && known!.Count == session.KnownIds.Length
                && session.Histories.Keys.All(known.Contains)
                && (session.ContinueAtUtc is null || session.ContinueAtUtc <= now.AddHours(1))) return session;
            _logger.LogWarning("Discarding incompatible or malformed TMDb history checkpoint; the feed window will be rebuilt.");
        }
        catch (JsonException exception)
        {
            _logger.LogWarning(exception, "Discarding unreadable TMDb history checkpoint; the feed window will be rebuilt.");
        }
        return null;
    }

    private sealed record TmdbHistorySession(DateOnly Start, DateOnly End, DateTimeOffset StartedUtc,
        bool CoverageGap, int ChangedCount, int[] KnownIds, Dictionary<int, DateTimeOffset?> Histories,
        DateTimeOffset? ContinueAtUtc = null)
    {
        public int Version { get; init; } = 1;
    }

    private async Task<ProviderDeltaFailure?> SyncTvmazeUpdatesAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(_options.CurrentValue.ProviderBudgetSeconds, 1, 900)));
        try
        {
            var started = Stopwatch.GetTimestamp();
            var prior = await _stateStore.GetAsync("tvmaze", ProviderCacheScope.Global, "show-updates", budget.Token);
            var age = prior is null ? TimeSpan.Zero : nowUtc - prior.LastCheckedUtc;
            if (prior is not null && age >= TimeSpan.Zero
                && age < TimeSpan.FromMinutes(Math.Clamp(_options.CurrentValue.TvmazeIntervalMinutes, 1, 1_440))) return null;
            var window = age <= TimeSpan.FromDays(1) ? TvmazeUpdateWindow.Day
                : age <= TimeSpan.FromDays(7) ? TvmazeUpdateWindow.Week : TvmazeUpdateWindow.Month;
            var updates = await _tvmazeClient.GetShowUpdatesCheckedAsync(window, budget.Token);
            var candidates = updates.Select(update => update.ShowId).Distinct().ToArray();
            IReadOnlySet<int> known = new HashSet<int>();
            var invalidated = 0;
            if (_itemEnrichmentStore is not null)
            {
                if (updates.Count > 0)
                    invalidated += await _itemEnrichmentStore.FenceUnmappedTvmazeReservationsAsync(
                        _timeProvider.GetUtcNow(), budget.Token);
                // Fence first: a first lookup either loses its old reservation or
                // publishes before this fresh known-ID read and is invalidated below.
                known = await _itemEnrichmentStore.FindKnownTvmazeIdsAsync(candidates, budget.Token);
                var changes = updates.Where(update => known.Contains(update.ShowId)).GroupBy(update => update.ShowId).ToDictionary(group => group.Key,
                    group => group.Max(update => update.UpdatedAtUtc) > nowUtc ? nowUtc
                        : group.Max(update => update.UpdatedAtUtc));
                invalidated += await _itemEnrichmentStore.InvalidateTvmazeManyAsync(changes, budget.Token);
            }
            var states = new List<ProviderCacheState>();

            foreach (var update in updates.Where(update => known.Contains(update.ShowId)).GroupBy(update => update.ShowId)
                         .Select(group => group.MaxBy(update => update.UpdatedAtUtc)!))
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

            await _stateStore.SaveManyAsync(states, budget.Token);
            await _stateStore.SaveAsync(new ProviderCacheState("tvmaze", ProviderCacheScope.Global, "show-updates",
                nowUtc, updates.Count > 0 ? updates.Max(update => update.UpdatedAtUtc) : null,
                window.ToString().ToLowerInvariant(), updates.Count,
                JsonSerializer.Serialize(new { coverageGap = age > TimeSpan.FromDays(30), invalidated })), budget.Token);
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
        catch (Exception ex) when (IsExpectedBackgroundFailure(ex))
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

    private sealed record ProviderDeltaFailure(string Provider, string Reason, DateTimeOffset? ContinueAtUtc = null);
}
