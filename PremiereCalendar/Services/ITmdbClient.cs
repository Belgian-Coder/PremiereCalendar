using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public interface ITmdbClient
{
    Task<IReadOnlyList<TmdbTvDiscoverItem>> DiscoverTvAsync(
        DateOnly start,
        DateOnly end,
        TmdbDiscoverFilters filters,
        CancellationToken cancellationToken,
        bool forceRefresh = false);

    IAsyncEnumerable<TmdbDiscoverBatch<TmdbTvDiscoverItem>> StreamDiscoverTvAsync(
        DateOnly start,
        DateOnly end,
        TmdbDiscoverFilters filters,
        CancellationToken cancellationToken,
        bool forceRefresh = false);

    Task<IReadOnlyList<TmdbTvDiscoverItem>> DiscoverTvByNetworksAsync(
        DateOnly start,
        DateOnly end,
        IReadOnlyList<int> networkIds,
        CancellationToken cancellationToken,
        bool forceRefresh = false);

    Task<IReadOnlyList<TmdbMovieDiscoverItem>> DiscoverMoviesAsync(
        DateOnly start,
        DateOnly end,
        TmdbDiscoverFilters filters,
        CancellationToken cancellationToken,
        bool forceRefresh = false);

    IAsyncEnumerable<TmdbDiscoverBatch<TmdbMovieDiscoverItem>> StreamDiscoverMoviesAsync(
        DateOnly start,
        DateOnly end,
        TmdbDiscoverFilters filters,
        CancellationToken cancellationToken,
        bool forceRefresh = false);

    Task<TmdbDetailsWithExtras?> GetTvDetailsAsync(int id, CancellationToken cancellationToken, bool forceRefresh = false);

    Task<TmdbDetailsWithExtras?> GetMovieDetailsAsync(int id, CancellationToken cancellationToken, bool forceRefresh = false);

    Task<int?> FindTmdbIdByExternalIdAsync(
        PremiereMediaType mediaType,
        string externalId,
        string externalSource,
        CancellationToken cancellationToken,
        bool forceRefresh = false);

    Task<IReadOnlyList<TmdbTitleSearchResult>> SearchTitlesAsync(
        PremiereMediaType mediaType,
        string query,
        int? year,
        CancellationToken cancellationToken,
        bool forceRefresh = false);

    Task<IReadOnlyList<TmdbGenre>> GetGenresAsync(PremiereMediaType mediaType, CancellationToken cancellationToken, bool forceRefresh = false);

    Task<IReadOnlyList<TmdbConfigurationLanguage>> GetLanguagesAsync(CancellationToken cancellationToken, bool forceRefresh = false);

    Task<IReadOnlyList<TmdbConfigurationCountry>> GetCountriesAsync(CancellationToken cancellationToken, bool forceRefresh = false);

    Task<IReadOnlyList<TmdbWatchProvider>> GetWatchProvidersAsync(
        PremiereMediaType mediaType,
        string region,
        CancellationToken cancellationToken,
        bool forceRefresh = false);

    Task<TmdbCertificationResponse?> GetCertificationsAsync(
        PremiereMediaType mediaType,
        CancellationToken cancellationToken,
        bool forceRefresh = false);

    Task<IReadOnlyList<TmdbKeyword>> SearchKeywordsAsync(
        string query,
        CancellationToken cancellationToken,
        bool forceRefresh = false);

    Task<IReadOnlyList<TmdbChangedItem>> GetChangedMovieIdsAsync(
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken,
        bool forceRefresh = false);

    Task<IReadOnlyList<TmdbChangedItem>> GetChangedTvIdsAsync(
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken,
        bool forceRefresh = false);
}
