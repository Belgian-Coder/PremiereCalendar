using Microsoft.Extensions.Options;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class CurrentWeekCalendarWarmupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<CalendarWarmupOptions> _warmupOptions;
    private readonly IOptionsMonitor<CacheMaintenanceOptions> _maintenanceOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CurrentWeekCalendarWarmupService> _logger;
    private readonly BackgroundJobTimelineService? _timeline;
    private DateTimeOffset? _lastMaintenanceUtc;

    public CurrentWeekCalendarWarmupService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<CalendarWarmupOptions> warmupOptions,
        IOptionsMonitor<CacheMaintenanceOptions> maintenanceOptions,
        TimeProvider timeProvider,
        ILogger<CurrentWeekCalendarWarmupService> logger,
        BackgroundJobTimelineService? timeline = null)
    {
        _scopeFactory = scopeFactory;
        _warmupOptions = warmupOptions;
        _maintenanceOptions = maintenanceOptions;
        _timeProvider = timeProvider;
        _logger = logger;
        _timeline = timeline;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_warmupOptions.CurrentValue.Enabled)
        {
            return;
        }

        try
        {
            if (_warmupOptions.CurrentValue.RunOnStartup)
            {
                var startupDelay = TimeSpan.FromSeconds(Math.Max(0, _warmupOptions.CurrentValue.StartupDelaySeconds));
                if (startupDelay > TimeSpan.Zero)
                {
                    await Task.Delay(startupDelay, stoppingToken);
                }

                await RunWarmupAndMaintenanceAsync(stoppingToken);
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                var wakeInterval = TimeSpan.FromMinutes(Math.Max(1, _warmupOptions.CurrentValue.WakeIntervalMinutes));
                await Task.Delay(wakeInterval, stoppingToken);
                await RunWarmupAndMaintenanceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunWarmupAndMaintenanceAsync(CancellationToken stoppingToken)
    {
        var startedUtc = _timeProvider.GetUtcNow();
        var startedTimestamp = _timeProvider.GetTimestamp();
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            await RecordTimelineAsync(
                "Calendar warmup",
                BackgroundJobStatus.Started,
                "Started current-week calendar warmup.",
                startedUtc,
                null,
                stoppingToken);
            await scope.ServiceProvider.GetRequiredService<CurrentWeekCalendarWarmupRunner>()
                .RunOnceAsync(stoppingToken);
            await RecordTimelineAsync(
                "Calendar warmup",
                BackgroundJobStatus.Succeeded,
                "Finished current-week calendar warmup.",
                _timeProvider.GetUtcNow(),
                _timeProvider.GetElapsedTime(startedTimestamp),
                stoppingToken);

            if (MaintenanceIsDue())
            {
                var maintenanceStartedUtc = _timeProvider.GetUtcNow();
                var maintenanceStartedTimestamp = _timeProvider.GetTimestamp();
                await RecordTimelineAsync(
                    "Cache maintenance",
                    BackgroundJobStatus.Started,
                    "Started cache maintenance.",
                    maintenanceStartedUtc,
                    null,
                    stoppingToken);
                await scope.ServiceProvider.GetRequiredService<CacheMaintenanceRunner>()
                    .RunOnceAsync(stoppingToken);
                _lastMaintenanceUtc = _timeProvider.GetUtcNow();
                await RecordTimelineAsync(
                    "Cache maintenance",
                    BackgroundJobStatus.Succeeded,
                    "Finished cache maintenance.",
                    _lastMaintenanceUtc.Value,
                    _timeProvider.GetElapsedTime(maintenanceStartedTimestamp),
                    stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Current-week calendar warmup cycle failed.");
            await RecordTimelineAsync(
                "Calendar warmup",
                BackgroundJobStatus.Failed,
                ex.Message,
                _timeProvider.GetUtcNow(),
                _timeProvider.GetElapsedTime(startedTimestamp),
                CancellationToken.None);
        }
    }

    private async Task RecordTimelineAsync(
        string jobName,
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
            await _timeline.RecordAsync(jobName, status, message, occurredUtc, duration, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not record {JobName} timeline event.", jobName);
        }
    }

    private bool MaintenanceIsDue()
    {
        if (!_maintenanceOptions.CurrentValue.Enabled)
        {
            return false;
        }

        var interval = TimeSpan.FromHours(Math.Max(1, _maintenanceOptions.CurrentValue.SweepIntervalHours));
        return _lastMaintenanceUtc is null || _timeProvider.GetUtcNow() - _lastMaintenanceUtc >= interval;
    }
}
