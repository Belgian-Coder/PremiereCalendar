using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed partial class PremiereService : IPremiereService, IPremiereLoadPipeline
{
    // Compatibility path for callers that do not supply the shared coordinator.
    // Whole-row fallback must not outlive the default ratings refresh interval.
    private static readonly TimeSpan CachedEnrichmentMaxAge = TimeSpan.FromHours(24);
    private const int ConflictingExternalIdsTmdbId = -1;

    private readonly ITmdbClient _tmdbClient;
    private readonly IOmdbClient _omdbClient;
    private readonly ITvmazeClient _tvmazeClient;
    private readonly IWatchmodeClient _watchmodeClient;
    private readonly CalendarLoadCacheOrchestrator _cacheOrchestrator;
    private readonly TrailerSelector _trailerSelector;
    private readonly RatingMapper _ratingMapper;
    private readonly IReadOnlyList<IArtworkProvider> _artworkProviders;
    private readonly IReadOnlyList<IPremiereDiscoveryProvider> _discoveryProviders;
    private readonly TmdbOptions _options;
    private readonly ILogger<PremiereService> _logger;
    private readonly IImdbRatingsStore? _imdbRatingsStore;
    private readonly IProviderCacheStateStore? _providerCacheStateStore;
    private readonly IRottenTomatoesClient? _rottenTomatoesClient;
    private readonly WeekDiagnosticsService? _weekDiagnosticsService;
    private readonly ScoreBackfillService? _scoreBackfillService;
    private readonly MissingExternalIdRepairService? _missingExternalIdRepairService;
    private readonly IProviderWorkScheduler? _workScheduler;
    private readonly PremiereTelemetry _telemetry;
    private readonly ItemEnrichmentCacheCoordinator? _itemEnrichmentCache;
    private readonly AsyncLocal<ItemEnrichmentCacheCoordinator.ItemEnrichmentSession?> _itemCacheSession = new();
    private readonly AsyncLocal<ItemEnrichmentAttempt?> _itemEnrichmentAttempt = new();

    public PremiereService(
        ITmdbClient tmdbClient,
        IOmdbClient omdbClient,
        ITvmazeClient tvmazeClient,
        IWatchmodeClient watchmodeClient,
        ICalendarCache calendarCache,
        TrailerSelector trailerSelector,
        RatingMapper ratingMapper,
        IEnumerable<IArtworkProvider> artworkProviders,
        IEnumerable<IPremiereDiscoveryProvider> discoveryProviders,
        IOptions<TmdbOptions> options,
        ILogger<PremiereService> logger,
        IImdbRatingsStore? imdbRatingsStore = null,
        IProviderCacheStateStore? providerCacheStateStore = null,
        IRottenTomatoesClient? rottenTomatoesClient = null,
        WeekDiagnosticsService? weekDiagnosticsService = null,
        ScoreBackfillService? scoreBackfillService = null,
        MissingExternalIdRepairService? missingExternalIdRepairService = null,
        IProviderWorkScheduler? workScheduler = null,
        PremiereTelemetry? telemetry = null,
        CalendarLoadCacheOrchestrator? cacheOrchestrator = null,
        ItemEnrichmentCacheCoordinator? itemEnrichmentCache = null)
    {
        _tmdbClient = tmdbClient;
        _omdbClient = omdbClient;
        _tvmazeClient = tvmazeClient;
        _watchmodeClient = watchmodeClient;
        _cacheOrchestrator = cacheOrchestrator ?? new CalendarLoadCacheOrchestrator(calendarCache);
        _trailerSelector = trailerSelector;
        _ratingMapper = ratingMapper;
        _artworkProviders = artworkProviders.ToArray();
        _discoveryProviders = discoveryProviders.ToArray();
        _options = options.Value;
        _logger = logger;
        _imdbRatingsStore = imdbRatingsStore;
        _providerCacheStateStore = providerCacheStateStore;
        _rottenTomatoesClient = rottenTomatoesClient;
        _weekDiagnosticsService = weekDiagnosticsService;
        _scoreBackfillService = scoreBackfillService;
        _missingExternalIdRepairService = missingExternalIdRepairService;
        _workScheduler = workScheduler;
        _telemetry = telemetry ?? new PremiereTelemetry();
        _itemEnrichmentCache = itemEnrichmentCache is { Enabled: true } ? itemEnrichmentCache : null;
    }

    public async Task<IReadOnlyList<PremiereItem>> GetPremieresAsync(
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken,
        bool forceRefresh = false,
        IProgress<PremiereLoadProgress>? progress = null,
        CalendarFilters? filters = null)
    {
        IReadOnlyList<PremiereItem> items = [];

        await foreach (var update in StreamPremieresAsync(start, end, forceRefresh, filters, cancellationToken)
            .WithCancellation(cancellationToken))
        {
            progress?.Report(update);
            items = update.Items;
        }

        return items;
    }

    private async IAsyncEnumerable<PremiereLoadProgress> StreamPremieresTrackedAsync(
        DateOnly start,
        DateOnly end,
        bool forceRefresh = false,
        CalendarFilters? filters = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default, bool refreshSourceLists = false)
    {
        using var activity = _telemetry.StartActivity("calendar.load");
        var started = Stopwatch.GetTimestamp();
        var firstResultRecorded = false;
        if (end < start)
        {
            throw new ArgumentException("The premiere end date must be on or after the start date.", nameof(end));
        }
        if (_workScheduler is null)
        {
            await foreach (var progress in StreamCoreAsync(start, end, forceRefresh, filters, refreshSourceLists
                               ? new ProviderWorkResumeState(new Dictionary<string, IReadOnlyList<PremiereItem>>(), null)
                                   { RefreshCalendarSnapshot = true, RefreshSourceLists = true } : null, cancellationToken)
                               .WithCancellation(cancellationToken))
            {
                if (!firstResultRecorded)
                {
                    _telemetry.RecordCalendarFirstResult(Stopwatch.GetElapsedTime(started), progress.FromCache);
                    firstResultRecorded = true;
                }
                if (progress.IsFinal) _telemetry.RecordCalendarCompletion(Stopwatch.GetElapsedTime(started), progress.Items.Count, progress.FromCache);
                yield return progress;
                if (progress.IsFinal && progress.RetryAfterUtc > DateTimeOffset.UtcNow) yield break;
            }
            yield break;
        }

        if (!forceRefresh && !refreshSourceLists)
        {
            var snapshot = await ReadCalendarCacheAsync(start, end, filters, cancellationToken);
            if (snapshot.Items is { } cached)
            {
                var hydrated = await TraceEnrichmentStageAsync("cache.local_hydration",
                    () => HydrateCachedImdbRatingsAsync(cached, filters, cancellationToken, allowRemote: false));
                var items = ApplyRequestedFilters(hydrated, filters);
                if (snapshot.CanReuseDegraded)
                {
                    _telemetry.RecordDiagnostic("calendar.cache.decision", details: new()
                    {
                        Outcome = "direct_degraded", Count = items.Count, FromCache = true, IsFinal = true,
                        Reason = "optional_source_access_denied", RetryAtUtc = snapshot.UnavailableSources.Min(source => source.RetryAtUtc),
                        DurationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds
                    });
                    yield return WithDegradedCoverage(CreateProgress("Week cache", items, items, isFinal: true, fromCache: true), snapshot.UnavailableSources, true);
                    yield break;
                }
                var isFinal = snapshot.IsComplete && !await NeedsRemoteCachedHydrationAsync(hydrated, filters, cancellationToken);
                _telemetry.RecordDiagnostic("calendar.cache.decision", details: new()
                {
                    Outcome = isFinal ? "direct_complete" : "provisional", Count = items.Count,
                    FromCache = true, IsFinal = isFinal
                });
                var progress = CreateProgress(snapshot.IsComplete ? "Week cache" : "Expired week cache",
                    items, items, isFinal: isFinal, fromCache: true, isStaleCache: !snapshot.IsComplete);
                if (isFinal || items.Count > 0)
                {
                    _telemetry.RecordCalendarFirstResult(Stopwatch.GetElapsedTime(started), true);
                    firstResultRecorded = true;
                    if (isFinal) _telemetry.RecordCalendarCompletion(Stopwatch.GetElapsedTime(started), items.Count, true);
                    yield return progress;
                }
                if (isFinal) yield break;
            }
        }

        var criteria = PremiereDiscoveryCriteria.FromFilters(filters);
        var payload = new CalendarProviderWorkPayload(start, end, forceRefresh, filters is null ? null : CalendarFilterState.Clone(filters)) { RefreshSourceLists = refreshSourceLists };
        var request = new ProviderWorkRequest(
            ProviderWorkKind.CalendarForeground,
            $"foreground:{start:yyyyMMdd}:{end:yyyyMMdd}:{(refreshSourceLists ? "sources" : forceRefresh.ToString())}:{criteria.CacheKey()}",
            ProviderWorkPriority.Foreground,
            JsonSerializer.Serialize(payload));
        var handle = await _workScheduler.EnqueueAsync(request, cancellationToken);
        _telemetry.RecordDiagnostic("calendar.job.attached",
            (_telemetry.CaptureDiagnosticContext() ?? _telemetry.CreateDiagnosticContext()) with { JobId = handle.JobId },
            new() { Outcome = handle.Created ? "created" : "attached", JobKind = request.Kind.ToString() });
        await foreach (var update in _workScheduler.WatchAsync(handle, cancellationToken)
                           .WithCancellation(cancellationToken))
        {
            if (update.CalendarProgress is { } progress)
            {
                if (!firstResultRecorded)
                {
                    _telemetry.RecordCalendarFirstResult(Stopwatch.GetElapsedTime(started), progress.FromCache);
                    firstResultRecorded = true;
                }
                if (progress.IsFinal) _telemetry.RecordCalendarCompletion(Stopwatch.GetElapsedTime(started), progress.Items.Count, progress.FromCache);
                yield return progress;
                if (progress.IsFinal && progress.RetryAfterUtc > DateTimeOffset.UtcNow) yield break;
            }
            else if (update.State == ProviderWorkState.Failed)
            {
                throw new ExternalApiException(update.Error ?? "Provider work failed.");
            }
        }
    }

    private async IAsyncEnumerable<PremiereLoadProgress> StreamCoreTrackedAsync(
        DateOnly start,
        DateOnly end,
        bool forceRefresh = false,
        CalendarFilters? filters = null,
        ProviderWorkResumeState? resumeState = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (end < start)
        {
            throw new ArgumentException("The premiere end date must be on or after the start date.", nameof(end));
        }

        var criteria = PremiereDiscoveryCriteria.FromFilters(filters);
        var cacheKey = criteria.CacheKey();
        var refreshStartedUtc = DateTimeOffset.UtcNow;
        var refreshStarted = Stopwatch.GetTimestamp();
        var fetchCriteria = criteria;
        var cachedEnrichment = new Dictionary<string, PremiereItem>(StringComparer.Ordinal);
        IReadOnlyList<PremiereItem> seededItems = [];
        IReadOnlyList<PremiereItem> staleSeededItems = [];

        if (!forceRefresh)
        {
            var snapshot = await ReadCalendarCacheAsync(start, end, filters, cancellationToken);
            if (snapshot.Items is { } cached)
            {
                if (snapshot.CanReuseDegraded && resumeState?.RefreshCalendarSnapshot != true)
                {
                    var filtered = ApplyRequestedFilters(cached, filters);
                    _telemetry.RecordDiagnostic("calendar.cache.decision", details: new()
                    {
                        Outcome = "direct_degraded", Reason = "optional_source_access_denied", Count = filtered.Count,
                        FromCache = true, IsFinal = true, RetryAtUtc = snapshot.UnavailableSources.Min(source => source.RetryAtUtc),
                        DurationMilliseconds = Stopwatch.GetElapsedTime(refreshStarted).TotalMilliseconds
                    });
                    yield return WithDegradedCoverage(CreateProgress("Week cache", filtered, filtered, isFinal: true, fromCache: true), snapshot.UnavailableSources, true);
                    yield break;
                }
                // Discovery will enrich rediscovered media. Fresh media retained
                // from a shared cache still needs its normal metadata/ratings repair.
                var refreshSnapshot = resumeState?.RefreshCalendarSnapshot == true;
                bool Rediscovered(PremiereItem item) => refreshSnapshot || !snapshot.IsComplete
                    && (item.MediaType == PremiereMediaType.Series ? !snapshot.HasFreshSeries : !snapshot.HasFreshMovies);
                var rediscoveredItems = cached.Where(Rediscovered).ToArray();
                var retainedItems = cached.Where(item => !Rediscovered(item)).ToArray();
                var metadataHydration = await TraceEnrichmentStageAsync("cache.metadata_hydration",
                    () => HydrateSharedItemMetadataAsync(retainedItems, start, end, cancellationToken));
                if (rediscoveredItems.Length > 0)
                    _telemetry.RecordDiagnostic("enrichment.skipped", details: new()
                    {
                        Stage = "cache.metadata_hydration", Outcome = "skipped",
                        Reason = "fresh_discovery_pending", Count = rediscoveredItems.Length
                    });
                var hydrated = await TraceEnrichmentStageAsync("cache.hydration", async () =>
                {
                    var retained = await HydrateCachedImdbRatingsAsync(metadataHydration.Items, filters, cancellationToken);
                    var rediscovered = await HydrateCachedImdbRatingsAsync(rediscoveredItems, filters, cancellationToken, allowRemote: false);
                    return (IReadOnlyList<PremiereItem>)retained.Concat(rediscovered).ToArray();
                });
                var cachedItems = ApplyRequestedFilters(hydrated, filters);
                if (_itemCacheSession.Value is { } cachedSession)
                {
                    var validation = await cachedSession.ReconcileAsync(hydrated, includeConsumed: true, cancellationToken);
                    cachedItems = ApplyRequestedFilters(validation.Items, filters);
                    if (validation.DirtyMedia.Count > 0)
                    {
                        metadataHydration = (metadataHydration.Items, true);
                        cachedSession.ForceDiscoveryRefresh = true;
                    }
                }
                if (snapshot.IsComplete && !metadataHydration.DiscoveryChanged && resumeState?.RefreshCalendarSnapshot != true)
                {
                    yield return CreateProgress("Week cache", cachedItems, cachedItems, isFinal: true, fromCache: true);
                    yield break;
                }

                if (metadataHydration.DiscoveryChanged)
                    snapshot = snapshot with { IsComplete = false, HasFreshSeries = false, HasFreshMovies = false, FreshItems = [] };

                var freshIds = snapshot.FreshItems.Select(item => item.CanonicalId).ToHashSet(StringComparer.Ordinal);
                // Snapshot refresh may reuse metadata and provisional rows, but its
                // successful final result must reflect fresh discovery (including removals).
                seededItems = resumeState?.RefreshCalendarSnapshot == true
                    ? [] : cachedItems.Where(item => freshIds.Contains(item.CanonicalId)).ToArray();
                staleSeededItems = resumeState?.RefreshCalendarSnapshot == true
                    ? cachedItems : cachedItems.Where(item => !freshIds.Contains(item.CanonicalId)).ToArray();
                if (resumeState?.RefreshCalendarSnapshot != true && (snapshot.HasFreshSeries || snapshot.HasFreshMovies))
                {
                    yield return CreateProgress("Week cache", seededItems, seededItems, fromCache: true);
                    if (ShouldUseSharedMediaCache(criteria))
                    {
                        fetchCriteria = criteria with
                        {
                            IncludeSeries = criteria.IncludeSeries && !snapshot.HasFreshSeries,
                            IncludeMovies = criteria.IncludeMovies && !snapshot.HasFreshMovies
                        };
                    }
                }
                if (staleSeededItems.Count > 0)
                {
                    yield return CreateProgress("Expired week cache", cachedItems, cachedItems,
                        fromCache: true, isStaleCache: true);
                }
                cachedEnrichment = CreateCachedEnrichmentLookup(cachedItems);
            }
        }
        else
        {
            if (ShouldUseSharedMediaCache(criteria))
            {
                var sharedExpiredItems = await GetSharedMediaCachedItemsAsync(
                    start,
                    end,
                    filters,
                    allowExpired: true,
                    requireBothMediaCaches: false,
                    cancellationToken);
                cachedEnrichment = CreateCachedEnrichmentLookup(sharedExpiredItems);
            }
            else
            {
                var cached = await _cacheOrchestrator.ReadAsync(start, end, cacheKey, cancellationToken, allowExpired: true);
                cachedEnrichment = CreateCachedEnrichmentLookup(cached);
            }
        }

        IReadOnlyList<PremiereItem> finalItems = [];
        var degradedBaselines = await CaptureDegradedBaselinesAsync(start, end, filters, criteria, cancellationToken);
        PremiereLoadProgress? finalUpdate = null;
        var itemCacheValidationFailed = false;
        var sourceAndItemCacheValidationFailed = false;
        var progressHistory = new Dictionary<string, PremiereLoadProgress>(StringComparer.Ordinal);
        Exception? refreshError = null;
        // The seed is immutable for this execution; avoid rebuilding it for every provider update.
        var provisionalSeed = seededItems.Count == 0 ? staleSeededItems
            : staleSeededItems.Count == 0 ? seededItems
            : MergePremiereItems(seededItems.Concat(staleSeededItems));

        await using (var enumerator = FetchFreshPremiereUpdatesAsync(start, end, forceRefresh, fetchCriteria, filters, cachedEnrichment, resumeState, cancellationToken)
            .GetAsyncEnumerator(cancellationToken))
        {
            while (true)
            {
                PremiereLoadProgress update;
                try
                {
                    if (!await enumerator.MoveNextAsync())
                    {
                        break;
                    }

                    update = enumerator.Current;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    refreshError = ex;
                    break;
                }

                var progressSeed = update.IsFinal && !update.HasSourceErrors
                    ? seededItems
                    : provisionalSeed;
                if (progressSeed.Count > 0)
                {
                    if (update.IsFinal && update.UnavailableSources.Count > 0)
                    {
                        var rows = PreserveUndiscoveredRows(update.Items, progressSeed);
                        update = update with { Items = rows, SourceItems = rows, TotalItemCount = rows.Count };
                    }
                    else update = MergeProgressWithSeed(update, progressSeed);
                }

                progressHistory[update.CheckpointKey ?? update.SourceName] = update;
                if (update.IsFinal)
                {
                    finalItems = update.Items;
                    finalUpdate = update;
                    continue;
                }

                yield return update;
            }
        }

        if (refreshError is not null)
        {
            var fallbackUpdate = ShouldUseSharedMediaCache(criteria)
                ? await TryCreateSharedMediaCacheUpdateAsync(
                    start,
                    end,
                    filters,
                    allowExpired: true,
                    cancellationToken)
                : await TryCreateExpiredCacheUpdateAsync(start, end, cacheKey, filters, refreshError, cancellationToken);
            if (fallbackUpdate is not null)
            {
                if (ShouldUseSharedMediaCache(criteria))
                {
                    _logger.LogWarning(
                        refreshError,
                        "Using shared cached premiere calendar results for {StartDate} through {EndDate} after source refresh failed.",
                        start,
                        end);
                }

                yield return fallbackUpdate;
                yield break;
            }

            ExceptionDispatchInfo.Capture(refreshError).Throw();
        }

        if (finalUpdate is { HasSourceErrors: true, RetryAfterUtc: not null } && finalItems.Count == 0)
        {
            var fallback = ShouldUseSharedMediaCache(criteria)
                ? await TryCreateSharedMediaCacheUpdateAsync(start, end, filters, allowExpired: true, cancellationToken)
                : await TryCreateExpiredCacheUpdateAsync(start, end, cacheKey, filters,
                    new ExternalApiException("Provider refresh is deferred until its recovery deadline."), cancellationToken);
            if (fallback is not null)
            {
                finalItems = fallback.Items;
                finalUpdate = fallback with
                {
                    HasSourceErrors = true,
                    FailedSourceNames = finalUpdate.FailedSourceNames,
                    RetryAfterUtc = finalUpdate.RetryAfterUtc,
                    IsDegradedResult = finalUpdate.IsDegradedResult,
                    UnavailableSources = finalUpdate.UnavailableSources
                };
            }
        }

        if (finalUpdate?.HasSourceErrors == true)
        {
            // An unavailable source cannot prove removals, including on explicit refresh.
            var prior = await ReadCalendarCacheCoreAsync(start, end, filters, cancellationToken);
            finalItems = ApplyRequestedFilters(PreserveUndiscoveredRows(finalItems, prior.Items ?? []), filters);
            finalUpdate = finalUpdate with { Items = finalItems, SourceItems = finalItems, TotalItemCount = finalItems.Count };
        }

        if (finalUpdate is not null && forceRefresh && !finalUpdate.HasSourceErrors)
        {
            finalItems = await RepairAndBackfillAsync(finalItems, cancellationToken, forceRefresh);
            if (_itemCacheSession.Value is { } itemSession)
            {
                itemSession.SynchronizeMaintainedProjection(finalItems);
                await itemSession.FlushAsync(cancellationToken);
                finalItems = await HydrateSharedItemRatingsAsync(finalItems, cancellationToken, allowRemote: true);
            }
            finalUpdate = finalUpdate with
            {
                Items = finalItems,
                TotalItemCount = finalItems.Count,
                SourceItems = finalItems
            };
        }

        if (finalUpdate is not null && _itemCacheSession.Value is { } validationSession)
        {
            await validationSession.FlushAsync(cancellationToken);
            var validation = await validationSession.ReconcileAsync(finalItems, includeConsumed: true, cancellationToken,
                windowStart: start, windowEnd: end);
            itemCacheValidationFailed = validation.DirtyMedia.Count > 0;
            sourceAndItemCacheValidationFailed = itemCacheValidationFailed && finalUpdate.HasSourceErrors;
            var denialCannotDeferInvalidItems = finalUpdate.IsDegradedResult && itemCacheValidationFailed;
            if (denialCannotDeferInvalidItems)
                _telemetry.RecordDiagnostic("source.retry.policy", details: new()
                {
                    Outcome = "terminal_partial", Reason = "optional_denial_with_item_cache_validation",
                    Count = validation.DirtyMedia.Count, WeekStart = start, WeekEnd = end, CacheKey = cacheKey,
                    DurationMilliseconds = Stopwatch.GetElapsedTime(refreshStarted).TotalMilliseconds
                });
            finalItems = ApplyRequestedFilters(validation.Items, filters);
            finalUpdate = finalUpdate with
            {
                Items = finalItems, SourceItems = finalItems, TotalItemCount = finalItems.Count,
                IsDegradedResult = finalUpdate.IsDegradedResult && validation.DirtyMedia.Count == 0,
                RetryAfterUtc = denialCannotDeferInvalidItems ? null : finalUpdate.RetryAfterUtc,
                HasSourceErrors = finalUpdate.HasSourceErrors || validation.DirtyMedia.Count > 0,
                FailedSourceNames = validation.DirtyMedia.Count == 0 ? finalUpdate.FailedSourceNames
                    : finalUpdate.FailedSourceNames.Append("Item cache validation").Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            };
        }

        if (finalUpdate is not null)
        {
            await RecordWeekDiagnosticsAsync(start, end, cacheKey, finalItems, progressHistory.Values.ToArray(), cancellationToken);
        }

        if (finalUpdate is not null && !finalUpdate.HasSourceErrors)
        {
            var publicationStarted = Stopwatch.GetTimestamp();
            try
            {
                if (ShouldUseSharedMediaCache(criteria))
                {
                    await SetSharedMediaCachesAsync(start, end, finalItems, filters, resumeState, cancellationToken);
                }
                else
                {
                    await PublishCalendarCacheAsync(resumeState,
                        token => _cacheOrchestrator.WriteAsync(start, end, cacheKey, finalItems, token), cancellationToken);
                    await RecordWeekCacheStateAsync(start, cacheKey, finalItems.Count, cancellationToken);
                }
                _telemetry.RecordDiagnostic("calendar.cache.publication", details: new()
                {
                    Cache = "complete", Outcome = "success", Count = finalItems.Count, CacheKey = cacheKey,
                    DurationMilliseconds = Stopwatch.GetElapsedTime(publicationStarted).TotalMilliseconds
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not write premiere calendar cache for {StartDate} through {EndDate}.", start, end);
                _telemetry.RecordDiagnostic("calendar.cache.publication", details: new()
                {
                    Cache = "complete", Outcome = "failed", Reason = ex.GetType().Name,
                    DurationMilliseconds = Stopwatch.GetElapsedTime(publicationStarted).TotalMilliseconds
                });
            }
        }
        else if (finalUpdate?.HasSourceErrors == true)
        {
            if (!itemCacheValidationFailed && finalUpdate.IsDegradedResult && finalUpdate.UnavailableSources.Count > 0)
            {
                var saved = await SaveDegradedCalendarCacheAsync(start, end, filters, criteria, finalItems,
                    finalUpdate.UnavailableSources, degradedBaselines, refreshStartedUtc, resumeState, cancellationToken);
                finalUpdate = WithDegradedCoverage(finalUpdate with { IsDegradedResult = true }, finalUpdate.UnavailableSources, saved);
            }
            else
            {
                await SavePartialCalendarCacheAsync(start, end, filters, criteria, finalItems, resumeState, cancellationToken,
                    degradedBaselines, refreshStartedUtc);
            }
            _telemetry.RecordDiagnostic("calendar.cache.incomplete", details: new()
            {
                Reason = sourceAndItemCacheValidationFailed ? "source_and_item_cache_validation"
                    : itemCacheValidationFailed ? "item_cache_validation"
                    : finalUpdate.IsReusableDegraded ? "optional_source_access_denied" : "source_failure", Count = finalItems.Count, CacheKey = cacheKey,
                WeekStart = start, WeekEnd = end, Outcome = finalItems.Count > 0 ? "partial_retained" : "no_rows"
            });
        }

        if (finalUpdate is not null)
        {
            yield return finalUpdate;
        }
    }

    private async Task<IReadOnlyList<PremiereItem>> RepairAndBackfillAsync(
        IReadOnlyList<PremiereItem> items,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        var repairedItems = items;
        if (_missingExternalIdRepairService is not null)
        {
            repairedItems = (await TraceEnrichmentStageAsync("refresh.external_id_repair",
                () => _missingExternalIdRepairService.RepairItemsAsync(
                    repairedItems, cancellationToken, forceRefresh))).Items;
        }

        if (_scoreBackfillService is not null)
        {
            repairedItems = (await TraceEnrichmentStageAsync("refresh.score_backfill",
                () => _scoreBackfillService.BackfillItemsAsync(
                    repairedItems, cancellationToken, forceRefresh))).Items;
        }

        return repairedItems;
    }

    private async Task RecordWeekDiagnosticsAsync(
        DateOnly start,
        DateOnly end,
        string cacheKey,
        IReadOnlyList<PremiereItem> finalItems,
        IReadOnlyList<PremiereLoadProgress> progressHistory,
        CancellationToken cancellationToken)
    {
        if (_weekDiagnosticsService is null)
        {
            return;
        }

        try
        {
            await _weekDiagnosticsService.RecordAsync(start, end, cacheKey, finalItems, progressHistory, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not record week diagnostics for {StartDate} through {EndDate}.", start, end);
        }
    }

    private sealed record CalendarCacheSnapshot(
        IReadOnlyList<PremiereItem>? Items,
        IReadOnlyList<PremiereItem> FreshItems,
        bool IsComplete,
        bool HasFreshSeries,
        bool HasFreshMovies,
        bool HasPartialCache = false)
    {
        public IReadOnlyList<SourceUnavailableNotice> UnavailableSources { get; init; } = [];
        public bool CanReuseDegraded { get; init; }
    }

    private async Task<CalendarCacheSnapshot> ReadCalendarCacheCoreAsync(
        DateOnly start, DateOnly end, CalendarFilters? filters, CancellationToken cancellationToken)
    {
        var criteria = PremiereDiscoveryCriteria.FromFilters(filters);
        if (!ShouldUseSharedMediaCache(criteria))
        {
            var envelope = await _cacheOrchestrator.ReadSnapshotAsync(start, end, criteria.CacheKey(), cancellationToken);
            var items = envelope?.Items;
            var fresh = envelope?.IsComplete == true ? items : null;
            return new CalendarCacheSnapshot(items is null ? null : MergePremiereItems(items), fresh ?? [],
                fresh is not null, criteria.IncludeSeries && fresh is not null, criteria.IncludeMovies && fresh is not null,
                envelope?.Metadata.Completeness == CalendarCacheCompleteness.Partial)
            { UnavailableSources = envelope?.UnavailableSources ?? [], CanReuseDegraded = envelope?.CanReuseDegraded(DateTimeOffset.UtcNow) == true };
        }

        var seriesKey = PremiereDiscoveryCriteria.FromFilters(FiltersForPageMode(filters, CalendarPageMode.Series)).CacheKey();
        var movieKey = PremiereDiscoveryCriteria.FromFilters(FiltersForPageMode(filters, CalendarPageMode.Movies)).CacheKey();
        var seriesEnvelope = await _cacheOrchestrator.ReadSnapshotAsync(start, end, seriesKey, cancellationToken);
        var movieEnvelope = await _cacheOrchestrator.ReadSnapshotAsync(start, end, movieKey, cancellationToken);
        var series = seriesEnvelope?.Items;
        var movies = movieEnvelope?.Items;
        var snapshot = new SharedMediaCacheSnapshot(seriesEnvelope?.IsComplete == true ? series : null,
            movieEnvelope?.IsComplete == true ? movies : null);
        return new CalendarCacheSnapshot(series is null && movies is null ? null : MergePremiereItems((series ?? []).Concat(movies ?? [])),
            snapshot.Items, snapshot.HasSeries && snapshot.HasMovies, snapshot.HasSeries, snapshot.HasMovies,
            seriesEnvelope?.Metadata.Completeness == CalendarCacheCompleteness.Partial
                || movieEnvelope?.Metadata.Completeness == CalendarCacheCompleteness.Partial)
        {
            UnavailableSources = (seriesEnvelope?.UnavailableSources ?? []).Concat(movieEnvelope?.UnavailableSources ?? []).Distinct().ToArray(),
            CanReuseDegraded = (seriesEnvelope?.IsComplete == true || seriesEnvelope?.CanReuseDegraded(DateTimeOffset.UtcNow) == true)
                && (movieEnvelope?.IsComplete == true || movieEnvelope?.CanReuseDegraded(DateTimeOffset.UtcNow) == true)
                && (seriesEnvelope?.UnavailableSources.Count > 0 || movieEnvelope?.UnavailableSources.Count > 0)
        };
    }

    private bool NeedsRemoteCachedHydration(IReadOnlyList<PremiereItem> items, CalendarFilters? filters)
        => filters?.ScoreSource == ScoreSource.RottenTomatoes && _rottenTomatoesClient is not null
            && items.Any(item => (item.RottenTomatoesScore is null || item.RottenTomatoesAudienceScore is null)
                && !_rottenTomatoesClient.TryGetCachedScores(item.MediaType, item.Title, item.PremiereDate.Year, item.WikidataId, out _));

    private async Task<PremiereLoadProgress?> TryCreateSharedMediaCacheUpdateAsync(
        DateOnly start,
        DateOnly end,
        CalendarFilters? filters,
        bool allowExpired,
        CancellationToken cancellationToken)
    {
        var cachedItems = await GetSharedMediaCachedItemsAsync(
            start,
            end,
            filters,
            allowExpired,
            requireBothMediaCaches: true,
            cancellationToken);
        if (cachedItems is null)
        {
            return null;
        }

        var hydratedItems = await HydrateCachedImdbRatingsAsync(
            MergePremiereItems(cachedItems),
            filters,
            cancellationToken,
            allowRemote: false);
        var mergedItems = ApplyRequestedFilters(hydratedItems, filters);
        return CreateProgress(
            allowExpired ? "Expired week cache" : "Week cache",
            mergedItems,
            mergedItems,
            isFinal: true,
            fromCache: true,
            isStaleCache: allowExpired);
    }

    private async Task<SharedMediaCacheSnapshot> GetSharedMediaCacheSnapshotAsync(
        DateOnly start,
        DateOnly end,
        CalendarFilters? filters,
        bool allowExpired,
        CancellationToken cancellationToken)
    {
        var seriesFilters = FiltersForPageMode(filters, CalendarPageMode.Series);
        var movieFilters = FiltersForPageMode(filters, CalendarPageMode.Movies);
        var seriesItems = await _cacheOrchestrator.ReadAsync(
            start,
            end,
            PremiereDiscoveryCriteria.FromFilters(seriesFilters).CacheKey(),
            cancellationToken,
            allowExpired);
        var movieItems = await _cacheOrchestrator.ReadAsync(
            start,
            end,
            PremiereDiscoveryCriteria.FromFilters(movieFilters).CacheKey(),
            cancellationToken,
            allowExpired);

        return new SharedMediaCacheSnapshot(seriesItems, movieItems);
    }

    private async Task<IReadOnlyList<PremiereItem>?> GetSharedMediaCachedItemsAsync(
        DateOnly start,
        DateOnly end,
        CalendarFilters? filters,
        bool allowExpired,
        bool requireBothMediaCaches,
        CancellationToken cancellationToken)
    {
        var snapshot = await GetSharedMediaCacheSnapshotAsync(start, end, filters, allowExpired, cancellationToken);

        if (requireBothMediaCaches && (!snapshot.HasSeries || !snapshot.HasMovies))
        {
            return null;
        }

        if (!snapshot.HasAny)
        {
            return null;
        }

        return snapshot.Items;
    }

    private static PremiereLoadProgress MergeProgressWithSeed(
        PremiereLoadProgress update,
        IReadOnlyList<PremiereItem> seededItems)
    {
        var mergedItems = MergePremiereItems(seededItems.Concat(update.Items));
        return update with
        {
            TotalItemCount = mergedItems.Count,
            Items = mergedItems
        };
    }

    private async Task SetSharedMediaCachesAsync(
        DateOnly start,
        DateOnly end,
        IReadOnlyList<PremiereItem> finalItems,
        CalendarFilters? filters,
        ProviderWorkResumeState? resumeState,
        CancellationToken cancellationToken)
    {
        var seriesFilters = FiltersForPageMode(filters, CalendarPageMode.Series);
        var movieFilters = FiltersForPageMode(filters, CalendarPageMode.Movies);
        var seriesItems = ApplyRequestedFilters(
            MergePremiereItems(finalItems.Where(item => item.MediaType == PremiereMediaType.Series)),
            seriesFilters);
        var movieItems = ApplyRequestedFilters(
            MergePremiereItems(finalItems.Where(item => item.MediaType == PremiereMediaType.Movie)),
            movieFilters);

        await PublishCalendarCacheAsync(resumeState, async token =>
        {
            await _cacheOrchestrator.WriteAsync(start, end,
                PremiereDiscoveryCriteria.FromFilters(seriesFilters).CacheKey(), seriesItems, token);
            await _cacheOrchestrator.WriteAsync(start, end,
                PremiereDiscoveryCriteria.FromFilters(movieFilters).CacheKey(), movieItems, token);
        }, cancellationToken);
        await RecordWeekCacheStateAsync(
            start,
            PremiereDiscoveryCriteria.FromFilters(seriesFilters).CacheKey(),
            seriesItems.Count,
            cancellationToken);
        await RecordWeekCacheStateAsync(
            start,
            PremiereDiscoveryCriteria.FromFilters(movieFilters).CacheKey(),
            movieItems.Count,
            cancellationToken);
    }

    private static Task PublishCalendarCacheAsync(ProviderWorkResumeState? resumeState,
        Func<CancellationToken, Task> publishFiles, CancellationToken cancellationToken)
        => resumeState?.PublishCacheAsync is { } publishOwned
            ? publishOwned(publishFiles, cancellationToken)
            : publishFiles(cancellationToken);

    private async Task RecordWeekCacheStateAsync(
        DateOnly weekStart,
        string cacheKey,
        int itemCount,
        CancellationToken cancellationToken)
    {
        if (_providerCacheStateStore is null)
        {
            return;
        }

        try
        {
            await _providerCacheStateStore.SaveAsync(
                new ProviderCacheState(
                    "calendar",
                    ProviderCacheScope.Week,
                    $"{weekStart:yyyyMMdd}:{cacheKey}",
                    DateTimeOffset.UtcNow,
                    null,
                    null,
                    itemCount,
                    null),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not record calendar cache state for {WeekStart}.", weekStart);
        }
    }

    private static CalendarFilters FiltersForPageMode(CalendarFilters? filters, CalendarPageMode pageMode)
    {
        var pageFilters = filters is null
            ? CalendarFilters.CreateDefault()
            : CalendarFilterState.Clone(filters);
        CalendarFilterState.ApplyPageMode(pageFilters, pageMode);
        CalendarFilterState.Normalize(pageFilters);
        return pageFilters;
    }

    private static bool ShouldUseSharedMediaCache(PremiereDiscoveryCriteria criteria)
    {
        return criteria.IncludeSeries && criteria.IncludeMovies;
    }

    private async Task<PremiereLoadProgress?> TryCreateExpiredCacheUpdateAsync(
        DateOnly start,
        DateOnly end,
        string cacheKey,
        CalendarFilters? filters,
        Exception refreshError,
        CancellationToken cancellationToken)
    {
        var cached = await _cacheOrchestrator.ReadAsync(
            start,
            end,
            cacheKey,
            cancellationToken,
            allowExpired: true);

        if (cached is null)
        {
            return null;
        }

        var hydratedItems = await HydrateCachedImdbRatingsAsync(
            MergePremiereItems(cached),
            filters,
            cancellationToken,
            allowRemote: false);
        var cachedItems = ApplyRequestedFilters(hydratedItems, filters);
        _logger.LogWarning(
            refreshError,
            "Using cached premiere calendar results for {StartDate} through {EndDate} after source refresh failed.",
            start,
            end);

        return CreateProgress(
            "Expired week cache",
            cachedItems,
            cachedItems,
            isFinal: true,
            fromCache: true,
            isStaleCache: true);
    }

    private async IAsyncEnumerable<PremiereLoadProgress> FetchFreshPremiereUpdatesAsync(
        DateOnly start,
        DateOnly end,
        bool forceRefresh,
        PremiereDiscoveryCriteria criteria,
        CalendarFilters? filters,
        IReadOnlyDictionary<string, PremiereItem> cachedEnrichment,
        ProviderWorkResumeState? resumeState,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var fetchStarted = Stopwatch.GetTimestamp();
        using var sourceCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sourceToken = sourceCancellation.Token;
        var sources = new List<PremiereSourceFactory>();

        void AddStreamingSource(
            string name,
            Func<CancellationToken, IAsyncEnumerable<PremiereItemBatch>> getItemBatches,
            DateOnly sourceStart,
            DateOnly sourceEnd,
            string? keySuffix = null)
        {
            var key = SourceKey(name, sourceStart, sourceEnd, keySuffix);
            sources.Add(new PremiereSourceFactory(
                key,
                name,
                sourceStart,
                sourceEnd,
                () => GetSourceBatchStreamAsync(
                    name,
                    getItemBatches,
                    sourceStart,
                    sourceEnd,
                    filters,
                    sourceToken)));
        }

        foreach (var discoveryProvider in _discoveryProviders.Where(provider => ProviderMatchesRequestedMedia(provider, criteria)))
        {
            var provider = discoveryProvider;
            AddStreamingSource(
                SourceNameForExternalProvider(provider),
                token => StreamExternalPremiereItemBatchesAsync(provider, start, end, forceRefresh, criteria, cachedEnrichment, token),
                start,
                end,
                provider.GetType().FullName);
        }

        if (criteria.IncludeSeries && criteria.Series.SeriesDateMode == SeriesDateMode.NewSeriesOnly)
        {
            foreach (var language in LanguageRequestValues(criteria.Series))
            {
                var requestedLanguage = language;
                AddStreamingSource(
                    "TMDb series",
                    token => StreamSeriesBatchesAsync(
                        start,
                        end,
                        criteria,
                        requestedLanguage,
                        forceRefresh,
                        cachedEnrichment,
                        token),
                    start,
                    end,
                    LanguageKey(requestedLanguage));
            }
        }

        foreach (var day in EachDay(start, end))
        {
            if (criteria.IncludeSeries && criteria.Series.SeriesDateMode == SeriesDateMode.AllEpisodes)
            {
                foreach (var language in LanguageRequestValues(criteria.Series))
                {
                    var requestedLanguage = language;
                    AddStreamingSource(
                        SourceNameForDay("TMDb series", day),
                        token => StreamSeriesEpisodeBatchesForDayAsync(
                            day,
                            criteria,
                            requestedLanguage,
                            forceRefresh,
                            cachedEnrichment,
                            token),
                        day,
                        day,
                        LanguageKey(requestedLanguage));
                }
            }

            if (criteria.IncludeMovies)
            {
                foreach (var language in LanguageRequestValues(criteria.Movies))
                {
                    var requestedLanguage = language;
                    AddStreamingSource(
                        SourceNameForDay("TMDb movies", day),
                        token => StreamMovieBatchesForDateRangeAsync(
                            day,
                            day,
                            criteria,
                            requestedLanguage,
                            forceRefresh,
                            cachedEnrichment,
                            token),
                        day,
                        day,
                        LanguageKey(requestedLanguage));
                }
            }
        }

        var orderedSources = OrderSourcesForPriority(sources, filters?.PriorityDate).ToArray();
        var latestBatchesByKey = new Dictionary<string, PremiereSourceBatch>(StringComparer.Ordinal);
        var currentItemsAccumulator = new SourceAwarePremiereMergeAccumulator();
        var errors = new List<Exception>();
        var failedSourceNames = new HashSet<string>(StringComparer.Ordinal);
        var active = new List<ActivePremiereSource>();
        var pendingSources = orderedSources.ToList();
        if (resumeState is { CompletedSources.Count: > 0 })
        {
            var sourceKeys = orderedSources.Select(source => source.Key).ToHashSet(StringComparer.Ordinal);
            var checkpoints = resumeState.CompletedSources.Where(source => sourceKeys.Contains(source.Key))
                .ToDictionary(source => source.Key, source => source.Value, StringComparer.Ordinal);
            var reusable = await ValidateSourceCheckpointsAsync(checkpoints, cancellationToken);
            foreach (var completedSource in reusable)
            {
                currentItemsAccumulator.ReplaceSource(completedSource.Key, completedSource.Value);
            }
            pendingSources.RemoveAll(source => reusable.ContainsKey(source.Key));
        }
        var sourceConcurrency = Math.Clamp(_options.SourceFetchConcurrency, 1, Math.Max(1, orderedSources.Length));

        void StartPendingSources()
        {
            while (active.Count < sourceConcurrency && pendingSources.Count > 0)
            {
                var sourceIndex = NextPendingSourceIndex(pendingSources, active, sourceConcurrency);
                if (sourceIndex < 0)
                {
                    break;
                }

                var factory = pendingSources[sourceIndex];
                pendingSources.RemoveAt(sourceIndex);
                var enumerator = TraceSourceBatchesAsync(factory, sourceToken).GetAsyncEnumerator(sourceToken);
                active.Add(new ActivePremiereSource(
                    factory.Key,
                    ProviderKeyForSource(factory.Name),
                    enumerator,
                    enumerator.MoveNextAsync().AsTask()));
            }
        }

        try
        {
            StartPendingSources();

            while (active.Count > 0)
            {
                var completed = await Task.WhenAny(active.Select(source => source.MoveNextTask));
                var source = active.First(candidate => ReferenceEquals(candidate.MoveNextTask, completed));

                if (await completed)
                {
                    var batch = source.Enumerator.Current;
                    latestBatchesByKey[source.Key] = batch;
                    currentItemsAccumulator.ReplaceSource(source.Key, batch.Items);
                    if (batch.Error is not null)
                    {
                        errors.Add(batch.Error);
                        failedSourceNames.Add(batch.Name);
                    }

                    var sourceItems = MergePremiereItems(batch.Items);
                    var currentItems = currentItemsAccumulator.ToMergedItems();
                    yield return CreateProgress(
                        batch.Name,
                        sourceItems,
                        currentItems,
                        completedWork: batch.CompletedWork,
                        totalWork: batch.TotalWork,
                        progressText: batch.ProgressText,
                        elapsedMilliseconds: batch.ElapsedMilliseconds,
                        isSourceComplete: batch.IsComplete,
                        hasSourceErrors: batch.Error is not null,
                        failedSourceNames: batch.Error is null ? [] : [batch.Name],
                        unmappedCount: batch.UnmappedCount,
                        filteredCount: batch.FilteredCount,
                        checkpointKey: source.Key,
                        retryAfterUtc: RetryDeadline(batch.Error));

                    source.MoveNextTask = source.Enumerator.MoveNextAsync().AsTask();
                    continue;
                }

                active.Remove(source);
                await DisposeActiveSourceAsync(source, cancellationToken);
                StartPendingSources();
            }
        }
        finally
        {
            try { await sourceCancellation.CancelAsync(); }
            catch (Exception ex) { _logger.LogDebug(ex, "Source cancellation callback failed during cleanup."); }
            await Task.WhenAll(active.Select(async source =>
            {
                try { await DisposeActiveSourceAsync(source, CancellationToken.None); }
                catch (Exception ex) { _logger.LogDebug(ex, "Source {SourceKey} cleanup failed.", source.Key); }
            }));
        }


        var items = currentItemsAccumulator.ToMergedItems();
        var failedBatches = latestBatchesByKey.Values.Where(batch => batch.Error is not null).ToArray();
        var failures = failedBatches.SelectMany(batch => Unwrap(batch.Error!).Select(error =>
            (Error: error, Notice: OptionalAccessDenial(batch.Name, error)))).ToArray();
        var retryDeadline = errors.Select(RetryDeadline).Where(deadline => deadline is not null).Min();
        var deadlineFailures = failures.Where(failure => RetryDeadline(failure.Error) is not null).ToArray();
        // Optional access-denial deadlines must not keep an unrelated, non-deferred
        // failure alive indefinitely without spending a scheduler retry attempt.
        var terminalMixedDenial = failures.Any(failure => failure.Notice is null)
            && deadlineFailures.Length > 0 && deadlineFailures.All(failure => failure.Notice is not null);
        if (terminalMixedDenial)
        {
            retryDeadline = null;
            _telemetry.RecordDiagnostic("source.retry.policy", details: new()
            {
                Outcome = "terminal_partial", Reason = "optional_denial_with_non_deferred_failure", Count = failures.Length,
                WeekStart = start, WeekEnd = end, CacheKey = criteria.CacheKey(),
                DurationMilliseconds = Stopwatch.GetElapsedTime(fetchStarted).TotalMilliseconds
            });
        }
        if (items.Count == 0 && errors.Count > 0 && retryDeadline is null && !terminalMixedDenial)
        {
            throw new AggregateException("Premiere discovery failed before any source returned items.", errors);
        }

        var finalProgress = CreateProgress("Complete", items, items, isFinal: true,
            hasSourceErrors: errors.Count > 0, failedSourceNames: failedSourceNames.ToArray(), retryAfterUtc: retryDeadline);
        if (errors.Count > 0)
        {
            var notices = failures.Where(failure => failure.Notice is not null).Select(failure => failure.Notice!).Distinct().ToArray();
            finalProgress = finalProgress with { UnavailableSources = notices };
            if (failedBatches.Length > 0 && failures.All(failure => failure.Notice is not null) && failedBatches.Length == errors.Count)
                finalProgress = WithDegradedCoverage(finalProgress with { IsDegradedResult = true }, notices, false);
        }
        yield return finalProgress;
    }

    private static DateTimeOffset? RetryDeadline(Exception? error)
    {
        if (error is ProviderRetryAfterException throttled) return throttled.RetryAtUtc;
        if (error is ProviderCircuitOpenException circuit) return circuit.RetryAtUtc;
        if (error is ProviderRequestException request && request.Failure.RetryAtUtc is { } retryAtUtc) return retryAtUtc;
        if (error is AggregateException aggregate)
            return aggregate.InnerExceptions.Select(RetryDeadline).Where(value => value is not null).Min();
        return error?.InnerException is { } inner ? RetryDeadline(inner) : null;
    }

    private async Task<PremiereSourceBatch> GetSourceBatchAsync(
        string name,
        Func<CancellationToken, Task<IReadOnlyList<PremiereItem>>> getItems,
        DateOnly start,
        DateOnly end,
        CalendarFilters? filters,
        CancellationToken cancellationToken)
    {
        var sourceTimeout = TimeSpan.FromSeconds(Math.Clamp(_options.SourceTimeoutSeconds, 1, 3600));
        using var sourceTimeoutCts = _options.SourceTimeoutSeconds > 0
            ? new CancellationTokenSource(sourceTimeout)
            : null;
        using var linkedCts = sourceTimeoutCts is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, sourceTimeoutCts.Token);
        var token = linkedCts?.Token ?? cancellationToken;

        try
        {
            var unfilteredItems = MergePremiereItems(await getItems(token));
            var items = ApplyRequestedFilters(unfilteredItems, filters);
            return new PremiereSourceBatch(
                name,
                items,
                FilteredCount: CountFilteredOut(unfilteredItems, items));
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested
            && sourceTimeoutCts?.IsCancellationRequested == true)
        {
            _logger.LogWarning(
                ex,
                "Skipping premiere discovery source {SourceName} for {StartDate} through {EndDate} after the configured {TimeoutSeconds}s source timeout.",
                name,
                start,
                end,
                _options.SourceTimeoutSeconds);

            return new PremiereSourceBatch(name, [], ex);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Skipping premiere discovery source {SourceName} for {StartDate} through {EndDate} after a request timeout.",
                name,
                start,
                end);

            return new PremiereSourceBatch(name, [], ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _logger.LogWarning(
                ex,
                "Skipping premiere discovery source {SourceName} for {StartDate} through {EndDate}.",
                name,
                start,
                end);

            return new PremiereSourceBatch(name, [], ex);
        }
    }

    private async IAsyncEnumerable<PremiereSourceBatch> GetSingleSourceBatchStreamAsync(
        string name,
        Func<CancellationToken, Task<IReadOnlyList<PremiereItem>>> getItems,
        DateOnly start,
        DateOnly end,
        CalendarFilters? filters,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var batch = await GetSourceBatchAsync(name, getItems, start, end, filters, cancellationToken);
        yield return batch with
        {
            IsComplete = true,
            ProgressText = SourceCompletionProgressText(batch.Items.Count, batch.ProgressText, batch.FilteredCount ?? 0)
        };
    }

    private async IAsyncEnumerable<PremiereSourceBatch> GetSourceBatchStreamAsync(
        string name,
        Func<CancellationToken, IAsyncEnumerable<PremiereItemBatch>> getItemBatches,
        DateOnly start,
        DateOnly end,
        CalendarFilters? filters,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var sourceTimeout = TimeSpan.FromSeconds(Math.Clamp(_options.SourceTimeoutSeconds, 1, 3600));
        using var sourceTimeoutCts = _options.SourceTimeoutSeconds > 0
            ? new CancellationTokenSource(sourceTimeout)
            : null;
        using var linkedCts = sourceTimeoutCts is null
            ? null
            : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, sourceTimeoutCts.Token);
        var token = linkedCts?.Token ?? cancellationToken;
        var accumulated = new PremiereMergeAccumulator();
        var emitted = false;
        var startedAt = Stopwatch.GetTimestamp();
        var lastEmissionAt = startedAt;
        PremiereItemBatch? lastItemBatch = null;
        var furthestCompletedWork = 0;

        long ElapsedMilliseconds()
        {
            return (long)Math.Round(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        }

        await using var enumerator = getItemBatches(token).GetAsyncEnumerator(token);
        while (true)
        {
            PremiereItemBatch? itemBatch = null;
            Exception? error = null;
            var hasNext = false;

            try
            {
                token.ThrowIfCancellationRequested();
                hasNext = await enumerator.MoveNextAsync();
                token.ThrowIfCancellationRequested();
                if (hasNext)
                {
                    itemBatch = enumerator.Current;
                }
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested
                && sourceTimeoutCts?.IsCancellationRequested == true)
            {
                _logger.LogWarning(
                    ex,
                    "Skipping premiere discovery source {SourceName} for {StartDate} through {EndDate} after {TimeoutSeconds}s without forward source progress.",
                    name,
                    start,
                    end,
                    _options.SourceTimeoutSeconds);
                error = ex;
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning(
                    ex,
                    "Skipping premiere discovery source {SourceName} for {StartDate} through {EndDate} after a request timeout.",
                    name,
                    start,
                    end);
                error = ex;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _logger.LogWarning(
                    ex,
                    "Skipping premiere discovery source {SourceName} for {StartDate} through {EndDate}.",
                    name,
                    start,
                    end);
                error = ex;
            }

            if (error is not null)
            {
                var unfilteredErrorItems = accumulated.ToMergedItems();
                var filteredErrorItems = ApplyRequestedFilters(unfilteredErrorItems, filters);
                var errorFilteredCount = CountFilteredOut(unfilteredErrorItems, filteredErrorItems);
                yield return new PremiereSourceBatch(
                    name,
                    filteredErrorItems,
                    error,
                    CompletedWork: lastItemBatch?.TotalWork ?? lastItemBatch?.CompletedWork ?? 0,
                    TotalWork: lastItemBatch?.TotalWork ?? lastItemBatch?.CompletedWork ?? 0,
                    ProgressText: ProgressTextWithFilteredCount(SourceFailureProgressText(error), errorFilteredCount),
                    ElapsedMilliseconds: ElapsedMilliseconds(),
                    IsComplete: true,
                    UnmappedCount: CountUnverified(filteredErrorItems),
                    FilteredCount: errorFilteredCount);
                yield break;
            }

            if (!hasNext)
            {
                var unfilteredCompletionItems = accumulated.ToMergedItems();
                var completionItems = ApplyRequestedFilters(unfilteredCompletionItems, filters);
                var completionFilteredCount = CountFilteredOut(unfilteredCompletionItems, completionItems);
                yield return new PremiereSourceBatch(
                    name,
                    completionItems,
                    CompletedWork: lastItemBatch?.TotalWork is > 0 ? lastItemBatch.TotalWork : lastItemBatch?.CompletedWork ?? 0,
                    TotalWork: lastItemBatch?.TotalWork ?? 0,
                    ProgressText: SourceCompletionProgressText(completionItems.Count, lastItemBatch?.ProgressText, completionFilteredCount),
                    ElapsedMilliseconds: ElapsedMilliseconds(),
                    IsComplete: true,
                    UnmappedCount: CountUnverified(completionItems),
                    FilteredCount: completionFilteredCount);

                yield break;
            }

            lastItemBatch = itemBatch;
            var previousItemCount = accumulated.Count;
            var sourceItems = itemBatch?.Items ?? [];
            if (itemBatch?.IsMetadataProgress != true && _itemCacheSession.Value is { } itemSession)
                sourceItems = itemSession.ApplyPreservedRatings(sourceItems);
            accumulated.AddSourceProgress(sourceItems, itemBatch?.IsMetadataProgress == true);
            var completedWork = itemBatch?.CompletedWork ?? 0;
            var madeProgress = accumulated.Count > previousItemCount || completedWork > furthestCompletedWork;
            if (madeProgress)
            {
                // This is a per-progress-batch inactivity budget, not a total
                // runtime cap on a finite source that continues making progress.
                if (!token.IsCancellationRequested) sourceTimeoutCts?.CancelAfter(sourceTimeout);
                furthestCompletedWork = Math.Max(furthestCompletedWork, completedWork);
                _telemetry.RecordDiagnostic("source.deadline.renewed", details: new()
                {
                    Stage = "inactivity", Count = accumulated.Count, Total = itemBatch?.TotalWork,
                    Sequence = furthestCompletedWork, DurationMilliseconds = ElapsedMilliseconds()
                });
            }
            // Dense sources can emit hundreds of tiny enrichment batches. Keep
            // their rows, but avoid rebuilding and serializing a full snapshot
            // faster than the page can render it. Completion/errors always flush.
            if (accumulated.Count == 0 || emitted && accumulated.Count >= 200
                && Stopwatch.GetElapsedTime(lastEmissionAt) < TimeSpan.FromMilliseconds(250)) continue;
            lastEmissionAt = Stopwatch.GetTimestamp();
            var unfilteredItems = accumulated.ToMergedItems();
            var filteredItems = ApplyRequestedFilters(unfilteredItems, filters);
            var filteredCount = CountFilteredOut(unfilteredItems, filteredItems);
            emitted = true;
            // A consumer persisting/rendering a real progress batch is not a
            // stalled provider. Do not spend its next inactivity budget here.
            if (madeProgress) sourceTimeoutCts?.CancelAfter(Timeout.InfiniteTimeSpan);
            yield return new PremiereSourceBatch(
                name,
                filteredItems,
                CompletedWork: itemBatch?.CompletedWork,
                TotalWork: itemBatch?.TotalWork,
                ProgressText: ProgressTextWithFilteredCount(itemBatch?.ProgressText, filteredCount),
                ElapsedMilliseconds: ElapsedMilliseconds(),
                IsComplete: false,
                UnmappedCount: CountUnverified(filteredItems),
                FilteredCount: filteredCount);
            if (madeProgress && !token.IsCancellationRequested) sourceTimeoutCts?.CancelAfter(sourceTimeout);
        }
    }

    private static IReadOnlyList<PremiereItem> ApplyRequestedFilters(
        IReadOnlyList<PremiereItem> items,
        CalendarFilters? filters)
    {
        return PremiereFilter.Apply(items, filters ?? CalendarFilters.CreateDefault());
    }

    private async Task<IReadOnlyList<PremiereItem>> HydrateCachedImdbRatingsAsync(
        IReadOnlyList<PremiereItem> items,
        CalendarFilters? filters,
        CancellationToken cancellationToken,
        bool allowRemote = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_itemEnrichmentCache is not null)
            return await HydrateSharedItemRatingsAsync(items, cancellationToken, allowRemote);
        if (items.Count == 0 || (_imdbRatingsStore is null && _rottenTomatoesClient is null))
        {
            return items;
        }

        List<PremiereItem>? hydratedItems = null;
        IReadOnlyDictionary<string, ImdbRatingRecord> ratingsByImdbId =
            new Dictionary<string, ImdbRatingRecord>(StringComparer.OrdinalIgnoreCase);
        if (_imdbRatingsStore is not null)
        {
            var imdbIds = items
                .Where(HasCachedImdbId)
                .Select(item => item.ImdbId!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            try
            {
                ratingsByImdbId = await _imdbRatingsStore.GetByImdbIdsAsync(imdbIds, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Skipping cached IMDb dataset batch lookup for {Count} IMDb IDs.", imdbIds.Length);
            }
        }
        var rottenTomatoesByItemKey = await HydrateCachedRottenTomatoesAsync(items, filters, cancellationToken, allowRemote);
        for (var index = 0; index < items.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = items[index];
            var hydratedItem = item;
            if (_imdbRatingsStore is not null && HasCachedImdbId(item))
            {
                var imdbId = item.ImdbId!.Trim();
                ratingsByImdbId.TryGetValue(imdbId, out var rating);

                if (rating is not null
                    && (item.ImdbScore != rating.AverageRating || item.ImdbVoteCount != rating.VoteCount))
                {
                    hydratedItem = item with
                    {
                        ImdbScore = rating.AverageRating,
                        ImdbVoteCount = rating.VoteCount
                    };
                }
            }

            if ((hydratedItem.RottenTomatoesScore is null || hydratedItem.RottenTomatoesAudienceScore is null)
                && _rottenTomatoesClient is not null)
            {
                if (rottenTomatoesByItemKey.TryGetValue(RottenTomatoesHydrationKey(hydratedItem), out var rottenTomatoesScores)
                    && rottenTomatoesScores.HasAnyScore)
                {
                    hydratedItem = hydratedItem with
                    {
                        RottenTomatoesScore = hydratedItem.RottenTomatoesScore ?? rottenTomatoesScores.CriticScore,
                        RottenTomatoesAudienceScore = hydratedItem.RottenTomatoesAudienceScore ?? rottenTomatoesScores.AudienceScore
                    };
                }
            }

            if (hydratedItems is not null)
            {
                hydratedItems.Add(hydratedItem);
            }
            else if (!ReferenceEquals(hydratedItem, item))
            {
                hydratedItems = new List<PremiereItem>(items.Count);
                for (var existingIndex = 0; existingIndex < index; existingIndex++)
                {
                    hydratedItems.Add(items[existingIndex]);
                }

                hydratedItems.Add(hydratedItem);
            }
        }

        return hydratedItems ?? items;
    }

    private async Task<IReadOnlyDictionary<CachedRottenTomatoesKey, RottenTomatoesScores>> HydrateCachedRottenTomatoesAsync(
        IReadOnlyList<PremiereItem> items,
        CalendarFilters? filters,
        CancellationToken cancellationToken,
        bool allowRemote)
    {
        if (_rottenTomatoesClient is null) return new Dictionary<CachedRottenTomatoesKey, RottenTomatoesScores>();

        var work = items
            .Where(item => item.RottenTomatoesScore is null || item.RottenTomatoesAudienceScore is null)
            .Select(RottenTomatoesHydrationKey)
            .Distinct()
            .ToArray();
        var results = new RottenTomatoesScores[work.Length];
        using var hydrationCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var nextIndex = -1;
        var workers = Enumerable.Range(0, Math.Min(2, work.Length)).Select(_ => RunWorkerAsync()).ToArray();
        await Task.WhenAll(workers);
        return work.Select((key, index) => (key, scores: results[index]))
            .ToDictionary(entry => entry.key, entry => entry.scores);

        async Task RunWorkerAsync()
        {
            try
            {
                while (true)
                {
                    hydrationCts.Token.ThrowIfCancellationRequested();
                    var index = Interlocked.Increment(ref nextIndex);
                    if (index >= work.Length) return;
                    var key = work[index];
                    RottenTomatoesScores? cachedScores = null;
                    try
                    {
                        if (filters?.ScoreSource == ScoreSource.RottenTomatoes)
                        {
                            cachedScores = await _rottenTomatoesClient.TryGetCachedScoresAsync(
                                key.MediaType, key.Title, key.Year, key.WikidataId, hydrationCts.Token);
                        }
                        else if (_rottenTomatoesClient.TryGetCachedScores(key.MediaType, key.Title, key.Year, key.WikidataId, out var memoryScores))
                        {
                            // Other score selections retain their memory-only optional hydration.
                            cachedScores = memoryScores;
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Skipping cached Rotten Tomatoes lookup for {Title}.", key.Title);
                    }

                    results[index] = cachedScores ?? (allowRemote && filters?.ScoreSource == ScoreSource.RottenTomatoes
                        ? await GetRottenTomatoesScoresAsync(key.MediaType, key.Title, key.Year, key.WikidataId,
                            hydrationCts.Token, forceRefresh: false)
                        : RottenTomatoesScores.Empty);
                }
            }
            catch
            {
                // Join every worker after canceling siblings, including an in-flight cache read.
                await hydrationCts.CancelAsync();
                throw;
            }
        }
    }

    private static CachedRottenTomatoesKey RottenTomatoesHydrationKey(PremiereItem item)
        => new(item.MediaType, item.Title, item.PremiereDate.Year, item.WikidataId);

    private readonly record struct CachedRottenTomatoesKey(PremiereMediaType MediaType, string Title, int Year, string? WikidataId);

    private static bool HasCachedImdbId(PremiereItem item)
    {
        return !string.IsNullOrWhiteSpace(item.ImdbId);
    }

    private static List<PremiereItem> MergePremiereItems(IEnumerable<PremiereItem> items)
    {
        var mergedByCanonicalId = items
            .GroupBy(item => item.CanonicalId)
            .Select(MergeCanonicalGroup)
            .ToArray();
        return ReconcileAndSortMergedItems(mergedByCanonicalId);
    }

    private static PremiereItem MergeCanonicalGroup(IGrouping<string, PremiereItem> group)
    {
        var items = group.ToArray();
        var selected = items
            .OrderByDescending(item => item.VerificationState == PremiereVerificationState.Verified)
            .ThenByDescending(SourceAuthorityScore)
            .ThenByDescending(item => item.TrailerUrl is not null)
            .ThenByDescending(EnrichmentScore)
            .ThenByDescending(item => item.PosterUrl is not null)
            .ThenByDescending(item => item.SourceNames.Length)
            .ThenBy(item => item.EpisodeSource ?? "", StringComparer.OrdinalIgnoreCase)
            .First();

        var sourceNames = items
            .SelectMany(item => item.SourceNames)
            .Concat(selected.SourceNames)
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Select(source => source.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var sources = items
            .SelectMany(item => item.Sources)
            .Concat(selected.Sources)
            .Where(source => !string.IsNullOrWhiteSpace(source.Name))
            .DistinctBy(source => $"{source.Kind}:{source.Id}:{source.Name}", StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var contributions = items
            .SelectMany(item => item.MergeContributions)
            .Concat(selected.MergeContributions)
            .DistinctBy(MergeContributionKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var merged = MergeSupplementalIdentityAndScores(selected, items);
        return merged with
        {
            SourceNames = sourceNames.Length > 0 ? sourceNames : selected.SourceNames,
            Sources = sources.Length > 0 ? sources : selected.Sources,
            MergeContributions = contributions.Length > 0 ? contributions : selected.MergeContributions
        };
    }

    private static List<PremiereItem> MergeVerifiedAndUnverifiedItems(IReadOnlyList<PremiereItem> items)
    {
        var verifiedItems = items
            .Where(item => item.VerificationState == PremiereVerificationState.Verified)
            .ToList();
        var result = new List<PremiereItem>(verifiedItems);
        var retainedUnverifiedItems = new List<PremiereItem>();

        foreach (var unverified in items.Where(item => item.VerificationState == PremiereVerificationState.Unverified))
        {
            var match = verifiedItems.FirstOrDefault(verified => UnverifiedMatchesVerified(unverified, verified));
            if (match is null)
            {
                var existingUnverified = retainedUnverifiedItems.FirstOrDefault(existing => UnverifiedMatchesUnverified(unverified, existing));
                if (existingUnverified is null)
                {
                    retainedUnverifiedItems.Add(unverified);
                    continue;
                }

                var mergedUnverified = MergeSourceAttribution(existingUnverified, unverified) with
                {
                    PosterUrl = CoalesceText(existingUnverified.PosterUrl, unverified.PosterUrl),
                    BackdropUrl = CoalesceText(existingUnverified.BackdropUrl, unverified.BackdropUrl),
                    ImageSource = CoalesceText(existingUnverified.ImageSource, unverified.ImageSource),
                    ExternalUrl = CoalesceText(existingUnverified.ExternalUrl, unverified.ExternalUrl),
                    ExternalProviderId = CoalesceText(existingUnverified.ExternalProviderId, unverified.ExternalProviderId)
                };
                var retainedIndex = retainedUnverifiedItems.FindIndex(item =>
                    string.Equals(item.CanonicalId, existingUnverified.CanonicalId, StringComparison.Ordinal));
                if (retainedIndex >= 0)
                {
                    retainedUnverifiedItems[retainedIndex] = mergedUnverified;
                }

                continue;
            }

            var merged = MergeSourceAttribution(match, unverified);
            var resultIndex = result.FindIndex(item => string.Equals(item.CanonicalId, match.CanonicalId, StringComparison.Ordinal));
            if (resultIndex >= 0)
            {
                result[resultIndex] = merged;
            }

            var verifiedIndex = verifiedItems.FindIndex(item => string.Equals(item.CanonicalId, match.CanonicalId, StringComparison.Ordinal));
            if (verifiedIndex >= 0)
            {
                verifiedItems[verifiedIndex] = merged;
            }
        }

        result.AddRange(retainedUnverifiedItems);
        return result;
    }

    private static PremiereItem MergeSourceAttribution(PremiereItem target, PremiereItem sourceItem)
    {
        var sourceNames = target.SourceNames
            .Concat(sourceItem.SourceNames)
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Select(source => source.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var sources = target.Sources
            .Concat(sourceItem.Sources)
            .Where(source => !string.IsNullOrWhiteSpace(source.Name))
            .DistinctBy(source => $"{source.Kind}:{source.Id}:{source.Name}", StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var contributions = target.MergeContributions
            .Concat(sourceItem.MergeContributions)
            .DistinctBy(MergeContributionKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var merged = MergeSupplementalIdentityAndScores(target, [sourceItem]);
        return merged with
        {
            SourceNames = sourceNames,
            Sources = sources,
            MergeContributions = contributions
        };
    }

    private static string MergeContributionKey(PremiereMergeContribution contribution)
    {
        return $"{contribution.Source}:{contribution.Status}:{contribution.MatchMethod}:{contribution.TmdbId}:{contribution.ImdbId}:{contribution.TvdbId}:{contribution.CandidateDate}:{contribution.ExternalProviderId}";
    }

    private static PremiereItem MergeSupplementalIdentityAndScores(
        PremiereItem target,
        IReadOnlyList<PremiereItem> candidates)
    {
        var allItems = candidates.Prepend(target).ToArray();
        var imdbId = FirstText(allItems.Select(item => item.ImdbId));
        return target with
        {
            ImdbId = imdbId,
            ImdbUrl = CoalesceText(
                target.ImdbUrl,
                FirstText(allItems.Select(item => item.ImdbUrl)),
                BuildImdbUrl(imdbId)),
            TvdbId = target.TvdbId ?? FirstPositiveInt(allItems.Select(item => item.TvdbId)),
            WikidataId = CoalesceText(target.WikidataId, FirstText(allItems.Select(item => item.WikidataId))),
            ExternalProviderId = CoalesceText(target.ExternalProviderId, FirstText(allItems.Select(item => item.ExternalProviderId))),
            ExternalUrl = CoalesceText(target.ExternalUrl, FirstText(allItems.Select(item => item.ExternalUrl))),
            ImdbScore = target.ImdbScore ?? FirstDouble(allItems.Select(item => item.ImdbScore)),
            ImdbVoteCount = target.ImdbVoteCount ?? FirstInt(allItems.Select(item => item.ImdbVoteCount)),
            RottenTomatoesScore = target.RottenTomatoesScore ?? FirstInt(allItems.Select(item => item.RottenTomatoesScore)),
            RottenTomatoesAudienceScore = target.RottenTomatoesAudienceScore ?? FirstInt(allItems.Select(item => item.RottenTomatoesAudienceScore)),
            MetacriticScore = target.MetacriticScore ?? FirstInt(allItems.Select(item => item.MetacriticScore))
        };
    }

    private static string? FirstText(IEnumerable<string?> values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static int? FirstPositiveInt(IEnumerable<int?> values)
    {
        return values.FirstOrDefault(value => value is > 0);
    }

    private static int? FirstInt(IEnumerable<int?> values)
    {
        return values.FirstOrDefault(value => value is not null);
    }

    private static double? FirstDouble(IEnumerable<double?> values)
    {
        return values.FirstOrDefault(value => value is not null);
    }

    private static bool UnverifiedMatchesVerified(PremiereItem unverified, PremiereItem verified)
    {
        if (unverified.MediaType != verified.MediaType)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(unverified.ImdbId) && !string.IsNullOrWhiteSpace(verified.ImdbId))
        {
            return string.Equals(unverified.ImdbId, verified.ImdbId, StringComparison.OrdinalIgnoreCase);
        }

        if (unverified.TvdbId is > 0 && verified.TvdbId is > 0)
        {
            return unverified.TvdbId == verified.TvdbId;
        }

        if (unverified.PremiereDate != verified.PremiereDate
            || !string.Equals(NormalizeTitleForIdentity(unverified.Title), NormalizeTitleForIdentity(verified.Title), StringComparison.Ordinal))
        {
            return false;
        }

        if (unverified.SeasonNumber is > 0 && verified.SeasonNumber is > 0
            && unverified.SeasonNumber != verified.SeasonNumber)
        {
            return false;
        }

        return unverified.EpisodeNumber is not > 0
            || verified.EpisodeNumber is not > 0
            || unverified.EpisodeNumber == verified.EpisodeNumber;
    }

    private static bool UnverifiedMatchesUnverified(PremiereItem left, PremiereItem right)
    {
        if (left.MediaType != right.MediaType || left.PremiereDate != right.PremiereDate)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(left.ImdbId) && !string.IsNullOrWhiteSpace(right.ImdbId))
        {
            return string.Equals(left.ImdbId, right.ImdbId, StringComparison.OrdinalIgnoreCase);
        }

        if (left.TvdbId is > 0 && right.TvdbId is > 0)
        {
            return left.TvdbId == right.TvdbId;
        }

        if (!string.Equals(NormalizeTitleForIdentity(left.Title), NormalizeTitleForIdentity(right.Title), StringComparison.Ordinal))
        {
            return false;
        }

        if (left.SeasonNumber is > 0 && right.SeasonNumber is > 0 && left.SeasonNumber != right.SeasonNumber)
        {
            return false;
        }

        return left.EpisodeNumber is not > 0
            || right.EpisodeNumber is not > 0
            || left.EpisodeNumber == right.EpisodeNumber;
    }

    private static int VerificationSortRank(PremiereItem item)
    {
        return item.VerificationState == PremiereVerificationState.Unverified ? 1 : 0;
    }

    private static int EnrichmentScore(PremiereItem item)
    {
        var score = 0;
        score += string.IsNullOrWhiteSpace(item.ImdbId) ? 0 : 8;
        score += item.TvdbId is > 0 ? 4 : 0;
        score += item.RuntimeMinutes is > 0 ? 4 : 0;
        score += item.SourceNames.Length * 3;
        score += item.Genres.Length * 2;
        score += item.Keywords.Length;
        score += item.MovieReleaseTypes.Length;
        score += item.Certifications.Length;
        score += string.IsNullOrWhiteSpace(item.OfficialSiteUrl) ? 0 : 2;
        score += item.TvStatus is null ? 0 : 1;
        score += item.TvType is null ? 0 : 1;
        return score;
    }

    private static int SourceAuthorityScore(PremiereItem item)
    {
        var score = 0;

        score += item.VerificationState == PremiereVerificationState.Verified ? 40 : -40;
        score += item.MediaType == PremiereMediaType.Series && item.Type == PremiereItemType.SeriesEpisode ? 20 : 0;
        score += IsExactSeriesEpisode(item) ? 30 : 0;
        score += item.Type == PremiereItemType.SeriesPremiere ? 25 : 0;
        score += item.MediaType == PremiereMediaType.Movie ? 25 : 0;
        score += string.Equals(item.EpisodeSource, "TMDb air date", StringComparison.OrdinalIgnoreCase) ? 12 : 0;
        score -= item.Sources.Any(source => string.Equals(source.Kind, "schedule", StringComparison.OrdinalIgnoreCase)) ? 12 : 0;
        score += item.SourceNames.Length > 0 ? 8 : 0;
        score += item.TmdbId > 0 ? 4 : 0;

        return score;
    }

    private static Dictionary<string, PremiereItem> CreateCachedEnrichmentLookup(IReadOnlyList<PremiereItem>? cachedItems)
    {
        if (cachedItems is not { Count: > 0 })
        {
            return new Dictionary<string, PremiereItem>(StringComparer.Ordinal);
        }

        return MergePremiereItems(cachedItems)
            .Where(item => item.VerificationState == PremiereVerificationState.Verified && EnrichmentScore(item) > 0)
            .GroupBy(item => item.CanonicalId, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(EnrichmentScore)
                    .ThenByDescending(item => item.TrailerUrl is not null)
                    .First(),
                StringComparer.Ordinal);
    }

    private static bool TryReuseCachedEnrichment(
        IReadOnlyDictionary<string, PremiereItem>? cachedEnrichment,
        PremiereItem discoveredItem,
        out PremiereItem reusedItem)
    {
        if (cachedEnrichment is not null
            && cachedEnrichment.TryGetValue(discoveredItem.CanonicalId, out var cachedItem)
            && IsFreshReusableEnrichment(cachedItem))
        {
            reusedItem = MergeCachedEnrichment(cachedItem, discoveredItem);
            return true;
        }

        reusedItem = discoveredItem;
        return false;
    }

    private static bool IsFreshReusableEnrichment(PremiereItem item)
    {
        return item.VerificationState == PremiereVerificationState.Verified
            && EnrichmentScore(item) > 0
            && DateTimeOffset.UtcNow - item.LastUpdatedUtc <= CachedEnrichmentMaxAge;
    }

    private static PremiereItem MergeCachedEnrichment(PremiereItem cachedItem, PremiereItem discoveredItem)
    {
        return ItemEnrichmentRatings.FromItem(cachedItem).Apply(ItemEnrichmentMetadata.FromItem(cachedItem).Apply(discoveredItem))
            with { LastUpdatedUtc = cachedItem.LastUpdatedUtc };
    }

    private static bool IsExactSeriesEpisode(PremiereItem item)
    {
        return item is
        {
            MediaType: PremiereMediaType.Series,
            Type: PremiereItemType.SeriesEpisode,
            SeasonNumber: > 0,
            EpisodeNumber: > 0
        };
    }

    private static bool IsGenericSeriesAirDate(PremiereItem item)
    {
        return item is
        {
            MediaType: PremiereMediaType.Series,
            Type: PremiereItemType.SeriesEpisode
        } && (item.SeasonNumber is null || item.EpisodeNumber is null);
    }

    private readonly record struct SeriesDayKey(int TmdbId, DateOnly PremiereDate);

    internal sealed class SourceAwarePremiereMergeAccumulator
    {
        private readonly Dictionary<string, IReadOnlyList<PremiereItem>> _itemsBySource = new(StringComparer.Ordinal);
        private readonly Dictionary<string, List<PremiereItem>> _itemsByCanonicalId = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PremiereItem> _mergedByCanonicalId = new(StringComparer.Ordinal);
        private readonly HashSet<string> _dirtyCanonicalIds = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PremiereItem> _reusableMergedRows = new(StringComparer.Ordinal);

        public void ReplaceSource(string sourceKey, IReadOnlyList<PremiereItem> items)
        {
            // Reuse unchanged single-source rows, but preserve the original dictionary and
            // dirty-set mutation order: reconciliation uses first-match identity resolution.
            _reusableMergedRows.Clear();
            if (_itemsBySource.TryGetValue(sourceKey, out var previousItems))
            {
                var incoming = items.GroupBy(item => item.CanonicalId, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
                foreach (var group in previousItems.GroupBy(item => item.CanonicalId, StringComparer.Ordinal))
                {
                    if (incoming.TryGetValue(group.Key, out var next)
                        && !_dirtyCanonicalIds.Contains(group.Key)
                        && _itemsByCanonicalId.TryGetValue(group.Key, out var contributors)
                        && contributors.Count == next.Length && group.SequenceEqual(next)
                        && _mergedByCanonicalId.TryGetValue(group.Key, out var merged))
                    {
                        _reusableMergedRows[group.Key] = merged;
                    }
                }
                foreach (var item in previousItems)
                {
                    if (_itemsByCanonicalId.TryGetValue(item.CanonicalId, out var existing))
                    {
                        existing.Remove(item);
                        _dirtyCanonicalIds.Add(item.CanonicalId);
                        if (existing.Count == 0)
                        {
                            _itemsByCanonicalId.Remove(item.CanonicalId);
                            _mergedByCanonicalId.Remove(item.CanonicalId);
                        }
                    }
                }
            }

            _itemsBySource[sourceKey] = items;
            foreach (var item in items)
            {
                if (!_itemsByCanonicalId.TryGetValue(item.CanonicalId, out var existing))
                {
                    existing = [];
                    _itemsByCanonicalId[item.CanonicalId] = existing;
                }

                existing.Add(item);
                _dirtyCanonicalIds.Add(item.CanonicalId);
            }
        }

        public List<PremiereItem> ToMergedItems()
        {
            foreach (var canonicalId in _dirtyCanonicalIds)
            {
                if (_itemsByCanonicalId.TryGetValue(canonicalId, out var items) && items.Count > 0)
                {
                    _mergedByCanonicalId[canonicalId] = _reusableMergedRows.TryGetValue(canonicalId, out var reusable)
                        ? reusable
                        : MergeCanonicalGroup(items.GroupBy(item => item.CanonicalId, StringComparer.Ordinal).Single());
                }
            }

            _dirtyCanonicalIds.Clear();
            _reusableMergedRows.Clear();
            return ReconcileAndSortMergedItems(_mergedByCanonicalId.Values);
        }
    }

    private sealed class PremiereMergeAccumulator
    {
        private readonly Dictionary<string, List<PremiereItem>> _itemsByCanonicalId = new(StringComparer.Ordinal);
        private readonly Dictionary<string, PremiereItem> _mergedByCanonicalId = new(StringComparer.Ordinal);
        private readonly HashSet<string> _dirtyCanonicalIds = new(StringComparer.Ordinal);

        private Dictionary<(string CanonicalId, DateOnly PremiereDate), List<PremiereItem>>? _provisionalSourceItems;
        private HashSet<(string CanonicalId, DateOnly PremiereDate)>? _completedSourceOccurrences;

        public int Count => _itemsByCanonicalId.Count;

        public void AddRange(IEnumerable<PremiereItem> items)
        {
            foreach (var item in items) Add(item);
        }

        public void AddSourceProgress(IEnumerable<PremiereItem> items, bool isMetadataProgress)
        {
            _provisionalSourceItems ??= [];
            _completedSourceOccurrences ??= [];
            foreach (var item in items)
            {
                var occurrence = (item.CanonicalId, item.PremiereDate);
                if (isMetadataProgress)
                {
                    // A late discovery/core hint cannot replace this source's
                    // completed occurrence. Other dates keep their own evidence.
                    if (_completedSourceOccurrences.Contains(occurrence)) continue;
                    if (!_provisionalSourceItems.TryGetValue(occurrence, out var provisional))
                        _provisionalSourceItems[occurrence] = provisional = [];
                    provisional.Add(item);
                }
                else
                {
                    _completedSourceOccurrences.Add(occurrence);
                    if (_provisionalSourceItems.Remove(occurrence, out var provisional)
                        && _itemsByCanonicalId.TryGetValue(item.CanonicalId, out var existing))
                    {
                        var replacementIndex = existing.IndexOf(provisional[0]);
                        foreach (var hint in provisional) existing.Remove(hint);
                        if (replacementIndex >= 0)
                        {
                            // Keep the occurrence's original position among
                            // legitimate duplicates and other discovery dates.
                            existing.Insert(Math.Min(replacementIndex, existing.Count), item);
                            _dirtyCanonicalIds.Add(item.CanonicalId);
                            continue;
                        }
                    }
                }
                Add(item);
            }
        }

        private void Add(PremiereItem item)
        {
            if (!_itemsByCanonicalId.TryGetValue(item.CanonicalId, out var existingItems))
                _itemsByCanonicalId[item.CanonicalId] = existingItems = [];
            existingItems.Add(item);
            _dirtyCanonicalIds.Add(item.CanonicalId);
        }

        public List<PremiereItem> ToMergedItems()
        {
            if (_itemsByCanonicalId.Count == 0)
            {
                return [];
            }

            foreach (var canonicalId in _dirtyCanonicalIds)
            {
                var items = _itemsByCanonicalId[canonicalId];
                _mergedByCanonicalId[canonicalId] = MergeCanonicalGroup(items.GroupBy(item => item.CanonicalId, StringComparer.Ordinal).Single());
            }

            _dirtyCanonicalIds.Clear();
            return ReconcileAndSortMergedItems(_mergedByCanonicalId.Values);
        }
    }

    private static List<PremiereItem> ReconcileAndSortMergedItems(IEnumerable<PremiereItem> mergedItems)
    {
        var mergedByCanonicalId = mergedItems.ToArray();
        var daysWithExactEpisodes = mergedByCanonicalId
            .Where(IsExactSeriesEpisode)
            .Select(item => new SeriesDayKey(item.TmdbId, item.PremiereDate))
            .ToHashSet();

        var withoutGenericAirDateRows = mergedByCanonicalId
            .Where(item => !IsGenericSeriesAirDate(item)
                || !daysWithExactEpisodes.Contains(new SeriesDayKey(item.TmdbId, item.PremiereDate)))
            .ToArray();

        return MergeVerifiedAndUnverifiedItems(withoutGenericAirDateRows)
            .OrderBy(item => item.PremiereDate)
            .ThenBy(VerificationSortRank)
            .ThenBy(item => item.MediaType)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static PremiereLoadProgress CreateProgress(
        string sourceName,
        IReadOnlyList<PremiereItem> sourceItems,
        IReadOnlyList<PremiereItem> allItems,
        bool isFinal = false,
        bool fromCache = false,
        int? completedWork = null,
        int? totalWork = null,
        string? progressText = null,
        long? elapsedMilliseconds = null,
        bool isSourceComplete = false,
        bool hasSourceErrors = false,
        IReadOnlyList<string>? failedSourceNames = null,
        int? unmappedCount = null,
        int? filteredCount = null,
        string? checkpointKey = null,
        bool isStaleCache = false,
        DateTimeOffset? retryAfterUtc = null)
    {
        var diagnosticSourceItems = sourceItems
            .Select(EnsureDiagnostics)
            .ToArray();
        var diagnosticAllItems = allItems
            .Select(EnsureDiagnostics)
            .ToArray();
        return new PremiereLoadProgress(
            sourceName,
            diagnosticSourceItems.Length,
            diagnosticAllItems.Length,
            diagnosticAllItems,
            isFinal,
            fromCache,
            completedWork,
            totalWork,
            progressText,
            elapsedMilliseconds)
        {
            ProviderKey = ProviderKeyForSource(sourceName),
            Phase = isFinal || isSourceComplete ? "complete" : fromCache ? "cache" : "loading",
            SourceItems = diagnosticSourceItems,
            IsStaleCache = isStaleCache,
            HasSourceErrors = hasSourceErrors,
            FailedSourceNames = failedSourceNames ?? [],
            UnmappedCount = unmappedCount ?? CountUnverified(diagnosticSourceItems),
            FilteredCount = filteredCount,
            CheckpointKey = checkpointKey,
            RetryAfterUtc = retryAfterUtc
        };
    }

    private static PremiereItem EnsureDiagnostics(PremiereItem item)
    {
        var dateSemantics = item.DateSemantics ?? InferDateSemantics(item);
        var contributions = item.MergeContributions.Length > 0
            ? item.MergeContributions
            : item.TmdbId > 0
                ? [PremiereDiagnosticsFactory.TmdbContribution(item.MediaType, item.TmdbId)]
                :
                [
                    new PremiereMergeContribution
                    {
                        Source = CoalesceText(item.ExternalProviderId, item.ExternalUrl, "External source") ?? "External source",
                        Status = "unverified",
                        MatchMethod = "External candidate",
                        Reason = item.VerificationNote ?? "Could not map this candidate to TMDb.",
                        ImdbId = item.ImdbId,
                        TvdbId = item.TvdbId,
                        CandidateDate = item.PremiereDate,
                        ExternalProviderId = item.ExternalProviderId
                    }
                ];

        return PremiereDiagnosticsFactory.ApplyMissingDataIssues(item with
        {
            DateSemantics = dateSemantics,
            MergeContributions = contributions
        });
    }

    private static PremiereDateSemantics InferDateSemantics(PremiereItem item)
    {
        if (item.VerificationState == PremiereVerificationState.Unverified)
        {
            return new PremiereDateSemantics(
                item.PremiereDate,
                PremiereDateSourceKind.ExternalProviderDate,
                PremiereDataConfidence.Low,
                "Unverified external provider date.");
        }

        if (item.Type == PremiereItemType.SeriesEpisode)
        {
            return new PremiereDateSemantics(
                item.PremiereDate,
                string.IsNullOrWhiteSpace(item.EpisodeSource)
                    ? PremiereDateSourceKind.TmdbEpisodeAirDate
                    : PremiereDateSourceKind.ExternalProviderDate,
                string.IsNullOrWhiteSpace(item.EpisodeSource)
                    ? PremiereDataConfidence.Medium
                    : PremiereDataConfidence.High,
                string.IsNullOrWhiteSpace(item.EpisodeSource)
                    ? "TMDb episode air-date discovery."
                    : $"Episode date from {item.EpisodeSource}.");
        }

        if (item.Type == PremiereItemType.SeriesPremiere)
        {
            return new PremiereDateSemantics(
                item.PremiereDate,
                PremiereDateSourceKind.TmdbFirstAirDate,
                PremiereDataConfidence.Medium,
                "TMDb first air date.");
        }

        return new PremiereDateSemantics(
            item.PremiereDate,
            PremiereDateSourceKind.TmdbMovieReleaseDate,
            PremiereDataConfidence.Medium,
            "TMDb movie release date.");
    }

    private static int CountUnverified(IEnumerable<PremiereItem> items)
    {
        return items.Count(item => item.VerificationState == PremiereVerificationState.Unverified);
    }

    private static int CountFilteredOut(
        IReadOnlyCollection<PremiereItem> unfilteredItems,
        IReadOnlyCollection<PremiereItem> filteredItems)
    {
        return Math.Max(0, unfilteredItems.Count - filteredItems.Count);
    }

    private static string ProviderKeyForSource(string sourceName)
    {
        if (sourceName.Equals("Watchmode releases", StringComparison.OrdinalIgnoreCase)) return "watchmode";
        if (sourceName.StartsWith("TMDb series", StringComparison.Ordinal))
        {
            return "tmdb-series";
        }

        if (sourceName.StartsWith("TMDb movies", StringComparison.Ordinal))
        {
            return "tmdb-movies";
        }

        return sourceName
            .Trim()
            .ToLowerInvariant()
            .Replace(' ', '-');
    }

    private static IEnumerable<DateOnly> EachDay(DateOnly start, DateOnly end)
    {
        for (var date = start; date <= end; date = date.AddDays(1))
        {
            yield return date;
        }
    }

    private static IEnumerable<PremiereSourceFactory> OrderSourcesForPriority(
        IReadOnlyList<PremiereSourceFactory> sources,
        DateOnly? priorityDate)
    {
        if (priorityDate is null)
        {
            return sources;
        }

        return sources
            .Select((source, index) => new
            {
                Source = source,
                Index = index,
                Priority = SourcePriority(source, priorityDate.Value)
            })
            .OrderBy(item => item.Priority)
            .ThenBy(item => item.Index)
            .Select(item => item.Source)
            .ToArray();
    }

    private static int NextPendingSourceIndex(
        IReadOnlyList<PremiereSourceFactory> pendingSources,
        IReadOnlyList<ActivePremiereSource> activeSources,
        int sourceConcurrency)
    {
        if (activeSources.Count == 0)
        {
            var firstProviderKey = ProviderKeyForSource(pendingSources[0].Name);
            return MaxActiveSourcesForProvider(firstProviderKey, sourceConcurrency) > 0 ? 0 : -1;
        }

        var activeProviderCounts = activeSources
            .GroupBy(source => source.ProviderKey, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        for (var index = 0; index < pendingSources.Count; index++)
        {
            var providerKey = ProviderKeyForSource(pendingSources[index].Name);
            var activeCount = activeProviderCounts.GetValueOrDefault(providerKey);
            if (activeCount == 0)
            {
                return index;
            }
        }

        for (var index = 0; index < pendingSources.Count; index++)
        {
            var providerKey = ProviderKeyForSource(pendingSources[index].Name);
            var activeCount = activeProviderCounts.GetValueOrDefault(providerKey);
            if (activeCount < MaxActiveSourcesForProvider(providerKey, sourceConcurrency))
            {
                return index;
            }
        }

        return -1;
    }

    private static int MaxActiveSourcesForProvider(string providerKey, int sourceConcurrency)
    {
        return providerKey is "tmdb-series" or "tmdb-movies"
            ? Math.Min(2, sourceConcurrency)
            : sourceConcurrency;
    }

    private static int SourcePriority(PremiereSourceFactory source, DateOnly priorityDate)
    {
        if (source.Start == priorityDate && source.End == priorityDate)
        {
            return 0;
        }

        if (source.Start == source.End)
        {
            var dayOffset = source.Start.DayNumber - priorityDate.DayNumber;
            return dayOffset switch
            {
                1 => 1,
                -1 => 2,
                _ => 3 + Math.Abs(dayOffset)
            };
        }

        if (source.Start <= priorityDate && source.End >= priorityDate)
        {
            return source.Name.StartsWith("TMDb ", StringComparison.Ordinal)
                ? 0
                : 20;
        }

        return 30 + Math.Abs(source.Start.DayNumber - priorityDate.DayNumber);
    }

    private static string SourceNameForDay(string sourceName, DateOnly day)
    {
        return $"{sourceName} {day.ToString("ddd dd MMM", CultureInfo.InvariantCulture)}";
    }

    private static string SourceKey(
        string sourceName,
        DateOnly sourceStart,
        DateOnly sourceEnd,
        string? keySuffix)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{sourceName}:{sourceStart:yyyyMMdd}:{sourceEnd:yyyyMMdd}:{keySuffix ?? ""}");
    }

    private static string LanguageKey(string? language)
    {
        return string.IsNullOrWhiteSpace(language)
            ? "language:any"
            : $"language:{language.Trim().ToLowerInvariant()}";
    }

    private static string SourceNameForExternalProvider(IPremiereDiscoveryProvider provider)
    {
        if (provider is INamedPremiereDiscoveryProvider namedProvider
            && !string.IsNullOrWhiteSpace(namedProvider.DisplayName))
        {
            return namedProvider.DisplayName.Trim();
        }

        var name = provider.GetType().Name;
        foreach (var suffix in new[] { "DiscoveryProvider", "Provider" })
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal))
            {
                name = name[..^suffix.Length];
                break;
            }
        }

        return string.IsNullOrWhiteSpace(name) ? "External provider" : name;
    }

    private static bool ProviderMatchesRequestedMedia(
        IPremiereDiscoveryProvider provider,
        PremiereDiscoveryCriteria criteria)
    {
        if (provider is not IMediaScopedPremiereDiscoveryProvider scopedProvider)
        {
            return true;
        }

        return (criteria.IncludeSeries && scopedProvider.SupportsMediaType(PremiereMediaType.Series))
            || (criteria.IncludeMovies && scopedProvider.SupportsMediaType(PremiereMediaType.Movie));
    }

    private static IReadOnlyList<string?> LanguageRequestValues(MediaDiscoveryCriteria filters)
    {
        return filters.OriginalLanguages.Length == 0
            ? [null]
            : filters.OriginalLanguages.Cast<string?>().ToArray();
    }

}
