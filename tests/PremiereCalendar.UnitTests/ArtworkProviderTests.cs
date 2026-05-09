using System.Text.Json;
using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class ArtworkProviderTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void ArtworkResolver_UsesExpectedPriorityOrder()
    {
        var candidates = new[]
        {
            new ArtworkCandidate("https://assets.fanart.tv/poster.jpg", ArtworkSources.Fanart),
            new ArtworkCandidate("https://static.tvmaze.com/poster.jpg", ArtworkSources.TvmazeImage),
            new ArtworkCandidate("https://artworks.thetvdb.com/poster.jpg", ArtworkSources.TheTvdb),
            new ArtworkCandidate("https://upload.wikimedia.org/poster.jpg", ArtworkSources.Wikimedia)
        };

        Assert.Equal("TMDb poster", ArtworkResolver.Resolve("https://image.tmdb.org/poster.jpg", candidates, "https://omdb/poster.jpg", "https://tvmaze/enrichment.jpg", "https://image.tmdb.org/backdrop.jpg")?.Source);
        Assert.Equal(ArtworkSources.Fanart, ArtworkResolver.Resolve(null, candidates, "https://omdb/poster.jpg", "https://tvmaze/enrichment.jpg", "https://image.tmdb.org/backdrop.jpg")?.Source);
        Assert.Equal("OMDb poster", ArtworkResolver.Resolve(null, candidates[1..], "https://omdb/poster.jpg", "https://tvmaze/enrichment.jpg", "https://image.tmdb.org/backdrop.jpg")?.Source);
        Assert.Equal(ArtworkSources.TvmazeImage, ArtworkResolver.Resolve(null, candidates[2..], null, "https://tvmaze/enrichment.jpg", "https://image.tmdb.org/backdrop.jpg")?.Source);
        Assert.Equal(ArtworkSources.TheTvdb, ArtworkResolver.Resolve(null, candidates[2..], null, null, "https://image.tmdb.org/backdrop.jpg")?.Source);
        Assert.Equal(ArtworkSources.Wikimedia, ArtworkResolver.Resolve(null, candidates[3..], null, null, "https://image.tmdb.org/backdrop.jpg")?.Source);
        Assert.Equal("TMDb backdrop", ArtworkResolver.Resolve(null, [], null, null, "https://image.tmdb.org/backdrop.jpg")?.Source);
    }

    [Fact]
    public async Task FanartProvider_PrefersEnglishThenDutchThenNeutralAndOrdersByLikes()
    {
        var movieArtwork = JsonSerializer.Deserialize<FanartMovieArtwork>(
            """
            {
              "movieposter": [
                { "url": "https://assets.fanart.tv/nl-99.jpg", "lang": "nl", "likes": "99" },
                { "url": "https://assets.fanart.tv/en-3.jpg", "lang": "en", "likes": "3" },
                { "url": "https://assets.fanart.tv/en-7.jpg", "lang": "en", "likes": "7" },
                { "url": "https://assets.fanart.tv/neutral-100.jpg", "lang": "00", "likes": "100" }
              ]
            }
            """,
            JsonOptions);
        var provider = new FanartArtworkProvider(new FakeFanartClient { MovieArtwork = movieArtwork });

        var candidate = await provider.GetArtworkAsync(
            new ArtworkRequest(PremiereMediaType.Movie, 100, "tt100", null, null, "Fanart Test"),
            CancellationToken.None);

        Assert.NotNull(candidate);
        Assert.Equal(ArtworkSources.Fanart, candidate.Source);
        Assert.Equal("https://assets.fanart.tv/en-7.jpg", candidate.Url);
    }

    private sealed class FakeFanartClient : IFanartClient
    {
        public FanartMovieArtwork? MovieArtwork { get; init; }

        public Task<FanartMovieArtwork?> GetMovieArtworkAsync(
            int tmdbId,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            return Task.FromResult(MovieArtwork);
        }

        public Task<FanartTvArtwork?> GetTvArtworkAsync(
            int tvdbId,
            CancellationToken cancellationToken,
            bool forceRefresh = false)
        {
            return Task.FromResult<FanartTvArtwork?>(null);
        }
    }
}
