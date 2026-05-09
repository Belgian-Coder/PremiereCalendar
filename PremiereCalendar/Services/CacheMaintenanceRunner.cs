using Microsoft.Extensions.Options;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class CacheMaintenanceRunner
{
    private readonly ICalendarCacheMaintenance _calendarCacheMaintenance;
    private readonly IImageCacheMaintenance _imageCacheMaintenance;
    private readonly ICalendarFilterUsageStore _usageStore;
    private readonly CalendarWarmupOptions _warmupOptions;
    private readonly CacheMaintenanceOptions _maintenanceOptions;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CacheMaintenanceRunner> _logger;

    public CacheMaintenanceRunner(
        ICalendarCacheMaintenance calendarCacheMaintenance,
        IImageCacheMaintenance imageCacheMaintenance,
        ICalendarFilterUsageStore usageStore,
        IOptions<CalendarWarmupOptions> warmupOptions,
        IOptions<CacheMaintenanceOptions> maintenanceOptions,
        TimeProvider timeProvider,
        ILogger<CacheMaintenanceRunner> logger)
    {
        _calendarCacheMaintenance = calendarCacheMaintenance;
        _imageCacheMaintenance = imageCacheMaintenance;
        _usageStore = usageStore;
        _warmupOptions = warmupOptions.Value;
        _maintenanceOptions = maintenanceOptions.Value;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!_maintenanceOptions.Enabled)
        {
            return;
        }

        var nowUtc = _timeProvider.GetUtcNow();
        var retentionDays = _maintenanceOptions.RetentionDays > 0
            ? _maintenanceOptions.RetentionDays
            : _warmupOptions.CleanupRetentionDays;
        var retention = TimeSpan.FromDays(Math.Max(1, retentionDays));
        var retainedProfiles = await _usageStore.GetTopProfilesAsync(
            Math.Max(0, _warmupOptions.TopFilterProfileCount),
            nowUtc,
            retention,
            cancellationToken);
        var retainedProfileKeys = retainedProfiles
            .Select(profile => profile.ProfileKey)
            .ToHashSet(StringComparer.Ordinal);
        var cutoffUtc = nowUtc - retention;

        var removedCalendarFiles = await _calendarCacheMaintenance.CleanupAsync(nowUtc, retention, cancellationToken);
        var removedImageEntries = await _imageCacheMaintenance.CleanupAsync(nowUtc, retention, cancellationToken);
        var removedUsageRows = await _usageStore.CleanupAsync(cutoffUtc, retainedProfileKeys, cancellationToken);

        _logger.LogInformation(
            "Cache maintenance removed {CalendarFiles} calendar files, {ImageEntries} image entries, and {UsageRows} filter usage rows older than {RetentionDays} days.",
            removedCalendarFiles,
            removedImageEntries,
            removedUsageRows,
            retention.TotalDays);
    }
}
