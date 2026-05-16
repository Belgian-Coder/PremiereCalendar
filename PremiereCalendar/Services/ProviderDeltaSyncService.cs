using Microsoft.Extensions.Options;
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

    public ProviderDeltaSyncService(
        ITmdbClient tmdbClient,
        ITvmazeClient tvmazeClient,
        IProviderCacheStateStore stateStore,
        IOptionsMonitor<ProviderDeltaSyncOptions> options,
        TimeProvider timeProvider,
        ILogger<ProviderDeltaSyncService> logger,
        BackgroundJobTimelineService? timeline = null)
    {
        _tmdbClient = tmdbClient;
        _tvmazeClient = tvmazeClient;
        _stateStore = stateStore;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _timeline = timeline;
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

                await RunOnceSafelyAsync(stoppingToken);
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(Math.Max(15, _options.CurrentValue.WakeIntervalMinutes)), stoppingToken);
                await RunOnceSafelyAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var nowUtc = _timeProvider.GetUtcNow();
        if (_options.CurrentValue.UseTmdbChanges)
        {
            await SyncTmdbChangesAsync(nowUtc, cancellationToken);
        }

        if (_options.CurrentValue.UseTvmazeUpdates)
        {
            await SyncTvmazeUpdatesAsync(nowUtc, cancellationToken);
        }
    }

    private async Task RunOnceSafelyAsync(CancellationToken cancellationToken)
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
            await RunOnceAsync(cancellationToken);
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

    private async Task SyncTmdbChangesAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        try
        {
            var end = DateOnly.FromDateTime(nowUtc.UtcDateTime);
            var lookbackDays = Math.Clamp(_options.CurrentValue.TmdbLookbackDays, 1, 14);
            var start = end.AddDays(-lookbackDays);
            await SyncTmdbMediaChangesAsync(PremiereMediaType.Movie, start, end, nowUtc, cancellationToken);
            await SyncTmdbMediaChangesAsync(PremiereMediaType.Series, start, end, nowUtc, cancellationToken);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "TMDb change tracking sync timed out or was canceled by an external dependency.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TMDb change tracking sync failed.");
        }
    }

    private async Task SyncTmdbMediaChangesAsync(
        PremiereMediaType mediaType,
        DateOnly start,
        DateOnly end,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var changes = mediaType == PremiereMediaType.Movie
            ? await _tmdbClient.GetChangedMovieIdsAsync(start, end, cancellationToken)
            : await _tmdbClient.GetChangedTvIdsAsync(start, end, cancellationToken);
        var keyPrefix = mediaType == PremiereMediaType.Movie ? "movie" : "tv";
        var watermark = end.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        var globalKey = $"{keyPrefix}-changes";

        await _stateStore.SaveAsync(
            new ProviderCacheState(
                "tmdb",
                ProviderCacheScope.Global,
                globalKey,
                nowUtc,
                nowUtc,
                watermark,
                changes.Count,
                null),
            cancellationToken);

        foreach (var change in changes)
        {
            if (change.Id <= 0)
            {
                continue;
            }

            await _stateStore.SaveAsync(
                new ProviderCacheState(
                    "tmdb",
                    ProviderCacheScope.Item,
                    $"{keyPrefix}:{change.Id}",
                    nowUtc,
                    nowUtc,
                    watermark,
                    null,
                    null),
                    cancellationToken);
        }
    }

    private async Task SyncTvmazeUpdatesAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        try
        {
            var updates = await _tvmazeClient.GetShowUpdatesAsync(TvmazeUpdateWindow.Day, cancellationToken);
            await _stateStore.SaveAsync(
                new ProviderCacheState(
                    "tvmaze",
                    ProviderCacheScope.Global,
                    "show-updates",
                    nowUtc,
                    updates.Count > 0 ? updates.Max(update => update.UpdatedAtUtc) : null,
                    "day",
                    updates.Count,
                    null),
                cancellationToken);

            foreach (var update in updates)
            {
                await _stateStore.SaveAsync(
                    new ProviderCacheState(
                        "tvmaze",
                        ProviderCacheScope.Item,
                        $"show:{update.ShowId}",
                        nowUtc,
                        update.UpdatedAtUtc,
                        "day",
                        null,
                        null),
                cancellationToken);
            }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "TVmaze update tracking sync timed out or was canceled by an external dependency.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TVmaze update tracking sync failed.");
        }
    }
}
