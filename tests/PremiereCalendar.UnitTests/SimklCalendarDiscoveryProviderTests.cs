using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class SimklCalendarDiscoveryProviderTests
{
    [Fact]
    public async Task GetCandidatesAsync_MapsTvEpisodesAndMovieReleases()
    {
        var provider = new SimklCalendarDiscoveryProvider(new FakeSimklClient
        {
            Items =
            [
                new SimklCalendarItem(
                    SimklCalendarItemType.Tv,
                    "Simkl Show",
                    new DateTimeOffset(2026, 5, 4, 0, 0, 0, TimeSpan.FromHours(-5)),
                    new DateOnly(2026, 5, 4),
                    "https://simkl.com/tv/100/simkl-show",
                    new SimklCalendarIds(100, "110", "tt0000110", "1110"),
                    new SimklCalendarRatings(new SimklRating(8.1, 1200)),
                    new SimklCalendarEpisode(1, 1, "https://simkl.com/tv/100/simkl-show/season-1/episode-1")),
                new SimklCalendarItem(
                    SimklCalendarItemType.MovieRelease,
                    "Simkl Movie",
                    new DateTimeOffset(2026, 5, 5, 0, 0, 0, TimeSpan.Zero),
                    new DateOnly(2026, 5, 5),
                    "https://simkl.com/movies/200/simkl-movie",
                    new SimklCalendarIds(200, "220", "tt0000220", null),
                    new SimklCalendarRatings(new SimklRating(7.2, 340)),
                    null)
            ]
        });

        var candidates = await provider.GetCandidatesAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            CancellationToken.None);

        var show = Assert.Single(candidates, candidate => candidate.MediaType == PremiereMediaType.Series);
        Assert.Equal("Simkl Show", show.Title);
        Assert.Equal(new DateOnly(2026, 5, 4), show.PremiereDate);
        Assert.Equal(110, show.TmdbId);
        Assert.Equal("tt0000110", show.ImdbId);
        Assert.Equal(1110, show.TvdbId);
        Assert.Equal("Simkl", show.Source);
        Assert.True(show.IsSeriesEpisode);
        Assert.Equal(1, show.SeasonNumber);
        Assert.Equal(1, show.EpisodeNumber);
        Assert.Equal(8.1, show.ImdbScore);
        Assert.Equal(1200, show.ImdbVoteCount);

        var movie = Assert.Single(candidates, candidate => candidate.MediaType == PremiereMediaType.Movie);
        Assert.Equal("Simkl Movie", movie.Title);
        Assert.Equal(new DateOnly(2026, 5, 5), movie.PremiereDate);
        Assert.Equal(220, movie.TmdbId);
        Assert.Equal("tt0000220", movie.ImdbId);
        Assert.Equal(7.2, movie.ImdbScore);
        Assert.Equal(340, movie.ImdbVoteCount);
    }

    private sealed class FakeSimklClient : ISimklClient
    {
        public IReadOnlyList<SimklCalendarItem> Items { get; init; } = [];

        public Task<SimklSyncResult> SyncLibraryAsync(CancellationToken cancellationToken, bool forceRefresh = false)
        {
            return Task.FromResult(new SimklSyncResult(SimklSyncStatus.Disabled));
        }

        public Task<SimklPinCodeResult> RequestPinCodeAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new SimklPinCodeResult(false));
        }

        public Task<SimklPinStatusResult> CheckPinCodeAsync(string userCode, CancellationToken cancellationToken)
        {
            return Task.FromResult(new SimklPinStatusResult(SimklPinStatus.Disabled));
        }

        public Task<IReadOnlyList<SimklCalendarItem>> GetCalendarAsync(
            DateOnly start,
            DateOnly end,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            return Task.FromResult(Items);
        }
    }
}
