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

    [Fact]
    public async Task RunOnceSafelyAsync_RecordsFailureWhenProviderFailsInternally()
    {
        var appStateStore = new InMemoryAppStateStore();
        var timeline = new BackgroundJobTimelineService(appStateStore, TimeProvider.System);
        var service = new ProviderDeltaSyncService(
            new TimeoutTmdbClient(),
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
            NullLogger<ProviderDeltaSyncService>.Instance,
            timeline);

        await service.RunOnceSafelyAsync(CancellationToken.None);

        var events = await timeline.GetRecentAsync(CancellationToken.None);
        var failure = Assert.Single(events, entry => entry.Status == BackgroundJobStatus.Failed);
        Assert.Contains("TMDb change tracking timed out", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(events, entry => entry.Status == BackgroundJobStatus.Succeeded);
    }

    [Fact]
    public async Task RunOnceAsync_TmdbLookbackUsesInclusiveFourteenDayRange()
    {
        var tmdb = new RecordingTmdbClient();
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
                TmdbLookbackDays = 14,
                UseTmdbChanges = true,
                UseTvmazeUpdates = false
            }),
            new FixedTimeProvider(DateTimeOffset.Parse("2026-05-16T12:00:00Z")),
            NullLogger<ProviderDeltaSyncService>.Instance);

        await service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(new DateOnly(2026, 5, 3), tmdb.MovieStart);
        Assert.Equal(new DateOnly(2026, 5, 16), tmdb.MovieEnd);
        Assert.Equal(new DateOnly(2026, 5, 3), tmdb.TvStart);
        Assert.Equal(new DateOnly(2026, 5, 16), tmdb.TvEnd);
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

    private sealed class InMemoryAppStateStore : IAppStateStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken)
        {
            _values.TryGetValue(key, out var value);
            return Task.FromResult(value);
        }

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken)
        {
            _values.Remove(key);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<string, string>> GetValuesByPrefixAsync(string prefix, CancellationToken cancellationToken)
        {
            IReadOnlyDictionary<string, string> values = _values
                .Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal))
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
            return Task.FromResult(values);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow()
        {
            return utcNow;
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

    private sealed class RecordingTmdbClient : ITmdbClient
    {
        public DateOnly? MovieStart { get; private set; }

        public DateOnly? MovieEnd { get; private set; }

        public DateOnly? TvStart { get; private set; }

        public DateOnly? TvEnd { get; private set; }

        public Task<IReadOnlyList<TmdbChangedItem>> GetChangedMovieIdsAsync(
            DateOnly start,
            DateOnly end,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            MovieStart = start;
            MovieEnd = end;
            return Task.FromResult<IReadOnlyList<TmdbChangedItem>>([]);
        }

        public Task<IReadOnlyList<TmdbChangedItem>> GetChangedTvIdsAsync(
            DateOnly start,
            DateOnly end,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            TvStart = start;
            TvEnd = end;
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
