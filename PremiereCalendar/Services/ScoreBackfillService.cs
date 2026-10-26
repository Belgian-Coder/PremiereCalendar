using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class ScoreBackfillService
{
    private static readonly Regex NonAlphaNumericRegex = new("[^a-z0-9]+", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRegex = new("\\s+", RegexOptions.Compiled);

    private readonly IImdbRatingsStore? _imdbRatingsStore;
    private readonly IOmdbClient _omdbClient;
    private readonly RatingMapper _ratingMapper;
    private readonly IRottenTomatoesClient? _rottenTomatoesClient;
    private readonly ILogger<ScoreBackfillService> _logger;
    private readonly int _maxConcurrency;

    public ScoreBackfillService(
        IImdbRatingsStore? imdbRatingsStore,
        IOmdbClient omdbClient,
        RatingMapper ratingMapper,
        IRottenTomatoesClient? rottenTomatoesClient,
        ILogger<ScoreBackfillService> logger,
        IOptions<TmdbOptions>? options = null)
    {
        _imdbRatingsStore = imdbRatingsStore;
        _omdbClient = omdbClient;
        _ratingMapper = ratingMapper;
        _rottenTomatoesClient = rottenTomatoesClient;
        _logger = logger;
        _maxConcurrency = Math.Clamp(options?.Value.MaxEnrichmentConcurrency ?? new TmdbOptions().MaxEnrichmentConcurrency, 1, 32);
    }

    public async Task<BackfillResult> BackfillItemsAsync(
        IReadOnlyList<PremiereItem> items,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var imdbRatings = await GetImdbRatingsAsync(items, cancellationToken);
        var omdbGroups = GroupItemsNeedingOmdb(items);
        var omdbResults = await RunBoundedAsync(
            omdbGroups,
            async (group, token) => await FetchOmdbRatingsAsync(group, token, forceRefresh),
            cancellationToken);
        var omdbById = new Dictionary<string, ExternalRatings>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < omdbGroups.Count; index++)
        {
            omdbById[omdbGroups[index].ImdbId] = omdbResults[index];
        }

        var rottenTomatoesGroups = GroupItemsNeedingRottenTomatoes(items);
        var rottenTomatoesResults = await RunBoundedAsync(
            rottenTomatoesGroups,
            async (group, token) => await FetchRottenTomatoesScoresAsync(group, token, forceRefresh),
            cancellationToken);
        var rottenTomatoesByKey = new Dictionary<RottenTomatoesKey, RottenTomatoesScores>();
        for (var index = 0; index < rottenTomatoesGroups.Count; index++)
        {
            rottenTomatoesByKey[rottenTomatoesGroups[index].Key] = rottenTomatoesResults[index];
        }

        var changed = 0;
        var backfilled = new List<PremiereItem>(items.Count);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var imdbRating = !string.IsNullOrWhiteSpace(item.ImdbId)
                ? imdbRatings.GetValueOrDefault(item.ImdbId)
                : null;
            var omdbRatings = !string.IsNullOrWhiteSpace(item.ImdbId)
                ? omdbById.GetValueOrDefault(item.ImdbId) ?? new ExternalRatings(null, null)
                : new ExternalRatings(null, null);
            var rottenTomatoesScores = _rottenTomatoesClient is not null
                && !string.IsNullOrWhiteSpace(item.ImdbId)
                && TryCreateRottenTomatoesKey(item, out var rottenTomatoesKey)
                ? rottenTomatoesByKey.GetValueOrDefault(rottenTomatoesKey) ?? RottenTomatoesScores.Empty
                : RottenTomatoesScores.Empty;

            var updated = item with
            {
                ImdbScore = item.ImdbScore ?? imdbRating?.AverageRating ?? omdbRatings.ImdbScore,
                ImdbVoteCount = item.ImdbVoteCount ?? imdbRating?.VoteCount ?? omdbRatings.ImdbVoteCount,
                RottenTomatoesScore = item.RottenTomatoesScore ?? omdbRatings.RottenTomatoesScore ?? rottenTomatoesScores.CriticScore,
                RottenTomatoesAudienceScore = item.RottenTomatoesAudienceScore ?? rottenTomatoesScores.AudienceScore,
                MetacriticScore = item.MetacriticScore ?? omdbRatings.MetacriticScore
            };

            if (!Equals(updated, item))
            {
                changed++;
            }

            backfilled.Add(PremiereDiagnosticsFactory.ApplyMissingDataIssues(updated));
        }

        return new BackfillResult(backfilled, changed, items.Count);
    }

    private async Task<IReadOnlyDictionary<string, ImdbRatingRecord>> GetImdbRatingsAsync(
        IReadOnlyList<PremiereItem> items,
        CancellationToken cancellationToken)
    {
        if (_imdbRatingsStore is null)
        {
            return new Dictionary<string, ImdbRatingRecord>(StringComparer.OrdinalIgnoreCase);
        }

        var imdbIds = items
            .Where(item => !string.IsNullOrWhiteSpace(item.ImdbId)
                && (item.ImdbScore is null || item.ImdbVoteCount is null))
            .Select(item => item.ImdbId!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (imdbIds.Length == 0)
        {
            return new Dictionary<string, ImdbRatingRecord>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var records = await _imdbRatingsStore.GetByImdbIdsAsync(imdbIds, cancellationToken);
            var result = new Dictionary<string, ImdbRatingRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in records)
            {
                result[entry.Key] = entry.Value;
                if (!string.IsNullOrWhiteSpace(entry.Value.ImdbId))
                {
                    result[entry.Value.ImdbId] = entry.Value;
                }
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not batch backfill IMDb dataset ratings.");
            return new Dictionary<string, ImdbRatingRecord>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private async Task<ExternalRatings> FetchOmdbRatingsAsync(
        OmdbGroup group,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        try
        {
            return _ratingMapper.Map(await _omdbClient.GetByImdbIdAsync(group.ImdbId, cancellationToken, forceRefresh));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not backfill OMDb ratings for {ImdbId}.", group.ImdbId);
            return new ExternalRatings(null, null);
        }
    }

    private async Task<RottenTomatoesScores> FetchRottenTomatoesScoresAsync(
        RottenTomatoesGroup group,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        try
        {
            return await _rottenTomatoesClient!.GetScoresAsync(
                group.Key.MediaType,
                group.Title,
                group.Key.Year,
                group.Key.WikidataId,
                cancellationToken,
                forceRefresh);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not backfill Rotten Tomatoes scores for {Title}.", group.Title);
            return RottenTomatoesScores.Empty;
        }
    }

    private static IReadOnlyList<OmdbGroup> GroupItemsNeedingOmdb(IReadOnlyList<PremiereItem> items)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var groups = new List<OmdbGroup>();
        foreach (var item in items)
        {
            if (string.IsNullOrWhiteSpace(item.ImdbId)
                || (item.RottenTomatoesScore is not null
                    && item.MetacriticScore is not null
                    && item.ImdbScore is not null)
                || !seen.Add(item.ImdbId))
            {
                continue;
            }

            groups.Add(new OmdbGroup(item.ImdbId));
        }

        return groups;
    }

    private IReadOnlyList<RottenTomatoesGroup> GroupItemsNeedingRottenTomatoes(IReadOnlyList<PremiereItem> items)
    {
        if (_rottenTomatoesClient is null)
        {
            return [];
        }

        var seen = new HashSet<RottenTomatoesKey>();
        var groups = new List<RottenTomatoesGroup>();
        foreach (var item in items)
        {
            if (item.RottenTomatoesScore is not null && item.RottenTomatoesAudienceScore is not null)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(item.ImdbId)
                && TryCreateRottenTomatoesKey(item, out var key)
                && seen.Add(key))
            {
                groups.Add(new RottenTomatoesGroup(key, item.Title));
            }
        }

        return groups;
    }

    private static bool TryCreateRottenTomatoesKey(PremiereItem item, out RottenTomatoesKey key)
    {
        var normalizedTitle = NormalizeRottenTomatoesTitle(item.Title);
        if (string.IsNullOrWhiteSpace(normalizedTitle))
        {
            key = default;
            return false;
        }

        key = new RottenTomatoesKey(item.MediaType, normalizedTitle, item.PremiereDate.Year, item.WikidataId?.Trim() ?? "");
        return true;
    }

    private static string NormalizeRottenTomatoesTitle(string value)
    {
        var normalized = WebUtility.HtmlDecode(value).ToLowerInvariant();
        normalized = NonAlphaNumericRegex.Replace(normalized, " ");
        return WhitespaceRegex.Replace(normalized, " ").Trim();
    }

    private async Task<TResult[]> RunBoundedAsync<TWork, TResult>(
        IReadOnlyList<TWork> work,
        Func<TWork, CancellationToken, Task<TResult>> execute,
        CancellationToken cancellationToken)
    {
        var results = new TResult[work.Count];
        if (work.Count == 0)
        {
            return results;
        }

        var nextIndex = -1;
        var workerCount = Math.Min(_maxConcurrency, work.Count);
        var workers = Enumerable.Range(0, workerCount).Select(async _ =>
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var index = Interlocked.Increment(ref nextIndex);
                if (index >= work.Count)
                {
                    return;
                }

                results[index] = await execute(work[index], cancellationToken);
            }
        });

        await Task.WhenAll(workers);
        return results;
    }

    private sealed record OmdbGroup(string ImdbId);

    private sealed record RottenTomatoesGroup(RottenTomatoesKey Key, string Title);

    private readonly record struct RottenTomatoesKey(
        PremiereMediaType MediaType,
        string NormalizedTitle,
        int Year,
        string WikidataId);
}
