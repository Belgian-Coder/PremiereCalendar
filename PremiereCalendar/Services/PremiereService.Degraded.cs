using System.Diagnostics;
using System.Text.Json;
using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public sealed partial class PremiereService
{
    private static IReadOnlyList<PremiereItem> PreserveUndiscoveredRows(IReadOnlyList<PremiereItem> incoming,
        IReadOnlyList<PremiereItem> prior)
    {
        var refreshedIds = incoming.Where(item => !string.IsNullOrWhiteSpace(item.CanonicalId))
            .Select(item => item.CanonicalId).ToHashSet(StringComparer.Ordinal);
        return incoming.Concat(prior.Where(item => string.IsNullOrWhiteSpace(item.CanonicalId)
            || !refreshedIds.Contains(item.CanonicalId))).ToArray();
    }

    private static SourceUnavailableNotice? OptionalAccessDenial(string sourceName, Exception error)
    {
        var leaves = Unwrap(error).ToArray();
        if (leaves.Length == 0) return null;
        var failures = leaves.Select(ProviderFailure.FromException).ToArray();
        if (failures.Any(failure => failure.StatusCode is not (401 or 403)
            || failure.RetryAtUtc is not { } retry || retry <= DateTimeOffset.UtcNow)) return null;
        var notice = new SourceUnavailableNotice(ProviderKeyForSource(sourceName), "authentication_denied",
            failures[0].StatusCode!.Value, failures.Min(failure => failure.RetryAtUtc)!.Value);
        return notice.IsOptionalAccessDenial ? notice : null;
    }

    private static IEnumerable<Exception> Unwrap(Exception error)
    {
        if (error is AggregateException aggregate)
        {
            foreach (var inner in aggregate.InnerExceptions)
                foreach (var leaf in Unwrap(inner)) yield return leaf;
        }
        else if (error.InnerException is { } inner && error is not ProviderRequestException)
        {
            foreach (var leaf in Unwrap(inner)) yield return leaf;
        }
        else yield return error;
    }

    private static PremiereLoadProgress WithDegradedCoverage(PremiereLoadProgress progress,
        IReadOnlyList<SourceUnavailableNotice> unavailableSources, bool reusable)
        => progress with
        {
            HasSourceErrors = true, UnavailableSources = unavailableSources, IsReusableDegraded = reusable,
            IsDegradedResult = reusable || progress.IsDegradedResult,
            FailedSourceNames = unavailableSources.Select(source => source.DisplayName).Distinct().ToArray(),
            RetryAfterUtc = unavailableSources.Min(source => source.RetryAtUtc), Phase = "degraded",
            ProgressText = "Some sources are temporarily unavailable; results may be incomplete."
        };

    private async Task<bool> SaveDegradedCalendarCacheAsync(DateOnly start, DateOnly end, CalendarFilters? filters,
        PremiereDiscoveryCriteria criteria, IReadOnlyList<PremiereItem> items,
        IReadOnlyList<SourceUnavailableNotice> unavailableSources, IReadOnlyDictionary<string, string?> baselines, DateTimeOffset refreshStartedUtc,
        ProviderWorkResumeState? resumeState, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var saved = false;
        try
        {
            await PublishCalendarCacheAsync(resumeState, async token =>
            {
                if (!ShouldUseSharedMediaCache(criteria))
                    saved = await _cacheOrchestrator.WriteDegradedAsync(start, end, criteria.CacheKey(), items, unavailableSources,
                        refreshStartedUtc, token, baselines.GetValueOrDefault(criteria.CacheKey()));
                else
                {
                    saved = true;
                    foreach (var mode in new[] { CalendarPageMode.Series, CalendarPageMode.Movies })
                    {
                        var mediaFilters = FiltersForPageMode(filters, mode);
                        var media = mode == CalendarPageMode.Series ? PremiereMediaType.Series : PremiereMediaType.Movie;
                        var key = PremiereDiscoveryCriteria.FromFilters(mediaFilters).CacheKey();
                        saved &= await _cacheOrchestrator.WriteDegradedAsync(start, end,
                            key, items.Where(item => item.MediaType == media).ToArray(),
                            unavailableSources, refreshStartedUtc, token, baselines.GetValueOrDefault(key));
                    }
                }
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Could not persist unavailable-source calendar coverage.");
            _telemetry.RecordDiagnostic("calendar.cache.degraded", details: new()
            {
                Outcome = "failed", Reason = ex.GetType().Name, Count = items.Count, CacheKey = criteria.CacheKey(),
                WeekStart = start, WeekEnd = end, DurationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds
            });
            return false;
        }
        _telemetry.RecordDiagnostic("calendar.cache.degraded", details: new()
        {
            Outcome = saved ? "saved" : "not_saved", Reason = "optional_source_access_denied", Count = items.Count,
            CacheKey = criteria.CacheKey(), WeekStart = start, WeekEnd = end,
            RetryAtUtc = unavailableSources.Min(source => source.RetryAtUtc),
            DurationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds
        });
        return saved;
    }

    private async Task<IReadOnlyDictionary<string, string?>> CaptureDegradedBaselinesAsync(DateOnly start, DateOnly end,
        CalendarFilters? filters, PremiereDiscoveryCriteria criteria, CancellationToken cancellationToken)
    {
        var keys = ShouldUseSharedMediaCache(criteria)
            ? new[] { CalendarPageMode.Series, CalendarPageMode.Movies }.Select(mode =>
                PremiereDiscoveryCriteria.FromFilters(FiltersForPageMode(filters, mode)).CacheKey()).ToArray()
            : [criteria.CacheKey()];
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var key in keys)
            result[key] = (await _cacheOrchestrator.ReadSnapshotAsync(start, end, key, cancellationToken))?.Generation;
        return result;
    }
}
