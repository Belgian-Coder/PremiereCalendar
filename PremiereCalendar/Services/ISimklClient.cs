namespace PremiereCalendar.Services;

public interface ISimklClient
{
    Task<SimklSyncResult> SyncLibraryAsync(CancellationToken cancellationToken, bool forceRefresh = false);

    Task<SimklPinCodeResult> RequestPinCodeAsync(CancellationToken cancellationToken);

    Task<SimklPinStatusResult> CheckPinCodeAsync(string userCode, CancellationToken cancellationToken);

    Task<IReadOnlyList<SimklCalendarItem>> GetCalendarAsync(
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken,
        bool forceRefresh = false);
}

public enum SimklSyncStatus
{
    Disabled,
    Throttled,
    Unchanged,
    InitialSyncCompleted,
    DeltaSyncCompleted,
    Failed
}

public sealed record SimklSyncResult(
    SimklSyncStatus Status,
    string? ActivitiesAllUtc = null,
    string? Error = null);

public enum SimklPinStatus
{
    Authorized,
    Pending,
    SlowDown,
    Disabled,
    Failed
}

public sealed record SimklPinCodeResult(
    bool Success,
    string? UserCode = null,
    string? VerificationUrl = null,
    int ExpiresInSeconds = 0,
    int IntervalSeconds = 5,
    string? Error = null);

public sealed record SimklPinStatusResult(
    SimklPinStatus Status,
    string? AccessToken = null,
    string? Message = null);

public enum SimklCalendarItemType
{
    Tv,
    MovieRelease
}

public sealed record SimklCalendarItem(
    SimklCalendarItemType Type,
    string? Title,
    DateTimeOffset Date,
    DateOnly? ReleaseDate,
    string? Url,
    SimklCalendarIds Ids,
    SimklCalendarRatings? Ratings,
    SimklCalendarEpisode? Episode);

public sealed record SimklCalendarIds(
    int? SimklId,
    string? Tmdb,
    string? Imdb,
    string? Tvdb);

public sealed record SimklCalendarRatings(SimklRating? Imdb);

public sealed record SimklRating(double? Rating, int? Votes);

public sealed record SimklCalendarEpisode(int? Season, int? Episode, string? Url);
