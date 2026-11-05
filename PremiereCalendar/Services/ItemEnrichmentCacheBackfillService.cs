using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

// Copies already available, verified payloads without making provider requests.
// Bounded and restart-safe; component timestamps remain those of the old records.
public sealed class ItemEnrichmentCacheBackfillService(
    ItemEnrichmentCacheCoordinator coordinator,
    ICalendarCache calendarCache,
    IProviderCacheStateStore cacheState,
    IAppStateStore appState,
    IOptions<ItemEnrichmentCacheOptions> options,
    IOptions<CalendarWarmupOptions> warmupOptions,
    TimeProvider clock,
    ILogger<ItemEnrichmentCacheBackfillService> logger,
    PremiereTelemetry telemetry,
    DatabaseRecoveryState? databaseState = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!coordinator.Enabled) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5), clock, stoppingToken);
            await RunOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Existing item-cache import stopped; ordinary calendar reads can continue importing cached items.");
        }
    }

    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        if (databaseState is not null) await databaseState.WaitUntilHealthyAsync(cancellationToken);
        var marker = "itemcache:backfill:v1:" + coordinator.Partition;
        if (await appState.GetValueAsync(marker, cancellationToken) is not null) return;
        var started = Stopwatch.GetTimestamp();
        var now = clock.GetUtcNow();
        var occurrence = NightlyCalendarWarmupPlanner.MostRecentOccurrence(now, warmupOptions.Value);
        var windows = NightlyCalendarWarmupPlanner.BuildWindows(occurrence.NightDate, occurrence.CutoffUtc)
            .Select(window => (window.Start, window.CacheKey)).ToList();
        // Do not resurrect broad historical diagnostic/episode profiles into the
        // active catalogue and its change-feed work. Other profiles import lazily.
        var defaultKeys = windows.Select(window => window.CacheKey).ToHashSet(StringComparer.Ordinal);
        var cutoff = now.AddMonths(-Math.Clamp(options.Value.RetentionMonths, 1, 24));
        foreach (var state in await cacheState.GetByProviderAsync("calendar", 1000, cancellationToken))
        {
            if (state.Scope != ProviderCacheScope.Week || state.LastCheckedUtc < cutoff) continue;
            var parts = state.Key.Split(':', 2);
            if (parts.Length == 2 && defaultKeys.Contains(parts[1]) && parts[1].Length is > 0 and <= 128
                && parts[1].All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_')
                && DateOnly.TryParseExact(parts[0], "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var start))
                windows.Add((start, parts[1]));
        }
        var scannedFiles = 0;
        var scannedItems = 0;
        var newest = new Dictionary<ItemEnrichmentIdentity, PremiereItem>();
        var candidates = windows.Distinct().ToArray();
        var completed = candidates.Length <= 512;
        foreach (var (start, key) in candidates.Take(512))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (scannedItems >= 100_000 || Stopwatch.GetElapsedTime(started) >= TimeSpan.FromMinutes(2))
            {
                completed = false;
                break;
            }
            var snapshot = await calendarCache.GetWeekSnapshotAsync(start, start.AddDays(6), key, cancellationToken);
            if (snapshot is null) continue;
            var items = snapshot.Items.Where(item => item.VerificationState == PremiereVerificationState.Verified
                && item.Type is PremiereItemType.SeriesPremiere or PremiereItemType.MovieFirstRelease
                && item.TmdbId > 0 && item.LastUpdatedUtc >= cutoff).ToArray();
            foreach (var item in items)
            {
                var identity = new ItemEnrichmentIdentity(item.MediaType, item.TmdbId);
                if (!newest.TryGetValue(identity, out var existing) || item.LastUpdatedUtc > existing.LastUpdatedUtc)
                    newest[identity] = item;
            }
            scannedFiles++;
            scannedItems += items.Length;
        }
        await coordinator.ImportAsync(newest.Values.ToArray(), cancellationToken);
        var duration = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        if (completed)
            await appState.SetValueAsync(marker, JsonSerializer.Serialize(new { completedUtc = clock.GetUtcNow(), scannedFiles, scannedItems, uniqueItems = newest.Count, durationMs = duration }), cancellationToken);
        logger.LogInformation("Existing item-cache import scanned {Files} week files and {Items} item rows in {DurationMilliseconds} ms; original success timestamps retained.", scannedFiles, scannedItems, duration);
        telemetry.RecordDiagnostic("itemcache.import.completed", details: new()
        {
            Outcome = completed ? "success" : "bounded", Count = scannedItems, Total = scannedFiles, DurationMilliseconds = duration
        });
    }
}
