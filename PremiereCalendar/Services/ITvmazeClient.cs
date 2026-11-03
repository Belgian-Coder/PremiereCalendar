using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public interface ITvmazeClient
{
    Task<TvmazeShow?> LookupShowAsync(
        int? tvdbId,
        string? imdbId,
        CancellationToken cancellationToken,
        bool forceRefresh = false);

    Task<TvmazeShow?> SearchShowByNameAsync(
        string title,
        CancellationToken cancellationToken,
        bool forceRefresh = false);

    async Task<ProviderRefreshResult<TvmazeShow?>> LookupShowWithStatusAsync(
        int? tvdbId, string? imdbId, CancellationToken cancellationToken, bool forceRefresh = false)
        => new(await LookupShowAsync(tvdbId, imdbId, cancellationToken, forceRefresh), true);

    async Task<ProviderRefreshResult<TvmazeShow?>> SearchShowByNameWithStatusAsync(
        string title, CancellationToken cancellationToken, bool forceRefresh = false)
        => new(await SearchShowByNameAsync(title, cancellationToken, forceRefresh), true);

    async Task<ProviderRefreshResult<IReadOnlyList<TvmazeShowImage>>> GetShowImagesWithStatusAsync(int showId, CancellationToken cancellationToken, bool forceRefresh = false)
        => new(await GetShowImagesAsync(showId, cancellationToken, forceRefresh), true);

    Task<IReadOnlyList<TvmazeShowImage>> GetShowImagesAsync(
        int showId,
        CancellationToken cancellationToken,
        bool forceRefresh = false);

    Task<IReadOnlyList<TvmazeScheduleEpisode>> GetScheduleAsync(
        DateOnly date,
        string? country,
        bool webSchedule,
        CancellationToken cancellationToken,
        bool forceRefresh = false);

    Task<IReadOnlyList<TvmazeShowUpdate>> GetShowUpdatesAsync(
        TvmazeUpdateWindow since,
        CancellationToken cancellationToken,
        bool forceRefresh = false);

    Task<IReadOnlyList<TvmazeShowUpdate>> GetShowUpdatesCheckedAsync(
        TvmazeUpdateWindow since, CancellationToken cancellationToken)
        => GetShowUpdatesAsync(since, cancellationToken);
}
