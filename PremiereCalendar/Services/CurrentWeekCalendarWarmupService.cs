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
    private readonly IProviderWorkScheduler? _workScheduler;
    private readonly NightlyCalendarWarmupPlanner? _nightlyPlanner;
    private DateTimeOffset? _lastMaintenanceUtc;

    public CurrentWeekCalendarWarmupService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<CalendarWarmupOptions> warmupOptions,
        IOptionsMonitor<CacheMaintenanceOptions> maintenanceOptions,
        TimeProvider timeProvider,
        ILogger<CurrentWeekCalendarWarmupService> logger,
        BackgroundJobTimelineService? timeline = null,
        IProviderWorkScheduler? workScheduler = null,
        NightlyCalendarWarmupPlanner? nightlyPlanner = null)
    {
        _scopeFactory = scopeFactory;
        _warmupOptions = warmupOptions;
        _maintenanceOptions = maintenanceOptions;
        _timeProvider = timeProvider;
        _logger = logger;
        _timeline = timeline;
        _workScheduler = workScheduler;
        _nightlyPlanner = nightlyPlanner;
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
                    await Task.Delay(startupDelay, _timeProvider, stoppingToken);
                }

                await RunWarmupAndMaintenanceAsync(stoppingToken);
            }
            else
            {
                // Catch up the most recent night even when the separate periodic
                // current-week startup warmup is disabled.
                await RunNightlyPlanAsync(stoppingToken);
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                var wakeInterval = TimeSpan.FromMinutes(Math.Max(1, _warmupOptions.CurrentValue.WakeIntervalMinutes));
                if (_warmupOptions.CurrentValue.NightlyEnabled)
                {
                    try
                    {
                        var now = _timeProvider.GetUtcNow();
                        var untilNight = NightlyCalendarWarmupPlanner.NextOccurrenceUtc(now, _warmupOptions.CurrentValue) - now;
                        if (untilNight < wakeInterval) wakeInterval = untilNight;
                    }
                    catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
                    {
                        _logger.LogWarning(ex, "Nightly calendar schedule is invalid; periodic warmup will continue at its configured wake interval.");
                    }
                }
                await Task.Delay(wakeInterval, _timeProvider, stoppingToken);
                await RunWarmupAndMaintenanceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunWarmupAndMaintenanceAsync(CancellationToken stoppingToken)
    {
        await RunNightlyPlanAsync(stoppingToken);
        if (_workScheduler is not null)
        {
            try
            {
                await _workScheduler.EnqueueAsync(new ProviderWorkRequest(
                    ProviderWorkKind.CalendarWarmup,
                    "calendar-warmup:current",
                    ProviderWorkPriority.Warmup,
                    "{}"), stoppingToken);
                if (MaintenanceIsDue())
                {
                    await using var maintenanceScope = _scopeFactory.CreateAsyncScope();
                    await RunMaintenanceAsync(maintenanceScope.ServiceProvider, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Current-week calendar warmup scheduling cycle failed.");
            }
            return;
        }

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
            var warmupResult = await scope.ServiceProvider.GetRequiredService<CurrentWeekCalendarWarmupRunner>()
                .RunOnceWithResultAsync(stoppingToken);
            var warmupStatus = warmupResult.Skipped
                ? BackgroundJobStatus.Skipped
                : warmupResult.FailedProfiles > 0
                    ? BackgroundJobStatus.Failed
                    : BackgroundJobStatus.Succeeded;
            var warmupMessage = warmupResult.Skipped
                ? "Skipped current-week calendar warmup."
                : warmupResult.FailedProfiles > 0
                    ? $"Current-week calendar warmup completed with {warmupResult.FailedProfiles} profile failure(s)."
                    : "Finished current-week calendar warmup.";
            await RecordTimelineAsync(
                "Calendar warmup",
                warmupStatus,
                warmupMessage,
                _timeProvider.GetUtcNow(),
                _timeProvider.GetElapsedTime(startedTimestamp),
                stoppingToken);

            if (MaintenanceIsDue())
            {
                await RunMaintenanceAsync(scope.ServiceProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Cancellation can race the filtered catch above between filter
            // evaluation and entering this block during service shutdown.
            if (ex is OperationCanceledException && stoppingToken.IsCancellationRequested)
            {
                throw;
            }
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

    private async Task RunNightlyPlanAsync(CancellationToken stoppingToken)
    {
        if (_nightlyPlanner is null) return;
        try
        {
            await _nightlyPlanner.RunOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Nightly calendar plan scheduling failed; coverage will be checked again on the next wake.");
        }
    }

    private async Task RunMaintenanceAsync(IServiceProvider serviceProvider, CancellationToken stoppingToken)
    {
        var maintenanceStartedUtc = _timeProvider.GetUtcNow();
        var maintenanceStartedTimestamp = _timeProvider.GetTimestamp();
        try
        {
            await RecordTimelineAsync(
                "Cache maintenance",
                BackgroundJobStatus.Started,
                "Started cache maintenance.",
                maintenanceStartedUtc,
                null,
                stoppingToken);
            await serviceProvider.GetRequiredService<CacheMaintenanceRunner>()
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
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException && stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            _logger.LogWarning(ex, "Cache maintenance failed.");
            await RecordTimelineAsync(
                "Cache maintenance",
                BackgroundJobStatus.Failed,
                ex.Message,
                _timeProvider.GetUtcNow(),
                _timeProvider.GetElapsedTime(maintenanceStartedTimestamp),
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
