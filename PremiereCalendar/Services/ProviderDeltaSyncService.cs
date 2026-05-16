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
        await RunOnceCoreAsync(cancellationToken);
    }

    private async Task<ProviderDeltaSyncResult> RunOnceCoreAsync(CancellationToken cancellationToken)
    {
        var nowUtc = _timeProvider.GetUtcNow();
        var failedProviders = new List<string>();
        if (_options.CurrentValue.UseTmdbChanges)
        {
            if (!await SyncTmdbChangesAsync(nowUtc, cancellationToken))
            {
                failedProviders.Add("TMDb");
            }
        }

        if (_options.CurrentValue.UseTvmazeUpdates)
        {
            if (!await SyncTvmazeUpdatesAsync(nowUtc, cancellationToken))
            {
                failedProviders.Add("TVmaze");
            }
        }

        return new ProviderDeltaSyncResult(failedProviders);
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
            if (result.FailedProviders.Count > 0)
            {
                await RecordTimelineAsync(
                    BackgroundJobStatus.Failed,
                    $"Provider delta sync completed with failures: {string.Join(", ", result.FailedProviders)}.",
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

    private async Task<bool> SyncTmdbChangesAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        try
        {
            var end = DateOnly.FromDateTime(nowUtc.UtcDateTime);
            var lookbackDays = Math.Clamp(_options.CurrentValue.TmdbLookbackDays, 1, 14);
            var start = end.AddDays(-lookbackDays);
            await SyncTmdbMediaChangesAsync(PremiereMediaType.Movie, start, end, nowUtc, cancellationToken);
            await SyncTmdbMediaChangesAsync(PremiereMediaType.Series, start, end, nowUtc, cancellationToken);
            return true;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "TMDb change tracking sync timed out or was canceled by an external dependency.");
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TMDb change tracking sync failed.");
            return false;
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

        var states = new List<ProviderCacheState>
        {
            new(
                "tmdb",
                ProviderCacheScope.Global,
                globalKey,
                nowUtc,
                nowUtc,
                watermark,
                changes.Count,
                null)
        };

        foreach (var change in changes)
        {
            if (change.Id <= 0)
            {
                continue;
            }

            states.Add(new ProviderCacheState(
                    "tmdb",
                    ProviderCacheScope.Item,
                    $"{keyPrefix}:{change.Id}",
                    nowUtc,
                    nowUtc,
                    watermark,
                    null,
                    null));
        }

        await _stateStore.SaveManyAsync(states, cancellationToken);
    }

    private async Task<bool> SyncTvmazeUpdatesAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        try
        {
            var updates = await _tvmazeClient.GetShowUpdatesAsync(TvmazeUpdateWindow.Day, cancellationToken);
            var states = new List<ProviderCacheState>
            {
                new(
                    "tvmaze",
                    ProviderCacheScope.Global,
                    "show-updates",
                    nowUtc,
                    updates.Count > 0 ? updates.Max(update => update.UpdatedAtUtc) : null,
                    "day",
                    updates.Count,
                    null)
            };

            foreach (var update in updates)
            {
                states.Add(new ProviderCacheState(
                        "tvmaze",
                        ProviderCacheScope.Item,
                        $"show:{update.ShowId}",
                        nowUtc,
                        update.UpdatedAtUtc,
                        "day",
                        null,
                        null));
            }

            await _stateStore.SaveManyAsync(states, cancellationToken);
            return true;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "TVmaze update tracking sync timed out or was canceled by an external dependency.");
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TVmaze update tracking sync failed.");
            return false;
        }
    }

    private sealed record ProviderDeltaSyncResult(IReadOnlyList<string> FailedProviders);
}
