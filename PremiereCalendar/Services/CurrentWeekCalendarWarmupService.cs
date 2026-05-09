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
    private DateTimeOffset? _lastMaintenanceUtc;

    public CurrentWeekCalendarWarmupService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<CalendarWarmupOptions> warmupOptions,
        IOptionsMonitor<CacheMaintenanceOptions> maintenanceOptions,
        TimeProvider timeProvider,
        ILogger<CurrentWeekCalendarWarmupService> logger)
    {
        _scopeFactory = scopeFactory;
        _warmupOptions = warmupOptions;
        _maintenanceOptions = maintenanceOptions;
        _timeProvider = timeProvider;
        _logger = logger;
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
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<CurrentWeekCalendarWarmupRunner>()
                .RunOnceAsync(stoppingToken);

            if (MaintenanceIsDue())
            {
                await scope.ServiceProvider.GetRequiredService<CacheMaintenanceRunner>()
                    .RunOnceAsync(stoppingToken);
                _lastMaintenanceUtc = _timeProvider.GetUtcNow();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Current-week calendar warmup cycle failed.");
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
