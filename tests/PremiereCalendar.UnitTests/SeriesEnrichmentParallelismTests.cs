using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class PremiereServiceTests
{
    [Fact]
    public async Task SeriesEnrichment_RatingsTvmazeAndRottenTomatoesOverlap_ArtworkAndSourcesOverlap()
    {
        var ratingsStarted = NewSignal();
        var mazeStarted = NewSignal();
        var tomatoesStarted = NewSignal();
        var artworkStarted = NewSignal();
        var sourcesStarted = NewSignal();
        var releaseRatings = new TaskCompletionSource<OmdbItem?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseMaze = new TaskCompletionSource<TvmazeShow?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTomatoes = NewSignal();
        var releaseArtwork = NewSignal();
        var releaseSources = NewSignal();
        var service = CreateParallelSeriesService(
            new DelegateSeriesTvmazeClient(async token =>
            {
                mazeStarted.TrySetResult(true);
                return await releaseMaze.Task.WaitAsync(token);
            }),
            new DelegateOmdbClient(async (_, token, _) =>
            {
                ratingsStarted.TrySetResult(true);
                return await releaseRatings.Task.WaitAsync(token);
            }),
            new DelegateRottenTomatoesClient(async (_, _, _, _, token, _) =>
            {
                tomatoesStarted.TrySetResult(true);
                await releaseTomatoes.Task.WaitAsync(token);
                return new RottenTomatoesScores(91, 69);
            }),
            new DelegateArtworkProvider(async (_, token) =>
            {
                artworkStarted.TrySetResult(true);
                await releaseArtwork.Task.WaitAsync(token);
                return new ArtworkCandidate("https://images.example/series.jpg", "Parallel artwork");
            }),
            new DelegateSeriesWatchmodeClient(async token =>
            {
                sourcesStarted.TrySetResult(true);
                await releaseSources.Task.WaitAsync(token);
                return [new PremiereSource { Name = "StreamCo", Id = 31, Kind = "flatrate" }];
            }));

        var operation = SeriesOperation(service, CancellationToken.None);
        await Task.WhenAll(ratingsStarted.Task, mazeStarted.Task, tomatoesStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(operation.IsCompleted);
        Assert.False(artworkStarted.Task.IsCompleted);
        releaseRatings.SetResult(new OmdbItem
        {
            Response = "True", ImdbRating = "7.6", ImdbVotes = "12,345", Metascore = "71",
            Ratings = [new OmdbRating { Source = "Rotten Tomatoes", Value = "80%" }]
        });
        releaseMaze.SetResult(new TvmazeShow
        {
            Name = "Parallel series", AverageRuntime = 42, Rating = new TvmazeRating { Average = 8.2 },
            OfficialSite = "https://series.example/", Url = "https://www.tvmaze.com/shows/9501"
        });
        await Task.WhenAll(artworkStarted.Task, sourcesStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(releaseTomatoes.Task.IsCompleted);
        releaseArtwork.SetResult(true);
        Assert.False(operation.IsCompleted);
        releaseSources.SetResult(true);
        releaseTomatoes.SetResult(true);

        var item = Assert.Single(await operation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(PremiereIdentity.CanonicalId(PremiereMediaType.Series, 9501), item.CanonicalId);
        Assert.Equal("Parallel series", item.Title);
        Assert.Equal("Parallel original title", item.OriginalTitle);
        Assert.Equal(new DateOnly(2026, 8, 12), item.PremiereDate);
        Assert.Equal("TMDb series overview", item.Overview);
        Assert.Equal("tt0095010", item.ImdbId);
        Assert.Equal(7.2, item.TmdbScore);
        Assert.Equal(7.6, item.ImdbScore);
        Assert.Equal(12_345, item.ImdbVoteCount);
        Assert.Equal(80, item.RottenTomatoesScore);
        Assert.Equal(69, item.RottenTomatoesAudienceScore);
        Assert.Equal(71, item.MetacriticScore);
        Assert.Equal("https://images.example/series.jpg", item.PosterUrl);
        Assert.Equal("Parallel artwork", item.ImageSource);
        Assert.EndsWith("/series-backdrop.jpg", item.BackdropUrl, StringComparison.Ordinal);
        Assert.Equal(42, item.RuntimeMinutes);
        Assert.Equal(8.2, item.TvmazeRating);
        Assert.Equal("https://series.example/", item.OfficialSiteUrl);
        Assert.Equal(["StreamCo"], item.SourceNames);
    }

    [Fact]
    public async Task SeriesEnrichment_EarlyCancellationObservesRatingsTvmazeAndRottenTomatoesCleanup()
    {
        var ratings = new CancellationGatedCall();
        var maze = new CancellationGatedCall();
        var tomatoes = new CancellationGatedCall();
        var service = CreateParallelSeriesService(
            new DelegateSeriesTvmazeClient(token => maze.RunAsync<TvmazeShow?>(token)),
            new DelegateOmdbClient((_, token, _) => ratings.RunAsync<OmdbItem?>(token)),
            new DelegateRottenTomatoesClient((_, _, _, _, token, _) => tomatoes.RunAsync(token, RottenTomatoesScores.Empty)),
            new DelegateArtworkProvider((_, _) => throw new InvalidOperationException("Artwork should not start before ratings and TVmaze finish.")),
            new ImmediateWatchmodeClient([]));
        using var cancellation = new CancellationTokenSource();
        var operation = SeriesOperation(service, cancellation.Token);
        await Task.WhenAll(ratings.Started.Task, maze.Started.Task, tomatoes.Started.Task).WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        await Task.WhenAll(ratings.CancellationObserved.Task, maze.CancellationObserved.Task, tomatoes.CancellationObserved.Task)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(operation.IsCompleted);
        ratings.Release.SetResult(true);
        maze.Release.SetResult(true);
        Assert.False(operation.IsCompleted);
        tomatoes.Release.SetResult(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(ratings.Completed.Task.IsCompletedSuccessfully);
        Assert.True(maze.Completed.Task.IsCompletedSuccessfully);
        Assert.True(tomatoes.Completed.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task SeriesEnrichment_LaterCancellationObservesArtworkSourcesAndRottenTomatoesCleanup()
    {
        var tomatoes = new CancellationGatedCall();
        var artwork = new CancellationGatedCall();
        var sources = new CancellationGatedCall();
        var service = CreateParallelSeriesService(
            new DelegateSeriesTvmazeClient(_ => Task.FromResult<TvmazeShow?>(null)),
            new DelegateOmdbClient((_, _, _) => Task.FromResult<OmdbItem?>(null)),
            new DelegateRottenTomatoesClient((_, _, _, _, token, _) => tomatoes.RunAsync(token, RottenTomatoesScores.Empty)),
            new DelegateArtworkProvider((_, token) => artwork.RunAsync<ArtworkCandidate?>(token)),
            new ImmediateWatchmodeClient([], sources));
        using var cancellation = new CancellationTokenSource();
        var operation = SeriesOperation(service, cancellation.Token);
        await Task.WhenAll(tomatoes.Started.Task, artwork.Started.Task, sources.Started.Task).WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        await Task.WhenAll(tomatoes.CancellationObserved.Task, artwork.CancellationObserved.Task, sources.CancellationObserved.Task)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(operation.IsCompleted);
        artwork.Release.SetResult(true);
        sources.Release.SetResult(true);
        Assert.False(operation.IsCompleted);
        tomatoes.Release.SetResult(true);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(tomatoes.Completed.Task.IsCompletedSuccessfully);
        Assert.True(artwork.Completed.Task.IsCompletedSuccessfully);
        Assert.True(sources.Completed.Task.IsCompletedSuccessfully);
    }

    private static Task<IReadOnlyList<PremiereItem>> SeriesOperation(PremiereService service, CancellationToken token)
        => service.GetPremieresAsync(new(2026, 8, 12), new(2026, 8, 12), token,
            filters: new CalendarFilters { ShowMovies = false, ShowSeries = true,
                SeriesFilters = { SeriesDateMode = SeriesDateMode.NewSeriesOnly } });

    private static PremiereService CreateParallelSeriesService(ITvmazeClient tvmaze, IOmdbClient omdb,
        IRottenTomatoesClient tomatoes, IArtworkProvider artwork, IWatchmodeClient watchmode)
    {
        var tmdb = new FakeTmdbClient
        {
            TvItems = [new TmdbTvDiscoverItem
            {
                Id = 9501, Name = "Parallel series", OriginalName = "Parallel original title", FirstAirDate = "2026-08-12",
                Overview = "TMDb series overview", OriginalLanguage = "en", OriginCountry = ["US"], GenreIds = [18],
                VoteAverage = 7.2, VoteCount = 234, BackdropPath = "/series-backdrop.jpg"
            }]
        };
        tmdb.TvDetailsById[9501] = new TmdbDetailsWithExtras
        {
            Id = 9501, Name = "Details title", ExternalIds = new TmdbExternalIds { ImdbId = "tt0095010", WikidataId = "Q9501" }
        };
        return new PremiereService(tmdb, omdb, tvmaze, watchmode, new NullCalendarCache(), new TrailerSelector(),
            new RatingMapper(), [artwork], [], Microsoft.Extensions.Options.Options.Create(new TmdbOptions()),
            NullLogger<PremiereService>.Instance, rottenTomatoesClient: tomatoes);
    }

    private sealed class DelegateSeriesTvmazeClient(Func<CancellationToken, Task<TvmazeShow?>> lookup) : ITvmazeClient
    {
        public Task<TvmazeShow?> LookupShowAsync(int? tvdbId, string? imdbId, CancellationToken token, bool forceRefresh = false) => lookup(token);
        public Task<TvmazeShow?> SearchShowByNameAsync(string title, CancellationToken token, bool forceRefresh = false) => Task.FromResult<TvmazeShow?>(null);
        public Task<IReadOnlyList<TvmazeShowImage>> GetShowImagesAsync(int id, CancellationToken token, bool forceRefresh = false) => Task.FromResult<IReadOnlyList<TvmazeShowImage>>([]);
        public Task<IReadOnlyList<TvmazeScheduleEpisode>> GetScheduleAsync(DateOnly date, string? country, bool web, CancellationToken token, bool forceRefresh = false) => Task.FromResult<IReadOnlyList<TvmazeScheduleEpisode>>([]);
        public Task<IReadOnlyList<TvmazeShowUpdate>> GetShowUpdatesAsync(TvmazeUpdateWindow since, CancellationToken token, bool forceRefresh = false) => Task.FromResult<IReadOnlyList<TvmazeShowUpdate>>([]);
    }

    private sealed class DelegateSeriesWatchmodeClient(Func<CancellationToken, Task<IReadOnlyList<PremiereSource>>> sources) : IWatchmodeClient
    {
        public Task<IReadOnlyList<PremiereSource>> GetTitleSourcesAsync(PremiereMediaType type, int tmdbId, string? imdbId,
            IReadOnlyList<string> regions, CancellationToken token, bool forceRefresh = false) => sources(token);
        public Task<IReadOnlyList<ExternalPremiereCandidate>> GetReleaseCandidatesAsync(DateOnly start, DateOnly end,
            CancellationToken token, bool forceRefresh = false) => Task.FromResult<IReadOnlyList<ExternalPremiereCandidate>>([]);
    }
}
