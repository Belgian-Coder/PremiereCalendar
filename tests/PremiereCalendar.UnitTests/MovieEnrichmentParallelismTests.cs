using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class PremiereServiceTests
{
    [Fact]
    public async Task MovieEnrichment_RatingsAndRottenTomatoesOverlap_ArtworkStartsBeforeRottenTomatoesCompletes()
    {
        var tmdb = CreateMovieTmdb();
        var ratingsStarted = NewSignal();
        var releaseRatings = new TaskCompletionSource<OmdbItem?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rottenTomatoesStarted = NewSignal();
        var releaseRottenTomatoes = NewSignal();
        var artworkStarted = NewSignal();
        var omdb = new DelegateOmdbClient(async (_, cancellationToken, _) =>
        {
            ratingsStarted.TrySetResult(true);
            return await releaseRatings.Task.WaitAsync(cancellationToken);
        });
        var rottenTomatoes = new DelegateRottenTomatoesClient(async (_, _, _, _, cancellationToken, _) =>
        {
            rottenTomatoesStarted.TrySetResult(true);
            await releaseRottenTomatoes.Task.WaitAsync(cancellationToken);
            return new RottenTomatoesScores(91, 69);
        });
        var artwork = new DelegateArtworkProvider((_, _) =>
        {
            artworkStarted.TrySetResult(true);
            return Task.FromResult<ArtworkCandidate?>(new ArtworkCandidate("https://images.example/movie.jpg", "Parallel artwork"));
        });
        var watchmode = new ImmediateWatchmodeClient(
        [new PremiereSource { Name = "StreamCo", Id = 31, Kind = "flatrate" }]);
        var service = CreateService(tmdb, omdb: omdb, rottenTomatoes: rottenTomatoes,
            artworkProviders: [artwork], watchmode: watchmode);

        var operation = service.GetPremieresAsync(
            new DateOnly(2026, 8, 12),
            new DateOnly(2026, 8, 12),
            CancellationToken.None,
            filters: MovieOnlyFilters());

        await ratingsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await rottenTomatoesStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(operation.IsCompleted);

        releaseRatings.SetResult(new OmdbItem
        {
            Response = "True",
            ImdbRating = "7.6",
            ImdbVotes = "12,345",
            Metascore = "71",
            Plot = "OMDb fallback plot",
            Ratings = [new OmdbRating { Source = "Rotten Tomatoes", Value = "80%" }]
        });
        await artworkStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(releaseRottenTomatoes.Task.IsCompleted);

        releaseRottenTomatoes.SetResult(true);
        var item = Assert.Single(await operation.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal("movie:9401", item.CanonicalId);
        Assert.Equal("Parallel movie", item.Title);
        Assert.Equal("Parallel original title", item.OriginalTitle);
        Assert.Equal(new DateOnly(2026, 8, 12), item.PremiereDate);
        Assert.Equal("TMDb overview", item.Overview);
        Assert.Equal(7.2, item.TmdbScore);
        Assert.Equal(7.6, item.ImdbScore);
        Assert.Equal(12_345, item.ImdbVoteCount);
        Assert.Equal(80, item.RottenTomatoesScore);
        Assert.Equal(69, item.RottenTomatoesAudienceScore);
        Assert.Equal(71, item.MetacriticScore);
        Assert.Equal("https://images.example/movie.jpg", item.PosterUrl);
        Assert.Equal("Parallel artwork", item.ImageSource);
        Assert.EndsWith("/backdrop.jpg", item.BackdropUrl, StringComparison.Ordinal);
        Assert.Equal(117, item.RuntimeMinutes);
        Assert.Equal(new[] { "StreamCo" }, item.SourceNames);
        Assert.Contains(item.Sources, source => source is { Name: "StreamCo", Kind: "flatrate", Id: 31 });
    }

    [Fact]
    public async Task MovieEnrichment_CancellationWaitsForRatingsRottenTomatoesArtworkAndSourcesToSettle()
    {
        var tmdb = CreateMovieTmdb();
        var rottenTomatoes = new CancellationGatedCall();
        var artwork = new CancellationGatedCall();
        var sources = new CancellationGatedCall();
        var watchmode = new ImmediateWatchmodeClient([], sources);
        var service = CreateService(
            tmdb,
            omdb: new DelegateOmdbClient((_, _, _) => Task.FromResult<OmdbItem?>(null)),
            rottenTomatoes: new DelegateRottenTomatoesClient((_, _, _, _, token, _) =>
                rottenTomatoes.RunAsync(token, RottenTomatoesScores.Empty)),
            artworkProviders: [new DelegateArtworkProvider((_, token) => artwork.RunAsync(token, result: (ArtworkCandidate?)null))],
            watchmode: watchmode);
        using var cancellation = new CancellationTokenSource();
        var operation = service.GetPremieresAsync(
            new DateOnly(2026, 8, 12),
            new DateOnly(2026, 8, 12),
            cancellation.Token,
            filters: MovieOnlyFilters());

        await Task.WhenAll(rottenTomatoes.Started.Task, artwork.Started.Task, sources.Started.Task)
            .WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Task.WhenAll(rottenTomatoes.CancellationObserved.Task, artwork.CancellationObserved.Task, sources.CancellationObserved.Task)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(operation.IsCompleted);
        rottenTomatoes.Release.TrySetResult(true);
        artwork.Release.TrySetResult(true);
        sources.Release.TrySetResult(true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(rottenTomatoes.Completed.Task.IsCompletedSuccessfully);
        Assert.True(artwork.Completed.Task.IsCompletedSuccessfully);
        Assert.True(sources.Completed.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ExternalCandidateFallbackImdbHydration_OverlapsRatingsAndRottenTomatoes()
    {
        var start = new DateOnly(2026, 8, 12);
        var cachedItem = new PremiereItem
        {
            CanonicalId = "movie:9402",
            Type = PremiereItemType.MovieFirstRelease,
            MediaType = PremiereMediaType.Movie,
            TmdbId = 9402,
            Title = "Cached same-origin movie",
            PremiereDate = start,
            Overview = "Cached overview",
            PosterUrl = "https://images.example/cached.jpg",
            ImageSource = "Cached artwork",
            WikidataId = "Q9402",
            RuntimeMinutes = 117,
            SourceNames = ["Cached schedule"],
            ImdbScore = 8.1,
            RottenTomatoesScore = 78,
            LastUpdatedUtc = DateTimeOffset.UtcNow
        };
        var discovery = new FakeDiscoveryProvider
        {
            DisplayName = "Same-origin provider",
            Candidates =
            [
                new ExternalPremiereCandidate(
                    PremiereMediaType.Movie,
                    start,
                    "Candidate fallback title",
                    9402,
                    "tt0094021",
                    null,
                    "Same-origin provider",
                    ReleaseYear: 2026)
            ]
        };
        var ratingsStarted = NewSignal();
        var releaseRatings = new TaskCompletionSource<OmdbItem?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rottenTomatoesStarted = NewSignal();
        var releaseRottenTomatoes = NewSignal();
        string? requestedImdbId = null;
        PremiereMediaType? requestedMediaType = null;
        string? requestedTitle = null;
        int? requestedYear = null;
        string? requestedWikidataId = null;
        bool? requestedForceRefresh = null;
        var service = CreateService(
            new FakeTmdbClient(),
            discoveryProviders: [discovery],
            calendarCache: new FakeCalendarCache { ExpiredItems = [cachedItem] },
            omdb: new DelegateOmdbClient(async (imdbId, cancellationToken, _) =>
            {
                requestedImdbId = imdbId;
                ratingsStarted.TrySetResult(true);
                return await releaseRatings.Task.WaitAsync(cancellationToken);
            }),
            rottenTomatoes: new DelegateRottenTomatoesClient(async (mediaType, title, year, wikidataId, cancellationToken, forceRefresh) =>
            {
                requestedMediaType = mediaType;
                requestedTitle = title;
                requestedYear = year;
                requestedWikidataId = wikidataId;
                requestedForceRefresh = forceRefresh;
                rottenTomatoesStarted.TrySetResult(true);
                await releaseRottenTomatoes.Task.WaitAsync(cancellationToken);
                return new RottenTomatoesScores(91, 67);
            }));

        var operation = service.GetPremieresAsync(
            start,
            start,
            CancellationToken.None,
            filters: MovieOnlyFilters());

        await ratingsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await rottenTomatoesStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(operation.IsCompleted);
        Assert.Equal("tt0094021", requestedImdbId);
        Assert.Equal(PremiereMediaType.Movie, requestedMediaType);
        Assert.Equal("Candidate fallback title", requestedTitle);
        Assert.Equal(2026, requestedYear);
        Assert.Equal("Q9402", requestedWikidataId);
        Assert.False(requestedForceRefresh);

        releaseRatings.SetResult(new OmdbItem
        {
            Response = "True",
            ImdbRating = "7.4",
            ImdbVotes = "4,321",
            Metascore = "73",
            Plot = "OMDb fallback overview",
            Poster = "https://images.example/omdb.jpg",
            Ratings = [new OmdbRating { Source = "Rotten Tomatoes", Value = "82%" }]
        });
        Assert.False(releaseRottenTomatoes.Task.IsCompleted);
        releaseRottenTomatoes.SetResult(true);
        var item = Assert.Single(await operation.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal("tt0094021", item.ImdbId);
        Assert.Equal("Candidate fallback title", item.Title);
        Assert.Equal(7.4, item.ImdbScore);
        Assert.Equal(4_321, item.ImdbVoteCount);
        Assert.Equal(82, item.RottenTomatoesScore);
        Assert.Equal(67, item.RottenTomatoesAudienceScore);
        Assert.Equal(73, item.MetacriticScore);
        Assert.Equal("Cached overview", item.Overview);
        Assert.Equal("https://images.example/cached.jpg", item.PosterUrl);
        Assert.Equal("Cached artwork", item.ImageSource);
        Assert.Contains("Same-origin provider", item.SourceNames);
    }

    private static FakeTmdbClient CreateMovieTmdb()
    {
        var tmdb = new FakeTmdbClient
        {
            FilterMovieItemsByDate = true,
            MovieItems =
            [
                new TmdbMovieDiscoverItem
                {
                    Id = 9401,
                    Title = "Parallel movie",
                    OriginalTitle = "Parallel original title",
                    ReleaseDate = "2026-08-12",
                    Overview = "TMDb overview",
                    BackdropPath = "/backdrop.jpg",
                    OriginalLanguage = "en",
                    OriginCountry = ["US"],
                    GenreIds = [18],
                    VoteAverage = 7.2,
                    VoteCount = 234
                }
            ]
        };
        tmdb.MovieDetailsById[9401] = new TmdbDetailsWithExtras
        {
            Id = 9401,
            Title = "Parallel movie details",
            OriginalTitle = "Parallel original details title",
            Overview = "Details overview",
            OriginalLanguage = "en",
            Runtime = 117,
            ExternalIds = new TmdbExternalIds { ImdbId = "tt0094010", WikidataId = "Q9401" }
        };
        return tmdb;
    }

    private static CalendarFilters MovieOnlyFilters() => new() { ShowSeries = false, ShowMovies = true };

    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class DelegateOmdbClient(Func<string, CancellationToken, bool, Task<OmdbItem?>> getByImdbId) : IOmdbClient
    {
        public Task<OmdbItem?> GetByImdbIdAsync(string imdbId, CancellationToken cancellationToken, bool forceRefresh = false)
            => getByImdbId(imdbId, cancellationToken, forceRefresh);
    }

    private sealed class DelegateRottenTomatoesClient(
        Func<PremiereMediaType, string, int?, string?, CancellationToken, bool, Task<RottenTomatoesScores>> getScores) : IRottenTomatoesClient
    {
        public Task<RottenTomatoesScores> GetScoresAsync(
            PremiereMediaType mediaType,
            string title,
            int? year,
            string? wikidataId,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
            => getScores(mediaType, title, year, wikidataId, cancellationToken, forceRefresh);
    }

    private sealed class DelegateArtworkProvider(
        Func<ArtworkRequest, CancellationToken, Task<ArtworkCandidate?>> getArtwork) : IArtworkProvider
    {
        public Task<ArtworkCandidate?> GetArtworkAsync(ArtworkRequest request, CancellationToken cancellationToken, bool forceRefresh = false)
            => getArtwork(request, cancellationToken);
    }

    private sealed class ImmediateWatchmodeClient(
        IReadOnlyList<PremiereSource> titleSources,
        CancellationGatedCall? gatedCall = null) : IWatchmodeClient
    {
        public Task<IReadOnlyList<PremiereSource>> GetTitleSourcesAsync(
            PremiereMediaType mediaType,
            int tmdbId,
            string? imdbId,
            IReadOnlyList<string> regions,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
            => gatedCall is null
                ? Task.FromResult(titleSources)
                : gatedCall.RunAsync(cancellationToken, titleSources);

        public Task<IReadOnlyList<ExternalPremiereCandidate>> GetReleaseCandidatesAsync(
            DateOnly start,
            DateOnly end,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
            => Task.FromResult<IReadOnlyList<ExternalPremiereCandidate>>([]);
    }

    private sealed class CancellationGatedCall
    {
        public TaskCompletionSource<bool> Started { get; } = NewSignal();
        public TaskCompletionSource<bool> CancellationObserved { get; } = NewSignal();
        public TaskCompletionSource<bool> Release { get; } = NewSignal();
        public TaskCompletionSource<bool> Completed { get; } = NewSignal();

        public async Task<T> RunAsync<T>(CancellationToken cancellationToken, T result = default!)
        {
            Started.TrySetResult(true);
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return result;
            }
            catch (OperationCanceledException)
            {
                CancellationObserved.TrySetResult(true);
                await Release.Task;
                throw;
            }
            finally
            {
                Completed.TrySetResult(true);
            }
        }
    }
}
