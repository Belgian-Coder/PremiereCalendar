using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed record NightlyCalendarWarmupPayload(
    string Marker,
    DateOnly NightDate,
    DateTimeOffset RefreshNotBeforeUtc,
    string Profile,
    DateOnly Start,
    DateOnly End,
    CalendarFilters Filters)
{
    public const string PayloadMarker = "nightly-calendar-window-v1";
    public string CacheKey => PremiereDiscoveryCriteria.FromFilters(Filters).CacheKey();
    private string ProfileHash => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(CacheKey)));
    public string DedupeKey => $"calendar-warmup:night:{NightDate:yyyyMMdd}:{RefreshNotBeforeUtc.UtcTicks}:{ProfileHash}:{Start:yyyyMMdd}:{End:yyyyMMdd}";
    public string ReceiptKey => $"calendar-warmup:nightly-receipt:v1:{RefreshNotBeforeUtc.UtcTicks}:{ProfileHash}:{Start:yyyyMMdd}:{End:yyyyMMdd}";
    public string ReceiptValue => RefreshNotBeforeUtc.ToString("O", CultureInfo.InvariantCulture);
}

public sealed record NightlyCalendarWarmupCoverage(DateOnly NightDate, int Total, int Fresh, int Pending, int Failed)
{
    public bool Complete => Fresh == Total;
}

// One durable plan points to independent scheduler jobs. Completion is established
// from each week's cache envelope, never from enqueue or a single successful job.
public sealed class NightlyCalendarWarmupPlanner(
    IAppStateStore stateStore,
    IProviderWorkScheduler scheduler,
    ICalendarCacheMaintenance cache,
    IOptionsMonitor<CalendarWarmupOptions> options,
    TimeProvider timeProvider,
    ILogger<NightlyCalendarWarmupPlanner> logger)
{
    public const string StateKey = "calendar-warmup:nightly-plan:v1";
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<NightlyCalendarWarmupCoverage?> RunOnceAsync(CancellationToken cancellationToken)
    {
        var settings = options.CurrentValue;
        if (!settings.Enabled || !settings.NightlyEnabled) return null;
        var (nightDate, cutoff) = MostRecentOccurrence(timeProvider.GetUtcNow(), settings);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var serialized = await stateStore.GetValueAsync(StateKey, cancellationToken);
            var plan = serialized is null ? null : JsonSerializer.Deserialize<NightPlan>(serialized);
            var windows = BuildWindows(nightDate, cutoff);
            if (plan is null || plan.NightDate != nightDate || plan.RefreshNotBeforeUtc != cutoff)
            {
                plan = new NightPlan(nightDate, cutoff, new Dictionary<string, string>(StringComparer.Ordinal), null);
                await SaveAsync(plan, cancellationToken);
                logger.LogInformation("Nightly calendar plan {NightDate} started: {WindowCount} default-profile week windows, refresh cutoff {RefreshNotBeforeUtc}.", nightDate, windows.Count, cutoff);
            }

            var fresh = 0;
            var pending = 0;
            var failed = 0;
            foreach (var window in windows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await IsFreshAsync(cache, stateStore, window, cancellationToken))
                {
                    fresh++;
                    continue;
                }
                if (plan.Jobs.TryGetValue(window.DedupeKey, out var jobId))
                {
                    var snapshot = await scheduler.GetAsync(jobId, cancellationToken);
                    if (snapshot?.State is ProviderWorkState.Queued or ProviderWorkState.Running or ProviderWorkState.RetryWaiting)
                        pending++;
                    else
                        failed++; // Terminal jobs do not restart an unbounded retry loop.
                    continue;
                }

                var handle = await scheduler.EnqueueAsync(new ProviderWorkRequest(
                    ProviderWorkKind.CalendarWarmup, window.DedupeKey, ProviderWorkPriority.Warmup,
                    JsonSerializer.Serialize(window)), cancellationToken);
                plan.Jobs[window.DedupeKey] = handle.JobId;
                // Persist every handle: a restart can attach to an active enqueue
                // interrupted before this write; a completed one is found in cache.
                await SaveAsync(plan, cancellationToken);
                pending++;
            }

            var coverage = new NightlyCalendarWarmupCoverage(nightDate, windows.Count, fresh, pending, failed);
            if (coverage != plan.Coverage)
            {
                plan = plan with { Coverage = coverage };
                await SaveAsync(plan, cancellationToken);
                logger.LogInformation("Nightly calendar coverage {NightDate}: {Fresh}/{Total} fresh, {Pending} pending, {Failed} uncovered terminal windows; complete={Complete}.",
                    nightDate, fresh, windows.Count, pending, failed, coverage.Complete);
            }
            return coverage;
        }
        finally { _gate.Release(); }
    }

    private Task SaveAsync(NightPlan plan, CancellationToken cancellationToken)
        => stateStore.SetValueAsync(StateKey, JsonSerializer.Serialize(plan), cancellationToken);

    public static async Task<bool> IsFreshAsync(ICalendarCacheMaintenance cache, IAppStateStore stateStore, NightlyCalendarWarmupPayload payload, CancellationToken cancellationToken)
        => await HasCompleteSnapshotAsync(cache, payload, cancellationToken)
            && await stateStore.GetValueAsync(payload.ReceiptKey, cancellationToken) == payload.ReceiptValue;

    public static async Task<bool> HasCompleteSnapshotAsync(ICalendarCacheMaintenance cache, NightlyCalendarWarmupPayload payload, CancellationToken cancellationToken)
    {
        var metadata = await cache.GetWeekMetadataAsync(payload.Start, payload.End, payload.CacheKey, cancellationToken);
        return metadata is { Completeness: CalendarCacheCompleteness.Complete }
            && metadata.CachedAtUtc >= payload.RefreshNotBeforeUtc;
    }

    public static IReadOnlyList<NightlyCalendarWarmupPayload> BuildWindows(DateOnly nightDate, DateTimeOffset refreshNotBeforeUtc)
    {
        var first = new DateOnly(nightDate.Year, nightDate.Month, 1);
        var last = first.AddMonths(2).AddDays(-1);
        var currentWeek = CalendarFilters.StartOfWeek(nightDate);
        var weeks = new List<DateOnly>();
        for (var week = CalendarFilters.StartOfWeek(first); week <= last; week = week.AddDays(7)) weeks.Add(week);
        var windows = new List<NightlyCalendarWarmupPayload>();
        foreach (var week in weeks.OrderBy(week => week == currentWeek ? 0 : week <= first.AddMonths(1).AddDays(-1) ? 1 : 2).ThenBy(week => week))
        {
            // Interleaving keeps Movies from waiting behind a whole Series month.
            foreach (var mode in new[] { CalendarPageMode.Series, CalendarPageMode.Movies })
            {
                var filters = CalendarFilters.CreateDefault();
                CalendarFilterState.ApplyPageMode(filters, mode);
                filters.WeekStart = week;
                filters.PriorityDate = nightDate >= week && nightDate <= week.AddDays(6) ? nightDate : week;
                CalendarFilterState.NormalizeForCalendar(filters);
                windows.Add(new(NightlyCalendarWarmupPayload.PayloadMarker, nightDate, refreshNotBeforeUtc,
                    mode.ToString(), week, week.AddDays(6), filters));
            }
        }
        return windows;
    }

    public static (DateOnly NightDate, DateTimeOffset CutoffUtc) MostRecentOccurrence(DateTimeOffset nowUtc, CalendarWarmupOptions settings)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(settings.NightlyTimeZone);
        var local = TimeZoneInfo.ConvertTime(nowUtc, zone);
        var date = DateOnly.FromDateTime(local.DateTime);
        var occurrence = OccurrenceUtc(date, settings.NightlyLocalTime, zone);
        return occurrence <= nowUtc ? (date, occurrence) : (date.AddDays(-1), OccurrenceUtc(date.AddDays(-1), settings.NightlyLocalTime, zone));
    }

    public static DateTimeOffset NextOccurrenceUtc(DateTimeOffset nowUtc, CalendarWarmupOptions settings)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(settings.NightlyTimeZone);
        var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, zone).DateTime);
        var occurrence = OccurrenceUtc(date, settings.NightlyLocalTime, zone);
        return occurrence > nowUtc ? occurrence : OccurrenceUtc(date.AddDays(1), settings.NightlyLocalTime, zone);
    }

    private static DateTimeOffset OccurrenceUtc(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        // A skipped local time runs at the first valid minute; an ambiguous time
        // runs once, at its earlier UTC occurrence.
        while (zone.IsInvalidTime(local)) local = local.AddMinutes(1);
        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    private sealed record NightPlan(DateOnly NightDate, DateTimeOffset RefreshNotBeforeUtc, Dictionary<string, string> Jobs, NightlyCalendarWarmupCoverage? Coverage);
}
