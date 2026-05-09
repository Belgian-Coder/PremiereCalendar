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

    Task ReplaceAllAsync(
        IEnumerable<ImdbRatingRecord> ratings,
        DateTimeOffset importedAtUtc,
        CancellationToken cancellationToken);

    Task<ImdbDatasetState> GetStateAsync(CancellationToken cancellationToken);

    Task SaveStateAsync(ImdbDatasetState state, CancellationToken cancellationToken);
}
