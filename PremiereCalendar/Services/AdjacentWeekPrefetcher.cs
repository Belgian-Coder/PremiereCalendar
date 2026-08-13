using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class AdjacentWeekPrefetcher : IAdjacentWeekPrefetcher, IHostedService, IDisposable
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHostApplicationLifetime _applicationLifetime;
    private readonly CalendarCacheOptions _options;
    private readonly ILogger<AdjacentWeekPrefetcher> _logger;
    private readonly CalendarLoadCoordinator? _loadCoordinator;
    private readonly BackgroundJobTimelineService? _timeline;
    private readonly IProviderWorkScheduler? _workScheduler;
    private readonly ConcurrentDictionary<string, byte> _scheduledWeeks = [];
    private readonly object _queueGate = new();
    private readonly PriorityQueue<PrefetchRequest, PrefetchPriority> _queue = new();
    private readonly Dictionary<string, PrefetchRequest> _pendingRequests = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _queueSignal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private long _nextSequence;
    private int _nextGeneration;
    private int _workerStarted;
    private Task? _workerTask;
    private volatile bool _disposed;

    public AdjacentWeekPrefetcher(
        IServiceScopeFactory scopeFactory,
        IHostApplicationLifetime applicationLifetime,
        IOptions<CalendarCacheOptions> options,
        ILogger<AdjacentWeekPrefetcher> logger,
        CalendarLoadCoordinator? loadCoordinator = null,
        BackgroundJobTimelineService? timeline = null,
        IProviderWorkScheduler? workScheduler = null)
    {
        _scopeFactory = scopeFactory;
        _applicationLifetime = applicationLifetime;
        _options = options.Value;
        _logger = logger;
        _loadCoordinator = loadCoordinator;
        _timeline = timeline;
        _workScheduler = workScheduler;
    }

    public void PrefetchAdjacentWeeks(DateOnly weekStart, CalendarFilters? filters = null)
    {
        if (!_options.Enabled || !_options.AdjacentWeekPrefetchEnabled)
        {
            return;
        }

        var filtersSnapshot = filters is null ? null : CloneFilters(filters);
        var generation = Interlocked.Increment(ref _nextGeneration);
        var rank = 0;
        if (_workScheduler is not null)
        {
            foreach (var offset in PrefetchWeekOffsets())
            {
                var target = weekStart.AddDays(offset * 7);
                _ = QueueDurablePrefetchAsync(target, filtersSnapshot, rank++);
            }
            return;
        }

        foreach (var offset in PrefetchWeekOffsets())
        {
            QueuePrefetch(weekStart.AddDays(offset * 7), filtersSnapshot, generation, rank++);
        }
    }

    private async Task QueueDurablePrefetchAsync(DateOnly weekStart, CalendarFilters? filters, int rank)
    {
        try
        {
            var criteria = PremiereDiscoveryCriteria.FromFilters(filters);
            var payload = new CalendarProviderWorkPayload(
                weekStart,
                weekStart.AddDays(6),
                false,
                filters is null ? null : CloneFilters(filters));
            await _workScheduler!.EnqueueAsync(new ProviderWorkRequest(
                ProviderWorkKind.AdjacentWeekPrefetch,
                $"prefetch:{weekStart:yyyyMMdd}:{criteria.CacheKey()}",
                (ProviderWorkPriority)Math.Min((int)ProviderWorkPriority.Maintenance - 1, (int)ProviderWorkPriority.Adjacent + rank),
                System.Text.Json.JsonSerializer.Serialize(payload)));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not persist adjacent week prefetch for {WeekStart}.", weekStart);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        EnsureWorkerStarted();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        var workerTask = _workerTask;
        if (workerTask is null)
        {
            return;
        }

        var completed = await Task.WhenAny(workerTask, Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        if (completed == workerTask)
        {
            await workerTask;
        }
    }

    private void QueuePrefetch(DateOnly weekStart, CalendarFilters? filters, int generation, int rank)
    {
        var key = $"{weekStart:yyyyMMdd}:{PremiereDiscoveryCriteria.FromFilters(filters).CacheKey()}";
        var request = new PrefetchRequest(
            weekStart,
            key,
            filters is null ? null : CloneFilters(filters),
            generation,
            rank,
            Interlocked.Increment(ref _nextSequence));

        var queued = false;
        lock (_queueGate)
        {
            if (_disposed)
            {
                return;
            }

            if (_pendingRequests.TryGetValue(key, out var pendingRequest))
            {
                if (request.Priority.CompareTo(pendingRequest.Priority) >= 0)
                {
                    return;
                }

                _pendingRequests[key] = request;
                _queue.Enqueue(request, request.Priority);
                queued = true;
            }
            else
            {
                if (!_scheduledWeeks.TryAdd(key, 0))
                {
                    return;
                }

                _pendingRequests[key] = request;
                _queue.Enqueue(request, request.Priority);
                queued = true;
            }
        }

        EnsureWorkerStarted();
        if (queued)
        {
            _queueSignal.Release();
        }
    }

    private IEnumerable<int> PrefetchWeekOffsets()
    {
        var futureWeeks = Math.Clamp(_options.FuturePrefetchWeeks, 0, 26);
        var pastWeeks = Math.Clamp(_options.PastPrefetchWeeks, 0, 26);

        if (futureWeeks >= 1)
        {
            yield return 1;
        }

        if (pastWeeks >= 1)
        {
            yield return -1;
        }

        for (var offset = 2; offset <= futureWeeks; offset++)
        {
            yield return offset;
        }

        for (var offset = 2; offset <= pastWeeks; offset++)
        {
            yield return -offset;
        }
    }

    private void EnsureWorkerStarted()
    {
        if (Interlocked.Exchange(ref _workerStarted, 1) == 1)
        {
            return;
        }

        _workerTask = Task.Run(ProcessQueueAsync, CancellationToken.None);
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            using var linkedShutdown = CancellationTokenSource.CreateLinkedTokenSource(
                _applicationLifetime.ApplicationStopping,
                _shutdown.Token);
            var workerToken = linkedShutdown.Token;
            while (!_disposed && !workerToken.IsCancellationRequested)
            {
                await _queueSignal.WaitAsync(workerToken);
                if (_disposed)
                {
                    break;
                }

                var request = TryDequeueNextRequest();
                if (request is null)
                {
                    continue;
                }

                await PrefetchWeekAsync(
                    request.WeekStart,
                    request.Key,
                    request.Filters,
                    workerToken);
            }
        }
        catch (OperationCanceledException) when (_applicationLifetime.ApplicationStopping.IsCancellationRequested
            || _shutdown.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (_disposed)
        {
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Adjacent week prefetch worker stopped unexpectedly.");
            Interlocked.Exchange(ref _workerStarted, 0);
        }
    }

    private PrefetchRequest? TryDequeueNextRequest()
    {
        lock (_queueGate)
        {
            while (_queue.TryDequeue(out var request, out _))
            {
                if (_pendingRequests.TryGetValue(request.Key, out var pendingRequest)
                    && pendingRequest.Sequence == request.Sequence)
                {
                    _pendingRequests.Remove(request.Key);
                    return request;
                }
            }
        }

        return null;
    }

    private async Task PrefetchWeekAsync(
        DateOnly weekStart,
        string key,
        CalendarFilters? filters,
        CancellationToken cancellationToken)
    {
        CalendarLoadCoordinator.BackgroundLoadLease? backgroundLoad = null;
        var startedUtc = DateTimeOffset.UtcNow;
        var startedTimestamp = TimeProvider.System.GetTimestamp();
        try
        {
            backgroundLoad = _loadCoordinator is null
                ? null
                : await _loadCoordinator.TryBeginBackgroundLoadAsync(
                    skipWhenForegroundActive: true,
                    cancellationToken);
            if (_loadCoordinator is not null && backgroundLoad is null)
            {
                return;
            }

            var runToken = backgroundLoad?.Token ?? cancellationToken;
            var timeoutSeconds = Math.Clamp(_options.AdjacentWeekPrefetchTimeoutSeconds, 1, 300);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var linkedTimeout = CancellationTokenSource.CreateLinkedTokenSource(runToken, timeout.Token);
            await using var scope = _scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<IPremiereService>();
            await RecordTimelineAsync(
                BackgroundJobStatus.Started,
                $"Started adjacent week prefetch for {weekStart:yyyy-MM-dd}.",
                startedUtc,
                null,
                cancellationToken);
            await service.GetPremieresAsync(weekStart, weekStart.AddDays(6), linkedTimeout.Token, filters: filters);
            await RecordTimelineAsync(
                BackgroundJobStatus.Succeeded,
                $"Finished adjacent week prefetch for {weekStart:yyyy-MM-dd}.",
                DateTimeOffset.UtcNow,
                TimeProvider.System.GetElapsedTime(startedTimestamp),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested
            || backgroundLoad?.Token.IsCancellationRequested == true)
        {
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug(
                "Adjacent week prefetch timed out for week starting {WeekStart} after {TimeoutSeconds}s.",
                weekStart,
                _options.AdjacentWeekPrefetchTimeoutSeconds);
            await RecordTimelineAsync(
                BackgroundJobStatus.Failed,
                $"Adjacent week prefetch timed out for {weekStart:yyyy-MM-dd}.",
                DateTimeOffset.UtcNow,
                TimeProvider.System.GetElapsedTime(startedTimestamp),
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Adjacent week prefetch failed for week starting {WeekStart}.", weekStart);
            await RecordTimelineAsync(
                BackgroundJobStatus.Failed,
                ex.Message,
                DateTimeOffset.UtcNow,
                TimeProvider.System.GetElapsedTime(startedTimestamp),
                CancellationToken.None);
        }
        finally
        {
            backgroundLoad?.Dispose();
            _scheduledWeeks.TryRemove(key, out _);
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
            await _timeline.RecordAsync("Adjacent week prefetch", status, message, occurredUtc, duration, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not record adjacent prefetch timeline event.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();
        _queueSignal.Release();
    }

    private static CalendarFilters CloneFilters(CalendarFilters source)
    {
        return new CalendarFilters
        {
            WeekStart = source.WeekStart,
            ShowSeries = source.ShowSeries,
            ShowMovies = source.ShowMovies,
            SortMode = source.SortMode,
            SortDirection = source.SortDirection,
            ScoreSource = source.ScoreSource,
            MinScore = source.MinScore,
            MaxScore = source.MaxScore,
            IncludeUnknownScores = source.IncludeUnknownScores,
            MinVoteCount = source.MinVoteCount,
            Language = source.Language,
            OriginGroup = source.OriginGroup,
            GenreIds = [.. source.GenreIds],
            SelectedSources = [.. source.SelectedSources],
            NetworkText = source.NetworkText,
            RuntimeMinMinutes = source.RuntimeMinMinutes,
            RuntimeMaxMinutes = source.RuntimeMaxMinutes,
            KeywordText = source.KeywordText,
            SearchText = source.SearchText,
            SeriesFilters = CloneMediaFilterSet(source.SeriesFilters),
            MovieFilters = CloneMediaFilterSet(source.MovieFilters)
        };
    }

    private static MediaFilterSet CloneMediaFilterSet(MediaFilterSet source)
    {
        return new MediaFilterSet
        {
            SeriesDateMode = source.SeriesDateMode,
            OriginalLanguages = [.. source.OriginalLanguages],
            OriginCountries = [.. source.OriginCountries],
            GenreIds = [.. source.GenreIds],
            SelectedSources = [.. source.SelectedSources],
            WatchRegion = source.WatchRegion,
            SourceText = source.SourceText,
            MonetizationTypes = [.. source.MonetizationTypes],
            MovieReleaseTypes = [.. source.MovieReleaseTypes],
            Certifications = [.. source.Certifications],
            CertificationCountry = source.CertificationCountry,
            TvStatuses = [.. source.TvStatuses],
            TvTypes = [.. source.TvTypes],
            RuntimeMinMinutes = source.RuntimeMinMinutes,
            RuntimeMaxMinutes = source.RuntimeMaxMinutes,
            KeywordText = source.KeywordText,
            SearchText = source.SearchText
        };
    }

    private sealed record PrefetchRequest(
        DateOnly WeekStart,
        string Key,
        CalendarFilters? Filters,
        int Generation,
        int Rank,
        long Sequence)
    {
        public PrefetchPriority Priority => new(-Generation, Rank, Sequence);
    }

    private readonly record struct PrefetchPriority(int GenerationRank, int Rank, long Sequence)
        : IComparable<PrefetchPriority>
    {
        public int CompareTo(PrefetchPriority other)
        {
            var generationComparison = GenerationRank.CompareTo(other.GenerationRank);
            if (generationComparison != 0)
            {
                return generationComparison;
            }

            var rankComparison = Rank.CompareTo(other.Rank);
            return rankComparison != 0 ? rankComparison : Sequence.CompareTo(other.Sequence);
        }
    }
}
