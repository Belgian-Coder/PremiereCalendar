using Microsoft.Extensions.Options;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class ImdbDatasetRefreshService : BackgroundService
{
    private readonly IImdbDatasetImporter _importer;
    private readonly IImdbRatingsStore _ratingsStore;
    private readonly IOptionsMonitor<ImdbDatasetOptions> _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ImdbDatasetRefreshService> _logger;
    private readonly BackgroundJobTimelineService? _timeline;
    private readonly IProviderWorkScheduler? _workScheduler;

    public ImdbDatasetRefreshService(
        IImdbDatasetImporter importer,
        IImdbRatingsStore ratingsStore,
        IOptionsMonitor<ImdbDatasetOptions> options,
        TimeProvider timeProvider,
        ILogger<ImdbDatasetRefreshService> logger,
        BackgroundJobTimelineService? timeline = null,
        IProviderWorkScheduler? workScheduler = null)
    {
        _importer = importer;
        _ratingsStore = ratingsStore;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _timeline = timeline;
        _workScheduler = workScheduler;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.CurrentValue.Enabled)
        {
            return;
        }

        try
        {
            if (_options.CurrentValue.RefreshOnStartup)
            {
                var delay = TimeSpan.FromSeconds(Math.Max(0, _options.CurrentValue.StartupDelaySeconds));
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, stoppingToken);
                }

                await QueueOrImportAsync(stoppingToken);
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                var interval = TimeSpan.FromHours(Math.Max(1, _options.CurrentValue.RefreshIntervalHours));
                await Task.Delay(interval, stoppingToken);
                await QueueOrImportAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task QueueOrImportAsync(CancellationToken cancellationToken)
    {
        if (_workScheduler is null)
        {
            await ImportIfDueAsync(cancellationToken);
            return;
        }

        await _workScheduler.EnqueueAsync(new ProviderWorkRequest(
            ProviderWorkKind.ImdbDatasetRefresh,
            "imdb-dataset-refresh",
            ProviderWorkPriority.Maintenance,
            "{}"), cancellationToken);
    }

    internal async Task ImportIfDueAsync(CancellationToken cancellationToken)
    {
        var startedUtc = _timeProvider.GetUtcNow();
        var startedTimestamp = _timeProvider.GetTimestamp();
        try
        {
            var state = await _ratingsStore.GetStateAsync(cancellationToken);
            var interval = TimeSpan.FromHours(Math.Max(1, _options.CurrentValue.RefreshIntervalHours));
            if (state.LastImportedUtc is { } lastImported
                && _timeProvider.GetUtcNow() - lastImported < interval)
            {
                await RecordTimelineAsync(
                    BackgroundJobStatus.Skipped,
                    "IMDb ratings import is still fresh.",
                    _timeProvider.GetUtcNow(),
                    null,
                    cancellationToken);
                return;
            }

            await RecordTimelineAsync(
                BackgroundJobStatus.Started,
                "Started IMDb ratings import.",
                startedUtc,
                null,
                cancellationToken);
            await _importer.ImportRatingsAsync(cancellationToken);
            await RecordTimelineAsync(
                BackgroundJobStatus.Succeeded,
                "Finished IMDb ratings import.",
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
            _logger.LogWarning(ex, "IMDb ratings refresh failed.");
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
            await _timeline.RecordAsync("IMDb ratings import", status, message, occurredUtc, duration, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not record IMDb timeline event.");
        }
    }
}
