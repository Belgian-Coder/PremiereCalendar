using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public sealed partial class PremiereService
{
    private Task<PremiereItem?> MapMovieAsync(
        TmdbMovieDiscoverItem item, CancellationToken cancellationToken, bool forceRefresh,
        IReadOnlyDictionary<string, PremiereItem>? cachedEnrichment = null, bool allowWatchmodeAvailabilityFallback = true,
        MediaDiscoveryCriteria? requestedMovieCriteria = null)
        => TraceItemEnrichmentAsync("movie", item.Id, () => MapMovieCoreAsync(
            item, cancellationToken, forceRefresh, cachedEnrichment, allowWatchmodeAvailabilityFallback,
            requestedMovieCriteria));

    private Task<PremiereItem?> MapSeriesAsync(
        TmdbTvDiscoverItem item, CancellationToken cancellationToken, bool forceRefresh,
        IReadOnlyDictionary<string, PremiereItem>? cachedEnrichment = null,
        DateOnly? premiereDateOverride = null, PremiereItemType? itemTypeOverride = null,
        string? episodeTitle = null, int? seasonNumber = null, int? episodeNumber = null,
        string? episodeSource = null, bool allowWatchmodeAvailabilityFallback = true,
        DateOnly? requestedStart = null, DateOnly? requestedEnd = null, bool canonicalizeSeriesPremiereDate = false)
        => TraceItemEnrichmentAsync("series", item.Id, () => MapSeriesCoreAsync(item, cancellationToken,
            forceRefresh, cachedEnrichment, premiereDateOverride, itemTypeOverride, episodeTitle, seasonNumber,
            episodeNumber, episodeSource, allowWatchmodeAvailabilityFallback, requestedStart, requestedEnd, canonicalizeSeriesPremiereDate));

    private async Task<PremiereItem?> TraceItemEnrichmentAsync(string media, int id, Func<Task<PremiereItem?>> operation)
    {
        var context = (_telemetry.CaptureDiagnosticContext() ?? _telemetry.CreateDiagnosticContext())
            with { OperationId = Guid.NewGuid().ToString("N") };
        using var scope = _telemetry.UseDiagnosticContext(context);
        _telemetry.RecordDiagnostic("enrichment.item.started", details: new() { Stage = media, MediaType = media, ItemId = id });
        return await TraceEnrichmentStageAsync(media + ".item", operation);
    }

    public IAsyncEnumerable<PremiereLoadProgress> StreamPremieresAsync(
        DateOnly start, DateOnly end, bool forceRefresh = false, CalendarFilters? filters = null,
        CancellationToken cancellationToken = default)
        => TraceCalendarStream(start, end, forceRefresh, filters, null, false, cancellationToken);

    public IAsyncEnumerable<PremiereLoadProgress> StreamCoreAsync(
        DateOnly start, DateOnly end, bool forceRefresh = false, CalendarFilters? filters = null,
        ProviderWorkResumeState? resumeState = null, CancellationToken cancellationToken = default)
        => WithRefreshScope(TraceCalendarStream(start, end, forceRefresh, filters, resumeState, true, cancellationToken),
            cancellationToken);

    private async IAsyncEnumerable<T> WithRefreshScope<T>(
        IAsyncEnumerable<T> source, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var scope = ProviderRefreshScope.Create();
        var itemSession = _itemEnrichmentCache is null ? null : await _itemEnrichmentCache.CreateSessionAsync(cancellationToken);
        IAsyncEnumerator<T> enumerator;
        using (ProviderRefreshScope.Use(scope))
        using (UseItemCacheSession(itemSession)) enumerator = source.GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                bool moved;
                T current = default!;
                // Async iterator callers restore their execution context after every yield.
                // Re-enter the same refresh generation for every continuation and cleanup.
                using (ProviderRefreshScope.Use(scope))
                using (UseItemCacheSession(itemSession))
                {
                    moved = await enumerator.MoveNextAsync();
                    if (moved) current = enumerator.Current;
                }

                if (!moved) yield break;
                yield return current;
            }
        }
        finally
        {
            using (ProviderRefreshScope.Use(scope))
            using (UseItemCacheSession(itemSession)) await enumerator.DisposeAsync();
            if (itemSession is not null && !cancellationToken.IsCancellationRequested)
                await itemSession.FlushAsync(cancellationToken);
        }
    }

    private IAsyncEnumerable<PremiereLoadProgress> TraceCalendarStream(
        DateOnly start, DateOnly end, bool forceRefresh, CalendarFilters? filters,
        ProviderWorkResumeState? resumeState, bool core, CancellationToken cancellationToken)
    {
        var context = _telemetry.CaptureDiagnosticContext() ?? _telemetry.CreateDiagnosticContext();
        return _telemetry.WithDiagnosticContext(ObserveCalendarStreamAsync(), context, cancellationToken);

        async IAsyncEnumerable<PremiereLoadProgress> ObserveCalendarStreamAsync(
            [EnumeratorCancellation] CancellationToken token = default)
        {
            var timer = Stopwatch.GetTimestamp();
            var prefix = core ? "calendar.pipeline" : "calendar.load";
            var criteria = PremiereDiscoveryCriteria.FromFilters(filters);
            var outcome = "abandoned";
            string? reason = null;
            var first = true;
            var count = 0;
            _telemetry.RecordDiagnostic(prefix + ".started", details: new()
            {
                WeekStart = start, WeekEnd = end, CacheKey = criteria.CacheKey(),
                Reason = forceRefresh ? "forced" : "normal"
            });
            await using var enumerator = (core
                ? StreamCoreTrackedAsync(start, end, forceRefresh, filters, resumeState, token)
                : StreamPremieresTrackedAsync(start, end, forceRefresh, filters, token)).GetAsyncEnumerator(token);
            try
            {
                while (true)
                {
                    bool moved;
                    try { moved = await enumerator.MoveNextAsync(); }
                    catch (Exception ex)
                    {
                        outcome = token.IsCancellationRequested ? "canceled" : "failed";
                        reason = ex.GetType().Name;
                        throw;
                    }
                    if (!moved) break;
                    var update = enumerator.Current;
                    count = update.Items.Count;
                    _telemetry.RecordDiagnostic(prefix + (first ? ".first_result" : ".progress"), details: new()
                    {
                        Count = count, IsFinal = update.IsFinal, FromCache = update.FromCache,
                        DurationMilliseconds = Stopwatch.GetElapsedTime(timer).TotalMilliseconds,
                        Outcome = update.HasSourceErrors ? "partial" : "success"
                    });
                    first = false;
                    if (update.IsFinal)
                    {
                        outcome = update.HasSourceErrors ? "partial" : "complete";
                        if (update.HasSourceErrors) reason = update.FailedSourceNames.Contains("Item cache validation", StringComparer.OrdinalIgnoreCase)
                            ? "item_cache_validation" : "source_failure";
                    }
                    yield return update;
                }
                if (outcome == "abandoned") outcome = "ended_without_final";
            }
            finally
            {
                _telemetry.RecordDiagnostic(prefix + ".finished", details: new()
                {
                    Outcome = token.IsCancellationRequested ? "canceled" : outcome, Reason = reason,
                    Count = count, DurationMilliseconds = Stopwatch.GetElapsedTime(timer).TotalMilliseconds
                });
            }
        }
    }

    private async Task<CalendarCacheSnapshot> ReadCalendarCacheAsync(
        DateOnly start, DateOnly end, CalendarFilters? filters, CancellationToken cancellationToken)
    {
        var timer = Stopwatch.GetTimestamp();
        var snapshot = await ReadCalendarCacheCoreAsync(start, end, filters, cancellationToken);
        if (_itemEnrichmentCache is not null && snapshot.Items is { Count: > 0 } legacyItems)
        {
            var session = _itemCacheSession.Value ?? await _itemEnrichmentCache.CreateSessionAsync(cancellationToken);
            await session.PrepareAsync(legacyItems.Select(item => new ItemEnrichmentIdentity(item.MediaType, item.TmdbId)), legacyItems, cancellationToken);
            await session.FlushAsync(cancellationToken, rejectGenerationConflicts: false);
            var reconciled = await session.ReconcileAsync(legacyItems, includeConsumed: false, cancellationToken);
            var freshIds = snapshot.FreshItems.Select(item => item.CanonicalId).ToHashSet(StringComparer.Ordinal);
            snapshot = snapshot with
            {
                Items = reconciled.Items,
                FreshItems = reconciled.Items.Where(item => freshIds.Contains(item.CanonicalId) && !reconciled.DirtyMedia.Contains(item.MediaType)).ToArray(),
                IsComplete = snapshot.IsComplete && reconciled.DirtyMedia.Count == 0,
                HasFreshSeries = snapshot.HasFreshSeries && !reconciled.DirtyMedia.Contains(PremiereMediaType.Series),
                HasFreshMovies = snapshot.HasFreshMovies && !reconciled.DirtyMedia.Contains(PremiereMediaType.Movie),
                HasPartialCache = snapshot.HasPartialCache || reconciled.DirtyMedia.Count > 0,
                CanReuseDegraded = snapshot.CanReuseDegraded && reconciled.DirtyMedia.Count == 0
            };
            session.ForceDiscoveryRefresh |= reconciled.DirtyMedia.Count > 0;
        }
        _telemetry.RecordDiagnostic("calendar.cache.read", details: new()
        {
            WeekStart = start, WeekEnd = end, CacheKey = PremiereDiscoveryCriteria.FromFilters(filters).CacheKey(),
            Outcome = snapshot.Items is null ? "miss" : snapshot.IsComplete ? "complete" : "provisional",
            Cache = snapshot.HasPartialCache ? "partial" : snapshot.IsComplete ? "fresh" : "stale",
            Count = snapshot.Items?.Count ?? 0, DurationMilliseconds = Stopwatch.GetElapsedTime(timer).TotalMilliseconds
        });
        return snapshot;
    }

    private async IAsyncEnumerable<PremiereSourceBatch> TraceSourceBatchesAsync(
        PremiereSourceFactory source, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.Key)))[..20].ToLowerInvariant();
        var context = (_telemetry.CaptureDiagnosticContext() ?? _telemetry.CreateDiagnosticContext()) with { SourceKey = key };
        var provider = ProviderKeyForSource(source.Name);
        var timer = Stopwatch.GetTimestamp();
        var outcome = "abandoned";
        string? reason = null;
        var count = 0;
        _telemetry.RecordDiagnostic("source.started", context, new()
        {
            Provider = provider, WeekStart = source.Start, WeekEnd = source.End
        });
        await using var enumerator = _telemetry.WithDiagnosticContext(source.Open(), context, cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        try
        {
            while (true)
            {
                bool moved;
                try { moved = await enumerator.MoveNextAsync(); }
                catch (Exception ex) { outcome = "failed"; reason = ex.GetType().Name; throw; }
                if (!moved) break;
                var batch = enumerator.Current;
                count = batch.Items.Count;
                var unavailable = batch.Error is null ? null : OptionalAccessDenial(batch.Name, batch.Error);
                outcome = unavailable is not null ? "unavailable" : batch.Error is null ? "success" : "failed";
                reason = unavailable?.Reason ?? batch.Error?.GetType().Name;
                _telemetry.RecordDiagnostic("source.progress", context, new()
                {
                    Provider = provider, Count = count, Outcome = outcome, Reason = reason, IsFinal = batch.IsComplete,
                    StatusCode = unavailable?.StatusCode, RetryAtUtc = unavailable?.RetryAtUtc,
                    DurationMilliseconds = Stopwatch.GetElapsedTime(timer).TotalMilliseconds
                });
                yield return batch;
            }
        }
        finally
        {
            _telemetry.RecordDiagnostic("source.finished", context, new()
            {
                Provider = provider, Count = count, Outcome = cancellationToken.IsCancellationRequested ? "canceled" : outcome,
                Reason = reason, DurationMilliseconds = Stopwatch.GetElapsedTime(timer).TotalMilliseconds
            });
        }
    }

    private async Task SavePartialCalendarCacheAsync(
        DateOnly start, DateOnly end, CalendarFilters? filters, PremiereDiscoveryCriteria criteria,
        IReadOnlyList<PremiereItem> items, ProviderWorkResumeState? resumeState, CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string?>? baselines = null, DateTimeOffset? refreshStartedUtc = null)
    {
        var timer = Stopwatch.GetTimestamp();
        var exact = baselines is not null && refreshStartedUtc.HasValue;
        var saved = true;
        try
        {
            await PublishCalendarCacheAsync(resumeState, async token =>
            {
                if (!ShouldUseSharedMediaCache(criteria))
                {
                    if (exact)
                        saved = await _cacheOrchestrator.WriteValidatedPartialAsync(start, end, criteria.CacheKey(), items,
                            refreshStartedUtc!.Value, token, baselines!.GetValueOrDefault(criteria.CacheKey()));
                    else await _cacheOrchestrator.WritePartialAsync(start, end, criteria.CacheKey(), items, token);
                    return;
                }
                foreach (var mode in new[] { CalendarPageMode.Series, CalendarPageMode.Movies })
                {
                    var mediaFilters = FiltersForPageMode(filters, mode);
                    var media = mode == CalendarPageMode.Series ? PremiereMediaType.Series : PremiereMediaType.Movie;
                    var mediaItems = ApplyRequestedFilters(MergePremiereItems(items.Where(item => item.MediaType == media)), mediaFilters);
                    var key = PremiereDiscoveryCriteria.FromFilters(mediaFilters).CacheKey();
                    if (exact)
                        saved &= await _cacheOrchestrator.WriteValidatedPartialAsync(start, end, key, mediaItems,
                            refreshStartedUtc!.Value, token, baselines!.GetValueOrDefault(key));
                    else if (mediaItems.Count > 0)
                        await _cacheOrchestrator.WritePartialAsync(start, end,
                            key, mediaItems, token);
                }
            }, cancellationToken);
            _telemetry.RecordDiagnostic(exact ? "calendar.cache.validated_partial" : "calendar.cache.partial_publication", details: new()
            {
                Outcome = exact ? saved ? "success" : "rejected" : "attempted", Count = items.Count,
                DurationMilliseconds = Stopwatch.GetElapsedTime(timer).TotalMilliseconds,
                CacheKey = criteria.CacheKey(), WeekStart = start, WeekEnd = end
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not retain partial calendar cache for {StartDate} through {EndDate}.", start, end);
            _telemetry.RecordDiagnostic(exact ? "calendar.cache.validated_partial" : "calendar.cache.partial_publication", details: new()
            {
                Outcome = "failed", Reason = ex.GetType().Name, DurationMilliseconds = Stopwatch.GetElapsedTime(timer).TotalMilliseconds
            });
        }
    }

    private async Task<T> TraceEnrichmentStageAsync<T>(string stage, Func<Task<T>> operation)
    {
        var timer = Stopwatch.GetTimestamp();
        var outcome = "failed";
        string? reason = null;
        _telemetry.RecordDiagnostic("enrichment.stage.started", details: new() { Stage = stage });
        try { var result = await operation(); outcome = "success"; return result; }
        catch (Exception ex)
        {
            outcome = ex is OperationCanceledException ? "cancelled" : "failed";
            reason = ex.GetType().Name;
            throw;
        }
        finally
        {
            _telemetry.RecordDiagnostic("enrichment.stage.finished", details: new()
            {
                Stage = stage, Outcome = outcome, Reason = reason, DurationMilliseconds = Stopwatch.GetElapsedTime(timer).TotalMilliseconds
            });
        }
    }
}
