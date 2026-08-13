using System.Diagnostics;
using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class CurrentWeekCalendarWarmupRunner
{
    private readonly IPremiereService _premiereService;
    private readonly ICalendarFilterUsageStore _usageStore;
    private readonly CalendarLoadCoordinator _loadCoordinator;
    private readonly CalendarWarmupOptions _options;
    private readonly CalendarCacheOptions _cacheOptions;
    private readonly ICalendarCacheMaintenance? _cacheMaintenance;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<CurrentWeekCalendarWarmupRunner> _logger;
    private readonly IPremiereLoadPipeline? _loadPipeline;

    public CurrentWeekCalendarWarmupRunner(
        IPremiereService premiereService,
        ICalendarFilterUsageStore usageStore,
        CalendarLoadCoordinator loadCoordinator,
        IOptions<CalendarWarmupOptions> options,
        TimeProvider timeProvider,
        ILogger<CurrentWeekCalendarWarmupRunner> logger,
        IOptions<CalendarCacheOptions>? cacheOptions = null,
        ICalendarCacheMaintenance? cacheMaintenance = null,
        IPremiereLoadPipeline? loadPipeline = null)
    {
        _premiereService = premiereService;
        _usageStore = usageStore;
        _loadCoordinator = loadCoordinator;
        _options = options.Value;
        _cacheOptions = cacheOptions?.Value ?? new CalendarCacheOptions();
        _cacheMaintenance = cacheMaintenance;
        _timeProvider = timeProvider;
        _logger = logger;
        _loadPipeline = loadPipeline;
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await RunOnceWithResultAsync(cancellationToken);
    }

    public async Task<CalendarWarmupRunResult> RunOnceWithResultAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return new CalendarWarmupRunResult(Skipped: true, WarmedProfiles: 0, FailedProfiles: 0);
        }

        using var loadLease = await _loadCoordinator.TryBeginBackgroundLoadAsync(
            _options.SkipWhenForegroundLoadActive,
            cancellationToken);
        if (loadLease is null)
        {
            _logger.LogDebug("Skipping current-week calendar warmup because another calendar load is active.");
            return new CalendarWarmupRunResult(Skipped: true, WarmedProfiles: 0, FailedProfiles: 0);
        }

        var runToken = loadLease.Token;
        var cycleBudget = TimeSpan.FromSeconds(Math.Max(1, _options.CycleBudgetSeconds));
        var cycleStartedAt = Stopwatch.GetTimestamp();
        var nowUtc = _timeProvider.GetUtcNow();
        var today = CurrentLocalDate(nowUtc);
        var windows = BuildWarmupWindows(today);
        var retention = TimeSpan.FromDays(Math.Max(1, _options.CleanupRetentionDays));
        var topProfiles = await _usageStore.GetTopProfilesAsync(
            Math.Max(0, _options.TopFilterProfileCount),
            nowUtc,
            retention,
            runToken);

        var profiles = new List<WarmupProfile>();
        foreach (var defaultProfile in DefaultProfiles())
        {
            var storedProfile = await _usageStore.GetProfileAsync(defaultProfile.ProfileKey, runToken);
            profiles.Add(defaultProfile with { LastWarmedUtc = storedProfile?.LastWarmedUtc });
        }

        profiles.AddRange(topProfiles.Select(profile => new WarmupProfile(
            profile.ProfileKey,
            profile.PageMode,
            profile.Filters,
            profile.LastWarmedUtc,
            IsDefault: false)));

        var maximumProfiles = Math.Max(1, _options.MaximumProfilesPerWake);
        var maximumRemoteWindows = Math.Max(1, _options.MaximumRemoteWindowsPerWake);
        var minimumRefreshAge = TimeSpan.FromMinutes(Math.Max(1, _options.MinimumRemoteRefreshMinutes));
        var warmedProfiles = 0;
        var failedProfiles = 0;
        var remoteWindowsStarted = 0;

        foreach (var profile in profiles
            .GroupBy(profile => profile.ProfileKey, StringComparer.Ordinal)
            .Select(group => group.First()))
        {
            runToken.ThrowIfCancellationRequested();
            if (Stopwatch.GetElapsedTime(cycleStartedAt) >= cycleBudget)
            {
                _logger.LogDebug(
                    "Stopping current-week calendar warmup because the configured {BudgetSeconds}s cycle budget was reached.",
                    _options.CycleBudgetSeconds);
                break;
            }

            if (warmedProfiles >= maximumProfiles)
            {
                break;
            }

            if (profile.LastWarmedUtc is { } lastWarmed
                && nowUtc - lastWarmed < minimumRefreshAge)
            {
                continue;
            }

            var totalItemCount = 0;
            var successfulWindows = 0;
            var failures = new List<string>();
            CalendarFilters? lastFilters = null;
            var completedAllWindows = true;

            foreach (var window in windows)
            {
                if (Stopwatch.GetElapsedTime(cycleStartedAt) >= cycleBudget)
                {
                    failures.Add($"cycle budget exceeded after {Math.Max(1, _options.CycleBudgetSeconds)}s");
                    completedAllWindows = false;
                    break;
                }

                var filters = RehydrateFilters(profile.Filters, profile.PageMode, window.Start, window.PriorityDate);
                lastFilters = filters;
                if (await IsWarmupWindowFreshAsync(window, filters, nowUtc, runToken))
                {
                    continue;
                }

                if (remoteWindowsStarted >= maximumRemoteWindows)
                {
                    completedAllWindows = false;
                    _logger.LogDebug(
                        "Stopping current-week calendar warmup because the configured {MaximumRemoteWindowsPerWake} missing/stale window limit was reached.",
                        _options.MaximumRemoteWindowsPerWake);
                    break;
                }

                remoteWindowsStarted++;
                var windowBudget = TimeSpan.FromSeconds(Math.Max(1, _options.WindowBudgetSeconds));
                using var windowTimeout = new CancellationTokenSource(windowBudget);
                using var windowCancellation = CancellationTokenSource.CreateLinkedTokenSource(runToken, windowTimeout.Token);
                try
                {
                    var items = await LoadWindowAsync(window, filters, windowCancellation.Token);
                    totalItemCount += items.Count;
                    successfulWindows++;
                }
                catch (OperationCanceledException) when (runToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException) when (windowTimeout.IsCancellationRequested)
                {
                    _logger.LogWarning(
                        "Calendar warmup timed out for profile {ProfileKey} and window {StartDate} through {EndDate} after {TimeoutSeconds}s.",
                        profile.ProfileKey,
                        window.Start,
                        window.End,
                        _options.WindowBudgetSeconds);
                    failures.Add($"{window.Start:yyyy-MM-dd}-{window.End:yyyy-MM-dd}: timeout after {Math.Max(1, _options.WindowBudgetSeconds)}s");
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Calendar warmup failed for profile {ProfileKey} and window {StartDate} through {EndDate}.",
                        profile.ProfileKey,
                        window.Start,
                        window.End);
                    failures.Add($"{window.Start:yyyy-MM-dd}-{window.End:yyyy-MM-dd}: {ex.Message}");
                }
            }

            var trackingFilters = lastFilters ?? RehydrateFilters(profile.Filters, profile.PageMode, today, today);
            if (completedAllWindows && failures.Count == 0)
            {
                await _usageStore.MarkWarmedAsync(
                    profile.ProfileKey,
                    profile.PageMode,
                    trackingFilters,
                    profile.IsDefault,
                    totalItemCount,
                    nowUtc,
                    runToken);
            }

            if (failures.Count > 0)
            {
                failedProfiles++;
                await _usageStore.MarkWarmFailedAsync(
                    profile.ProfileKey,
                    profile.PageMode,
                    trackingFilters,
                    profile.IsDefault,
                    string.Join("; ", failures),
                    nowUtc,
                    runToken);
            }

            warmedProfiles++;
        }

        return new CalendarWarmupRunResult(Skipped: false, warmedProfiles, failedProfiles);
    }

    private async Task<IReadOnlyList<PremiereItem>> LoadWindowAsync(
        CalendarWarmupWindow window,
        CalendarFilters filters,
        CancellationToken cancellationToken)
    {
        if (_loadPipeline is null)
        {
            return await _premiereService.GetPremieresAsync(
                window.Start,
                window.End,
                cancellationToken,
                forceRefresh: !_options.StaleOnlyRemoteRefresh,
                filters: filters);
        }

        IReadOnlyList<PremiereItem> items = [];
        await foreach (var progress in _loadPipeline.StreamCoreAsync(
                           window.Start,
                           window.End,
                           !_options.StaleOnlyRemoteRefresh,
                           filters,
                           cancellationToken).WithCancellation(cancellationToken))
        {
            items = progress.Items;
        }
        return items;
    }

    private async Task<bool> IsWarmupWindowFreshAsync(
        CalendarWarmupWindow window,
        CalendarFilters filters,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        if (_cacheMaintenance is null)
        {
            return false;
        }

        var metadata = await _cacheMaintenance.GetWeekMetadataAsync(
            window.Start,
            window.End,
            PremiereDiscoveryCriteria.FromFilters(filters).CacheKey(),
            cancellationToken);
        if (metadata is null || metadata.Completeness != CalendarCacheCompleteness.Complete)
        {
            return false;
        }

        var maxAge = TimeSpan.FromHours(Math.Max(1, _cacheOptions.WeekCacheHours));
        return nowUtc - metadata.CachedAtUtc <= maxAge;
    }

    public static IReadOnlyList<CalendarWarmupWindow> BuildWarmupWindows(DateOnly today)
    {
        var windows = new List<CalendarWarmupWindow>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var weekStart = CalendarFilters.StartOfWeek(today);
        var weekEnd = weekStart.AddDays(6);
        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var monthEnd = monthStart.AddMonths(1).AddDays(-1);

        AddSpan(today, today);
        AddSpan(today.AddDays(1), today.AddDays(1));
        AddSpan(today.AddDays(-1), today.AddDays(-1));
        AddSpan(today.AddDays(1), weekEnd);
        AddSpan(weekEnd.AddDays(1), monthEnd);
        AddSpan(monthStart, weekStart.AddDays(-1));
        AddMonth(1);
        AddMonth(-1);

        for (var offset = 2; offset <= 6; offset++)
        {
            AddMonth(offset);
        }

        return windows;

        void AddMonth(int offset)
        {
            var target = monthStart.AddMonths(offset);
            AddSpan(target, target.AddMonths(1).AddDays(-1));
        }

        void AddSpan(DateOnly start, DateOnly end)
        {
            if (end < start)
            {
                return;
            }

            for (var currentWeekStart = CalendarFilters.StartOfWeek(start);
                 currentWeekStart <= end;
                 currentWeekStart = currentWeekStart.AddDays(7))
            {
                var currentWeekEnd = currentWeekStart.AddDays(6);
                var priorityStart = Max(start, currentWeekStart);
                var priorityEnd = Min(end, currentWeekEnd);
                AddWindowWithPriority(currentWeekStart, currentWeekEnd, PriorityDateForWindow(today, priorityStart, priorityEnd));
            }
        }

        void AddWindowWithPriority(DateOnly start, DateOnly end, DateOnly priorityDate)
        {
            if (end < start)
            {
                return;
            }

            var key = $"{start:yyyyMMdd}:{end:yyyyMMdd}";
            if (!seen.Add(key))
            {
                return;
            }

            windows.Add(new CalendarWarmupWindow(start, end, priorityDate));
        }
    }

    public sealed record CalendarWarmupRunResult(bool Skipped, int WarmedProfiles, int FailedProfiles);

    private static DateOnly Max(DateOnly left, DateOnly right)
    {
        return left >= right ? left : right;
    }

    private static DateOnly Min(DateOnly left, DateOnly right)
    {
        return left <= right ? left : right;
    }

    private DateOnly CurrentLocalDate(DateTimeOffset nowUtc)
    {
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, _timeProvider.LocalTimeZone);
        return DateOnly.FromDateTime(localNow.DateTime);
    }

    private static CalendarFilters RehydrateFilters(
        CalendarFilters template,
        CalendarPageMode pageMode,
        DateOnly rangeStart,
        DateOnly priorityDate)
    {
        var filters = CalendarFilterState.Clone(template);
        filters.WeekStart = rangeStart;
        filters.PriorityDate = priorityDate;
        CalendarFilterState.ApplyPageMode(filters, pageMode);
        CalendarFilterState.Normalize(filters);
        return filters;
    }

    private static DateOnly PriorityDateForWindow(DateOnly today, DateOnly start, DateOnly end)
    {
        if (today < start)
        {
            return start;
        }

        return today > end ? end : today;
    }

    private static IReadOnlyList<WarmupProfile> DefaultProfiles()
    {
        return
        [
            new WarmupProfile(
                "default:series",
                CalendarPageMode.Series,
                new CalendarFilters { ShowSeries = true, ShowMovies = false },
                LastWarmedUtc: null,
                IsDefault: true),
            new WarmupProfile(
                "default:movies",
                CalendarPageMode.Movies,
                new CalendarFilters { ShowSeries = false, ShowMovies = true },
                LastWarmedUtc: null,
                IsDefault: true)
        ];
    }

    private sealed record WarmupProfile(
        string ProfileKey,
        CalendarPageMode PageMode,
        CalendarFilters Filters,
        DateTimeOffset? LastWarmedUtc,
        bool IsDefault);
}

public sealed record CalendarWarmupWindow(
    DateOnly Start,
    DateOnly End,
    DateOnly PriorityDate);
