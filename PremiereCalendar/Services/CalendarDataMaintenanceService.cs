using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public sealed class CalendarDataMaintenanceService
{
    private readonly IProviderCacheStateStore _providerCacheStateStore;
    private readonly ICalendarCache _calendarCache;
    private readonly ScoreBackfillService _scoreBackfillService;
    private readonly MissingExternalIdRepairService _missingExternalIdRepairService;
    private readonly ILogger<CalendarDataMaintenanceService> _logger;

    public CalendarDataMaintenanceService(
        IProviderCacheStateStore providerCacheStateStore,
        ICalendarCache calendarCache,
        ScoreBackfillService scoreBackfillService,
        MissingExternalIdRepairService missingExternalIdRepairService,
        ILogger<CalendarDataMaintenanceService> logger)
    {
        _providerCacheStateStore = providerCacheStateStore;
        _calendarCache = calendarCache;
        _scoreBackfillService = scoreBackfillService;
        _missingExternalIdRepairService = missingExternalIdRepairService;
        _logger = logger;
    }

    public Task<BackfillResult> BackfillRecentScoresAsync(CancellationToken cancellationToken)
    {
        return MaintainRecentWeeksAsync(
            (items, token) => _scoreBackfillService.BackfillItemsAsync(items, token, forceRefresh: true),
            cancellationToken);
    }

    public Task<BackfillResult> RepairRecentExternalIdsAsync(CancellationToken cancellationToken)
    {
        return MaintainRecentWeeksAsync(
            (items, token) => _missingExternalIdRepairService.RepairItemsAsync(items, token, forceRefresh: true),
            cancellationToken);
    }

    private async Task<BackfillResult> MaintainRecentWeeksAsync(
        Func<IReadOnlyList<PremiereItem>, CancellationToken, Task<BackfillResult>> maintain,
        CancellationToken cancellationToken)
    {
        var states = await _providerCacheStateStore.GetByProviderAsync("calendar", 20, cancellationToken);
        var changed = 0;
        var scanned = 0;
        var latestItems = new List<PremiereItem>();
        foreach (var state in states.Where(state => state.Scope == ProviderCacheScope.Week))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!TryParseWeekCacheKey(state.Key, out var weekStart, out var cacheKey))
            {
                continue;
            }

            var weekEnd = weekStart.AddDays(6);
            var items = await _calendarCache.GetWeekAsync(weekStart, weekEnd, cacheKey, cancellationToken, allowExpired: true);
            if (items is null || items.Count == 0)
            {
                continue;
            }

            try
            {
                var result = await maintain(items, cancellationToken);
                scanned += result.ScannedCount;
                changed += result.ChangedCount;
                if (result.ChangedCount > 0)
                {
                    await _calendarCache.SetWeekAsync(weekStart, weekEnd, cacheKey, result.Items, cancellationToken);
                }

                latestItems = [.. result.Items];
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not maintain cached week {WeekKey}.", state.Key);
            }
        }

        return new BackfillResult(latestItems, changed, scanned);
    }

    private static bool TryParseWeekCacheKey(string key, out DateOnly weekStart, out string cacheKey)
    {
        weekStart = default;
        cacheKey = "";
        var parts = key.Split(':', 2, StringSplitOptions.TrimEntries);
        if (parts.Length != 2
            || !DateOnly.TryParseExact(
                parts[0],
                "yyyyMMdd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None,
                out weekStart))
        {
            return false;
        }

        cacheKey = parts[1];
        return !string.IsNullOrWhiteSpace(cacheKey);
    }
}
