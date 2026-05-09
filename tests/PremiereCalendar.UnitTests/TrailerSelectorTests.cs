using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class TrailerSelectorTests
{
    private readonly TrailerSelector _selector = new();

    [Fact]
    public void SelectBestYouTubeTrailer_ChoosesOfficialTrailerFirst()
    {
        var videos = new[]
        {
            new TmdbVideo { Site = "YouTube", Key = "unofficial", Type = "Trailer", Official = false, PublishedAt = DateTimeOffset.Parse("2026-01-02") },
            new TmdbVideo { Site = "YouTube", Key = "official", Type = "Trailer", Official = true, PublishedAt = DateTimeOffset.Parse("2026-01-01") }
        };

        var url = _selector.SelectBestYouTubeTrailer(videos);

        Assert.Equal("https://www.youtube.com/watch?v=official", url);
    }

    [Fact]
    public void SelectBestYouTubeTrailer_FallsBackToTeaser()
    {
        var videos = new[]
        {
            new TmdbVideo { Site = "YouTube", Key = "teaser", Type = "Teaser", Official = true }
        };

        var url = _selector.SelectBestYouTubeTrailer(videos);

        Assert.Equal("https://www.youtube.com/watch?v=teaser", url);
    }

    [Fact]
    public void SelectBestYouTubeTrailer_IgnoresNonYoutubeAndMissingKeys()
    {
        var videos = new[]
        {
            new TmdbVideo { Site = "Vimeo", Key = "vimeo-key", Type = "Trailer", Official = true },
            new TmdbVideo { Site = "YouTube", Key = "", Type = "Trailer", Official = true }
        };

        var url = _selector.SelectBestYouTubeTrailer(videos);

        Assert.Null(url);
    }
}
