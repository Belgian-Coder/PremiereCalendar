using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class ProviderDeltaSyncServiceTests
{
    [Fact]
    public async Task RunOnceAsync_TmdbTimeoutDoesNotEscapeSyncCycle()
    {
        var tmdb = new TimeoutTmdbClient();
        var service = new ProviderDeltaSyncService(
            tmdb,
            new EmptyTvmazeClient(),
            new InMemoryProviderCacheStateStore(),
            new FixedOptionsMonitor<ProviderDeltaSyncOptions>(new ProviderDeltaSyncOptions
            {
                Enabled = true,
                RunOnStartup = true,
                StartupDelaySeconds = 0,
                WakeIntervalMinutes = 15,
                UseTmdbChanges = true,
                UseTvmazeUpdates = false
            }),
            TimeProvider.System,
            NullLogger<ProviderDeltaSyncService>.Instance);

        var exception = await Record.ExceptionAsync(() => service.RunOnceAsync(CancellationToken.None));

        Assert.Null(exception);
        Assert.Equal(1, tmdb.ChangedMovieCalls);
    }

    private sealed class FixedOptionsMonitor<T> : IOptionsMonitor<T>
        where T : class
    {
        public FixedOptionsMonitor(T value)
        {
            CurrentValue = value;
        }

        public T CurrentValue { get; }

        public T Get(string? name)
        {
            return CurrentValue;
        }

        public IDisposable? OnChange(Action<T, string?> listener)
        {
            return null;
        }
    }

    private sealed class InMemoryProviderCacheStateStore : IProviderCacheStateStore
    {
        public Task<ProviderCacheState?> GetAsync(
            string provider,
            ProviderCacheScope scope,
            string key,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<ProviderCacheState?>(null);
        }

        public Task SaveAsync(ProviderCacheState state, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class TimeoutTmdbClient : ITmdbClient
    {
        public int ChangedMovieCalls { get; private set; }

        public Task<IReadOnlyList<TmdbChangedItem>> GetChangedMovieIdsAsync(
            DateOnly start,
            DateOnly end,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            ChangedMovieCalls++;
            throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout of 20 seconds elapsing.");
        }

        public Task<IReadOnlyList<TmdbChangedItem>> GetChangedTvIdsAsync(
            DateOnly start,
            DateOnly end,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            return Task.FromResult<IReadOnlyList<TmdbChangedItem>>([]);
        }

        public Task<IReadOnlyList<TmdbTvDiscoverItem>> DiscoverTvAsync(DateOnly start, DateOnly end, TmdbDiscoverFilters filters, CancellationToken cancellationToken, bool forceRefresh = false) => throw new NotImplementedException();
        public IAsyncEnumerable<TmdbDiscoverBatch<TmdbTvDiscoverItem>> StreamDiscoverTvAsync(DateOnly start, DateOnly end, TmdbDiscoverFilters filters, CancellationToken cancellationToken, bool forceRefresh = false) => throw new NotImplementedException();
        public Task<IReadOnlyList<TmdbTvDiscoverItem>> DiscoverTvByNetworksAsync(DateOnly start, DateOnly end, IReadOnlyList<int> networkIds, CancellationToken cancellationToken, bool forceRefresh = false) => throw new NotImplementedException();
        public Task<IReadOnlyList<TmdbMovieDiscoverItem>> DiscoverMoviesAsync(DateOnly start, DateOnly end, TmdbDiscoverFilters filters, CancellationToken cancellationToken, bool forceRefresh = false) => throw new NotImplementedException();
        public IAsyncEnumerable<TmdbDiscoverBatch<TmdbMovieDiscoverItem>> StreamDiscoverMoviesAsync(DateOnly start, DateOnly end, TmdbDiscoverFilters filters, CancellationToken cancellationToken, bool forceRefresh = false) => throw new NotImplementedException();
        public Task<TmdbDetailsWithExtras?> GetTvDetailsAsync(int id, CancellationToken cancellationToken, bool forceRefresh = false) => throw new NotImplementedException();
        public Task<TmdbDetailsWithExtras?> GetMovieDetailsAsync(int id, CancellationToken cancellationToken, bool forceRefresh = false) => throw new NotImplementedException();
        public Task<int?> FindTmdbIdByExternalIdAsync(PremiereMediaType mediaType, string externalId, string externalSource, CancellationToken cancellationToken, bool forceRefresh = false) => throw new NotImplementedException();
        public Task<IReadOnlyList<TmdbTitleSearchResult>> SearchTitlesAsync(PremiereMediaType mediaType, string query, int? year, CancellationToken cancellationToken, bool forceRefresh = false) => throw new NotImplementedException();
        public Task<IReadOnlyList<TmdbGenre>> GetGenresAsync(PremiereMediaType mediaType, CancellationToken cancellationToken, bool forceRefresh = false) => throw new NotImplementedException();
        public Task<IReadOnlyList<TmdbConfigurationLanguage>> GetLanguagesAsync(CancellationToken cancellationToken, bool forceRefresh = false) => throw new NotImplementedException();
        public Task<IReadOnlyList<TmdbConfigurationCountry>> GetCountriesAsync(CancellationToken cancellationToken, bool forceRefresh = false) => throw new NotImplementedException();
        public Task<IReadOnlyList<TmdbWatchProvider>> GetWatchProvidersAsync(PremiereMediaType mediaType, string region, CancellationToken cancellationToken, bool forceRefresh = false) => throw new NotImplementedException();
        public Task<TmdbCertificationResponse?> GetCertificationsAsync(PremiereMediaType mediaType, CancellationToken cancellationToken, bool forceRefresh = false) => throw new NotImplementedException();
        public Task<IReadOnlyList<TmdbKeyword>> SearchKeywordsAsync(string query, CancellationToken cancellationToken, bool forceRefresh = false) => throw new NotImplementedException();
    }

    private sealed class EmptyTvmazeClient : ITvmazeClient
    {
        public Task<IReadOnlyList<TvmazeShowUpdate>> GetShowUpdatesAsync(
            TvmazeUpdateWindow since,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            return Task.FromResult<IReadOnlyList<TvmazeShowUpdate>>([]);
        }

        public Task<TvmazeShow?> LookupShowAsync(int? tvdbId, string? imdbId, CancellationToken cancellationToken, bool forceRefresh = false) => throw new NotImplementedException();
        public Task<TvmazeShow?> SearchShowByNameAsync(string title, CancellationToken cancellationToken, bool forceRefresh = false) => throw new NotImplementedException();
        public Task<IReadOnlyList<TvmazeShowImage>> GetShowImagesAsync(int showId, CancellationToken cancellationToken, bool forceRefresh = false) => throw new NotImplementedException();
        public Task<IReadOnlyList<TvmazeScheduleEpisode>> GetScheduleAsync(DateOnly date, string? country, bool webSchedule, CancellationToken cancellationToken, bool forceRefresh = false) => throw new NotImplementedException();
    }
}
