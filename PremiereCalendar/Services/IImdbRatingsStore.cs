namespace PremiereCalendar.Services;

public sealed record ImdbRatingRecord(
    string ImdbId,
    double AverageRating,
    int VoteCount,
    DateTimeOffset ImportedAtUtc);

public sealed record ImdbDatasetState(
    DateTimeOffset? LastImportedUtc,
    int RatingCount,
    string? LastError);

public interface IImdbRatingsStore
{
    Task<ImdbRatingRecord?> GetByImdbIdAsync(string imdbId, CancellationToken cancellationToken);

    async Task<IReadOnlyDictionary<string, ImdbRatingRecord>> GetByImdbIdsAsync(
        IReadOnlyCollection<string> imdbIds,
        CancellationToken cancellationToken)
    {
        var ratings = new Dictionary<string, ImdbRatingRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var imdbId in imdbIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var rating = await GetByImdbIdAsync(imdbId, cancellationToken);
            if (rating is not null)
            {
                ratings[rating.ImdbId] = rating;
            }
        }

        return ratings;
    }

    Task ReplaceAllAsync(
        IEnumerable<ImdbRatingRecord> ratings,
        DateTimeOffset importedAtUtc,
        CancellationToken cancellationToken);

    async Task<int> ReplaceAllStreamingAsync(
        IAsyncEnumerable<ImdbRatingRecord> ratings,
        DateTimeOffset importedAtUtc,
        CancellationToken cancellationToken)
    {
        var buffered = new List<ImdbRatingRecord>();
        await foreach (var rating in ratings.WithCancellation(cancellationToken))
        {
            buffered.Add(rating);
        }

        await ReplaceAllAsync(buffered, importedAtUtc, cancellationToken);
        return buffered.Count;
    }

    Task<ImdbDatasetState> GetStateAsync(CancellationToken cancellationToken);

    Task SaveStateAsync(ImdbDatasetState state, CancellationToken cancellationToken);
}
