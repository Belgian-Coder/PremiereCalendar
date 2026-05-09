using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class TraktDiscoveryProviderTests
{
    [Fact]
    public async Task StreamCandidatesAsync_KeepsMovieCandidatesWhenShowCalendarFails()
    {
        var provider = new TraktDiscoveryProvider(new FakeTraktClient
        {
            MovieItems =
            [
                new TraktMovieCalendarItem
                {
                    Released = "2026-05-09",
                    Movie = new TraktMovie
                    {
                        Title = "Trakt Movie",
                        Ids = new TraktIds { Tmdb = 123, Imdb = "tt0000123" }
                    }
                }
            ],
            ThrowShows = true
        });

        var batches = new List<IReadOnlyList<ExternalPremiereCandidate>>();
        await foreach (var batch in provider.StreamCandidatesAsync(
            new DateOnly(2026, 5, 4),
            new DateOnly(2026, 5, 10),
            cancellationToken: CancellationToken.None))
        {
            batches.Add(batch);
        }

        var candidate = Assert.Single(batches.SelectMany(batch => batch));
        Assert.Equal(PremiereMediaType.Movie, candidate.MediaType);
        Assert.Equal("Trakt", candidate.Source);
        Assert.Equal(123, candidate.TmdbId);
    }

    private sealed class FakeTraktClient : ITraktClient
    {
        public IReadOnlyList<TraktMovieCalendarItem> MovieItems { get; init; } = [];
        public IReadOnlyList<TraktShowCalendarItem> ShowItems { get; init; } = [];
        public bool ThrowMovies { get; init; }
        public bool ThrowShows { get; init; }

        public Task<IReadOnlyList<TraktMovieCalendarItem>> GetMovieCalendarAsync(
            DateOnly start,
            DateOnly end,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            if (ThrowMovies)
            {
                throw new ExternalApiException("Movie calendar failed.");
            }

            return Task.FromResult(MovieItems);
        }

        public Task<IReadOnlyList<TraktShowCalendarItem>> GetNewShowCalendarAsync(
            DateOnly start,
            DateOnly end,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            if (ThrowShows)
            {
                throw new ExternalApiException("Show calendar failed.");
            }

            return Task.FromResult(ShowItems);
        }
    }
}
