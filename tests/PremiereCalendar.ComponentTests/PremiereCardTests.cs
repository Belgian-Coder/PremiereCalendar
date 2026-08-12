using Bunit;
using PremiereCalendar.Components.Shared;
using PremiereCalendar.Models;

namespace PremiereCalendar.ComponentTests;

public sealed class PremiereCardTests : BunitContext
{
    [Fact]
    public void PremiereCard_ShowsMetadataScoresAndLinks()
    {
        var item = new PremiereItem
        {
            CanonicalId = "movie:200",
            Type = PremiereItemType.MovieFirstRelease,
            MediaType = PremiereMediaType.Movie,
            TmdbId = 200,
            Title = "Independent Feature",
            PremiereDate = new DateOnly(2026, 5, 6),
            Overview = "A compact test overview.",
            PosterUrl = "https://image.tmdb.org/t/p/w342/poster.jpg",
            ImageSource = "TMDb poster",
            TrailerUrl = "https://www.youtube.com/watch?v=feature",
            TmdbUrl = "https://www.themoviedb.org/movie/200",
            ImdbUrl = "https://www.imdb.com/title/tt0000200/",
            OriginalLanguage = "en",
            OriginCountries = ["AU"],
            SourceNames = ["Netflix", "Apple TV"],
            RuntimeMinutes = 102,
            TmdbScore = 6.9,
            TmdbVoteCount = 18,
            ImdbScore = 7.4,
            ImdbVoteCount = 1234,
            RottenTomatoesScore = 83,
            RottenTomatoesAudienceScore = 91,
            MetacriticScore = 72
        };

        var component = Render<PremiereCard>(parameters => parameters
            .Add(x => x.Item, item)
            .Add(x => x.ScoreSource, ScoreSource.RottenTomatoes));

        Assert.Contains("Independent Feature", component.Markup);
        Assert.Contains("Movie release", component.Markup);
        Assert.Contains("movie:200", component.Markup);
        Assert.Contains("Original language", component.Markup);
        Assert.Contains("English (EN)", component.Markup);
        Assert.Contains("AU", component.Markup);
        Assert.Contains("Source", component.Markup);
        Assert.Contains("Netflix", component.Markup);
        Assert.Contains("Apple TV", component.Markup);
        Assert.Contains("102 min", component.Markup);
        Assert.Contains("TMDb 6.9 / 10 (18)", component.Markup);
        Assert.Contains("IMDb 7.4 / 10", component.Markup);
        Assert.Contains("1", component.Markup);
        Assert.Contains("234", component.Markup);
        Assert.Contains("RT critics 83%", component.Markup);
        Assert.Contains("RT audience 91%", component.Markup);
        Assert.Contains("Meta 72/100", component.Markup);
        var provenance = component.Find(".source-details");
        Assert.Equal("false", provenance.GetAttribute("data-provenance-loaded"));
        Assert.DoesNotContain("Trailer via TMDb Videos", component.Markup);
        Assert.DoesNotContain("TMDb poster", component.Markup);

        provenance.QuerySelector("summary")!.Click();

        Assert.Equal("true", component.Find(".source-details").GetAttribute("data-provenance-loaded"));
        Assert.Contains("Trailer via TMDb Videos", component.Markup);
        Assert.Contains("TMDb poster", component.Markup);
        var image = component.Find("img");
        Assert.Equal(
            "/cached-image?url=https%3A%2F%2Fimage.tmdb.org%2Ft%2Fp%2Fw342%2Fposter.jpg&w=185",
            image.GetAttribute("data-lazy-src"));
        Assert.StartsWith("data:image/gif;base64,", image.GetAttribute("src"));
        Assert.NotEmpty(component.FindAll("a[href='https://www.youtube.com/watch?v=feature']"));
        Assert.NotEmpty(component.FindAll("a[href='https://www.youtube.com/results?search_query=Independent%20Feature%20trailer']"));
        Assert.NotEmpty(component.FindAll(".item-facts .language-value"));
        Assert.NotEmpty(component.FindAll(".selected-score"));
    }

    [Fact]
    public void PremiereCard_ShowsNotAvailableWhenLanguageIsMissing()
    {
        var item = new PremiereItem
        {
            CanonicalId = "tv:100",
            Type = PremiereItemType.SeriesPremiere,
            MediaType = PremiereMediaType.Series,
            TmdbId = 100,
            Title = "Quiet Launch",
            PremiereDate = new DateOnly(2026, 5, 4),
            OriginalLanguage = ""
        };

        var component = Render<PremiereCard>(parameters => parameters
            .Add(x => x.Item, item));

        Assert.Contains("Original language", component.Markup);
        Assert.Contains("n/a", component.Markup);
    }

    [Fact]
    public void PremiereCard_AddsImageRefreshParametersWhenRequested()
    {
        var item = new PremiereItem
        {
            CanonicalId = "tv:100",
            Type = PremiereItemType.SeriesPremiere,
            MediaType = PremiereMediaType.Series,
            TmdbId = 100,
            Title = "Fresh Poster",
            PremiereDate = new DateOnly(2026, 5, 4),
            PosterUrl = "https://image.tmdb.org/t/p/w342/fresh.jpg"
        };

        var component = Render<PremiereCard>(parameters => parameters
            .Add(x => x.Item, item)
            .Add(x => x.ImageCacheVersion, "123")
            .Add(x => x.RefreshImageCache, true));

        var imageUrl = component.Find("img").GetAttribute("data-lazy-src");
        Assert.Equal(
            "/cached-image?url=https%3A%2F%2Fimage.tmdb.org%2Ft%2Fp%2Fw342%2Ffresh.jpg&w=185&v=123&refresh=true",
            imageUrl);
    }

    [Fact]
    public void PremiereCard_SkipsRenderWhenRenderedFingerprintIsUnchanged()
    {
        var item = new PremiereItem
        {
            CanonicalId = "tv:100",
            Type = PremiereItemType.SeriesPremiere,
            MediaType = PremiereMediaType.Series,
            TmdbId = 100,
            Title = "Stable Poster",
            PremiereDate = new DateOnly(2026, 5, 4),
            PosterUrl = "https://image.tmdb.org/t/p/w342/stable.jpg",
            Overview = "Stable description."
        };

        var component = Render<PremiereCard>(parameters => parameters
            .Add(x => x.Item, item)
            .Add(x => x.ScoreSource, ScoreSource.Tmdb));

        var renderCount = component.RenderCount;
        component.Render(parameters => parameters
            .Add(x => x.Item, item)
            .Add(x => x.ScoreSource, ScoreSource.Tmdb));

        Assert.Equal(renderCount, component.RenderCount);
    }

    [Fact]
    public void PremiereCard_RendersProvenanceOnlyAfterItIsOpened()
    {
        var item = new PremiereItem
        {
            CanonicalId = "movie:201",
            Type = PremiereItemType.MovieFirstRelease,
            MediaType = PremiereMediaType.Movie,
            TmdbId = 201,
            Title = "Deferred Provenance",
            PremiereDate = new DateOnly(2026, 5, 6),
            DateSemantics = new PremiereDateSemantics(
                new DateOnly(2026, 5, 6),
                PremiereDateSourceKind.TmdbMovieReleaseDate,
                PremiereDataConfidence.High,
                "Verified release date"),
            MergeContributions =
            [
                new PremiereMergeContribution
                {
                    Source = "TMDb",
                    MatchMethod = "TMDb ID",
                    Reason = "Canonical match"
                }
            ]
        };

        var component = Render<PremiereCard>(parameters => parameters.Add(x => x.Item, item));

        Assert.Empty(component.FindAll(".provenance-section"));
        Assert.Equal("false", component.Find(".source-details").GetAttribute("data-provenance-loaded"));

        component.Find(".source-details summary").Click();

        Assert.NotEmpty(component.FindAll(".provenance-section"));
        Assert.Contains("Verified release date", component.Markup);
        Assert.Equal("true", component.Find(".source-details").GetAttribute("data-provenance-loaded"));
    }

    [Fact]
    public void PremiereCard_RerendersWhenRenderedFingerprintChanges()
    {
        var item = new PremiereItem
        {
            CanonicalId = "tv:100",
            Type = PremiereItemType.SeriesPremiere,
            MediaType = PremiereMediaType.Series,
            TmdbId = 100,
            Title = "Score Source",
            PremiereDate = new DateOnly(2026, 5, 4),
            TmdbScore = 7.2,
            ImdbScore = 8.1
        };

        var component = Render<PremiereCard>(parameters => parameters
            .Add(x => x.Item, item)
            .Add(x => x.ScoreSource, ScoreSource.Tmdb));

        var renderCount = component.RenderCount;
        component.Render(parameters => parameters
            .Add(x => x.Item, item)
            .Add(x => x.ScoreSource, ScoreSource.Imdb));

        Assert.True(component.RenderCount > renderCount);
        Assert.NotEmpty(component.FindAll(".selected-score"));
    }

    [Fact]
    public void PremiereCard_OmitsTrailerLinkWhenTrailerUrlIsMissing()
    {
        var item = new PremiereItem
        {
            CanonicalId = "tv:100",
            Type = PremiereItemType.SeriesPremiere,
            MediaType = PremiereMediaType.Series,
            TmdbId = 100,
            Title = "Quiet Launch",
            PremiereDate = new DateOnly(2026, 5, 4)
        };

        var component = Render<PremiereCard>(parameters => parameters
            .Add(x => x.Item, item)
            .Add(x => x.ScoreSource, ScoreSource.Tmdb));

        Assert.Contains("No trailer", component.Markup);
        Assert.Empty(component.FindAll("a[href*='watch?v=']"));
        Assert.NotEmpty(component.FindAll("a[href='https://www.youtube.com/results?search_query=Quiet%20Launch%20trailer']"));
    }

    [Fact]
    public void PremiereCard_ShowsRadarrButtonOnlyWhenMovieIntegrationIsEnabled()
    {
        PremiereItem? clickedItem = null;
        var item = new PremiereItem
        {
            CanonicalId = "movie:200",
            Type = PremiereItemType.MovieFirstRelease,
            MediaType = PremiereMediaType.Movie,
            TmdbId = 200,
            Title = "Radarr Candidate",
            PremiereDate = new DateOnly(2026, 5, 6)
        };

        var component = Render<PremiereCard>(parameters => parameters
            .Add(x => x.Item, item)
            .Add(x => x.IntegrationSettings, new IntegrationSettings
            {
                Radarr = new RadarrIntegrationSettings { Enabled = true }
            })
            .Add(x => x.OnAddToArr, selected => clickedItem = selected));

        var button = component.Find(".arr-add-button.radarr");
        Assert.Equal("Add to Radarr", button.TextContent.Trim());

        button.Click();

        Assert.Equal(item, clickedItem);
    }

    [Fact]
    public void PremiereCard_HidesArrButtonWhenIntegrationIsDisabled()
    {
        var item = new PremiereItem
        {
            CanonicalId = "tv:100",
            Type = PremiereItemType.SeriesPremiere,
            MediaType = PremiereMediaType.Series,
            TmdbId = 100,
            Title = "Hidden Action",
            PremiereDate = new DateOnly(2026, 5, 4)
        };

        var component = Render<PremiereCard>(parameters => parameters
            .Add(x => x.Item, item)
            .Add(x => x.IntegrationSettings, new IntegrationSettings
            {
                Sonarr = new SonarrIntegrationSettings { Enabled = false }
            })
            .Add(x => x.OnAddToArr, _ => { }));

        Assert.Empty(component.FindAll(".arr-add-button"));
    }
}
