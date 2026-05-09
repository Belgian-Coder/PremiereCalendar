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

    public ImdbDatasetRefreshService(
        IImdbDatasetImporter importer,
        IImdbRatingsStore ratingsStore,
        IOptionsMonitor<ImdbDatasetOptions> options,
        TimeProvider timeProvider,
        ILogger<ImdbDatasetRefreshService> logger)
    {
        _importer = importer;
        _ratingsStore = ratingsStore;
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
            if (_options.CurrentValue.RefreshOnStartup)
            {
                var delay = TimeSpan.FromSeconds(Math.Max(0, _options.CurrentValue.StartupDelaySeconds));
                if (delay > TimeSpan.Zero)
                {
                    await Task.Delay(delay, stoppingToken);
                }

                await ImportIfDueAsync(stoppingToken);
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                var interval = TimeSpan.FromHours(Math.Max(1, _options.CurrentValue.RefreshIntervalHours));
                await Task.Delay(interval, stoppingToken);
                await ImportIfDueAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task ImportIfDueAsync(CancellationToken cancellationToken)
    {
        try
        {
            var state = await _ratingsStore.GetStateAsync(cancellationToken);
            var interval = TimeSpan.FromHours(Math.Max(1, _options.CurrentValue.RefreshIntervalHours));
            if (state.LastImportedUtc is { } lastImported
                && _timeProvider.GetUtcNow() - lastImported < interval)
            {
                return;
            }

            await _importer.ImportRatingsAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "IMDb ratings refresh failed.");
        }
    }
}
