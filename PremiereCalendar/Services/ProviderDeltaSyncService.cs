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

    public ProviderDeltaSyncService(
        ITmdbClient tmdbClient,
        ITvmazeClient tvmazeClient,
        IProviderCacheStateStore stateStore,
        IOptionsMonitor<ProviderDeltaSyncOptions> options,
        TimeProvider timeProvider,
        ILogger<ProviderDeltaSyncService> logger)
    {
        _tmdbClient = tmdbClient;
        _tvmazeClient = tvmazeClient;
        _stateStore = stateStore;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
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

                await RunOnceAsync(stoppingToken);
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(Math.Max(15, _options.CurrentValue.WakeIntervalMinutes)), stoppingToken);
                await RunOnceAsync(stoppingToken);
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "TVmaze update tracking sync failed.");
        }
    }
}
