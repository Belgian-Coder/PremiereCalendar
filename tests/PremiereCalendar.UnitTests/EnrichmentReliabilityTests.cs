using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class EnrichmentReliabilityTests
{
    [Fact]
    public async Task ScoreBackfill_BatchesImdbAndDeduplicatesProviderLookupsWithoutOverwritingKnownFields()
    {
        var imdb = new FakeImdbRatingsStore
        {
            Ratings = new Dictionary<string, ImdbRatingRecord>(StringComparer.OrdinalIgnoreCase)
            {
                ["tt1000001"] = new("tt1000001", 7.9, 1200, DateTimeOffset.UtcNow)
            }
        };
        var omdb = new FakeOmdbClient
        {
            GetByImdbId = (id, _, _) => Task.FromResult<OmdbItem?>(new OmdbItem
            {
                Response = "True",
                ImdbRating = "7.1",
                ImdbVotes = "900",
                Metascore = "68",
                Ratings = [new OmdbRating { Source = "Rotten Tomatoes", Value = "81%" }]
            })
        };
        var rottenTomatoes = new FakeRottenTomatoesClient
        {
            GetScores = (_, _, _, _, _, _) => Task.FromResult(new RottenTomatoesScores(82, 91))
        };
        var service = CreateScoreBackfillService(imdb, omdb, rottenTomatoes, maxConcurrency: 2);
        var known = NewItem("episode-known", 10, "tt1000001") with
        {
            ImdbScore = 6.4,
            ImdbVoteCount = 44,
            RottenTomatoesScore = 70,
            RottenTomatoesAudienceScore = 72,
            MetacriticScore = 60
        };
        var missing = NewItem("episode-missing", 10, "TT1000001");

        var result = await service.BackfillItemsAsync([known, missing], CancellationToken.None);

        Assert.Equal(1, imdb.BatchLookupCount);
        Assert.Equal(new[] { "TT1000001" }, imdb.LastRequestedIds);
        Assert.Equal(1, omdb.Calls);
        Assert.Equal(1, rottenTomatoes.Calls);
        Assert.Equal(new[] { "episode-known", "episode-missing" }, result.Items.Select(item => item.CanonicalId));
        Assert.Equal(6.4, result.Items[0].ImdbScore);
        Assert.Equal(44, result.Items[0].ImdbVoteCount);
        Assert.Equal(70, result.Items[0].RottenTomatoesScore);
        Assert.Equal(72, result.Items[0].RottenTomatoesAudienceScore);
        Assert.Equal(60, result.Items[0].MetacriticScore);
        Assert.Equal(7.9, result.Items[1].ImdbScore);
        Assert.Equal(1200, result.Items[1].ImdbVoteCount);
        Assert.Equal(81, result.Items[1].RottenTomatoesScore);
        Assert.Equal(91, result.Items[1].RottenTomatoesAudienceScore);
        Assert.Equal(68, result.Items[1].MetacriticScore);
    }

    [Fact]
    public async Task ScoreBackfill_WithNoImdbIdSkipsProviderLookupsAndPreservesKnownScores()
    {
        var imdb = new FakeImdbRatingsStore();
        var omdb = new FakeOmdbClient
        {
            GetByImdbId = (_, _, _) => Task.FromResult<OmdbItem?>(new OmdbItem { Response = "True", ImdbRating = "9.0" })
        };
        var rottenTomatoes = new FakeRottenTomatoesClient
        {
            GetScores = (_, _, _, _, _, _) => Task.FromResult(new RottenTomatoesScores(99, 98))
        };
        var service = CreateScoreBackfillService(imdb, omdb, rottenTomatoes, maxConcurrency: 2);
        var item = NewItem("no-imdb", 15, " ") with
        {
            ImdbScore = 6.1,
            ImdbVoteCount = 500,
            RottenTomatoesScore = 83,
            MetacriticScore = 71
        };

        var result = await service.BackfillItemsAsync([item], CancellationToken.None);

        Assert.Equal(0, imdb.BatchLookupCount);
        Assert.Equal(0, omdb.Calls);
        Assert.Equal(0, rottenTomatoes.Calls);
        Assert.Equal(6.1, result.Items[0].ImdbScore);
        Assert.Equal(500, result.Items[0].ImdbVoteCount);
        Assert.Equal(83, result.Items[0].RottenTomatoesScore);
        Assert.Null(result.Items[0].RottenTomatoesAudienceScore);
        Assert.Equal(71, result.Items[0].MetacriticScore);
    }

    [Fact]
    public async Task ScoreBackfill_DoesNotCallOmdbWhenOnlyImdbVoteCountIsMissing()
    {
        var imdb = new FakeImdbRatingsStore();
        var omdb = new FakeOmdbClient
        {
            GetByImdbId = (_, _, _) => Task.FromResult<OmdbItem?>(new OmdbItem { Response = "True", ImdbVotes = "900" })
        };
        var service = CreateScoreBackfillService(imdb, omdb, rottenTomatoes: null, maxConcurrency: 2);
        var item = NewItem("vote-count-only", 16, "tt1000016") with
        {
            ImdbScore = 6.1,
            RottenTomatoesScore = 83,
            RottenTomatoesAudienceScore = 75,
            MetacriticScore = 71
        };

        var result = await service.BackfillItemsAsync([item], CancellationToken.None);

        Assert.Equal(1, imdb.BatchLookupCount);
        Assert.Equal(new[] { "tt1000016" }, imdb.LastRequestedIds);
        Assert.Equal(0, omdb.Calls);
        Assert.Equal(6.1, result.Items[0].ImdbScore);
        Assert.Null(result.Items[0].ImdbVoteCount);
    }

    [Fact]
    public async Task MissingExternalIdRepair_DeduplicatesMediaDetailsByTypeAndTmdbId()
    {
        var tmdb = new FakeTmdbClient();
        tmdb.GetTvDetails = (id, _, _) => Task.FromResult<TmdbDetailsWithExtras?>(new TmdbDetailsWithExtras
        {
            Id = id,
            ExternalIds = new TmdbExternalIds { ImdbId = $"tt{id:0000000}", TvdbId = id + 1000 }
        });
        tmdb.GetMovieDetails = (id, _, _) => Task.FromResult<TmdbDetailsWithExtras?>(new TmdbDetailsWithExtras
        {
            Id = id,
            ExternalIds = new TmdbExternalIds { ImdbId = $"tt{id:0000000}" }
        });
        var service = CreateRepairService(tmdb, maxConcurrency: 2);
        var items = new[]
        {
            NewItem("episode-a", 20, null, PremiereMediaType.Series) with { Type = PremiereItemType.SeriesEpisode },
            NewItem("episode-b", 20, null, PremiereMediaType.Series) with { Type = PremiereItemType.SeriesEpisode },
            NewItem("movie-20", 20, null, PremiereMediaType.Movie)
        };

        var result = await service.RepairItemsAsync(items, CancellationToken.None);

        Assert.Equal(1, tmdb.TvDetailCalls);
        Assert.Equal(1, tmdb.MovieDetailCalls);
        Assert.Equal(new[] { "episode-a", "episode-b", "movie-20" }, result.Items.Select(item => item.CanonicalId));
        Assert.All(result.Items, item => Assert.Equal("tt0000020", item.ImdbId));
        Assert.Equal(1020, result.Items[0].TvdbId);
        Assert.Equal(1020, result.Items[1].TvdbId);
        Assert.Null(result.Items[2].TvdbId);
    }

    [Fact]
    public async Task MissingExternalIdRepair_BoundsConcurrentCallsAndKeepsInputOrder()
    {
        var tmdb = new FakeTmdbClient();
        var active = 0;
        var maximum = 0;
        tmdb.GetTvDetails = async (id, token, _) =>
        {
            UpdateMaximum(ref maximum, Interlocked.Increment(ref active));
            try
            {
                await Task.Delay((9 - id) * 5, token);
                return new TmdbDetailsWithExtras { Id = id, ExternalIds = new TmdbExternalIds { ImdbId = $"tt{id:0000000}" } };
            }
            finally
            {
                Interlocked.Decrement(ref active);
            }
        };
        var service = CreateRepairService(tmdb, maxConcurrency: 2);
        var items = Enumerable.Range(1, 8)
            .Select(id => NewItem($"episode-{id}", id, null, PremiereMediaType.Series))
            .ToArray();

        var result = await service.RepairItemsAsync(items, CancellationToken.None);

        Assert.InRange(maximum, 2, 2);
        Assert.Equal(items.Select(item => item.CanonicalId), result.Items.Select(item => item.CanonicalId));
        Assert.Equal(items.Select(item => $"tt{item.TmdbId:0000000}"), result.Items.Select(item => item.ImdbId));
    }

    [Fact]
    public async Task ScoreBackfill_BoundsConcurrentCallsAndKeepsInputOrder()
    {
        var active = 0;
        var maximum = 0;
        var omdb = new FakeOmdbClient
        {
            GetByImdbId = async (id, token, _) =>
            {
                var current = Interlocked.Increment(ref active);
                UpdateMaximum(ref maximum, current);
                try
                {
                    var index = int.Parse(id[^1..], System.Globalization.CultureInfo.InvariantCulture);
                    await Task.Delay((9 - index) * 5, token);
                    return new OmdbItem { Response = "True", ImdbRating = $"{index}.0" };
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            }
        };
        var service = CreateScoreBackfillService(null, omdb, rottenTomatoes: null, maxConcurrency: 2);
        var items = Enumerable.Range(1, 8)
            .Select(index => NewItem($"movie-{index}", index, $"tt100000{index}"))
            .ToArray();

        var result = await service.BackfillItemsAsync(items, CancellationToken.None);

        Assert.Equal(2, maximum);
        Assert.Equal(items.Select(item => item.CanonicalId), result.Items.Select(item => item.CanonicalId));
        Assert.Equal(items.Select(item => (double?)item.TmdbId), result.Items.Select(item => item.ImdbScore));
    }

    [Fact]
    public async Task ScoreBackfill_IsolatesProviderFailureAndKeepsOtherItems()
    {
        var omdb = new FakeOmdbClient
        {
            GetByImdbId = (id, _, _) => id == "tt1000002"
                ? Task.FromException<OmdbItem?>(new HttpRequestException("temporary failure"))
                : Task.FromResult<OmdbItem?>(new OmdbItem { Response = "True", ImdbRating = "8.3", Metascore = "75" })
        };
        var service = CreateScoreBackfillService(null, omdb, rottenTomatoes: null, maxConcurrency: 2);
        var items = new[] { NewItem("first", 1, "tt1000001"), NewItem("second", 2, "tt1000002") };

        var result = await service.BackfillItemsAsync(items, CancellationToken.None);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal(new[] { "first", "second" }, result.Items.Select(item => item.CanonicalId));
        Assert.Equal(8.3, result.Items[0].ImdbScore);
        Assert.Equal(75, result.Items[0].MetacriticScore);
        Assert.Null(result.Items[1].ImdbScore);
        Assert.Equal(2, omdb.Calls);
    }

    [Fact]
    public async Task MissingExternalIdRepair_IsolatesProviderFailureAndKeepsOtherItems()
    {
        var tmdb = new FakeTmdbClient
        {
            GetTvDetails = (id, _, _) => id == 1
                ? Task.FromException<TmdbDetailsWithExtras?>(new HttpRequestException("temporary failure"))
                : Task.FromResult<TmdbDetailsWithExtras?>(new TmdbDetailsWithExtras
                {
                    Id = id,
                    ExternalIds = new TmdbExternalIds { ImdbId = $"tt{id:0000000}" }
                })
        };
        var service = CreateRepairService(tmdb, maxConcurrency: 2);
        var items = new[]
        {
            NewItem("failed", 1, null, PremiereMediaType.Series),
            NewItem("repaired", 2, null, PremiereMediaType.Series)
        };

        var result = await service.RepairItemsAsync(items, CancellationToken.None);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal(new[] { "failed", "repaired" }, result.Items.Select(item => item.CanonicalId));
        Assert.Null(result.Items[0].ImdbId);
        Assert.Equal("tt0000002", result.Items[1].ImdbId);
    }

    [Fact]
    public async Task ScoreBackfill_PropagatesCancellationFromRemoteLookup()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var omdb = new FakeOmdbClient
        {
            GetByImdbId = async (_, token, _) =>
            {
                started.SetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return null;
            }
        };
        var service = CreateScoreBackfillService(null, omdb, rottenTomatoes: null, maxConcurrency: 1);
        using var cancellation = new CancellationTokenSource();
        var operation = service.BackfillItemsAsync([NewItem("cancel", 1, "tt1000001")], cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    [Fact]
    public async Task MissingExternalIdRepair_PropagatesCancellationFromRemoteLookup()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var tmdb = new FakeTmdbClient
        {
            GetTvDetails = async (_, token, _) =>
            {
                started.SetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return null;
            }
        };
        var service = CreateRepairService(tmdb, maxConcurrency: 1);
        using var cancellation = new CancellationTokenSource();
        var operation = service.RepairItemsAsync([NewItem("cancel", 1, null, PremiereMediaType.Series)], cancellation.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
    }

    private static ScoreBackfillService CreateScoreBackfillService(
        IImdbRatingsStore? imdb,
        FakeOmdbClient omdb,
        FakeRottenTomatoesClient? rottenTomatoes,
        int maxConcurrency)
    {
        return new ScoreBackfillService(
            imdb,
            omdb,
            new RatingMapper(),
            rottenTomatoes,
            NullLogger<ScoreBackfillService>.Instance,
            Microsoft.Extensions.Options.Options.Create(new TmdbOptions { MaxEnrichmentConcurrency = maxConcurrency }));
    }

    private static MissingExternalIdRepairService CreateRepairService(FakeTmdbClient tmdb, int maxConcurrency)
    {
        return new MissingExternalIdRepairService(
            tmdb.Client,
            NullLogger<MissingExternalIdRepairService>.Instance,
            Microsoft.Extensions.Options.Options.Create(new TmdbOptions { MaxEnrichmentConcurrency = maxConcurrency }));
    }

    private static PremiereItem NewItem(
        string canonicalId,
        int tmdbId,
        string? imdbId,
        PremiereMediaType mediaType = PremiereMediaType.Movie)
    {
        return new PremiereItem
        {
            CanonicalId = canonicalId,
            Type = mediaType == PremiereMediaType.Movie ? PremiereItemType.MovieFirstRelease : PremiereItemType.SeriesPremiere,
            MediaType = mediaType,
            TmdbId = tmdbId,
            ImdbId = imdbId,
            Title = $"Title {tmdbId}",
            PremiereDate = new DateOnly(2026, 5, 25)
        };
    }

    private static void UpdateMaximum(ref int maximum, int value)
    {
        var observed = Volatile.Read(ref maximum);
        while (value > observed)
        {
            var previous = Interlocked.CompareExchange(ref maximum, value, observed);
            if (previous == observed)
            {
                return;
            }

            observed = previous;
        }
    }

    private sealed class FakeImdbRatingsStore : IImdbRatingsStore
    {
        public IReadOnlyDictionary<string, ImdbRatingRecord> Ratings { get; init; } = new Dictionary<string, ImdbRatingRecord>();
        public int BatchLookupCount { get; private set; }
        public string[] LastRequestedIds { get; private set; } = [];

        public Task<ImdbRatingRecord?> GetByImdbIdAsync(string imdbId, CancellationToken cancellationToken)
        {
            return Task.FromResult(Ratings.GetValueOrDefault(imdbId));
        }

        public Task<IReadOnlyDictionary<string, ImdbRatingRecord>> GetByImdbIdsAsync(
            IReadOnlyCollection<string> imdbIds,
            CancellationToken cancellationToken)
        {
            BatchLookupCount++;
            LastRequestedIds = imdbIds.ToArray();
            return Task.FromResult(Ratings);
        }

        public Task ReplaceAllAsync(IEnumerable<ImdbRatingRecord> ratings, DateTimeOffset importedAtUtc, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<ImdbDatasetState> GetStateAsync(CancellationToken cancellationToken)
            => Task.FromResult(new ImdbDatasetState(null, 0, null));

        public Task SaveStateAsync(ImdbDatasetState state, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class FakeOmdbClient : IOmdbClient
    {
        public Func<string, CancellationToken, bool, Task<OmdbItem?>> GetByImdbId { get; init; }
            = (_, _, _) => Task.FromResult<OmdbItem?>(null);
        public int Calls;

        public Task<OmdbItem?> GetByImdbIdAsync(string imdbId, CancellationToken cancellationToken, bool forceRefresh = false)
        {
            Interlocked.Increment(ref Calls);
            return GetByImdbId(imdbId, cancellationToken, forceRefresh);
        }
    }

    private sealed class FakeRottenTomatoesClient : IRottenTomatoesClient
    {
        public Func<PremiereMediaType, string, int?, string?, CancellationToken, bool, Task<RottenTomatoesScores>> GetScores { get; init; }
            = (_, _, _, _, _, _) => Task.FromResult(RottenTomatoesScores.Empty);
        public int Calls;

        public Task<RottenTomatoesScores> GetScoresAsync(
            PremiereMediaType mediaType,
            string title,
            int? year,
            string? wikidataId,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            Interlocked.Increment(ref Calls);
            return GetScores(mediaType, title, year, wikidataId, cancellationToken, forceRefresh);
        }
    }

    private sealed class FakeTmdbClient
    {
        public Func<int, CancellationToken, bool, Task<TmdbDetailsWithExtras?>> GetTvDetails { get; set; }
            = (_, _, _) => Task.FromResult<TmdbDetailsWithExtras?>(null);
        public Func<int, CancellationToken, bool, Task<TmdbDetailsWithExtras?>> GetMovieDetails { get; set; }
            = (_, _, _) => Task.FromResult<TmdbDetailsWithExtras?>(null);
        public int TvDetailCalls;
        public int MovieDetailCalls;

        public ITmdbClient Client { get; }

        public FakeTmdbClient()
        {
            Client = DispatchProxy.Create<ITmdbClient, TmdbClientDispatchProxy>();
            ((TmdbClientDispatchProxy)(object)Client).Owner = this;
        }

        public class TmdbClientDispatchProxy : DispatchProxy
        {
            public FakeTmdbClient? Owner { get; set; }

            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            {
                ArgumentNullException.ThrowIfNull(targetMethod);
                args ??= [];
                if (Owner is null)
                {
                    throw new InvalidOperationException("Proxy has no fake owner.");
                }

                var id = (int)args[0]!;
                var cancellationToken = (CancellationToken)args[1]!;
                var forceRefresh = args.Length > 2 && (bool)args[2]!;
                if (targetMethod.Name == nameof(ITmdbClient.GetTvDetailsAsync))
                {
                    Interlocked.Increment(ref Owner.TvDetailCalls);
                    return Owner.GetTvDetails(id, cancellationToken, forceRefresh);
                }

                if (targetMethod.Name == nameof(ITmdbClient.GetMovieDetailsAsync))
                {
                    Interlocked.Increment(ref Owner.MovieDetailCalls);
                    return Owner.GetMovieDetails(id, cancellationToken, forceRefresh);
                }

                throw new NotSupportedException($"The test fake does not implement {targetMethod.Name}.");
            }
        }
    }
}
