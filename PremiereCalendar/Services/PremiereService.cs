using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class PremiereService : IPremiereService
{
    private static readonly TimeSpan CachedEnrichmentMaxAge = TimeSpan.FromHours(12);
    private const int ConflictingExternalIdsTmdbId = -1;

    private readonly ITmdbClient _tmdbClient;
    private readonly IOmdbClient _omdbClient;
    private readonly ITvmazeClient _tvmazeClient;
    private readonly IWatchmodeClient _watchmodeClient;
    private readonly ICalendarCache _calendarCache;
    private readonly TrailerSelector _trailerSelector;
    private readonly RatingMapper _ratingMapper;
    private readonly IReadOnlyList<IArtworkProvider> _artworkProviders;
    private readonly IReadOnlyList<IPremiereDiscoveryProvider> _discoveryProviders;
    private readonly TmdbOptions _options;
    private readonly ILogger<PremiereService> _logger;
    private readonly IImdbRatingsStore? _imdbRatingsStore;
    private readonly IProviderCacheStateStore? _providerCacheStateStore;
    private readonly IRottenTomatoesClient? _rottenTomatoesClient;

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
        IRottenTomatoesClient? rottenTomatoesClient = null)
    {
        _tmdbClient = tmdbClient;
        _omdbClient = omdbClient;
        _tvmazeClient = tvmazeClient;
        _watchmodeClient = watchmodeClient;
        _calendarCache = calendarCache;
        _trailerSelector = trailerSelector;
        _ratingMapper = ratingMapper;
        _artworkProviders = artworkProviders.ToArray();
        _discoveryProviders = discoveryProviders.ToArray();
        _options = options.Value;
        _logger = logger;
        _imdbRatingsStore = imdbRatingsStore;
        _providerCacheStateStore = providerCacheStateStore;
        _rottenTomatoesClient = rottenTomatoesClient;
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

    public async IAsyncEnumerable<PremiereLoadProgress> StreamPremieresAsync(
        DateOnly start,
        DateOnly end,
        bool forceRefresh = false,
        CalendarFilters? filters = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (end < start)
        {
            throw new ArgumentException("The premiere end date must be on or after the start date.", nameof(end));
        }

        var criteria = PremiereDiscoveryCriteria.FromFilters(filters);
        var cacheKey = criteria.CacheKey();
        var fetchCriteria = criteria;
        var cachedEnrichment = new Dictionary<string, PremiereItem>(StringComparer.Ordinal);
        IReadOnlyList<PremiereItem> seededItems = [];

        if (!forceRefresh)
        {
            if (ShouldUseSharedMediaCache(criteria))
            {
                var sharedCacheSnapshot = await GetSharedMediaCacheSnapshotAsync(
                    start,
                    end,
                    filters,
                    allowExpired: false,
                    cancellationToken);
                if (sharedCacheSnapshot.HasSeries && sharedCacheSnapshot.HasMovies)
                {
                    var hydratedItems = await HydrateCachedImdbRatingsAsync(
                        MergePremiereItems(sharedCacheSnapshot.Items),
                        cancellationToken);
                    var cachedItems = ApplyRequestedFilters(hydratedItems, filters);
                    yield return CreateProgress("Week cache", cachedItems, cachedItems, isFinal: true, fromCache: true);
                    yield break;
                }

                if (sharedCacheSnapshot.HasAny)
                {
                    var hydratedItems = await HydrateCachedImdbRatingsAsync(
                        MergePremiereItems(sharedCacheSnapshot.Items),
                        cancellationToken);
                    seededItems = ApplyRequestedFilters(hydratedItems, filters);
                    yield return CreateProgress("Week cache", seededItems, seededItems, fromCache: true);
                    fetchCriteria = criteria with
                    {
                        IncludeSeries = criteria.IncludeSeries && !sharedCacheSnapshot.HasSeries,
                        IncludeMovies = criteria.IncludeMovies && !sharedCacheSnapshot.HasMovies
                    };
                }

                var sharedExpiredItems = await GetSharedMediaCachedItemsAsync(
                    start,
                    end,
                    filters,
                    allowExpired: true,
                    requireBothMediaCaches: false,
                    cancellationToken);
                cachedEnrichment = CreateCachedEnrichmentLookup(seededItems.Concat(sharedExpiredItems ?? []).ToArray());
            }
            else
            {
                var cached = await _calendarCache.GetWeekAsync(start, end, cacheKey, cancellationToken);
                if (cached is not null)
                {
                    var hydratedItems = await HydrateCachedImdbRatingsAsync(
                        MergePremiereItems(cached),
                        cancellationToken);
                    var cachedItems = ApplyRequestedFilters(hydratedItems, filters);
                    yield return CreateProgress("Week cache", cachedItems, cachedItems, isFinal: true, fromCache: true);
                    yield break;
                }

                cached = await _calendarCache.GetWeekAsync(start, end, cacheKey, cancellationToken, allowExpired: true);
                cachedEnrichment = CreateCachedEnrichmentLookup(cached);
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
                var cached = await _calendarCache.GetWeekAsync(start, end, cacheKey, cancellationToken, allowExpired: true);
                cachedEnrichment = CreateCachedEnrichmentLookup(cached);
            }
        }

        IReadOnlyList<PremiereItem> finalItems = [];
        PremiereLoadProgress? finalUpdate = null;
        Exception? refreshError = null;

        await using (var enumerator = FetchFreshPremiereUpdatesAsync(start, end, forceRefresh, fetchCriteria, filters, cachedEnrichment, cancellationToken)
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

                if (seededItems.Count > 0)
                {
                    update = MergeProgressWithSeed(update, seededItems);
                }

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

        if (finalUpdate is not null && !finalUpdate.HasSourceErrors)
        {
            try
            {
                if (ShouldUseSharedMediaCache(criteria))
                {
                    await SetSharedMediaCachesAsync(start, end, finalItems, filters, cancellationToken);
                }
                else
                {
                    await _calendarCache.SetWeekAsync(start, end, cacheKey, finalItems, cancellationToken);
                    await RecordWeekCacheStateAsync(start, cacheKey, finalItems.Count, cancellationToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not write premiere calendar cache for {StartDate} through {EndDate}.", start, end);
            }
        }
        else if (finalUpdate?.HasSourceErrors == true)
        {
            _logger.LogDebug(
                "Skipping premiere calendar cache write for {StartDate} through {EndDate} because one or more sources failed.",
                start,
                end);
        }

        if (finalUpdate is not null)
        {
            yield return finalUpdate;
        }
    }

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
            cancellationToken);
        var mergedItems = ApplyRequestedFilters(hydratedItems, filters);
        return CreateProgress(
            allowExpired ? "Expired week cache" : "Week cache",
            mergedItems,
            mergedItems,
            isFinal: true,
            fromCache: true);
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
        var seriesItems = await _calendarCache.GetWeekAsync(
            start,
            end,
            PremiereDiscoveryCriteria.FromFilters(seriesFilters).CacheKey(),
            cancellationToken,
            allowExpired);
        var movieItems = await _calendarCache.GetWeekAsync(
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

        await _calendarCache.SetWeekAsync(
            start,
            end,
            PremiereDiscoveryCriteria.FromFilters(seriesFilters).CacheKey(),
            seriesItems,
            cancellationToken);
        await RecordWeekCacheStateAsync(
            start,
            PremiereDiscoveryCriteria.FromFilters(seriesFilters).CacheKey(),
            seriesItems.Count,
            cancellationToken);
        await _calendarCache.SetWeekAsync(
            start,
            end,
            PremiereDiscoveryCriteria.FromFilters(movieFilters).CacheKey(),
            movieItems,
            cancellationToken);
        await RecordWeekCacheStateAsync(
            start,
            PremiereDiscoveryCriteria.FromFilters(movieFilters).CacheKey(),
            movieItems.Count,
            cancellationToken);
    }

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
            ? new CalendarFilters()
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
        var cached = await _calendarCache.GetWeekAsync(
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
            cancellationToken);
        var cachedItems = ApplyRequestedFilters(hydratedItems, filters);
        _logger.LogWarning(
            refreshError,
            "Using cached premiere calendar results for {StartDate} through {EndDate} after source refresh failed.",
            start,
            end);

        return CreateProgress("Expired week cache", cachedItems, cachedItems, isFinal: true, fromCache: true);
    }

    private async IAsyncEnumerable<PremiereLoadProgress> FetchFreshPremiereUpdatesAsync(
        DateOnly start,
        DateOnly end,
        bool forceRefresh,
        PremiereDiscoveryCriteria criteria,
        CalendarFilters? filters,
        IReadOnlyDictionary<string, PremiereItem> cachedEnrichment,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
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
                    cancellationToken)));
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
        var errors = new List<Exception>();
        var failedSourceNames = new HashSet<string>(StringComparer.Ordinal);
        var active = new List<ActivePremiereSource>();
        var pendingSources = orderedSources.ToList();
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
                var enumerator = factory.Open().GetAsyncEnumerator(cancellationToken);
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
                    if (batch.Error is not null)
                    {
                        errors.Add(batch.Error);
                        failedSourceNames.Add(batch.Name);
                    }

                    var sourceItems = MergePremiereItems(batch.Items);
                    var currentItems = MergePremiereItems(latestBatchesByKey.Values.SelectMany(candidate => candidate.Items));
                    yield return CreateProgress(
                        batch.Name,
                        sourceItems,
                        currentItems,
                        completedWork: batch.CompletedWork,
                        totalWork: batch.TotalWork,
                        progressText: batch.ProgressText,
                        elapsedMilliseconds: batch.ElapsedMilliseconds,
                        isSourceComplete: batch.IsComplete,
                        unmappedCount: batch.UnmappedCount,
                        filteredCount: batch.FilteredCount);

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
            foreach (var source in active)
            {
                await DisposeActiveSourceAsync(source, cancellationToken);
            }
        }


        var items = MergePremiereItems(latestBatchesByKey.Values.SelectMany(batch => batch.Items));
        if (items.Count == 0 && errors.Count > 0)
        {
            throw new AggregateException("Premiere discovery failed before any source returned items.", errors);
        }

        yield return CreateProgress(
            "Complete",
            items,
            items,
            isFinal: true,
            hasSourceErrors: errors.Count > 0,
            failedSourceNames: failedSourceNames.ToArray());
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
        PremiereItemBatch? lastItemBatch = null;
        var lastYieldWasComplete = false;

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
                hasNext = await enumerator.MoveNextAsync();
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
                    "Skipping premiere discovery source {SourceName} for {StartDate} through {EndDate} after the configured {TimeoutSeconds}s source timeout.",
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
                if (!emitted)
                {
                    yield return new PremiereSourceBatch(
                        name,
                        [],
                        CompletedWork: 0,
                        TotalWork: 0,
                        ProgressText: SourceCompletionProgressText(0, null),
                        ElapsedMilliseconds: ElapsedMilliseconds(),
                        IsComplete: true);
                }
                else if (!lastYieldWasComplete)
                {
                    var unfilteredCompletionItems = accumulated.ToMergedItems();
                    var completionItems = ApplyRequestedFilters(unfilteredCompletionItems, filters);
                    var completionFilteredCount = CountFilteredOut(unfilteredCompletionItems, completionItems);
                    yield return new PremiereSourceBatch(
                        name,
                        completionItems,
                        CompletedWork: lastItemBatch?.TotalWork is > 0 ? lastItemBatch.TotalWork : lastItemBatch?.CompletedWork,
                        TotalWork: lastItemBatch?.TotalWork,
                        ProgressText: SourceCompletionProgressText(completionItems.Count, lastItemBatch?.ProgressText, completionFilteredCount),
                        ElapsedMilliseconds: ElapsedMilliseconds(),
                        IsComplete: true,
                        UnmappedCount: CountUnverified(completionItems),
                        FilteredCount: completionFilteredCount);
                }

                yield break;
            }

            lastItemBatch = itemBatch;
            accumulated.AddRange(itemBatch?.Items ?? []);
            var unfilteredItems = accumulated.ToMergedItems();
            var filteredItems = ApplyRequestedFilters(unfilteredItems, filters);
            var filteredCount = CountFilteredOut(unfilteredItems, filteredItems);
            var isKnownWorkComplete = itemBatch is
            {
                CompletedWork: { } completedWork,
                TotalWork: { } totalWork
            } && completedWork >= totalWork;
            lastYieldWasComplete = isKnownWorkComplete;
            emitted = true;
            yield return new PremiereSourceBatch(
                name,
                filteredItems,
                CompletedWork: itemBatch?.CompletedWork,
                TotalWork: itemBatch?.TotalWork,
                ProgressText: isKnownWorkComplete
                    ? SourceCompletionProgressText(filteredItems.Count, itemBatch?.ProgressText, filteredCount)
                    : ProgressTextWithFilteredCount(itemBatch?.ProgressText, filteredCount),
                ElapsedMilliseconds: ElapsedMilliseconds(),
                IsComplete: isKnownWorkComplete,
                UnmappedCount: CountUnverified(filteredItems),
                FilteredCount: filteredCount);
        }
    }

    private static IReadOnlyList<PremiereItem> ApplyRequestedFilters(
        IReadOnlyList<PremiereItem> items,
        CalendarFilters? filters)
    {
        return filters is null ? items : PremiereFilter.Apply(items, filters);
    }

    private async Task<IReadOnlyList<PremiereItem>> HydrateCachedImdbRatingsAsync(
        IReadOnlyList<PremiereItem> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0 || (_imdbRatingsStore is null && _rottenTomatoesClient is null))
        {
            return items;
        }

        List<PremiereItem>? hydratedItems = null;
        var ratingsByImdbId = new Dictionary<string, ImdbRatingRecord?>(StringComparer.OrdinalIgnoreCase);
        var rottenTomatoesByItemKey = new Dictionary<string, RottenTomatoesScores>(StringComparer.Ordinal);
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];
            var hydratedItem = item;
            if (_imdbRatingsStore is not null && HasCachedImdbId(item))
            {
                var imdbId = item.ImdbId!.Trim();
                if (!ratingsByImdbId.TryGetValue(imdbId, out var rating))
                {
                    try
                    {
                        rating = await _imdbRatingsStore.GetByImdbIdAsync(imdbId, cancellationToken);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogWarning(ex, "Skipping cached IMDb dataset rating lookup for IMDb ID {ImdbId}.", imdbId);
                        rating = null;
                    }

                    ratingsByImdbId[imdbId] = rating;
                }

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
                var rottenTomatoesKey = RottenTomatoesHydrationKey(hydratedItem);
                if (!rottenTomatoesByItemKey.TryGetValue(rottenTomatoesKey, out var rottenTomatoesScores))
                {
                    rottenTomatoesScores = await GetRottenTomatoesScoresAsync(
                        hydratedItem.MediaType,
                        hydratedItem.Title,
                        hydratedItem.PremiereDate.Year,
                        hydratedItem.WikidataId,
                        cancellationToken,
                        forceRefresh: false);
                    rottenTomatoesByItemKey[rottenTomatoesKey] = rottenTomatoesScores;
                }

                if (rottenTomatoesScores.HasAnyScore)
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

    private static string RottenTomatoesHydrationKey(PremiereItem item)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{item.MediaType}:{item.Title}:{item.PremiereDate.Year}:{item.WikidataId}");
    }

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

        var merged = MergeSupplementalIdentityAndScores(selected, items);
        return merged with
        {
            SourceNames = sourceNames.Length > 0 ? sourceNames : selected.SourceNames,
            Sources = sources.Length > 0 ? sources : selected.Sources
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

        var merged = MergeSupplementalIdentityAndScores(target, [sourceItem]);
        return merged with
        {
            SourceNames = sourceNames,
            Sources = sources
        };
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
            .Where(IsFreshReusableEnrichment)
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
        return cachedItem with
        {
            Type = discoveredItem.Type,
            MediaType = discoveredItem.MediaType,
            TmdbId = discoveredItem.TmdbId,
            Title = discoveredItem.Title,
            OriginalTitle = CoalesceText(discoveredItem.OriginalTitle, cachedItem.OriginalTitle),
            PremiereDate = discoveredItem.PremiereDate,
            Overview = CoalesceText(discoveredItem.Overview, cachedItem.Overview),
            PosterUrl = CoalesceText(discoveredItem.PosterUrl, cachedItem.PosterUrl),
            BackdropUrl = CoalesceText(discoveredItem.BackdropUrl, cachedItem.BackdropUrl),
            ImageSource = discoveredItem.PosterUrl is not null ? discoveredItem.ImageSource : cachedItem.ImageSource,
            TmdbUrl = CoalesceText(discoveredItem.TmdbUrl, cachedItem.TmdbUrl),
            OriginalLanguage = CoalesceText(discoveredItem.OriginalLanguage, cachedItem.OriginalLanguage) ?? "",
            OriginCountries = discoveredItem.OriginCountries.Length > 0 ? discoveredItem.OriginCountries : cachedItem.OriginCountries,
            GenreIds = discoveredItem.GenreIds.Length > 0 ? discoveredItem.GenreIds : cachedItem.GenreIds,
            EpisodeTitle = CoalesceText(discoveredItem.EpisodeTitle, cachedItem.EpisodeTitle),
            SeasonNumber = discoveredItem.SeasonNumber ?? cachedItem.SeasonNumber,
            EpisodeNumber = discoveredItem.EpisodeNumber ?? cachedItem.EpisodeNumber,
            EpisodeSource = CoalesceText(discoveredItem.EpisodeSource, cachedItem.EpisodeSource),
            TmdbScore = discoveredItem.TmdbScore ?? cachedItem.TmdbScore,
            TmdbVoteCount = discoveredItem.TmdbVoteCount ?? cachedItem.TmdbVoteCount
        };
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

    private sealed class PremiereMergeAccumulator
    {
        private readonly Dictionary<string, List<PremiereItem>> _itemsByCanonicalId = new(StringComparer.Ordinal);

        public void AddRange(IEnumerable<PremiereItem> items)
        {
            foreach (var item in items)
            {
                if (!_itemsByCanonicalId.TryGetValue(item.CanonicalId, out var existingItems))
                {
                    existingItems = [];
                    _itemsByCanonicalId[item.CanonicalId] = existingItems;
                }

                existingItems.Add(item);
            }
        }

        public List<PremiereItem> ToMergedItems()
        {
            if (_itemsByCanonicalId.Count == 0)
            {
                return [];
            }

            return MergePremiereItems(_itemsByCanonicalId.Values.SelectMany(static items => items));
        }
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
        int? filteredCount = null)
    {
        return new PremiereLoadProgress(
            sourceName,
            sourceItems.Count,
            allItems.Count,
            allItems,
            isFinal,
            fromCache,
            completedWork,
            totalWork,
            progressText,
            elapsedMilliseconds)
        {
            ProviderKey = ProviderKeyForSource(sourceName),
            Phase = isFinal || isSourceComplete ? "complete" : fromCache ? "cache" : "loading",
            SourceItems = sourceItems,
            HasSourceErrors = hasSourceErrors,
            FailedSourceNames = failedSourceNames ?? [],
            UnmappedCount = unmappedCount ?? CountUnverified(sourceItems),
            FilteredCount = filteredCount
        };
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

    private async Task<IReadOnlyList<PremiereItem>> GetSeriesAsync(
        DateOnly start,
        DateOnly end,
        PremiereDiscoveryCriteria criteria,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        var keywordIds = await SearchKeywordIdsAsync(criteria.Series.KeywordText, cancellationToken, forceRefresh);

        if (criteria.Series.SeriesDateMode == SeriesDateMode.AllEpisodes)
        {
            var dayResults = await Task.WhenAll(EachDay(start, end)
                .Select(day => GetSeriesEpisodesForDayAsync(day, criteria, keywordIds, cancellationToken, forceRefresh)));

            return dayResults.SelectMany(items => items).ToArray();
        }

        var languageRequestValues = LanguageRequestValues(criteria.Series);
        var rawItemGroups = await Task.WhenAll(languageRequestValues.Select(language =>
            _tmdbClient.DiscoverTvAsync(
                start,
                end,
                criteria.ToTmdbFilters(PremiereMediaType.Series, keywordIds, language),
                cancellationToken,
                forceRefresh)));
        var rawItems = rawItemGroups.SelectMany(items => items);

        return await MapWithLimitedConcurrencyAsync(
            rawItems,
            (item, token) => MapSeriesAsync(
                item,
                token,
                forceRefresh,
                requestedStart: start,
                requestedEnd: end,
                canonicalizeSeriesPremiereDate: true),
            cancellationToken);
    }

    private async IAsyncEnumerable<PremiereItemBatch> StreamSeriesBatchesAsync(
        DateOnly start,
        DateOnly end,
        PremiereDiscoveryCriteria criteria,
        string? language,
        bool forceRefresh,
        IReadOnlyDictionary<string, PremiereItem> cachedEnrichment,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var keywordIds = await SearchKeywordIdsAsync(criteria.Series.KeywordText, cancellationToken, forceRefresh);

        var completedRawItems = 0;
        await foreach (var rawBatch in _tmdbClient.StreamDiscoverTvAsync(
                start,
                end,
                criteria.ToTmdbFilters(PremiereMediaType.Series, keywordIds, language),
                cancellationToken,
                forceRefresh)
            .WithCancellation(cancellationToken))
        {
            var metadataItems = await MapWithLimitedConcurrencyAsync(
                rawBatch.Results,
                (item, token) => MapSeriesPremiereMetadataAsync(item, token, forceRefresh, start, end),
                cancellationToken);
            if (metadataItems.Count > 0)
            {
                yield return WithTmdbMetadataProgress(new PremiereItemBatch(metadataItems), rawBatch, completedRawItems);
            }

            await foreach (var mappedBatch in MapInProgressBatchesAsync(
                    rawBatch.Results,
                    (item, token) => MapSeriesAsync(
                        item,
                        token,
                        forceRefresh,
                        cachedEnrichment: cachedEnrichment,
                        requestedStart: start,
                        requestedEnd: end,
                        canonicalizeSeriesPremiereDate: true),
                    cancellationToken)
                .WithCancellation(cancellationToken))
            {
                yield return WithTmdbProgress(mappedBatch, rawBatch, completedRawItems);
            }

            completedRawItems += rawBatch.Results.Count;
        }
    }

    private async Task<IReadOnlyList<PremiereItem>> GetSeriesEpisodesForDayAsync(
        DateOnly day,
        PremiereDiscoveryCriteria criteria,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        var keywordIds = await SearchKeywordIdsAsync(criteria.Series.KeywordText, cancellationToken, forceRefresh);
        return await GetSeriesEpisodesForDayAsync(day, criteria, keywordIds, cancellationToken, forceRefresh);
    }

    private async Task<IReadOnlyList<PremiereItem>> GetSeriesEpisodesForDayAsync(
        DateOnly day,
        PremiereDiscoveryCriteria criteria,
        IReadOnlyList<int> keywordIds,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        var languageRequestValues = LanguageRequestValues(criteria.Series);
        var rawItemGroups = await Task.WhenAll(languageRequestValues.Select(language =>
            _tmdbClient.DiscoverTvAsync(
                day,
                day,
                criteria.ToTmdbFilters(PremiereMediaType.Series, keywordIds, language),
                cancellationToken,
                forceRefresh)));

        var rawItems = rawItemGroups
            .SelectMany(items => items)
            .Select(item => (Date: day, Item: item));

        return await MapWithLimitedConcurrencyAsync(
            rawItems,
            (result, token) => MapSeriesAsync(
                result.Item,
                token,
                forceRefresh,
                premiereDateOverride: result.Date,
                itemTypeOverride: PremiereItemType.SeriesEpisode,
                episodeSource: "TMDb air date"),
            cancellationToken);
    }

    private async IAsyncEnumerable<PremiereItemBatch> StreamSeriesEpisodeBatchesForDayAsync(
        DateOnly day,
        PremiereDiscoveryCriteria criteria,
        string? language,
        bool forceRefresh,
        IReadOnlyDictionary<string, PremiereItem> cachedEnrichment,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var keywordIds = await SearchKeywordIdsAsync(criteria.Series.KeywordText, cancellationToken, forceRefresh);

        var completedRawItems = 0;
        await foreach (var rawBatch in _tmdbClient.StreamDiscoverTvAsync(
                day,
                day,
                criteria.ToTmdbFilters(PremiereMediaType.Series, keywordIds, language),
                cancellationToken,
                forceRefresh)
            .WithCancellation(cancellationToken))
        {
            var metadataItems = rawBatch.Results
                .Select(item => MapSeriesMetadata(
                    item,
                    premiereDateOverride: day,
                    itemTypeOverride: PremiereItemType.SeriesEpisode,
                    episodeSource: "TMDb air date"))
                .Where(item => item is not null)
                .Select(item => item!)
                .ToArray();
            if (metadataItems.Length > 0)
            {
                yield return WithTmdbMetadataProgress(new PremiereItemBatch(metadataItems), rawBatch, completedRawItems);
            }

            var rawItems = rawBatch.Results.Select(item => (Date: day, Item: item));
            await foreach (var mappedBatch in MapInProgressBatchesAsync(
                    rawItems,
                    (result, token) => MapSeriesAsync(
                        result.Item,
                        token,
                        forceRefresh,
                        cachedEnrichment,
                        premiereDateOverride: result.Date,
                        itemTypeOverride: PremiereItemType.SeriesEpisode,
                        episodeSource: "TMDb air date"),
                    cancellationToken)
                .WithCancellation(cancellationToken))
            {
                yield return WithTmdbProgress(mappedBatch, rawBatch, completedRawItems);
            }

            completedRawItems += rawBatch.Results.Count;
        }
    }

    private async Task<IReadOnlyList<PremiereItem>> GetMoviesAsync(
        DateOnly start,
        DateOnly end,
        PremiereDiscoveryCriteria criteria,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        var keywordIds = await SearchKeywordIdsAsync(criteria.Movies.KeywordText, cancellationToken, forceRefresh);
        return await GetMoviesForDateRangeAsync(start, end, criteria, keywordIds, cancellationToken, forceRefresh);
    }

    private async Task<IReadOnlyList<PremiereItem>> GetMoviesForDateRangeAsync(
        DateOnly start,
        DateOnly end,
        PremiereDiscoveryCriteria criteria,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        var keywordIds = await SearchKeywordIdsAsync(criteria.Movies.KeywordText, cancellationToken, forceRefresh);
        return await GetMoviesForDateRangeAsync(start, end, criteria, keywordIds, cancellationToken, forceRefresh);
    }

    private async Task<IReadOnlyList<PremiereItem>> GetMoviesForDateRangeAsync(
        DateOnly start,
        DateOnly end,
        PremiereDiscoveryCriteria criteria,
        IReadOnlyList<int> keywordIds,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        var rawItemGroups = await Task.WhenAll(LanguageRequestValues(criteria.Movies).Select(language =>
            _tmdbClient.DiscoverMoviesAsync(
                start,
                end,
                criteria.ToTmdbFilters(PremiereMediaType.Movie, keywordIds, language),
                cancellationToken,
                forceRefresh)));
        var rawItems = rawItemGroups.SelectMany(items => items);

        return await MapWithLimitedConcurrencyAsync(
            rawItems,
            (item, token) => MapMovieAsync(item, token, forceRefresh),
            cancellationToken);
    }

    private async IAsyncEnumerable<PremiereItemBatch> StreamMovieBatchesForDateRangeAsync(
        DateOnly start,
        DateOnly end,
        PremiereDiscoveryCriteria criteria,
        string? language,
        bool forceRefresh,
        IReadOnlyDictionary<string, PremiereItem> cachedEnrichment,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var keywordIds = await SearchKeywordIdsAsync(criteria.Movies.KeywordText, cancellationToken, forceRefresh);

        var completedRawItems = 0;
        await foreach (var rawBatch in _tmdbClient.StreamDiscoverMoviesAsync(
                start,
                end,
                criteria.ToTmdbFilters(PremiereMediaType.Movie, keywordIds, language),
                cancellationToken,
                forceRefresh)
            .WithCancellation(cancellationToken))
        {
            var metadataItems = rawBatch.Results
                .Select(MapMovieMetadata)
                .Where(item => item is not null)
                .Select(item => item!)
                .ToArray();
            if (metadataItems.Length > 0)
            {
                yield return WithTmdbMetadataProgress(new PremiereItemBatch(metadataItems), rawBatch, completedRawItems);
            }

            await foreach (var mappedBatch in MapInProgressBatchesAsync(
                    rawBatch.Results,
                    (item, token) => MapMovieAsync(item, token, forceRefresh, cachedEnrichment),
                    cancellationToken)
                .WithCancellation(cancellationToken))
            {
                yield return WithTmdbProgress(mappedBatch, rawBatch, completedRawItems);
            }

            completedRawItems += rawBatch.Results.Count;
        }
    }


    private async Task<IReadOnlyList<PremiereItem>> GetExternalPremiereItemsAsync(
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken,
        bool forceRefresh,
        PremiereDiscoveryCriteria criteria)
    {
        if (_discoveryProviders.Count == 0)
        {
            return [];
        }

        var candidateTasks = _discoveryProviders.Select(provider =>
            GetCandidatesFromProviderAsync(provider, start, end, cancellationToken, forceRefresh));

        var candidateGroups = await Task.WhenAll(candidateTasks);
        var candidates = candidateGroups
            .SelectMany(group => group)
            .Where(candidate => candidate.PremiereDate >= start && candidate.PremiereDate <= end)
            .Where(candidate => candidate.MediaType != PremiereMediaType.Series || criteria.IncludeSeries)
            .Where(candidate => candidate.MediaType != PremiereMediaType.Movie || criteria.IncludeMovies)
            .Where(candidate => CandidateMatchesSeriesDateMode(candidate, criteria))
            .Where(candidate => CandidateMatchesKnownRequestFilters(candidate, criteria))
            .GroupBy(ExternalCandidateKey, StringComparer.OrdinalIgnoreCase)
            .Select(MergeExternalCandidateGroup)
            .ToArray();

        return await MapWithLimitedConcurrencyAsync(
            candidates,
            (candidate, token) => MapExternalCandidateAsync(
                candidate,
                token,
                forceRefresh,
                criteria,
                new Dictionary<string, PremiereItem>(StringComparer.Ordinal),
                start,
                end),
            cancellationToken);
    }

    private async IAsyncEnumerable<PremiereItemBatch> StreamExternalPremiereItemBatchesAsync(
        IPremiereDiscoveryProvider provider,
        DateOnly start,
        DateOnly end,
        bool forceRefresh,
        PremiereDiscoveryCriteria criteria,
        IReadOnlyDictionary<string, PremiereItem> cachedEnrichment,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var candidateBatchSize = Math.Clamp(_options.ExternalCandidateBatchSize, 1, 500);
        var seenCandidateKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingCandidates = new List<ExternalPremiereCandidate>(candidateBatchSize);
        var rawCandidateCount = 0;
        var acceptedCandidateCount = 0;
        var emitted = false;

        await foreach (var providerBatch in StreamCandidatesFromProviderAsync(provider, start, end, forceRefresh, cancellationToken)
            .WithCancellation(cancellationToken))
        {
            rawCandidateCount += providerBatch.Count;
            var candidates = providerBatch
                .Where(candidate => candidate.PremiereDate >= start && candidate.PremiereDate <= end)
                .Where(candidate => candidate.MediaType != PremiereMediaType.Series || criteria.IncludeSeries)
                .Where(candidate => candidate.MediaType != PremiereMediaType.Movie || criteria.IncludeMovies)
                .Where(candidate => CandidateMatchesSeriesDateMode(candidate, criteria))
                .Where(candidate => CandidateMatchesKnownRequestFilters(candidate, criteria))
                .GroupBy(ExternalCandidateKey, StringComparer.OrdinalIgnoreCase)
                .Select(MergeExternalCandidateGroup)
                .Where(candidate => seenCandidateKeys.Add(ExternalCandidateKey(candidate)))
                .ToArray();

            if (candidates.Length == 0)
            {
                continue;
            }

            acceptedCandidateCount += candidates.Length;
            pendingCandidates.AddRange(candidates);
            if (pendingCandidates.Count < candidateBatchSize)
            {
                continue;
            }

            await foreach (var mappedBatch in MapExternalCandidatesInProgressAsync(
                    pendingCandidates,
                    forceRefresh,
                    criteria,
                    cachedEnrichment,
                    start,
                    end,
                    cancellationToken)
                .WithCancellation(cancellationToken))
            {
                emitted = true;
                yield return mappedBatch;
            }

            pendingCandidates.Clear();
        }

        if (pendingCandidates.Count > 0)
        {
            await foreach (var mappedBatch in MapExternalCandidatesInProgressAsync(
                    pendingCandidates,
                    forceRefresh,
                    criteria,
                    cachedEnrichment,
                    start,
                    end,
                    cancellationToken)
                .WithCancellation(cancellationToken))
            {
                emitted = true;
                yield return mappedBatch;
            }
        }

        if (!emitted)
        {
            yield return EmptyExternalCandidateProgress(rawCandidateCount, acceptedCandidateCount);
        }
    }

    private static PremiereItemBatch EmptyExternalCandidateProgress(int rawCandidateCount, int acceptedCandidateCount)
    {
        if (rawCandidateCount == 0)
        {
            return new PremiereItemBatch([], 0, 0, "no candidates returned");
        }

        if (acceptedCandidateCount == 0)
        {
            return new PremiereItemBatch(
                [],
                rawCandidateCount,
                rawCandidateCount,
                $"0 of {rawCandidateCount:N0} candidates matched request filters");
        }

        return new PremiereItemBatch(
            [],
            acceptedCandidateCount,
            acceptedCandidateCount,
            $"0 of {acceptedCandidateCount:N0} accepted candidates resolved to cards");
    }

    private async IAsyncEnumerable<IReadOnlyList<ExternalPremiereCandidate>> StreamCandidatesFromProviderAsync(
        IPremiereDiscoveryProvider provider,
        DateOnly start,
        DateOnly end,
        bool forceRefresh,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (provider is IStreamingPremiereDiscoveryProvider streamingProvider)
        {
            await foreach (var batch in streamingProvider.StreamCandidatesAsync(start, end, forceRefresh, cancellationToken)
                .WithCancellation(cancellationToken))
            {
                yield return batch;
            }

            yield break;
        }

        yield return await GetCandidatesFromProviderAsync(provider, start, end, cancellationToken, forceRefresh);
    }

    private async IAsyncEnumerable<PremiereItemBatch> MapExternalCandidatesInProgressAsync(
        IReadOnlyList<ExternalPremiereCandidate> candidates,
        bool forceRefresh,
        PremiereDiscoveryCriteria criteria,
        IReadOnlyDictionary<string, PremiereItem> cachedEnrichment,
        DateOnly requestStart,
        DateOnly requestEnd,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var mappedBatch in MapInProgressBatchesAsync(
                candidates,
                (candidate, token) => MapExternalCandidateAsync(
                    candidate,
                    token,
                    forceRefresh,
                    criteria,
                    cachedEnrichment,
                    requestStart,
                    requestEnd),
                cancellationToken)
            .WithCancellation(cancellationToken))
        {
            yield return WithCandidateProgress(mappedBatch, candidates.Count);
        }
    }

    private async Task<int[]> SearchKeywordIdsAsync(string keywordText, CancellationToken cancellationToken, bool forceRefresh)
    {
        if (string.IsNullOrWhiteSpace(keywordText))
        {
            return [];
        }

        var keywords = await _tmdbClient.SearchKeywordsAsync(keywordText, cancellationToken, forceRefresh);
        return keywords
            .Where(keyword => keyword.Id > 0)
            .Select(keyword => keyword.Id)
            .Distinct()
            .Order()
            .ToArray();
    }

    private async Task<IReadOnlyList<ExternalPremiereCandidate>> GetCandidatesFromProviderAsync(
        IPremiereDiscoveryProvider provider,
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        try
        {
            return await provider.GetCandidatesAsync(start, end, cancellationToken, forceRefresh);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Skipping external discovery provider {ProviderType} for {StartDate} through {EndDate}.",
                provider.GetType().Name,
                start,
                end);

            return [];
        }
    }

    private async Task<PremiereItem?> MapExternalCandidateAsync(
        ExternalPremiereCandidate candidate,
        CancellationToken cancellationToken,
        bool forceRefresh,
        PremiereDiscoveryCriteria criteria,
        IReadOnlyDictionary<string, PremiereItem> cachedEnrichment,
        DateOnly requestStart,
        DateOnly requestEnd)
    {
        var canonicalCandidate = await CanonicalizeExternalSeriesPremiereCandidateAsync(
            candidate,
            criteria,
            requestStart,
            requestEnd,
            cancellationToken,
            forceRefresh);
        if (canonicalCandidate is null)
        {
            return null;
        }

        candidate = canonicalCandidate;
        if (TryReuseCachedExternalCandidate(cachedEnrichment, candidate, criteria, out var cachedCandidateItem))
        {
            return await HydrateExternalCandidateRatingsAsync(cachedCandidateItem, candidate, cancellationToken, forceRefresh);
        }

        var tmdbId = await ResolveCandidateTmdbIdAsync(candidate, cancellationToken, forceRefresh);
        if (tmdbId == ConflictingExternalIdsTmdbId)
        {
            return null;
        }

        if (tmdbId is not > 0)
        {
            return CreateUnverifiedPremiereItem(candidate, criteria);
        }

        var title = candidate.Title;
        if (string.IsNullOrWhiteSpace(title))
        {
            var details = candidate.MediaType == PremiereMediaType.Movie
                ? await TryGetDetailsAsync(
                    () => _tmdbClient.GetMovieDetailsAsync(tmdbId.Value, cancellationToken, forceRefresh),
                    candidate.MediaType,
                    tmdbId.Value,
                    cancellationToken)
                : await TryGetDetailsAsync(
                    () => _tmdbClient.GetTvDetailsAsync(tmdbId.Value, cancellationToken, forceRefresh),
                    candidate.MediaType,
                    tmdbId.Value,
                    cancellationToken);

            title = CoalesceText(details?.Title, details?.Name, details?.OriginalTitle, details?.OriginalName);
        }

        if (string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        var premiereDate = candidate.PremiereDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var mappedItem = candidate.MediaType == PremiereMediaType.Movie
            ? await MapMovieAsync(
                new TmdbMovieDiscoverItem
                {
                    Id = tmdbId.Value,
                    Title = title,
                    OriginalTitle = title,
                    ReleaseDate = premiereDate,
                    PrimaryReleaseDate = premiereDate,
                    OriginalLanguage = candidate.OriginalLanguage
                },
                cancellationToken,
                forceRefresh,
                cachedEnrichment,
                allowWatchmodeAvailabilityFallback: false)
            : await MapSeriesAsync(
                new TmdbTvDiscoverItem
                {
                    Id = tmdbId.Value,
                    Name = title,
                    OriginalName = title,
                    FirstAirDate = premiereDate,
                    OriginalLanguage = candidate.OriginalLanguage
                },
                cancellationToken,
                forceRefresh,
                cachedEnrichment,
                premiereDateOverride: candidate.PremiereDate,
                itemTypeOverride: criteria.Series.SeriesDateMode == SeriesDateMode.AllEpisodes
                    ? PremiereItemType.SeriesEpisode
                    : PremiereItemType.SeriesPremiere,
                episodeTitle: candidate.EpisodeTitle,
                seasonNumber: candidate.SeasonNumber,
                episodeNumber: candidate.EpisodeNumber,
                episodeSource: candidate.Source,
                allowWatchmodeAvailabilityFallback: false);

        if (mappedItem is null)
        {
            return null;
        }

        var mergedItem = MergeExternalCandidateSource(mappedItem, candidate);
        return await HydrateExternalCandidateRatingsAsync(mergedItem, candidate, cancellationToken, forceRefresh);
    }

    private async Task<ExternalPremiereCandidate?> CanonicalizeExternalSeriesPremiereCandidateAsync(
        ExternalPremiereCandidate candidate,
        PremiereDiscoveryCriteria criteria,
        DateOnly requestStart,
        DateOnly requestEnd,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        if (candidate.MediaType != PremiereMediaType.Series
            || criteria.Series.SeriesDateMode != SeriesDateMode.NewSeriesOnly
            || candidate.TmdbId is not > 0)
        {
            return candidate;
        }

        var canonicalDate = await GetSeasonOneEpisodeOneDateAsync(candidate.TmdbId.Value, cancellationToken, forceRefresh);
        if (canonicalDate is null)
        {
            return candidate;
        }

        if (canonicalDate < requestStart || canonicalDate > requestEnd)
        {
            return null;
        }

        return candidate with
        {
            PremiereDate = canonicalDate.Value,
            SeriesPremiereDate = canonicalDate.Value
        };
    }

    private static PremiereItem? CreateUnverifiedPremiereItem(
        ExternalPremiereCandidate candidate,
        PremiereDiscoveryCriteria criteria)
    {
        if (string.IsNullOrWhiteSpace(candidate.Title))
        {
            return null;
        }

        var type = candidate.MediaType == PremiereMediaType.Movie
            ? PremiereItemType.MovieFirstRelease
            : candidate.IsSeriesEpisode || criteria.Series.SeriesDateMode == SeriesDateMode.AllEpisodes
                ? PremiereItemType.SeriesEpisode
                : PremiereItemType.SeriesPremiere;
        var candidateKey = ExternalCandidateKey(candidate);
        var sourceNames = CandidateSourceNames(candidate);
        var posterUrl = CoalesceText(candidate.PosterUrl, candidate.BackdropUrl);

        return new PremiereItem
        {
            CanonicalId = UnverifiedCanonicalId(candidate, candidateKey),
            Type = type,
            MediaType = candidate.MediaType,
            TmdbId = 0,
            ImdbId = NormalizeExternalId(candidate.ImdbId),
            TvdbId = candidate.TvdbId,
            VerificationState = PremiereVerificationState.Unverified,
            VerificationNote = "Could not match to TMDb yet",
            ExternalProviderId = candidate.ExternalProviderId,
            ExternalUrl = candidate.ExternalUrl,
            ExternalCandidateKey = candidateKey,
            Title = candidate.Title.Trim(),
            PremiereDate = candidate.PremiereDate,
            PosterUrl = posterUrl,
            BackdropUrl = candidate.BackdropUrl,
            ImageSource = string.IsNullOrWhiteSpace(posterUrl)
                ? null
                : $"{sourceNames.FirstOrDefault() ?? candidate.Source} artwork",
            OriginalLanguage = candidate.OriginalLanguage ?? "",
            SourceNames = sourceNames,
            Sources = SourceEntriesWithCandidate([], candidate),
            EpisodeTitle = candidate.EpisodeTitle,
            SeasonNumber = candidate.SeasonNumber,
            EpisodeNumber = candidate.EpisodeNumber,
            EpisodeSource = candidate.Source,
            ImdbScore = candidate.ImdbScore,
            ImdbVoteCount = candidate.ImdbVoteCount,
            NetworkName = candidate.MediaType == PremiereMediaType.Series ? candidate.Source : null
        };
    }

    private static ExternalPremiereCandidate MergeExternalCandidateGroup(IGrouping<string, ExternalPremiereCandidate> group)
    {
        var candidates = group.ToArray();
        var selected = candidates
            .OrderBy(candidate => candidate.PremiereDate)
            .ThenByDescending(candidate => CandidateSourceNames(candidate).Length)
            .ThenBy(candidate => candidate.Title ?? "", StringComparer.OrdinalIgnoreCase)
            .First();
        var sourceNames = candidates
            .SelectMany(CandidateSourceNames)
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Select(source => source.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var title = candidates
            .OrderBy(candidate => string.IsNullOrWhiteSpace(candidate.Title))
            .ThenBy(candidate => candidate.PremiereDate)
            .Select(candidate => candidate.Title)
            .FirstOrDefault(title => !string.IsNullOrWhiteSpace(title));
        var posterUrl = candidates
            .Select(candidate => candidate.PosterUrl)
            .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));
        var backdropUrl = candidates
            .Select(candidate => candidate.BackdropUrl)
            .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));
        var externalUrl = candidates
            .Select(candidate => candidate.ExternalUrl)
            .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url));
        var externalProviderId = candidates
            .Select(candidate => candidate.ExternalProviderId)
            .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
        var imdbId = candidates
            .Select(candidate => NormalizeExternalId(candidate.ImdbId))
            .FirstOrDefault(id => !string.IsNullOrWhiteSpace(id));
        var tvdbId = candidates
            .Select(candidate => candidate.TvdbId)
            .FirstOrDefault(id => id is > 0);
        var imdbScore = candidates
            .Select(candidate => candidate.ImdbScore)
            .FirstOrDefault(score => score is not null);
        var imdbVoteCount = candidates
            .Select(candidate => candidate.ImdbVoteCount)
            .FirstOrDefault(votes => votes is not null);

        return selected with
        {
            Title = title ?? selected.Title,
            Source = sourceNames.FirstOrDefault() ?? selected.Source,
            SourceNames = sourceNames,
            PosterUrl = posterUrl ?? selected.PosterUrl,
            BackdropUrl = backdropUrl ?? selected.BackdropUrl,
            ExternalUrl = externalUrl ?? selected.ExternalUrl,
            ExternalProviderId = externalProviderId ?? selected.ExternalProviderId,
            ImdbId = imdbId ?? selected.ImdbId,
            TvdbId = tvdbId ?? selected.TvdbId,
            ImdbScore = imdbScore ?? selected.ImdbScore,
            ImdbVoteCount = imdbVoteCount ?? selected.ImdbVoteCount
        };
    }

    private static bool TryReuseCachedExternalCandidate(
        IReadOnlyDictionary<string, PremiereItem> cachedEnrichment,
        ExternalPremiereCandidate candidate,
        PremiereDiscoveryCriteria criteria,
        out PremiereItem reusedItem)
    {
        foreach (var cachedItem in cachedEnrichment.Values)
        {
            if (!IsFreshReusableEnrichment(cachedItem)
                || cachedItem.VerificationState != PremiereVerificationState.Verified
                || cachedItem.MediaType != candidate.MediaType
                || cachedItem.PremiereDate != candidate.PremiereDate
                || !ExternalCandidateMatchesCachedItem(candidate, cachedItem)
                || !ExternalEpisodeMatchesCachedItem(candidate, criteria, cachedItem))
            {
                continue;
            }

            reusedItem = MergeCachedExternalCandidate(cachedItem, candidate);
            return true;
        }

        reusedItem = new PremiereItem
        {
            MediaType = candidate.MediaType,
            TmdbId = 0,
            Title = candidate.Title ?? "",
            PremiereDate = candidate.PremiereDate
        };
        return false;
    }

    private static bool ExternalCandidateMatchesCachedItem(ExternalPremiereCandidate candidate, PremiereItem cachedItem)
    {
        return (candidate.TmdbId is > 0 && candidate.TmdbId == cachedItem.TmdbId)
            || (candidate.TvdbId is > 0 && candidate.TvdbId == cachedItem.TvdbId)
            || (!string.IsNullOrWhiteSpace(candidate.ImdbId)
                && string.Equals(candidate.ImdbId, cachedItem.ImdbId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ExternalEpisodeMatchesCachedItem(
        ExternalPremiereCandidate candidate,
        PremiereDiscoveryCriteria criteria,
        PremiereItem cachedItem)
    {
        if (candidate.MediaType != PremiereMediaType.Series
            || (!candidate.IsSeriesEpisode && criteria.Series.SeriesDateMode != SeriesDateMode.AllEpisodes))
        {
            return true;
        }

        if (criteria.Series.SeriesDateMode == SeriesDateMode.NewSeriesOnly
            && IsSeasonOneEpisodeOne(candidate))
        {
            if (candidate.SeriesPremiereDate is { } canonicalPremiereDate
                && canonicalPremiereDate != candidate.PremiereDate)
            {
                return false;
            }

            return cachedItem.Type == PremiereItemType.SeriesPremiere
                || cachedItem is
                {
                    Type: PremiereItemType.SeriesEpisode,
                    SeasonNumber: 1,
                    EpisodeNumber: 1
                };
        }

        if (candidate.SeasonNumber is > 0 && candidate.EpisodeNumber is > 0)
        {
            return candidate.SeasonNumber == cachedItem.SeasonNumber
                && candidate.EpisodeNumber == cachedItem.EpisodeNumber;
        }

        return cachedItem.Type == PremiereItemType.SeriesEpisode;
    }

    private static bool CandidateMatchesSeriesDateMode(
        ExternalPremiereCandidate candidate,
        PremiereDiscoveryCriteria criteria)
    {
        if (candidate.MediaType != PremiereMediaType.Series
            || criteria.Series.SeriesDateMode == SeriesDateMode.AllEpisodes
            || !candidate.IsSeriesEpisode)
        {
            return true;
        }

        return IsSeasonOneEpisodeOne(candidate)
            && (candidate.SeriesPremiereDate is null || candidate.SeriesPremiereDate == candidate.PremiereDate);
    }

    private static bool IsSeasonOneEpisodeOne(ExternalPremiereCandidate candidate)
    {
        return candidate is
        {
            SeasonNumber: 1,
            EpisodeNumber: 1
        };
    }

    private static PremiereItem MergeCachedExternalCandidate(
        PremiereItem cachedItem,
        ExternalPremiereCandidate candidate)
    {
        var sourceNames = SourceNamesWithCandidate(cachedItem.SourceNames, candidate);
        var candidateImdbId = NormalizeExternalId(candidate.ImdbId);
        var imdbId = CoalesceText(cachedItem.ImdbId, candidateImdbId);
        return cachedItem with
        {
            ImdbId = imdbId,
            ImdbUrl = CoalesceText(cachedItem.ImdbUrl, BuildImdbUrl(imdbId)),
            TvdbId = cachedItem.TvdbId ?? candidate.TvdbId,
            Title = CoalesceText(candidate.Title, cachedItem.Title) ?? cachedItem.Title,
            PremiereDate = candidate.PremiereDate,
            EpisodeTitle = CoalesceText(candidate.EpisodeTitle, cachedItem.EpisodeTitle),
            SeasonNumber = candidate.SeasonNumber ?? cachedItem.SeasonNumber,
            EpisodeNumber = candidate.EpisodeNumber ?? cachedItem.EpisodeNumber,
            EpisodeSource = CoalesceText(candidate.Source, cachedItem.EpisodeSource),
            ImdbScore = cachedItem.ImdbScore ?? candidate.ImdbScore,
            ImdbVoteCount = cachedItem.ImdbVoteCount ?? candidate.ImdbVoteCount,
            SourceNames = sourceNames,
            Sources = SourceEntriesWithCandidate(cachedItem.Sources, candidate),
            NetworkName = CoalesceText(candidate.Source, cachedItem.NetworkName)
        };
    }

    private static PremiereItem MergeExternalCandidateSource(
        PremiereItem item,
        ExternalPremiereCandidate candidate)
    {
        var candidateImdbId = NormalizeExternalId(candidate.ImdbId);
        var imdbId = CoalesceText(item.ImdbId, candidateImdbId);
        return item with
        {
            ImdbId = imdbId,
            ImdbUrl = CoalesceText(item.ImdbUrl, BuildImdbUrl(imdbId)),
            TvdbId = item.TvdbId ?? candidate.TvdbId,
            ImdbScore = item.ImdbScore ?? candidate.ImdbScore,
            ImdbVoteCount = item.ImdbVoteCount ?? candidate.ImdbVoteCount,
            SourceNames = SourceNamesWithCandidate(item.SourceNames, candidate),
            Sources = SourceEntriesWithCandidate(item.Sources, candidate)
        };
    }

    private async Task<PremiereItem> HydrateExternalCandidateRatingsAsync(
        PremiereItem item,
        ExternalPremiereCandidate candidate,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        var candidateImdbId = NormalizeExternalId(candidate.ImdbId);
        if (string.IsNullOrWhiteSpace(candidateImdbId)
            || !string.Equals(candidateImdbId, item.ImdbId, StringComparison.OrdinalIgnoreCase))
        {
            return item;
        }

        var ratings = await GetExternalRatingsAsync(candidateImdbId, cancellationToken, forceRefresh);
        var rottenTomatoesScores = await GetRottenTomatoesScoresAsync(
            candidate.MediaType,
            CoalesceText(candidate.Title, item.Title) ?? item.Title,
            candidate.ReleaseYear ?? item.PremiereDate.Year,
            item.WikidataId,
            cancellationToken,
            forceRefresh);
        return item with
        {
            ImdbScore = ratings.ImdbScore ?? item.ImdbScore,
            ImdbVoteCount = ratings.ImdbVoteCount ?? item.ImdbVoteCount,
            RottenTomatoesScore = ratings.RottenTomatoesScore ?? rottenTomatoesScores.CriticScore ?? item.RottenTomatoesScore,
            RottenTomatoesAudienceScore = ratings.RottenTomatoesAudienceScore ?? rottenTomatoesScores.AudienceScore ?? item.RottenTomatoesAudienceScore,
            MetacriticScore = ratings.MetacriticScore ?? item.MetacriticScore,
            Overview = CoalesceText(item.Overview, ratings.Plot),
            PosterUrl = CoalesceText(item.PosterUrl, ratings.PosterUrl)
        };
    }

    private static string[] SourceNamesWithCandidate(IReadOnlyList<string> sourceNames, ExternalPremiereCandidate candidate)
    {
        return CandidateSourceNames(candidate)
            .Concat(sourceNames)
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] SourceNamesWithCandidate(IReadOnlyList<string> sourceNames, string? candidateSource)
    {
        return (string.IsNullOrWhiteSpace(candidateSource) ? Enumerable.Empty<string>() : [candidateSource.Trim()])
            .Concat(sourceNames)
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static PremiereSource[] SourceEntriesWithCandidate(
        IReadOnlyList<PremiereSource> sources,
        ExternalPremiereCandidate candidate)
    {
        return SourceEntriesWithCandidate(sources, CandidateSourceNames(candidate));
    }

    private static PremiereSource[] SourceEntriesWithCandidate(
        IReadOnlyList<PremiereSource> sources,
        string? candidateSource)
    {
        return SourceEntriesWithCandidate(
            sources,
            string.IsNullOrWhiteSpace(candidateSource) ? [] : [candidateSource.Trim()]);
    }

    private static PremiereSource[] SourceEntriesWithCandidate(
        IReadOnlyList<PremiereSource> sources,
        IReadOnlyList<string> candidateSources)
    {
        var candidateEntries = candidateSources
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Select(source =>
            {
                return new PremiereSource
                {
                    Name = source.Trim(),
                    Kind = "schedule"
                };
            });

        return candidateEntries
            .Concat(sources)
            .Where(source => !string.IsNullOrWhiteSpace(source.Name))
            .DistinctBy(source => $"{source.Kind}:{source.Id}:{source.Name}", StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] CandidateSourceNames(ExternalPremiereCandidate candidate)
    {
        return (candidate.SourceNames is { Count: > 0 }
                ? candidate.SourceNames
                : [candidate.Source])
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Select(source => source.Trim())
            .Where(IsDisplayableCandidateSource)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsDisplayableCandidateSource(string source)
    {
        return !string.Equals(source, "Trakt", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(source, "Simkl", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<int?> ResolveCandidateTmdbIdAsync(
        ExternalPremiereCandidate candidate,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        if (candidate.TmdbId is > 0)
        {
            return candidate.TmdbId.Value;
        }

        var matches = new List<(string Source, int Id)>();
        if (candidate.MediaType == PremiereMediaType.Series && candidate.TvdbId is > 0)
        {
            var tvdbMatch = await TryFindTmdbIdAsync(
                candidate.MediaType,
                candidate.TvdbId.Value.ToString(CultureInfo.InvariantCulture),
                "tvdb_id",
                candidate.Source,
                cancellationToken,
                forceRefresh);

            if (tvdbMatch is > 0)
            {
                matches.Add(("tvdb_id", tvdbMatch.Value));
            }
        }

        if (!string.IsNullOrWhiteSpace(candidate.ImdbId))
        {
            var imdbMatch = await TryFindTmdbIdAsync(
                candidate.MediaType,
                candidate.ImdbId,
                "imdb_id",
                candidate.Source,
                cancellationToken,
                forceRefresh);
            if (imdbMatch is > 0)
            {
                matches.Add(("imdb_id", imdbMatch.Value));
            }
        }

        var distinctIds = matches.Select(match => match.Id).Distinct().ToArray();
        if (distinctIds.Length == 0)
        {
            return await TryResolveCandidateByStrictTitleAsync(candidate, cancellationToken, forceRefresh);
        }

        if (distinctIds.Length > 1)
        {
            _logger.LogWarning(
                "Skipping {ProviderName} candidate {Title} because external IDs resolved to multiple TMDb IDs: {ResolvedIds}.",
                candidate.Source,
                candidate.Title,
                string.Join(", ", matches.Select(match => $"{match.Source}:{match.Id}")));
            return ConflictingExternalIdsTmdbId;
        }

        return distinctIds[0];
    }

    private async Task<int?> TryResolveCandidateByStrictTitleAsync(
        ExternalPremiereCandidate candidate,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        if (string.IsNullOrWhiteSpace(candidate.Title))
        {
            return null;
        }

        var candidateYear = CandidateYear(candidate);
        try
        {
            var results = await _tmdbClient.SearchTitlesAsync(
                candidate.MediaType,
                candidate.Title,
                candidateYear,
                cancellationToken,
                forceRefresh);
            var candidateTitleKey = NormalizeTitleForIdentity(candidate.Title);
            var matchingIds = results
                .Where(result => TitleSearchResultMatches(candidate.MediaType, result, candidateTitleKey, candidateYear))
                .Select(result => result.Id)
                .Where(id => id > 0)
                .Distinct()
                .ToArray();

            if (matchingIds.Length == 1)
            {
                return matchingIds[0];
            }

            if (matchingIds.Length > 1)
            {
                _logger.LogInformation(
                    "Keeping {ProviderName} candidate {Title} unverified because strict TMDb title search returned multiple exact {Year} matches.",
                    candidate.Source,
                    candidate.Title,
                    candidateYear);
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Skipping strict TMDb title lookup for {ProviderName} candidate {Title} after a request timeout.",
                candidate.Source,
                candidate.Title);
        }
        catch (ExternalApiException ex)
        {
            _logger.LogWarning(
                ex,
                "Skipping strict TMDb title lookup for {ProviderName} candidate {Title}.",
                candidate.Source,
                candidate.Title);
        }

        return null;
    }

    private static int CandidateYear(ExternalPremiereCandidate candidate)
    {
        return candidate.ReleaseYear
            ?? candidate.SeriesPremiereDate?.Year
            ?? candidate.PremiereDate.Year;
    }

    private static bool TitleSearchResultMatches(
        PremiereMediaType mediaType,
        TmdbTitleSearchResult result,
        string candidateTitleKey,
        int candidateYear)
    {
        if (result.Id <= 0 || string.IsNullOrWhiteSpace(candidateTitleKey))
        {
            return false;
        }

        var title = mediaType == PremiereMediaType.Movie
            ? result.Title
            : result.Name;
        var originalTitle = mediaType == PremiereMediaType.Movie
            ? result.OriginalTitle
            : result.OriginalName;
        var dateText = mediaType == PremiereMediaType.Movie
            ? result.ReleaseDate
            : result.FirstAirDate;

        return (string.Equals(NormalizeTitleForIdentity(title), candidateTitleKey, StringComparison.Ordinal)
                || string.Equals(NormalizeTitleForIdentity(originalTitle), candidateTitleKey, StringComparison.Ordinal))
            && TryParseTmdbDate(dateText, out var resultDate)
            && resultDate.Year == candidateYear;
    }

    private async Task<int?> TryFindTmdbIdAsync(
        PremiereMediaType mediaType,
        string externalId,
        string externalSource,
        string providerName,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        try
        {
            return await _tmdbClient.FindTmdbIdByExternalIdAsync(
                mediaType,
                externalId,
                externalSource,
                cancellationToken,
                forceRefresh);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Skipping {ProviderName} candidate because TMDb lookup for {ExternalSource}:{ExternalId} timed out.",
                providerName,
                externalSource,
                externalId);

            return null;
        }
        catch (ExternalApiException ex)
        {
            _logger.LogWarning(
                ex,
                "Skipping {ProviderName} candidate because TMDb could not resolve {ExternalSource}:{ExternalId}.",
                providerName,
                externalSource,
                externalId);

            return null;
        }
    }

    private static string ExternalCandidateKey(ExternalPremiereCandidate candidate)
    {
        if (candidate.TmdbId is > 0)
        {
            return candidate.IsSeriesEpisode
                ? $"{candidate.MediaType}:tmdb:{candidate.TmdbId.Value}:{candidate.PremiereDate:yyyyMMdd}:{candidate.SeasonNumber}:{candidate.EpisodeNumber}"
                : $"{candidate.MediaType}:tmdb:{candidate.TmdbId.Value}";
        }

        if (candidate.MediaType == PremiereMediaType.Series && candidate.TvdbId is > 0)
        {
            return candidate.IsSeriesEpisode
                ? $"{candidate.MediaType}:tvdb:{candidate.TvdbId.Value}:{candidate.PremiereDate:yyyyMMdd}:{candidate.SeasonNumber}:{candidate.EpisodeNumber}"
                : $"{candidate.MediaType}:tvdb:{candidate.TvdbId.Value}";
        }

        if (!string.IsNullOrWhiteSpace(candidate.ImdbId))
        {
            return candidate.IsSeriesEpisode
                ? $"{candidate.MediaType}:imdb:{candidate.ImdbId}:{candidate.PremiereDate:yyyyMMdd}:{candidate.SeasonNumber}:{candidate.EpisodeNumber}"
                : $"{candidate.MediaType}:imdb:{candidate.ImdbId}";
        }

        if (!string.IsNullOrWhiteSpace(candidate.ExternalProviderId))
        {
            return candidate.IsSeriesEpisode
                ? $"{candidate.MediaType}:provider:{candidate.Source}:{candidate.ExternalProviderId}:{candidate.PremiereDate:yyyyMMdd}:{candidate.SeasonNumber}:{candidate.EpisodeNumber}"
                : $"{candidate.MediaType}:provider:{candidate.Source}:{candidate.ExternalProviderId}";
        }

        var year = CandidateYear(candidate);
        return $"{candidate.MediaType}:title:{candidate.PremiereDate:yyyyMMdd}:{year}:{NormalizeTitleForIdentity(candidate.Title)}";
    }

    private static string UnverifiedCanonicalId(ExternalPremiereCandidate candidate, string candidateKey)
    {
        var media = candidate.MediaType == PremiereMediaType.Movie ? "movie" : "series";
        var titleSegment = SlugForCanonicalId(candidate.Title);
        var hashBytes = SHA256.HashData(Encoding.UTF8.GetBytes(candidateKey));
        var hash = Convert.ToHexString(hashBytes)[..16].ToLowerInvariant();
        return $"unverified:{media}:{titleSegment}:{hash}";
    }

    private static string SlugForCanonicalId(string? value)
    {
        var normalized = NormalizeTitleForIdentity(value).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return "external";
        }

        return normalized.Length <= 48 ? normalized : normalized[..48];
    }

    private static string NormalizeTitleForIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Normalize(NormalizationForm.FormD))
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToUpperInvariant(character));
            }
        }

        return builder.ToString();
    }

    private static string? NormalizeExternalId(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool CandidateMatchesKnownRequestFilters(
        ExternalPremiereCandidate candidate,
        PremiereDiscoveryCriteria criteria)
    {
        var languageFilters = candidate.MediaType == PremiereMediaType.Series
            ? criteria.Series.OriginalLanguages
            : criteria.Movies.OriginalLanguages;

        if (languageFilters.Length > 0
            && !string.IsNullOrWhiteSpace(candidate.OriginalLanguage)
            && !languageFilters.Contains(candidate.OriginalLanguage, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private async Task<IReadOnlyList<PremiereItem>> MapWithLimitedConcurrencyAsync<T>(
        IEnumerable<T> rawItems,
        Func<T, CancellationToken, Task<PremiereItem?>> mapItem,
        CancellationToken cancellationToken)
    {
        var concurrency = Math.Clamp(_options.MaxEnrichmentConcurrency, 1, 32);
        using var gate = new SemaphoreSlim(concurrency);

        var tasks = rawItems.Select(async item =>
        {
            await gate.WaitAsync(cancellationToken);

            try
            {
                return await mapItem(item, cancellationToken);
            }
            finally
            {
                gate.Release();
            }
        });

        var mappedItems = await Task.WhenAll(tasks);
        return mappedItems.Where(item => item is not null).Select(item => item!).ToList();
    }

    private async IAsyncEnumerable<PremiereItemBatch> MapInProgressBatchesAsync<T>(
        IEnumerable<T> rawItems,
        Func<T, CancellationToken, Task<PremiereItem?>> mapItem,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var concurrency = Math.Clamp(_options.MaxEnrichmentConcurrency, 1, 32);
        var progressBatchSize = Math.Clamp(_options.EnrichmentProgressBatchSize, 1, 100);
        var rawItemList = rawItems as IReadOnlyCollection<T> ?? rawItems.ToArray();
        using var enumerator = rawItemList.GetEnumerator();
        using var mapperCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var mapToken = mapperCancellation.Token;
        var pending = new List<Task<PremiereItem?>>(concurrency);
        var batch = new List<PremiereItem>(progressBatchSize);
        var hasMore = true;
        var completedWork = 0;
        var totalWork = rawItemList.Count;
        var lastEmittedCompletedWork = 0;

        void StartPending()
        {
            while (hasMore && pending.Count < concurrency)
            {
                mapToken.ThrowIfCancellationRequested();
                if (!enumerator.MoveNext())
                {
                    hasMore = false;
                    break;
                }

                var item = enumerator.Current;
                pending.Add(mapItem(item, mapToken));
            }
        }

        try
        {
            StartPending();

            while (pending.Count > 0)
            {
                var completed = await Task.WhenAny(pending);
                pending.Remove(completed);

                PremiereItem? mapped;
                try
                {
                    mapped = await completed;
                }
                catch
                {
                    mapperCancellation.Cancel();
                    await ObservePendingMappingsAsync(pending);
                    throw;
                }

                completedWork++;
                if (mapped is not null)
                {
                    batch.Add(mapped);
                }

                StartPending();

                if (batch.Count >= progressBatchSize)
                {
                    lastEmittedCompletedWork = completedWork;
                    yield return new PremiereItemBatch(batch.ToArray(), completedWork, totalWork);
                    batch.Clear();
                }
            }

            if (batch.Count > 0)
            {
                lastEmittedCompletedWork = completedWork;
                yield return new PremiereItemBatch(batch.ToArray(), completedWork, totalWork);
                batch.Clear();
            }
            else if (totalWork > 0 && lastEmittedCompletedWork < completedWork)
            {
                yield return new PremiereItemBatch([], completedWork, totalWork);
            }
        }
        finally
        {
            mapperCancellation.Cancel();
            await ObservePendingMappingsAsync(pending);
        }
    }

    private static async Task ObservePendingMappingsAsync(IEnumerable<Task<PremiereItem?>> pending)
    {
        foreach (var task in pending)
        {
            try
            {
                await task;
            }
            catch
            {
            }
        }
    }

    private static PremiereItemBatch WithTmdbProgress<T>(
        PremiereItemBatch batch,
        TmdbDiscoverBatch<T> rawBatch,
        int completedRawItemsBeforeBatch)
    {
        var completed = Math.Max(0, completedRawItemsBeforeBatch + (batch.CompletedWork ?? rawBatch.Results.Count));
        var total = EstimateTmdbWork(rawBatch, completed);
        return batch with
        {
            CompletedWork = Math.Min(completed, total),
            TotalWork = total,
            ProgressText = $"{TmdbPageText(rawBatch)} · processed {Math.Min(completed, total):N0} of {total:N0} rows"
        };
    }

    private static PremiereItemBatch WithTmdbMetadataProgress<T>(
        PremiereItemBatch batch,
        TmdbDiscoverBatch<T> rawBatch,
        int completedRawItemsBeforeBatch)
    {
        var completed = Math.Max(0, completedRawItemsBeforeBatch + rawBatch.Results.Count);
        var total = EstimateTmdbWork(rawBatch, completed);
        return batch with
        {
            CompletedWork = Math.Min(completed, total),
            TotalWork = total,
            ProgressText = $"{TmdbPageText(rawBatch)} · metadata {Math.Min(completed, total):N0} of {total:N0} rows"
        };
    }

    private static PremiereItemBatch WithCandidateProgress(PremiereItemBatch batch, int candidateCount)
    {
        var total = Math.Max(0, candidateCount);
        var completed = Math.Clamp(batch.CompletedWork ?? total, 0, Math.Max(1, total));
        var unmapped = CountUnverified(batch.Items);
        var progressText = total == 1
            ? "resolved 1 of 1 candidate"
            : $"resolved {completed:N0} of {total:N0} candidates";
        if (unmapped > 0)
        {
            progressText = $"{progressText} · {unmapped:N0} unverified";
        }

        return batch with
        {
            CompletedWork = completed,
            TotalWork = total,
            ProgressText = progressText,
            UnmappedCount = unmapped
        };
    }

    private static string SourceCompletionProgressText(
        int itemCount,
        string? previousProgressText,
        int filteredCount = 0)
    {
        var summary = itemCount == 0 ? "Done - no matching cards" : "Done";
        var progressText = ProgressTextWithFilteredCount(previousProgressText, filteredCount);
        return string.IsNullOrWhiteSpace(progressText)
            ? summary
            : $"{summary} - {progressText}";
    }

    private static string? ProgressTextWithFilteredCount(string? progressText, int filteredCount)
    {
        if (filteredCount <= 0)
        {
            return progressText;
        }

        var filteredText = filteredCount == 1
            ? "1 filtered by active filters"
            : $"{filteredCount:N0} filtered by active filters";
        return string.IsNullOrWhiteSpace(progressText)
            ? filteredText
            : $"{progressText} · {filteredText}";
    }

    private static string SourceFailureProgressText(Exception error)
    {
        return error is OperationCanceledException
            ? "Skipped - source timed out"
            : "Skipped - source failed";
    }

    private static int EstimateTmdbWork<T>(TmdbDiscoverBatch<T> rawBatch, int completed)
    {
        var pageLimitedTotal = rawBatch.TotalPages > 0
            ? rawBatch.TotalPages * 20
            : rawBatch.TotalResults;
        var total = rawBatch.TotalResults > 0
            ? Math.Min(rawBatch.TotalResults, pageLimitedTotal)
            : rawBatch.Results.Count;

        return Math.Max(Math.Max(total, completed), 1);
    }

    private static string TmdbPageText<T>(TmdbDiscoverBatch<T> rawBatch)
    {
        var pageStart = Math.Max(1, rawBatch.PageStart);
        var pageEnd = Math.Max(pageStart, rawBatch.PageEnd);
        var totalPages = Math.Max(1, rawBatch.TotalPages);
        return $"pages {pageStart:N0}-{pageEnd:N0} of {totalPages:N0}";
    }

    private PremiereItem? MapSeriesMetadata(
        TmdbTvDiscoverItem item,
        DateOnly? premiereDateOverride = null,
        PremiereItemType? itemTypeOverride = null,
        string? episodeTitle = null,
        int? seasonNumber = null,
        int? episodeNumber = null,
        string? episodeSource = null)
    {
        var itemType = itemTypeOverride ?? PremiereItemType.SeriesPremiere;
        if (item.Id <= 0 || string.IsNullOrWhiteSpace(item.Name))
        {
            return null;
        }

        DateOnly premiereDate;
        if (premiereDateOverride is { } dateOverride)
        {
            premiereDate = dateOverride;
        }
        else if (!TryParseTmdbDate(item.FirstAirDate, out premiereDate))
        {
            return null;
        }

        var posterUrl = BuildImageUrl(_options.PosterSize, item.PosterPath);
        var backdropUrl = BuildImageUrl(_options.BackdropSize, item.BackdropPath);
        return new PremiereItem
        {
            CanonicalId = itemType == PremiereItemType.SeriesEpisode
                ? PremiereIdentity.SeriesEpisodeCanonicalId(item.Id, premiereDate, seasonNumber, episodeNumber)
                : PremiereIdentity.CanonicalId(PremiereMediaType.Series, item.Id),
            Type = itemType,
            MediaType = PremiereMediaType.Series,
            TmdbId = item.Id,
            Title = item.Name,
            OriginalTitle = item.OriginalName,
            PremiereDate = premiereDate,
            Overview = item.Overview,
            PosterUrl = posterUrl,
            BackdropUrl = backdropUrl,
            ImageSource = posterUrl is null ? null : "TMDb poster",
            TmdbUrl = $"https://www.themoviedb.org/tv/{item.Id}",
            OriginalLanguage = item.OriginalLanguage ?? "",
            OriginCountries = item.OriginCountry,
            GenreIds = item.GenreIds,
            EpisodeTitle = episodeTitle,
            SeasonNumber = seasonNumber,
            EpisodeNumber = episodeNumber,
            EpisodeSource = episodeSource,
            TmdbScore = item.VoteAverage,
            TmdbVoteCount = item.VoteCount
        };
    }

    private async Task<PremiereItem?> MapSeriesPremiereMetadataAsync(
        TmdbTvDiscoverItem item,
        CancellationToken cancellationToken,
        bool forceRefresh,
        DateOnly requestedStart,
        DateOnly requestedEnd)
    {
        var premiereDate = await GetSeasonOneEpisodeOneDateAsync(item.Id, cancellationToken, forceRefresh);
        if (premiereDate is { } canonicalDate
            && (canonicalDate < requestedStart || canonicalDate > requestedEnd))
        {
            return null;
        }

        return MapSeriesMetadata(item, premiereDateOverride: premiereDate);
    }

    private PremiereItem? MapMovieMetadata(TmdbMovieDiscoverItem item)
    {
        if (item.Id <= 0
            || string.IsNullOrWhiteSpace(item.Title)
            || !TryParseTmdbDate(CoalesceText(item.ReleaseDate, item.PrimaryReleaseDate), out var premiereDate))
        {
            return null;
        }

        var posterUrl = BuildImageUrl(_options.PosterSize, item.PosterPath);
        var backdropUrl = BuildImageUrl(_options.BackdropSize, item.BackdropPath);
        return new PremiereItem
        {
            CanonicalId = PremiereIdentity.CanonicalId(PremiereMediaType.Movie, item.Id),
            Type = PremiereIdentity.ItemType(PremiereMediaType.Movie),
            MediaType = PremiereMediaType.Movie,
            TmdbId = item.Id,
            Title = item.Title,
            OriginalTitle = item.OriginalTitle,
            PremiereDate = premiereDate,
            Overview = item.Overview,
            PosterUrl = posterUrl,
            BackdropUrl = backdropUrl,
            ImageSource = posterUrl is null ? null : "TMDb poster",
            TmdbUrl = $"https://www.themoviedb.org/movie/{item.Id}",
            OriginalLanguage = item.OriginalLanguage ?? "",
            OriginCountries = item.OriginCountry,
            GenreIds = item.GenreIds,
            TmdbScore = item.VoteAverage,
            TmdbVoteCount = item.VoteCount
        };
    }

    private async Task<PremiereItem?> MapSeriesAsync(
        TmdbTvDiscoverItem item,
        CancellationToken cancellationToken,
        bool forceRefresh,
        IReadOnlyDictionary<string, PremiereItem>? cachedEnrichment = null,
        DateOnly? premiereDateOverride = null,
        PremiereItemType? itemTypeOverride = null,
        string? episodeTitle = null,
        int? seasonNumber = null,
        int? episodeNumber = null,
        string? episodeSource = null,
        bool allowWatchmodeAvailabilityFallback = true,
        DateOnly? requestedStart = null,
        DateOnly? requestedEnd = null,
        bool canonicalizeSeriesPremiereDate = false)
    {
        var itemType = itemTypeOverride ?? PremiereItemType.SeriesPremiere;
        if (item.Id <= 0
            || string.IsNullOrWhiteSpace(item.Name))
        {
            return null;
        }

        DateOnly premiereDate;
        if (premiereDateOverride is { } dateOverride)
        {
            premiereDate = dateOverride;
        }
        else if (!TryParseTmdbDate(item.FirstAirDate, out premiereDate))
        {
            return null;
        }

        if (canonicalizeSeriesPremiereDate)
        {
            var canonicalDate = await GetSeasonOneEpisodeOneDateAsync(item.Id, cancellationToken, forceRefresh);
            if (canonicalDate is { } seasonOneEpisodeOneDate)
            {
                premiereDate = seasonOneEpisodeOneDate;
            }

            if (requestedStart is { } start && premiereDate < start)
            {
                return null;
            }

            if (requestedEnd is { } end && premiereDate > end)
            {
                return null;
            }
        }

        var discoveredItem = MapSeriesMetadata(
            item,
            premiereDate,
            itemType,
            episodeTitle,
            seasonNumber,
            episodeNumber,
            episodeSource);
        if (discoveredItem is not null && TryReuseCachedEnrichment(cachedEnrichment, discoveredItem, out var cachedItem))
        {
            return cachedItem;
        }

        var details = await TryGetDetailsAsync(
            () => _tmdbClient.GetTvDetailsAsync(item.Id, cancellationToken, forceRefresh),
            PremiereMediaType.Series,
            item.Id,
            cancellationToken);

        var ratingsTask = GetExternalRatingsAsync(details?.ExternalIds?.ImdbId, cancellationToken, forceRefresh);
        var tvmazeTask = GetTvSeriesEnrichmentAsync(details?.ExternalIds, item.Name, cancellationToken, forceRefresh);
        await Task.WhenAll(ratingsTask, tvmazeTask);
        var ratings = await ratingsTask;
        var tvmaze = await tvmazeTask;
        var rottenTomatoesScores = await GetRottenTomatoesScoresAsync(
            PremiereMediaType.Series,
            item.Name,
            premiereDate.Year,
            details?.ExternalIds?.WikidataId,
            cancellationToken,
            forceRefresh);
        var bestBackdropPath = CoalesceText(
            item.BackdropPath,
            details?.BackdropPath,
            SelectBestImagePath(details?.Images?.Backdrops));
        var tmdbPosterUrl = BuildImageUrl(
            _options.PosterSize,
            CoalesceText(item.PosterPath, details?.PosterPath, SelectBestImagePath(details?.Images?.Posters)));
        var tmdbBackdropUrl = BuildImageUrl(_options.BackdropSize, bestBackdropPath);
        var artwork = await ResolveArtworkAsync(
            tmdbPosterUrl,
            ratings.PosterUrl,
            tvmaze.ImageUrl,
            tmdbBackdropUrl,
            new ArtworkRequest(
                PremiereMediaType.Series,
                item.Id,
                details?.ExternalIds?.ImdbId,
                details?.ExternalIds?.TvdbId,
                details?.ExternalIds?.WikidataId,
                item.Name),
            cancellationToken,
            forceRefresh);
        var baseSources = SourceEntries(details, _options.SourceRegions, tvmaze.NetworkName, tvmaze.WebChannelName);
        var sources = allowWatchmodeAvailabilityFallback
            ? await SourceEntriesWithWatchmodeFallbackAsync(
                baseSources,
                PremiereMediaType.Series,
                item.Id,
                details?.ExternalIds?.ImdbId,
                cancellationToken,
                forceRefresh)
            : baseSources;
        var tmdbRuntime = details?.EpisodeRunTime.FirstOrDefault(runtime => runtime > 0);

        return new PremiereItem
        {
            CanonicalId = itemType == PremiereItemType.SeriesEpisode
                ? PremiereIdentity.SeriesEpisodeCanonicalId(item.Id, premiereDate, seasonNumber, episodeNumber)
                : PremiereIdentity.CanonicalId(PremiereMediaType.Series, item.Id),
            Type = itemType,
            MediaType = PremiereMediaType.Series,
            TmdbId = item.Id,
            ImdbId = details?.ExternalIds?.ImdbId,
            TvdbId = details?.ExternalIds?.TvdbId,
            WikidataId = details?.ExternalIds?.WikidataId,
            Title = CoalesceText(item.Name, details?.Name, details?.OriginalName) ?? item.Name,
            OriginalTitle = CoalesceText(item.OriginalName, details?.OriginalName),
            PremiereDate = premiereDate,
            Overview = CoalesceText(item.Overview, details?.Overview, ratings.Plot, tvmaze.Summary),
            PosterUrl = artwork?.Url,
            BackdropUrl = tmdbBackdropUrl,
            ImageSource = artwork?.Source,
            TrailerUrl = _trailerSelector.SelectBestYouTubeTrailer(details?.Videos?.Results),
            TmdbUrl = $"https://www.themoviedb.org/tv/{item.Id}",
            ImdbUrl = BuildImdbUrl(details?.ExternalIds?.ImdbId),
            OriginalLanguage = CoalesceText(item.OriginalLanguage, details?.OriginalLanguage) ?? "",
            OriginCountries = OriginCountriesOrFallback(details, item.OriginCountry),
            SourceNames = SourceNames(sources),
            Sources = sources,
            GenreIds = GenreIdsOrFallback(details, item.GenreIds),
            Genres = GenreNames(details),
            Keywords = KeywordNames(details?.Keywords),
            Certifications = TvCertifications(details, _options.SourceRegions),
            TvStatus = details?.Status,
            TvType = details?.TvType,
            EpisodeTitle = episodeTitle,
            SeasonNumber = seasonNumber,
            EpisodeNumber = episodeNumber,
            EpisodeSource = episodeSource,
            RuntimeMinutes = tmdbRuntime is > 0 ? tmdbRuntime : tvmaze.AverageRuntimeMinutes,
            TmdbScore = item.VoteAverage,
            TmdbVoteCount = item.VoteCount,
            ImdbScore = ratings.ImdbScore,
            ImdbVoteCount = ratings.ImdbVoteCount,
            RottenTomatoesScore = ratings.RottenTomatoesScore ?? rottenTomatoesScores.CriticScore,
            RottenTomatoesAudienceScore = ratings.RottenTomatoesAudienceScore ?? rottenTomatoesScores.AudienceScore,
            MetacriticScore = ratings.MetacriticScore,
            NetworkName = tvmaze.NetworkName,
            WebChannelName = tvmaze.WebChannelName,
            TvmazeAverageRuntimeMinutes = tvmaze.AverageRuntimeMinutes,
            TvmazeRating = tvmaze.TvmazeRating,
            OfficialSiteUrl = tvmaze.OfficialSiteUrl,
            TvmazeUrl = tvmaze.TvmazeUrl
        };
    }

    private async Task<PremiereItem?> MapMovieAsync(
        TmdbMovieDiscoverItem item,
        CancellationToken cancellationToken,
        bool forceRefresh,
        IReadOnlyDictionary<string, PremiereItem>? cachedEnrichment = null,
        bool allowWatchmodeAvailabilityFallback = true)
    {
        if (item.Id <= 0
            || string.IsNullOrWhiteSpace(item.Title)
            || !TryParseTmdbDate(CoalesceText(item.ReleaseDate, item.PrimaryReleaseDate), out var premiereDate))
        {
            return null;
        }

        var discoveredItem = MapMovieMetadata(item);
        if (discoveredItem is not null && TryReuseCachedEnrichment(cachedEnrichment, discoveredItem, out var cachedItem))
        {
            return cachedItem;
        }

        var details = await TryGetDetailsAsync(
            () => _tmdbClient.GetMovieDetailsAsync(item.Id, cancellationToken, forceRefresh),
            PremiereMediaType.Movie,
            item.Id,
            cancellationToken);

        var ratingsTask = GetExternalRatingsAsync(details?.ExternalIds?.ImdbId, cancellationToken, forceRefresh);
        var sourcesTask = allowWatchmodeAvailabilityFallback
            ? SourceEntriesWithWatchmodeFallbackAsync(
                SourceEntries(details, _options.SourceRegions),
                PremiereMediaType.Movie,
                item.Id,
                details?.ExternalIds?.ImdbId,
                cancellationToken,
                forceRefresh)
            : Task.FromResult(SourceEntries(details, _options.SourceRegions));
        var ratings = await ratingsTask;
        var rottenTomatoesScores = await GetRottenTomatoesScoresAsync(
            PremiereMediaType.Movie,
            item.Title,
            premiereDate.Year,
            details?.ExternalIds?.WikidataId,
            cancellationToken,
            forceRefresh);
        var bestBackdropPath = CoalesceText(
            item.BackdropPath,
            details?.BackdropPath,
            SelectBestImagePath(details?.Images?.Backdrops));
        var tmdbPosterUrl = BuildImageUrl(
            _options.PosterSize,
            CoalesceText(item.PosterPath, details?.PosterPath, SelectBestImagePath(details?.Images?.Posters)));
        var tmdbBackdropUrl = BuildImageUrl(_options.BackdropSize, bestBackdropPath);
        var artwork = await ResolveArtworkAsync(
            tmdbPosterUrl,
            ratings.PosterUrl,
            null,
            tmdbBackdropUrl,
            new ArtworkRequest(
                PremiereMediaType.Movie,
                item.Id,
                details?.ExternalIds?.ImdbId,
                details?.ExternalIds?.TvdbId,
                details?.ExternalIds?.WikidataId,
                item.Title),
            cancellationToken,
            forceRefresh);
        var sources = await sourcesTask;

        return new PremiereItem
        {
            CanonicalId = PremiereIdentity.CanonicalId(PremiereMediaType.Movie, item.Id),
            Type = PremiereIdentity.ItemType(PremiereMediaType.Movie),
            MediaType = PremiereMediaType.Movie,
            TmdbId = item.Id,
            ImdbId = details?.ExternalIds?.ImdbId,
            WikidataId = details?.ExternalIds?.WikidataId,
            Title = CoalesceText(item.Title, details?.Title, details?.OriginalTitle) ?? item.Title,
            OriginalTitle = CoalesceText(item.OriginalTitle, details?.OriginalTitle),
            PremiereDate = premiereDate,
            Overview = CoalesceText(item.Overview, details?.Overview, ratings.Plot),
            PosterUrl = artwork?.Url,
            BackdropUrl = tmdbBackdropUrl,
            ImageSource = artwork?.Source,
            TrailerUrl = _trailerSelector.SelectBestYouTubeTrailer(details?.Videos?.Results),
            TmdbUrl = $"https://www.themoviedb.org/movie/{item.Id}",
            ImdbUrl = BuildImdbUrl(details?.ExternalIds?.ImdbId),
            OriginalLanguage = CoalesceText(item.OriginalLanguage, details?.OriginalLanguage) ?? "",
            OriginCountries = ProductionCountriesOrFallback(details, item.OriginCountry),
            SourceNames = SourceNames(sources),
            Sources = sources,
            GenreIds = GenreIdsOrFallback(details, item.GenreIds),
            Genres = GenreNames(details),
            Keywords = KeywordNames(details?.Keywords),
            MovieReleaseTypes = MovieReleaseTypes(details, _options.SourceRegions),
            Certifications = MovieCertifications(details, _options.SourceRegions),
            RuntimeMinutes = details?.Runtime,
            TmdbScore = item.VoteAverage,
            TmdbVoteCount = item.VoteCount,
            ImdbScore = ratings.ImdbScore,
            ImdbVoteCount = ratings.ImdbVoteCount,
            RottenTomatoesScore = ratings.RottenTomatoesScore ?? rottenTomatoesScores.CriticScore,
            RottenTomatoesAudienceScore = ratings.RottenTomatoesAudienceScore ?? rottenTomatoesScores.AudienceScore,
            MetacriticScore = ratings.MetacriticScore
        };
    }

    private async Task<TmdbDetailsWithExtras?> TryGetDetailsAsync(
        Func<Task<TmdbDetailsWithExtras?>> getDetails,
        PremiereMediaType mediaType,
        int tmdbId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await getDetails();
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Skipping TMDb detail enrichment for {MediaType} {TmdbId} after a request timeout.", mediaType, tmdbId);
            return null;
        }
        catch (ExternalApiException ex)
        {
            _logger.LogWarning(ex, "Skipping TMDb detail enrichment for {MediaType} {TmdbId}.", mediaType, tmdbId);
            return null;
        }
    }

    private async Task<DateOnly?> GetSeasonOneEpisodeOneDateAsync(
        int tmdbId,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        var season = await TryGetSeasonDetailsAsync(
            () => _tmdbClient.GetTvSeasonDetailsAsync(tmdbId, 1, cancellationToken, forceRefresh),
            tmdbId,
            cancellationToken);
        var episode = season?.Episodes.FirstOrDefault(episode =>
            episode.SeasonNumber == 1 && episode.EpisodeNumber == 1);

        return TryParseTmdbDate(episode?.AirDate, out var airDate) ? airDate : null;
    }

    private async Task<TmdbSeasonDetails?> TryGetSeasonDetailsAsync(
        Func<Task<TmdbSeasonDetails?>> getDetails,
        int tmdbId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await getDetails();
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Skipping TMDb season detail enrichment for series {TmdbId} after a request timeout.", tmdbId);
            return null;
        }
        catch (ExternalApiException ex)
        {
            _logger.LogWarning(ex, "Skipping TMDb season detail enrichment for series {TmdbId}.", tmdbId);
            return null;
        }
    }

    private async Task<ExternalRatings> GetExternalRatingsAsync(string? imdbId, CancellationToken cancellationToken, bool forceRefresh)
    {
        if (string.IsNullOrWhiteSpace(imdbId))
        {
            return new ExternalRatings(null, null);
        }

        ImdbRatingRecord? imdbRating = null;
        if (_imdbRatingsStore is not null)
        {
            try
            {
                imdbRating = await _imdbRatingsStore.GetByImdbIdAsync(imdbId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Skipping IMDb dataset rating lookup for IMDb ID {ImdbId}.", imdbId);
            }
        }

        try
        {
            var omdbItem = await _omdbClient.GetByImdbIdAsync(imdbId, cancellationToken, forceRefresh);
            var omdbRatings = _ratingMapper.Map(omdbItem);
            return MergeExternalRatings(imdbRating, omdbRatings);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Skipping OMDb ratings enrichment for IMDb ID {ImdbId}.", imdbId);
            return MergeExternalRatings(imdbRating, new ExternalRatings(null, null));
        }
    }

    private async Task<RottenTomatoesScores> GetRottenTomatoesScoresAsync(
        PremiereMediaType mediaType,
        string? title,
        int? year,
        string? wikidataId,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        if (_rottenTomatoesClient is null || string.IsNullOrWhiteSpace(title))
        {
            return RottenTomatoesScores.Empty;
        }

        try
        {
            return await _rottenTomatoesClient.GetScoresAsync(
                mediaType,
                title,
                year,
                wikidataId,
                cancellationToken,
                forceRefresh);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Skipping Rotten Tomatoes enrichment for {MediaType} {Title}.", mediaType, title);
            return RottenTomatoesScores.Empty;
        }
    }

    private static ExternalRatings MergeExternalRatings(ImdbRatingRecord? imdbRating, ExternalRatings omdbRatings)
    {
        return imdbRating is null
            ? omdbRatings
            : omdbRatings with
            {
                ImdbScore = imdbRating.AverageRating,
                ImdbVoteCount = imdbRating.VoteCount
            };
    }

    private async Task<ArtworkCandidate?> ResolveArtworkAsync(
        string? tmdbPosterUrl,
        string? omdbPosterUrl,
        string? tvmazeEnrichmentImageUrl,
        string? tmdbBackdropUrl,
        ArtworkRequest request,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        var knownCover = ArtworkResolver.ResolveKnownCover(
            tmdbPosterUrl,
            omdbPosterUrl,
            tvmazeEnrichmentImageUrl);
        if (knownCover is not null)
        {
            return knownCover;
        }

        foreach (var provider in _artworkProviders)
        {
            var candidate = await GetArtworkCandidateFromProviderAsync(provider, request, cancellationToken, forceRefresh);
            if (candidate is not null && !string.IsNullOrWhiteSpace(candidate.Url))
            {
                return candidate;
            }
        }

        return string.IsNullOrWhiteSpace(tmdbBackdropUrl)
            ? null
            : new ArtworkCandidate(tmdbBackdropUrl, "TMDb backdrop");
    }

    private async Task<ArtworkCandidate?> GetArtworkCandidateFromProviderAsync(
        IArtworkProvider provider,
        ArtworkRequest request,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        try
        {
            return await provider.GetArtworkAsync(request, cancellationToken, forceRefresh);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Skipping artwork provider {ProviderType} for {MediaType} {TmdbId} after a request timeout.",
                provider.GetType().Name,
                request.MediaType,
                request.TmdbId);

            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Skipping artwork provider {ProviderType} for {MediaType} {TmdbId}.",
                provider.GetType().Name,
                request.MediaType,
                request.TmdbId);

            return null;
        }
    }

    private async Task<TvSeriesEnrichment> GetTvSeriesEnrichmentAsync(
        TmdbExternalIds? externalIds,
        string title,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        TvmazeShow? show = null;
        TvmazeShow? imageShow = null;
        if (externalIds?.TvdbId is not null || !string.IsNullOrWhiteSpace(externalIds?.ImdbId))
        {
            try
            {
                show = await _tvmazeClient.LookupShowAsync(externalIds.TvdbId, externalIds.ImdbId, cancellationToken, forceRefresh);
                if (show is not null && HasTvmazeImage(show))
                {
                    imageShow = show;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "Skipping TVmaze lookup enrichment for TVDB ID {TvdbId} / IMDb ID {ImdbId}.",
                    externalIds.TvdbId,
                    externalIds.ImdbId);
            }
        }

        if ((show is null || !HasTvmazeImage(show)) && !string.IsNullOrWhiteSpace(title))
        {
            TvmazeShow? titleMatch = null;
            try
            {
                titleMatch = await _tvmazeClient.SearchShowByNameAsync(title, cancellationToken, forceRefresh);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Skipping TVmaze title enrichment for {Title}.", title);
            }

            if (titleMatch is not null)
            {
                show ??= titleMatch;
                if (HasTvmazeImage(titleMatch))
                {
                    imageShow = titleMatch;
                }
            }
        }

        if (show is null)
        {
            return EmptyTvSeriesEnrichment;
        }

        return new TvSeriesEnrichment(
            show.Network?.Name,
            show.WebChannel?.Name,
            show.AverageRuntime ?? show.Runtime,
            show.Rating?.Average,
            show.OfficialSite,
            show.Url,
            StripHtml(show.Summary),
            imageShow?.Image?.Original ?? imageShow?.Image?.Medium ?? show.Image?.Original ?? show.Image?.Medium);
    }

    private static bool HasTvmazeImage(TvmazeShow show)
    {
        return !string.IsNullOrWhiteSpace(show.Image?.Original)
            || !string.IsNullOrWhiteSpace(show.Image?.Medium);
    }

    private string? BuildImageUrl(string size, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        return $"{_options.ImageBaseUrl.TrimEnd('/')}/{size.Trim('/')}/{path.TrimStart('/')}";
    }

    private static string[] ProductionCountriesOrFallback(TmdbDetailsWithExtras? details, string[] fallback)
    {
        var countries = details?.ProductionCountries
            .Select(country => country.Iso31661)
            .Where(country => !string.IsNullOrWhiteSpace(country))
            .Select(country => country!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return countries is { Length: > 0 } ? countries : fallback;
    }

    private static string[] OriginCountriesOrFallback(TmdbDetailsWithExtras? details, string[] fallback)
    {
        var countries = details?.OriginCountry
            .Where(country => !string.IsNullOrWhiteSpace(country))
            .Select(country => country.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return countries is { Length: > 0 } ? countries : fallback;
    }

    private static int[] GenreIdsOrFallback(TmdbDetailsWithExtras? details, int[] fallback)
    {
        var ids = details?.Genres
            .Where(genre => genre.Id > 0)
            .Select(genre => genre.Id)
            .Distinct()
            .ToArray();

        return ids is { Length: > 0 } ? ids : fallback;
    }

    private static string[] GenreNames(TmdbDetailsWithExtras? details)
    {
        return details?.Genres
            .Select(genre => genre.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray() ?? [];
    }

    private static string[] KeywordNames(TmdbKeywordResponse? keywords)
    {
        return (keywords?.Keywords ?? [])
            .Concat(keywords?.Results ?? [])
            .Select(keyword => keyword.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static PremiereSource[] SourceEntries(
        TmdbDetailsWithExtras? details,
        IReadOnlyList<string> sourceRegions,
        string? tvmazeNetworkName = null,
        string? tvmazeWebChannelName = null)
    {
        var sources = new List<PremiereSource>();

        AddSource(sources, tvmazeNetworkName, null, "network");
        AddSource(sources, tvmazeWebChannelName, null, "web");

        foreach (var network in details?.Networks ?? [])
        {
            AddSource(sources, network.Name, network.Id > 0 ? network.Id : null, "network");
        }

        sources.AddRange(WatchProviderEntries(details?.WatchProviders, sourceRegions));

        return sources
            .Where(source => !string.IsNullOrWhiteSpace(source.Name))
            .DistinctBy(source => $"{source.Kind}:{source.Id}:{source.Name}", StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<PremiereSource[]> SourceEntriesWithWatchmodeFallbackAsync(
        PremiereSource[] sources,
        PremiereMediaType mediaType,
        int tmdbId,
        string? imdbId,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        if (HasWatchProviderSource(sources))
        {
            return sources;
        }

        IReadOnlyList<PremiereSource> watchmodeSources;
        try
        {
            watchmodeSources = await _watchmodeClient.GetTitleSourcesAsync(
                mediaType,
                tmdbId,
                imdbId,
                _options.SourceRegions,
                cancellationToken,
                forceRefresh);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                ex,
                "Skipping Watchmode availability fallback for {MediaType} {TmdbId} after a request timeout.",
                mediaType,
                tmdbId);
            return sources;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "Skipping Watchmode availability fallback for {MediaType} {TmdbId}.",
                mediaType,
                tmdbId);
            return sources;
        }

        if (watchmodeSources.Count == 0)
        {
            return sources;
        }

        return sources
            .Concat(watchmodeSources)
            .Where(source => !string.IsNullOrWhiteSpace(source.Name))
            .DistinctBy(source => $"{source.Kind}:{source.Id}:{source.Name}", StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool HasWatchProviderSource(IEnumerable<PremiereSource> sources)
    {
        return sources.Any(source => source.Kind.Equals("flatrate", StringComparison.OrdinalIgnoreCase)
            || source.Kind.Equals("free", StringComparison.OrdinalIgnoreCase)
            || source.Kind.Equals("ads", StringComparison.OrdinalIgnoreCase)
            || source.Kind.Equals("buy", StringComparison.OrdinalIgnoreCase)
            || source.Kind.Equals("rent", StringComparison.OrdinalIgnoreCase));
    }

    private static string[] SourceNames(IEnumerable<PremiereSource> sources)
    {
        return sources
            .Select(source => source.Name)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddSource(List<PremiereSource> sources, string? name, int? id, string kind)
    {
        if (!string.IsNullOrWhiteSpace(name))
        {
            sources.Add(new PremiereSource
            {
                Name = name.Trim(),
                Id = id,
                Kind = kind
            });
        }
    }

    private static IEnumerable<PremiereSource> WatchProviderEntries(
        TmdbWatchProviders? watchProviders,
        IReadOnlyList<string> sourceRegions)
    {
        if (watchProviders?.Results is not { Count: > 0 } results)
        {
            yield break;
        }

        var preferredRegions = PreferredSourceRegions(sourceRegions)
            .Select(region => results.TryGetValue(region, out var providers) ? providers : null)
            .Where(region => region is not null)
            .Select(region => region!)
            .ToArray();

        var regions = preferredRegions.Length > 0
            ? preferredRegions
            : results.OrderBy(region => region.Key, StringComparer.OrdinalIgnoreCase).Select(region => region.Value);

        foreach (var region in regions)
        {
            foreach (var provider in WatchProvidersFor(region))
            {
                yield return provider;
            }
        }
    }

    private static string[] PreferredSourceRegions(IReadOnlyList<string> sourceRegions)
    {
        return sourceRegions
            .Where(region => !string.IsNullOrWhiteSpace(region))
            .Select(region => region.Trim().ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<PremiereSource> WatchProvidersFor(TmdbWatchProviderRegion region)
    {
        return ProviderEntries(region.Flatrate, "flatrate")
            .Concat(ProviderEntries(region.Free, "free"))
            .Concat(ProviderEntries(region.Ads, "ads"))
            .Concat(ProviderEntries(region.Buy, "buy"))
            .Concat(ProviderEntries(region.Rent, "rent"));
    }

    private static IEnumerable<PremiereSource> ProviderEntries(IEnumerable<TmdbWatchProvider> providers, string kind)
    {
        return OrderedProviders(providers)
            .Select(provider => new PremiereSource
            {
                Name = provider.ProviderName!,
                Id = provider.ProviderId > 0 ? provider.ProviderId : null,
                Kind = kind
            });
    }

    private static IEnumerable<TmdbWatchProvider> OrderedProviders(IEnumerable<TmdbWatchProvider> providers)
    {
        return providers
            .Where(provider => !string.IsNullOrWhiteSpace(provider.ProviderName))
            .OrderBy(provider => provider.DisplayPriority ?? int.MaxValue)
            .ThenBy(provider => provider.ProviderName, StringComparer.OrdinalIgnoreCase);
    }

    private static int[] MovieReleaseTypes(TmdbDetailsWithExtras? details, IReadOnlyList<string> sourceRegions)
    {
        return PreferredMovieReleaseDateRegions(details?.ReleaseDates, sourceRegions)
            .SelectMany(region => region.ReleaseDates)
            .Where(releaseDate => releaseDate.Type > 0)
            .Select(releaseDate => releaseDate.Type)
            .Distinct()
            .Order()
            .ToArray();
    }

    private static string[] MovieCertifications(TmdbDetailsWithExtras? details, IReadOnlyList<string> sourceRegions)
    {
        return PreferredMovieReleaseDateRegions(details?.ReleaseDates, sourceRegions)
            .SelectMany(region => region.ReleaseDates.Select(releaseDate => CertificationValue(region.Iso31661, releaseDate.Certification)))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] TvCertifications(TmdbDetailsWithExtras? details, IReadOnlyList<string> sourceRegions)
    {
        if (details?.ContentRatings?.Results is not { Count: > 0 } results)
        {
            return [];
        }

        var preferredRegions = PreferredSourceRegions(sourceRegions);
        var preferred = preferredRegions.Length == 0
            ? Array.Empty<TmdbTvContentRating>()
            : preferredRegions
                .SelectMany(region => results.Where(rating => string.Equals(rating.Iso31661, region, StringComparison.OrdinalIgnoreCase)))
                .ToArray();

        var ratings = preferred.Length > 0 ? preferred : results.ToArray();
        return ratings
            .Select(rating => CertificationValue(rating.Iso31661, rating.Rating))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<TmdbMovieReleaseDateRegion> PreferredMovieReleaseDateRegions(
        TmdbMovieReleaseDateResponse? releaseDates,
        IReadOnlyList<string> sourceRegions)
    {
        if (releaseDates?.Results is not { Count: > 0 } results)
        {
            return [];
        }

        var preferredRegions = PreferredSourceRegions(sourceRegions);
        var preferred = preferredRegions.Length == 0
            ? Array.Empty<TmdbMovieReleaseDateRegion>()
            : preferredRegions
                .SelectMany(region => results.Where(result => string.Equals(result.Iso31661, region, StringComparison.OrdinalIgnoreCase)))
                .ToArray();

        return preferred.Length > 0
            ? preferred
            : results;
    }

    private static string? CertificationValue(string? region, string? certification)
    {
        if (string.IsNullOrWhiteSpace(region) || string.IsNullOrWhiteSpace(certification))
        {
            return null;
        }

        return $"{region.Trim().ToUpperInvariant()}:{certification.Trim()}";
    }

    private static string? SelectBestImagePath(IEnumerable<TmdbImage>? images)
    {
        return images?
            .Where(image => !string.IsNullOrWhiteSpace(image.FilePath))
            .OrderByDescending(image => image.VoteCount ?? 0)
            .ThenByDescending(image => image.VoteAverage ?? 0)
            .Select(image => image.FilePath)
            .FirstOrDefault();
    }

    private static string? BuildImdbUrl(string? imdbId)
    {
        return string.IsNullOrWhiteSpace(imdbId)
            ? null
            : $"https://www.imdb.com/title/{Uri.EscapeDataString(imdbId)}/";
    }

    private static string? CoalesceText(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static string? StripHtml(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var withoutTags = System.Text.RegularExpressions.Regex.Replace(value, "<.*?>", " ");
        return System.Text.RegularExpressions.Regex.Replace(withoutTags, "\\s+", " ").Trim();
    }

    private static bool TryParseTmdbDate(string? value, out DateOnly date)
    {
        return DateOnly.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out date);
    }

    private static readonly TvSeriesEnrichment EmptyTvSeriesEnrichment = new(null, null, null, null, null, null, null, null);

    private sealed record SharedMediaCacheSnapshot(
        IReadOnlyList<PremiereItem>? SeriesItems,
        IReadOnlyList<PremiereItem>? MovieItems)
    {
        public bool HasSeries => SeriesItems is not null;
        public bool HasMovies => MovieItems is not null;
        public bool HasAny => HasSeries || HasMovies;
        public IReadOnlyList<PremiereItem> Items => (SeriesItems ?? [])
            .Concat(MovieItems ?? [])
            .ToArray();
    }

    private sealed class ActivePremiereSource(
        string key,
        string providerKey,
        IAsyncEnumerator<PremiereSourceBatch> enumerator,
        Task<bool> moveNextTask)
    {
        public string Key { get; } = key;
        public string ProviderKey { get; } = providerKey;
        public IAsyncEnumerator<PremiereSourceBatch> Enumerator { get; } = enumerator;
        public Task<bool> MoveNextTask { get; set; } = moveNextTask;
    }

    private static async ValueTask DisposeActiveSourceAsync(
        ActivePremiereSource source,
        CancellationToken cancellationToken)
    {
        if (!source.MoveNextTask.IsCompleted)
        {
            try
            {
                await source.MoveNextTask.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken);
            }
            catch (Exception ex) when (IsExpectedSourceShutdownException(ex))
            {
            }
        }

        try
        {
            await source.Enumerator.DisposeAsync();
        }
        catch (Exception ex) when (IsExpectedSourceShutdownException(ex))
        {
        }
    }

    private static bool IsExpectedSourceShutdownException(Exception ex)
    {
        return ex is OperationCanceledException
            or ObjectDisposedException
            or NotSupportedException;
    }

    private sealed record PremiereSourceFactory(
        string Key,
        string Name,
        DateOnly Start,
        DateOnly End,
        Func<IAsyncEnumerable<PremiereSourceBatch>> Open);

    private sealed record PremiereItemBatch(
        IReadOnlyList<PremiereItem> Items,
        int? CompletedWork = null,
        int? TotalWork = null,
        string? ProgressText = null,
        int? UnmappedCount = null);

    private sealed record PremiereSourceBatch(
        string Name,
        IReadOnlyList<PremiereItem> Items,
        Exception? Error = null,
        int? CompletedWork = null,
        int? TotalWork = null,
        string? ProgressText = null,
        long? ElapsedMilliseconds = null,
        bool IsComplete = false,
        int? UnmappedCount = null,
        int? FilteredCount = null);
}
