using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public sealed class MissingExternalIdRepairService
{
    private readonly ITmdbClient _tmdbClient;
    private readonly ILogger<MissingExternalIdRepairService> _logger;

    public MissingExternalIdRepairService(
        ITmdbClient tmdbClient,
        ILogger<MissingExternalIdRepairService> logger)
    {
        _tmdbClient = tmdbClient;
        _logger = logger;
    }

    public async Task<BackfillResult> RepairItemsAsync(
        IReadOnlyList<PremiereItem> items,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        var changed = 0;
        var repaired = new List<PremiereItem>(items.Count);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var updated = await RepairItemAsync(item, cancellationToken, forceRefresh);
            if (!Equals(updated, item))
            {
                changed++;
            }

            repaired.Add(PremiereDiagnosticsFactory.ApplyMissingDataIssues(updated));
        }

        return new BackfillResult(repaired, changed, items.Count);
    }

    private async Task<PremiereItem> RepairItemAsync(PremiereItem item, CancellationToken cancellationToken, bool forceRefresh)
    {
        if (item.TmdbId <= 0 || (!string.IsNullOrWhiteSpace(item.ImdbId) && (item.MediaType == PremiereMediaType.Movie || item.TvdbId is > 0)))
        {
            return item;
        }

        try
        {
            var details = item.MediaType == PremiereMediaType.Movie
                ? await _tmdbClient.GetMovieDetailsAsync(item.TmdbId, cancellationToken, forceRefresh)
                : await _tmdbClient.GetTvDetailsAsync(item.TmdbId, cancellationToken, forceRefresh);
            if (details?.ExternalIds is not { } externalIds)
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not repair external IDs for {MediaType} {TmdbId}.", item.MediaType, item.TmdbId);
            return item;
        }
    }
}
