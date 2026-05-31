using System.Text.Json;
using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public interface IWeekDiagnosticsStore
{
    Task<WeekDiagnostics?> GetAsync(DateOnly weekStart, string cacheKey, CancellationToken cancellationToken);

    Task<IReadOnlyList<WeekDiagnostics>> GetRecentAsync(int take, CancellationToken cancellationToken);

    Task SaveAsync(WeekDiagnostics diagnostics, CancellationToken cancellationToken);
}

public sealed class AppStateWeekDiagnosticsStore : IWeekDiagnosticsStore
{
    private const string Prefix = "Diagnostics.Week.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly IAppStateStore _store;

    public AppStateWeekDiagnosticsStore(IAppStateStore store)
    {
        _store = store;
    }

    public async Task<WeekDiagnostics?> GetAsync(DateOnly weekStart, string cacheKey, CancellationToken cancellationToken)
    {
        var json = await _store.GetValueAsync(Key(weekStart, cacheKey), cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<WeekDiagnostics>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<WeekDiagnostics>> GetRecentAsync(int take, CancellationToken cancellationToken)
    {
        var values = await _store.GetValuesByPrefixAsync(Prefix, cancellationToken);
        return values.Values
            .Select(static json =>
            {
                try
                {
                    return JsonSerializer.Deserialize<WeekDiagnostics>(json, JsonOptions);
                }
                catch (JsonException)
                {
                    return null;
                }
            })
            .Where(static diagnostics => diagnostics is not null)
            .Select(static diagnostics => diagnostics!)
            .OrderByDescending(diagnostics => diagnostics.RecordedUtc)
            .ThenByDescending(diagnostics => diagnostics.WeekStart)
            .Take(Math.Clamp(take, 1, 200))
            .ToArray();
    }

    public Task SaveAsync(WeekDiagnostics diagnostics, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(diagnostics, JsonOptions);
        return _store.SetValueAsync(Key(diagnostics.WeekStart, diagnostics.CacheKey), json, cancellationToken);
    }

    private static string Key(DateOnly weekStart, string cacheKey)
    {
        var safeCacheKey = string.Concat(cacheKey.Where(character => char.IsLetterOrDigit(character) || character is '-' or '_' or ':'));
        return $"{Prefix}{weekStart:yyyyMMdd}.{safeCacheKey}";
    }
}

public sealed class WeekDiagnosticsService
{
    private const int LowItemCountThreshold = 6;
    private const double HighMissingScoreThreshold = 0.60;
    private const double HighMissingExternalIdThreshold = 0.35;
    private const double LanguageSkewThreshold = 0.80;

    private readonly IWeekDiagnosticsStore _store;
    private readonly TimeProvider _timeProvider;

    public WeekDiagnosticsService(IWeekDiagnosticsStore store, TimeProvider timeProvider)
    {
        _store = store;
        _timeProvider = timeProvider;
    }

    public async Task<WeekDiagnostics> RecordAsync(
        DateOnly weekStart,
        DateOnly weekEnd,
        string cacheKey,
        IReadOnlyList<PremiereItem> items,
        IReadOnlyList<PremiereLoadProgress> progress,
        CancellationToken cancellationToken)
    {
        var previous = await _store.GetAsync(weekStart, cacheKey, cancellationToken);
        var diagnostics = Build(weekStart, weekEnd, cacheKey, items, progress, previous);
        await _store.SaveAsync(diagnostics, cancellationToken);
        return diagnostics;
    }

    public Task<WeekDiagnostics?> GetAsync(DateOnly weekStart, string cacheKey, CancellationToken cancellationToken)
    {
        return _store.GetAsync(weekStart, cacheKey, cancellationToken);
    }

    public Task<IReadOnlyList<WeekDiagnostics>> GetRecentAsync(int take, CancellationToken cancellationToken)
    {
        return _store.GetRecentAsync(take, cancellationToken);
    }

    private WeekDiagnostics Build(
        DateOnly weekStart,
        DateOnly weekEnd,
        string cacheKey,
        IReadOnlyList<PremiereItem> items,
        IReadOnlyList<PremiereLoadProgress> progress,
        WeekDiagnostics? previous)
    {
        var sources = progress
            .Where(update => !string.Equals(update.SourceName, "Complete", StringComparison.OrdinalIgnoreCase))
            .GroupBy(update => update.SourceName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Last())
            .Select(update => new WeekSourceDiagnostic(
                update.SourceName,
                update.ProviderKey,
                update.SourceItemCount,
                update.TotalItemCount,
                update.AcceptedCount,
                update.TotalCandidateCount,
                update.ProcessedCount,
                update.FilteredCount,
                update.UnmappedCount,
                update.Phase,
                update.ProgressText,
                update.HasSourceErrors))
            .OrderBy(source => source.SourceName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var languageDistribution = items
            .GroupBy(item => string.IsNullOrWhiteSpace(item.OriginalLanguage) ? "unknown" : item.OriginalLanguage.Trim().ToLowerInvariant())
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);

        var scoreCoverage = new WeekScoreCoverage(
            items.Count,
            items.Count(item => item.TmdbScore is not null),
            items.Count(item => item.ImdbScore is not null),
            items.Count(item => item.RottenTomatoesScore is not null),
            items.Count(item => item.RottenTomatoesAudienceScore is not null),
            items.Count(item => item.MetacriticScore is not null));

        var anomalies = BuildAnomalies(items, sources, languageDistribution, scoreCoverage, previous);
        return new WeekDiagnostics
        {
            WeekStart = weekStart,
            WeekEnd = weekEnd,
            CacheKey = cacheKey,
            RecordedUtc = _timeProvider.GetUtcNow(),
            TotalItemCount = items.Count,
            LanguageDistribution = languageDistribution,
            ScoreCoverage = scoreCoverage,
            Sources = sources,
            Anomalies = anomalies
        };
    }

    private static IReadOnlyList<WeekAnomaly> BuildAnomalies(
        IReadOnlyList<PremiereItem> items,
        IReadOnlyList<WeekSourceDiagnostic> sources,
        IReadOnlyDictionary<string, int> languageDistribution,
        WeekScoreCoverage scoreCoverage,
        WeekDiagnostics? previous)
    {
        var anomalies = new List<WeekAnomaly>();
        if (items.Count > 0 && items.Count < LowItemCountThreshold)
        {
            anomalies.Add(new WeekAnomaly(
                WeekAnomalyKind.LowItemCount,
                "warning",
                $"Only {items.Count:N0} items are available for this week and filter set."));
        }

        if (items.Count > 0 && (double)scoreCoverage.MissingImdbCount / items.Count >= HighMissingScoreThreshold)
        {
            anomalies.Add(new WeekAnomaly(
                WeekAnomalyKind.HighMissingScoreRate,
                "info",
                $"{scoreCoverage.MissingImdbCount:N0} of {items.Count:N0} items are missing IMDb scores."));
        }

        var missingExternalIds = items.Count(item => string.IsNullOrWhiteSpace(item.ImdbId));
        if (items.Count > 0 && (double)missingExternalIds / items.Count >= HighMissingExternalIdThreshold)
        {
            anomalies.Add(new WeekAnomaly(
                WeekAnomalyKind.HighMissingExternalIdRate,
                "info",
                $"{missingExternalIds:N0} of {items.Count:N0} items are missing IMDb IDs."));
        }

        var largestLanguageGroup = languageDistribution.Values.DefaultIfEmpty(0).Max();
        if (items.Count > 0 && (double)largestLanguageGroup / items.Count >= LanguageSkewThreshold)
        {
            var language = languageDistribution
                .OrderByDescending(entry => entry.Value)
                .First()
                .Key;
            anomalies.Add(new WeekAnomaly(
                WeekAnomalyKind.LanguageSkew,
                "info",
                $"{largestLanguageGroup:N0} of {items.Count:N0} items share original language {language.ToUpperInvariant()}."));
        }

        var unmapped = sources.Sum(source => source.UnmappedCount ?? 0);
        if (unmapped > 0)
        {
            anomalies.Add(new WeekAnomaly(
                WeekAnomalyKind.UnmappedExternalCandidates,
                "info",
                $"{unmapped:N0} external candidates did not map to TMDb-backed cards."));
        }

        if (sources.Any(source => source.HasErrors))
        {
            anomalies.Add(new WeekAnomaly(
                WeekAnomalyKind.SourceFailure,
                "warning",
                "One or more sources failed or timed out while loading this week."));
        }

        if (previous is not null && previous.TotalItemCount > 0 && items.Count < previous.TotalItemCount / 2)
        {
            anomalies.Add(new WeekAnomaly(
                WeekAnomalyKind.SourceContributionDrop,
                "warning",
                $"Item count dropped from {previous.TotalItemCount:N0} to {items.Count:N0} since the previous diagnostics snapshot."));
        }

        return anomalies;
    }
}
