using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class PremiereServiceTests
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(true, true)]
    [InlineData(false, false)]
    [InlineData(false, true)]
    public async Task PartialMediaHydration_RepairsRetainedFreshMediaWhileRediscoveringExpiredMedia(bool freshMovies, bool metadataExpired)
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var filters = new CalendarFilters
        {
            MovieFilters = { OriginalLanguages = ["en"], RuntimeMinMinutes = 45 },
            SeriesFilters = { OriginalLanguages = ["en"], SeriesDateMode = SeriesDateMode.NewSeriesOnly }
        };
        var primed = await CreateService(Tmdb(prime: true), omdb: Omdb("7.1"), itemEnrichmentCache: coordinator)
            .GetPremieresAsync(date, date, default, forceRefresh: true, filters: filters);
        Assert.Equal(2, primed.Count);
        clock.Advance(TimeSpan.FromDays(metadataExpired ? 8 : 2));
        var retainedMedia = freshMovies ? PremiereMediaType.Movie : PremiereMediaType.Series;
        var staleMedia = freshMovies ? PremiereMediaType.Series : PremiereMediaType.Movie;
        var retained = Assert.Single(primed, item => item.MediaType == retainedMedia);
        var stale = Assert.Single(primed, item => item.MediaType == staleMedia);
        var cache = new FakeCalendarCache();
        var seriesKey = CacheKeyForPageMode(filters, CalendarPageMode.Series);
        var movieKey = CacheKeyForPageMode(filters, CalendarPageMode.Movies);
        cache.ItemsByKey[(date, date, freshMovies ? movieKey : seriesKey)] = [retained];
        cache.ExpiredItemsByKey[(date, date, freshMovies ? seriesKey : movieKey)] = [stale];
        var tmdb = Tmdb(prime: false);
        var omdb = Omdb("8.2");
        var updates = new List<PremiereLoadProgress>();
        await foreach (var update in CreateService(tmdb, omdb: omdb, calendarCache: cache, itemEnrichmentCache: coordinator)
            .StreamCoreAsync(date, date, filters: filters)) updates.Add(update);

        var final = Assert.Single(updates, update => update.IsFinal);
        Assert.False(final.HasSourceErrors);
        Assert.Equal(2, final.Items.Count);
        var repaired = Assert.Single(final.Items, item => item.MediaType == retainedMedia);
        Assert.Equal(retained.TmdbId, repaired.TmdbId);
        Assert.Equal(8.2, repaired.ImdbScore);
        Assert.Equal(metadataExpired ? freshMovies ? 130 : 51 : retained.RuntimeMinutes, repaired.RuntimeMinutes);
        Assert.Contains(omdb.Calls, id => id == retained.ImdbId);
        if (freshMovies)
        {
            Assert.Empty(tmdb.MovieCalls);
            Assert.Single(tmdb.TvCalls);
            Assert.Equal(metadataExpired ? 1 : 0, tmdb.MovieDetailCalls.Count(call => call.Id == retained.TmdbId));
            Assert.DoesNotContain(tmdb.TvDetailCalls, call => call.Id == stale.TmdbId);
        }
        else
        {
            Assert.Empty(tmdb.TvCalls);
            Assert.Single(tmdb.MovieCalls);
            Assert.Equal(metadataExpired ? 1 : 0, tmdb.TvDetailCalls.Count(call => call.Id == retained.TmdbId));
            Assert.DoesNotContain(tmdb.MovieDetailCalls, call => call.Id == stale.TmdbId);
        }
        Assert.DoesNotContain(final.Items, item => item.TmdbId == stale.TmdbId);
        Assert.Contains(final.Items, item => item.TmdbId == (freshMovies ? 1002 : 902));
        Assert.All(cache.SetCalls, call => Assert.DoesNotContain(call.Items, item => item.TmdbId == stale.TmdbId));

        FakeOmdbClient Omdb(string score)
        {
            var client = new FakeOmdbClient();
            foreach (var id in new[] { 901, 902, 1001, 1002 })
                client.ItemsByImdbId[$"tt{id:0000000}"] = new OmdbItem { Response = "True", ImdbRating = score, ImdbVotes = "1,000" };
            return client;
        }
        FakeTmdbClient Tmdb(bool prime)
        {
            var movieIds = prime ? new[] { 901 } : freshMovies ? [] : new[] { 902 };
            var seriesIds = prime ? new[] { 1001 } : freshMovies ? new[] { 1002 } : [];
            var client = new FakeTmdbClient
            {
                FilterMovieItemsByDate = true,
                MovieItems = movieIds.Select(id => new TmdbMovieDiscoverItem
                    { Id = id, Title = $"Movie {id}", ReleaseDate = date.ToString("yyyy-MM-dd"), OriginalLanguage = "en" }).ToArray(),
                TvItems = seriesIds.Select(id => new TmdbTvDiscoverItem
                    { Id = id, Name = $"Series {id}", FirstAirDate = date.ToString("yyyy-MM-dd"), OriginalLanguage = "en" }).ToArray()
            };
            foreach (var id in new[] { 901, 902 }) client.MovieDetailsById[id] = new TmdbDetailsWithExtras
            { Id = id, Runtime = prime ? 112 : 130, OriginalLanguage = "en", ExternalIds = new TmdbExternalIds { ImdbId = $"tt{id:0000000}" } };
            foreach (var id in new[] { 1001, 1002 })
            {
                client.TvDetailsById[id] = new TmdbDetailsWithExtras
                { Id = id, EpisodeRunTime = [prime ? 43 : 51], OriginalLanguage = "en",
                    ExternalIds = new TmdbExternalIds { ImdbId = $"tt{id:0000000}", TvdbId = id + 100_000 } };
                client.SeasonOneEpisodeOneDatesById[id] = date;
            }
            return client;
        }
    }
}
