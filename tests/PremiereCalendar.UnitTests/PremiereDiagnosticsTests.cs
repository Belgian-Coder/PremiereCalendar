using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class PremiereDiagnosticsTests
{
    [Fact]
    public async Task WeekDiagnosticsService_FlagsLowCountMissingScoresAndLanguageSkew()
    {
        var store = new InMemoryWeekDiagnosticsStore();
        var service = new WeekDiagnosticsService(store, TimeProvider.System);
        var weekStart = new DateOnly(2026, 5, 25);
        var items = Enumerable.Range(1, 5)
            .Select(index => new PremiereItem
            {
                CanonicalId = $"tv:{index}",
                Type = PremiereItemType.SeriesPremiere,
                MediaType = PremiereMediaType.Series,
                TmdbId = index,
                Title = $"Dutch Show {index}",
                PremiereDate = weekStart,
                OriginalLanguage = "nl",
                TmdbScore = index == 1 ? 6.8 : null
            })
            .ToArray();

        var diagnostics = await service.RecordAsync(
            weekStart,
            weekStart.AddDays(6),
            "series-new-nl-en",
            items,
            [
                new PremiereLoadProgress("TMDb series", 5, 5, items, IsFinal: true)
                {
                    UnmappedCount = 2
                }
            ],
            CancellationToken.None);

        Assert.Contains(diagnostics.Anomalies, anomaly => anomaly.Kind == WeekAnomalyKind.LowItemCount);
        Assert.Contains(diagnostics.Anomalies, anomaly => anomaly.Kind == WeekAnomalyKind.HighMissingScoreRate);
        Assert.Contains(diagnostics.Anomalies, anomaly => anomaly.Kind == WeekAnomalyKind.LanguageSkew);
        Assert.Contains(diagnostics.Anomalies, anomaly => anomaly.Kind == WeekAnomalyKind.UnmappedExternalCandidates);
        Assert.Equal(5, diagnostics.LanguageDistribution["nl"]);
        Assert.True(diagnostics.ScoreCoverage.MissingImdbCount >= 5);
        Assert.Same(diagnostics, await store.GetAsync(weekStart, "series-new-nl-en", CancellationToken.None));
    }

    [Fact]
    public async Task SourceHealthService_CombinesProviderCacheOmdbAndBackgroundJobs()
    {
        var providerStore = new InMemoryProviderCacheStateStore();
        var appState = new InMemoryAppStateStore();
        var timeline = new BackgroundJobTimelineService(appState, TimeProvider.System);
        var omdb = new FakeOmdbCacheStore
        {
            State = new OmdbProviderCacheState(
                RateLimitedUntilUtc: DateTimeOffset.Parse("2026-05-31T12:30:00Z"),
                LastError: "429 Too Many Requests",
                LastFailureUtc: DateTimeOffset.Parse("2026-05-31T12:00:00Z"))
        };
        await providerStore.SaveAsync(
            new ProviderCacheState(
                "tmdb",
                ProviderCacheScope.Week,
                "20260525:series",
                DateTimeOffset.Parse("2026-05-31T11:00:00Z"),
                DateTimeOffset.Parse("2026-05-31T10:30:00Z"),
                "2026-05-31",
                42,
                null),
            CancellationToken.None);
        await timeline.RecordAsync(
            "Provider delta sync",
            BackgroundJobStatus.Failed,
            "TMDb change tracking timed out.",
            DateTimeOffset.Parse("2026-05-31T11:05:00Z"),
            TimeSpan.FromSeconds(20),
            CancellationToken.None);
        var service = new SourceHealthService(
            providerStore,
            timeline,
            omdb,
            imdbRatingsStore: null,
            TimeProvider.System);

        var health = await service.GetOverviewAsync(CancellationToken.None);

        Assert.Contains(health.Providers, provider => provider.Provider == "tmdb" && provider.ItemCount == 42);
        Assert.Equal("429 Too Many Requests", health.Omdb?.LastError);
        Assert.Contains(health.RecentJobs, job => job.Status == BackgroundJobStatus.Failed);
    }

    [Fact]
    public async Task ScoreBackfillService_HydratesMissingScoresFromImdbOmdbAndRottenTomatoes()
    {
        var imdb = new FakeImdbRatingsStore();
        imdb.Ratings["tt1234567"] = new ImdbRatingRecord("tt1234567", 7.8, 1200, DateTimeOffset.UtcNow);
        var omdb = new FakeOmdbClient
        {
            ItemsByImdbId =
            {
                ["tt1234567"] = new OmdbItem
                {
                    Response = "True",
                    ImdbRating = "7.1",
                    ImdbVotes = "900",
                    Metascore = "68",
                    Ratings = [new OmdbRating { Source = "Rotten Tomatoes", Value = "81%" }]
                }
            }
        };
        var rottenTomatoes = new FakeRottenTomatoesClient
        {
            Scores = new RottenTomatoesScores(81, 92)
        };
        var service = new ScoreBackfillService(imdb, omdb, new RatingMapper(), rottenTomatoes, NullLogger<ScoreBackfillService>.Instance);
        var item = new PremiereItem
        {
            CanonicalId = "movie:10",
            Type = PremiereItemType.MovieFirstRelease,
            MediaType = PremiereMediaType.Movie,
            TmdbId = 10,
            ImdbId = "tt1234567",
            Title = "Backfill Movie",
            PremiereDate = new DateOnly(2026, 5, 25)
        };

        var result = await service.BackfillItemsAsync([item], CancellationToken.None, forceRefresh: true);

        var backfilled = Assert.Single(result.Items);
        Assert.Equal(7.8, backfilled.ImdbScore);
        Assert.Equal(1200, backfilled.ImdbVoteCount);
        Assert.Equal(81, backfilled.RottenTomatoesScore);
        Assert.Equal(92, backfilled.RottenTomatoesAudienceScore);
        Assert.Equal(68, backfilled.MetacriticScore);
        Assert.Equal(1, result.ChangedCount);
    }

    [Fact]
    public async Task MissingExternalIdRepairService_FillsIdsFromTmdbDetails()
    {
        var tmdb = new FakeTmdbClient();
        tmdb.TvDetailsById[15] = new TmdbDetailsWithExtras
        {
            Id = 15,
            ExternalIds = new TmdbExternalIds
            {
                ImdbId = "tt7654321",
                TvdbId = 987
            }
        };
        var service = new MissingExternalIdRepairService(tmdb, NullLogger<MissingExternalIdRepairService>.Instance);
        var item = new PremiereItem
        {
            CanonicalId = "tv:15",
            Type = PremiereItemType.SeriesPremiere,
            MediaType = PremiereMediaType.Series,
            TmdbId = 15,
            Title = "Missing IDs",
            PremiereDate = new DateOnly(2026, 5, 25)
        };

        var result = await service.RepairItemsAsync([item], CancellationToken.None, forceRefresh: false);

        var repaired = Assert.Single(result.Items);
        Assert.Equal("tt7654321", repaired.ImdbId);
        Assert.Equal(987, repaired.TvdbId);
        Assert.Equal(1, result.ChangedCount);
    }

    private sealed class InMemoryWeekDiagnosticsStore : IWeekDiagnosticsStore
    {
        private readonly Dictionary<string, WeekDiagnostics> _items = new(StringComparer.Ordinal);

        public Task<WeekDiagnostics?> GetAsync(DateOnly weekStart, string cacheKey, CancellationToken cancellationToken)
        {
            return Task.FromResult(_items.GetValueOrDefault(Key(weekStart, cacheKey)));
        }

        public Task<IReadOnlyList<WeekDiagnostics>> GetRecentAsync(int take, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<WeekDiagnostics>>(
                _items.Values
                    .OrderByDescending(item => item.RecordedUtc)
                    .Take(take)
                    .ToArray());
        }

        public Task SaveAsync(WeekDiagnostics diagnostics, CancellationToken cancellationToken)
        {
            _items[Key(diagnostics.WeekStart, diagnostics.CacheKey)] = diagnostics;
            return Task.CompletedTask;
        }

        private static string Key(DateOnly weekStart, string cacheKey)
        {
            return $"{weekStart:yyyyMMdd}:{cacheKey}";
        }
    }

    private sealed class InMemoryProviderCacheStateStore : IProviderCacheStateStore
    {
        private readonly List<ProviderCacheState> _states = [];

        public Task<ProviderCacheState?> GetAsync(string provider, ProviderCacheScope scope, string key, CancellationToken cancellationToken)
        {
            return Task.FromResult(_states.LastOrDefault(state =>
                string.Equals(state.Provider, provider, StringComparison.OrdinalIgnoreCase)
                && state.Scope == scope
                && string.Equals(state.Key, key, StringComparison.Ordinal)));
        }

        public Task<IReadOnlyList<ProviderCacheState>> GetRecentAsync(int take, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<ProviderCacheState>>(
                _states
                    .OrderByDescending(state => state.LastCheckedUtc)
                    .Take(take)
                    .ToArray());
        }

        public Task<IReadOnlyList<ProviderCacheState>> GetByProviderAsync(string provider, int take, CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<ProviderCacheState>>(
                _states
                    .Where(state => string.Equals(state.Provider, provider, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(state => state.LastCheckedUtc)
                    .Take(take)
                    .ToArray());
        }

        public Task SaveAsync(ProviderCacheState state, CancellationToken cancellationToken)
        {
            _states.Add(state);
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryAppStateStore : IAppStateStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken)
        {
            return Task.FromResult(_values.GetValueOrDefault(key));
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
            return Task.FromResult<IReadOnlyDictionary<string, string>>(
                _values
                    .Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal))
                    .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal));
        }
    }

    private sealed class FakeOmdbCacheStore : IOmdbCacheStore
    {
        public OmdbProviderCacheState State { get; init; } = new(null, null, null);

        public Task<OmdbCacheEntry?> GetAsync(string imdbId, CancellationToken cancellationToken)
        {
            return Task.FromResult<OmdbCacheEntry?>(null);
        }

        public Task SetAsync(string imdbId, OmdbItem item, DateTimeOffset cachedAtUtc, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<OmdbProviderCacheState> GetProviderStateAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(State);
        }

        public Task MarkRateLimitedAsync(DateTimeOffset untilUtc, string error, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task MarkFailureAsync(string error, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeImdbRatingsStore : IImdbRatingsStore
    {
        public Dictionary<string, ImdbRatingRecord> Ratings { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<ImdbRatingRecord?> GetByImdbIdAsync(string imdbId, CancellationToken cancellationToken)
        {
            return Task.FromResult(Ratings.GetValueOrDefault(imdbId));
        }

        public Task ReplaceAllAsync(IEnumerable<ImdbRatingRecord> ratings, DateTimeOffset importedAtUtc, CancellationToken cancellationToken)
        {
            Ratings.Clear();
            foreach (var rating in ratings)
            {
                Ratings[rating.ImdbId] = rating;
            }

            return Task.CompletedTask;
        }

        public Task<ImdbDatasetState> GetStateAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new ImdbDatasetState(DateTimeOffset.UtcNow, Ratings.Count, null));
        }

        public Task SaveStateAsync(ImdbDatasetState state, CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class FakeOmdbClient : IOmdbClient
    {
        public Dictionary<string, OmdbItem> ItemsByImdbId { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Task<OmdbItem?> GetByImdbIdAsync(string imdbId, CancellationToken cancellationToken, bool forceRefresh = false)
        {
            return Task.FromResult(ItemsByImdbId.GetValueOrDefault(imdbId));
        }
    }

    private sealed class FakeRottenTomatoesClient : IRottenTomatoesClient
    {
        public RottenTomatoesScores Scores { get; init; } = RottenTomatoesScores.Empty;

        public Task<RottenTomatoesScores> GetScoresAsync(
            PremiereMediaType mediaType,
            string title,
            int? year,
            string? wikidataId,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            return Task.FromResult(Scores);
        }
    }

    private sealed class FakeTmdbClient : ITmdbClient
    {
        public Dictionary<int, TmdbDetailsWithExtras> TvDetailsById { get; } = [];
        public Dictionary<int, TmdbDetailsWithExtras> MovieDetailsById { get; } = [];

        public Task<IReadOnlyList<TmdbTvDiscoverItem>> DiscoverTvAsync(DateOnly start, DateOnly end, TmdbDiscoverFilters filters, CancellationToken cancellationToken, bool forceRefresh = false)
        {
            return Task.FromResult<IReadOnlyList<TmdbTvDiscoverItem>>([]);
        }

        public async IAsyncEnumerable<TmdbDiscoverBatch<TmdbTvDiscoverItem>> StreamDiscoverTvAsync(DateOnly start, DateOnly end, TmdbDiscoverFilters filters, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken, bool forceRefresh = false)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<IReadOnlyList<TmdbTvDiscoverItem>> DiscoverTvByNetworksAsync(DateOnly start, DateOnly end, IReadOnlyList<int> networkIds, CancellationToken cancellationToken, bool forceRefresh = false)
        {
            return Task.FromResult<IReadOnlyList<TmdbTvDiscoverItem>>([]);
        }

        public Task<IReadOnlyList<TmdbMovieDiscoverItem>> DiscoverMoviesAsync(DateOnly start, DateOnly end, TmdbDiscoverFilters filters, CancellationToken cancellationToken, bool forceRefresh = false)
        {
            return Task.FromResult<IReadOnlyList<TmdbMovieDiscoverItem>>([]);
        }

        public async IAsyncEnumerable<TmdbDiscoverBatch<TmdbMovieDiscoverItem>> StreamDiscoverMoviesAsync(DateOnly start, DateOnly end, TmdbDiscoverFilters filters, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken, bool forceRefresh = false)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<TmdbDetailsWithExtras?> GetTvDetailsAsync(int id, CancellationToken cancellationToken, bool forceRefresh = false)
        {
            return Task.FromResult(TvDetailsById.GetValueOrDefault(id));
        }

        public Task<TmdbDetailsWithExtras?> GetMovieDetailsAsync(int id, CancellationToken cancellationToken, bool forceRefresh = false)
        {
            return Task.FromResult(MovieDetailsById.GetValueOrDefault(id));
        }

        public Task<int?> FindTmdbIdByExternalIdAsync(PremiereMediaType mediaType, string externalId, string externalSource, CancellationToken cancellationToken, bool forceRefresh = false)
        {
            return Task.FromResult<int?>(null);
        }

        public Task<IReadOnlyList<TmdbTitleSearchResult>> SearchTitlesAsync(PremiereMediaType mediaType, string query, int? year, CancellationToken cancellationToken, bool forceRefresh = false)
        {
            return Task.FromResult<IReadOnlyList<TmdbTitleSearchResult>>([]);
        }

        public Task<IReadOnlyList<TmdbGenre>> GetGenresAsync(PremiereMediaType mediaType, CancellationToken cancellationToken, bool forceRefresh = false)
        {
            return Task.FromResult<IReadOnlyList<TmdbGenre>>([]);
        }

        public Task<IReadOnlyList<TmdbConfigurationLanguage>> GetLanguagesAsync(CancellationToken cancellationToken, bool forceRefresh = false)
        {
            return Task.FromResult<IReadOnlyList<TmdbConfigurationLanguage>>([]);
        }

        public Task<IReadOnlyList<TmdbConfigurationCountry>> GetCountriesAsync(CancellationToken cancellationToken, bool forceRefresh = false)
        {
            return Task.FromResult<IReadOnlyList<TmdbConfigurationCountry>>([]);
        }

        public Task<IReadOnlyList<TmdbWatchProvider>> GetWatchProvidersAsync(PremiereMediaType mediaType, string region, CancellationToken cancellationToken, bool forceRefresh = false)
        {
            return Task.FromResult<IReadOnlyList<TmdbWatchProvider>>([]);
        }

        public Task<TmdbCertificationResponse?> GetCertificationsAsync(PremiereMediaType mediaType, CancellationToken cancellationToken, bool forceRefresh = false)
        {
            return Task.FromResult<TmdbCertificationResponse?>(null);
        }

        public Task<IReadOnlyList<TmdbKeyword>> SearchKeywordsAsync(string query, CancellationToken cancellationToken, bool forceRefresh = false)
        {
            return Task.FromResult<IReadOnlyList<TmdbKeyword>>([]);
        }

        public Task<IReadOnlyList<TmdbChangedItem>> GetChangedMovieIdsAsync(DateOnly start, DateOnly end, CancellationToken cancellationToken, bool forceRefresh = false)
        {
            return Task.FromResult<IReadOnlyList<TmdbChangedItem>>([]);
        }

        public Task<IReadOnlyList<TmdbChangedItem>> GetChangedTvIdsAsync(DateOnly start, DateOnly end, CancellationToken cancellationToken, bool forceRefresh = false)
        {
            return Task.FromResult<IReadOnlyList<TmdbChangedItem>>([]);
        }
    }
}
