using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public sealed class ScoreBackfillService
{
    private readonly IImdbRatingsStore? _imdbRatingsStore;
    private readonly IOmdbClient _omdbClient;
    private readonly RatingMapper _ratingMapper;
    private readonly IRottenTomatoesClient? _rottenTomatoesClient;
    private readonly ILogger<ScoreBackfillService> _logger;

    public ScoreBackfillService(
        IImdbRatingsStore? imdbRatingsStore,
        IOmdbClient omdbClient,
        RatingMapper ratingMapper,
        IRottenTomatoesClient? rottenTomatoesClient,
        ILogger<ScoreBackfillService> logger)
    {
        _imdbRatingsStore = imdbRatingsStore;
        _omdbClient = omdbClient;
        _ratingMapper = ratingMapper;
        _rottenTomatoesClient = rottenTomatoesClient;
        _logger = logger;
    }

    public async Task<BackfillResult> BackfillItemsAsync(
        IReadOnlyList<PremiereItem> items,
        CancellationToken cancellationToken,
        bool forceRefresh = false)
    {
        var changed = 0;
        var backfilled = new List<PremiereItem>(items.Count);
        foreach (var item in items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var updated = await BackfillItemAsync(item, cancellationToken, forceRefresh);
            if (!Equals(updated, item))
            {
                changed++;
            }

            backfilled.Add(PremiereDiagnosticsFactory.ApplyMissingDataIssues(updated));
        }

        return new BackfillResult(backfilled, changed, items.Count);
    }

    private async Task<PremiereItem> BackfillItemAsync(PremiereItem item, CancellationToken cancellationToken, bool forceRefresh)
    {
        if (string.IsNullOrWhiteSpace(item.ImdbId))
        {
            return item;
        }

        ImdbRatingRecord? imdbRating = null;
        if (_imdbRatingsStore is not null && (item.ImdbScore is null || item.ImdbVoteCount is null))
        {
            try
            {
                imdbRating = await _imdbRatingsStore.GetByImdbIdAsync(item.ImdbId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not backfill IMDb dataset rating for {ImdbId}.", item.ImdbId);
            }
        }

        ExternalRatings omdbRatings = new(null, null);
        if (item.RottenTomatoesScore is null || item.MetacriticScore is null || item.ImdbScore is null)
        {
            try
            {
                omdbRatings = _ratingMapper.Map(await _omdbClient.GetByImdbIdAsync(item.ImdbId, cancellationToken, forceRefresh));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not backfill OMDb ratings for {ImdbId}.", item.ImdbId);
            }
        }

        RottenTomatoesScores rtScores = RottenTomatoesScores.Empty;
        if (_rottenTomatoesClient is not null && (item.RottenTomatoesScore is null || item.RottenTomatoesAudienceScore is null))
        {
            try
            {
                rtScores = await _rottenTomatoesClient.GetScoresAsync(
                    item.MediaType,
                    item.Title,
                    item.PremiereDate.Year,
                    item.WikidataId,
                    cancellationToken,
                    forceRefresh);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Could not backfill Rotten Tomatoes scores for {Title}.", item.Title);
            }
        }

        return item with
        {
            ImdbScore = item.ImdbScore ?? imdbRating?.AverageRating ?? omdbRatings.ImdbScore,
            ImdbVoteCount = item.ImdbVoteCount ?? imdbRating?.VoteCount ?? omdbRatings.ImdbVoteCount,
            RottenTomatoesScore = item.RottenTomatoesScore ?? omdbRatings.RottenTomatoesScore ?? rtScores.CriticScore,
            RottenTomatoesAudienceScore = item.RottenTomatoesAudienceScore ?? rtScores.AudienceScore,
            MetacriticScore = item.MetacriticScore ?? omdbRatings.MetacriticScore
        };
    }
}
