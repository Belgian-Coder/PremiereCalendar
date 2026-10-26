using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class MissingExternalIdRepairService
{
    private readonly ITmdbClient _tmdbClient;
    private readonly ILogger<MissingExternalIdRepairService> _logger;
    private readonly int _maxConcurrency;

    public MissingExternalIdRepairService(
        ITmdbClient tmdbClient,
        ILogger<MissingExternalIdRepairService> logger,
        IOptions<TmdbOptions>? options = null)
    {
        _tmdbClient = tmdbClient;
        _logger = logger;
        _maxConcurrency = Math.Clamp(options?.Value.MaxEnrichmentConcurrency ?? new TmdbOptions().MaxEnrichmentConcurrency, 1, 32);
    }

    public async Task<BackfillResult> RepairItemsAsync(
        IReadOnlyList<PremiereItem> items,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var groups = GroupItemsNeedingRepair(items);
        var fetchedDetails = await RunBoundedAsync(
            groups,
            async (group, token) => await FetchDetailsAsync(group, token, forceRefresh),
            cancellationToken);

        var detailsByKey = new Dictionary<MediaKey, TmdbDetailsWithExtras?>();
        for (var index = 0; index < groups.Count; index++)
        {
            detailsByKey[groups[index].Key] = fetchedDetails[index];
        }

        var changed = 0;
        var repaired = new List<PremiereItem>(items.Count);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var updated = ShouldRepair(item)
                && detailsByKey.TryGetValue(new MediaKey(item.MediaType, item.TmdbId), out var details)
                ? ApplyExternalIds(item, details?.ExternalIds)
                : item;
            if (!Equals(updated, item))
            {
                changed++;
            }

            repaired.Add(PremiereDiagnosticsFactory.ApplyMissingDataIssues(updated));
        }

        return new BackfillResult(repaired, changed, items.Count);
    }

    private async Task<TmdbDetailsWithExtras?> FetchDetailsAsync(
        RepairGroup group,
        CancellationToken cancellationToken,
        bool forceRefresh)
    {
        try
        {
            return group.Key.MediaType == PremiereMediaType.Movie
                ? await _tmdbClient.GetMovieDetailsAsync(group.Key.TmdbId, cancellationToken, forceRefresh)
                : await _tmdbClient.GetTvDetailsAsync(group.Key.TmdbId, cancellationToken, forceRefresh);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not repair external IDs for {MediaType} {TmdbId}.", group.Key.MediaType, group.Key.TmdbId);
            return null;
        }
    }

    private static IReadOnlyList<RepairGroup> GroupItemsNeedingRepair(IReadOnlyList<PremiereItem> items)
    {
        var seen = new HashSet<MediaKey>();
        var groups = new List<RepairGroup>();
        foreach (var item in items)
        {
            if (!ShouldRepair(item))
            {
                continue;
            }

            var key = new MediaKey(item.MediaType, item.TmdbId);
            if (seen.Add(key))
            {
                groups.Add(new RepairGroup(key));
            }
        }

        return groups;
    }

    private static bool ShouldRepair(PremiereItem item)
    {
        return item.TmdbId > 0
            && (string.IsNullOrWhiteSpace(item.ImdbId)
                || item.MediaType == PremiereMediaType.Series && item.TvdbId is null or <= 0);
    }

    private static PremiereItem ApplyExternalIds(PremiereItem item, TmdbExternalIds? externalIds)
    {
        if (externalIds is null)
        {
            return item;
        }

        var imdbId = string.IsNullOrWhiteSpace(item.ImdbId) ? externalIds.ImdbId : item.ImdbId;
        return item with
        {
            ImdbId = imdbId,
            ImdbUrl = string.IsNullOrWhiteSpace(item.ImdbUrl) && !string.IsNullOrWhiteSpace(imdbId)
                ? $"https://www.imdb.com/title/{Uri.EscapeDataString(imdbId)}/"
                : item.ImdbUrl,
            TvdbId = item.TvdbId ?? externalIds.TvdbId,
            WikidataId = string.IsNullOrWhiteSpace(item.WikidataId) ? externalIds.WikidataId : item.WikidataId
        };
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

    private sealed record RepairGroup(MediaKey Key);

    private readonly record struct MediaKey(PremiereMediaType MediaType, int TmdbId);
}
