using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class PremiereFilterTests
{
    [Fact]
    public void CountMatches_ReturnsSameCountAsApplyWithoutSorting()
    {
        var items = new[]
        {
            new PremiereItem
            {
                CanonicalId = "movie:1",
                Title = "High Score",
                MediaType = PremiereMediaType.Movie,
                Type = PremiereItemType.MovieFirstRelease,
                TmdbId = 1,
                PremiereDate = new DateOnly(2026, 5, 4),
                OriginalLanguage = "en",
                OriginCountries = ["US"],
                TmdbScore = 8.2,
                TmdbVoteCount = 120,
                RuntimeMinutes = 105
            },
            new PremiereItem
            {
                CanonicalId = "movie:2",
                Title = "Low Score",
                MediaType = PremiereMediaType.Movie,
                Type = PremiereItemType.MovieFirstRelease,
                TmdbId = 2,
                PremiereDate = new DateOnly(2026, 5, 5),
                OriginalLanguage = "en",
                OriginCountries = ["US"],
                TmdbScore = 4.1,
                TmdbVoteCount = 20,
                RuntimeMinutes = 95
            },
            new PremiereItem
            {
                CanonicalId = "series:1",
                Title = "Series",
                MediaType = PremiereMediaType.Series,
                Type = PremiereItemType.SeriesPremiere,
                TmdbId = 3,
                PremiereDate = new DateOnly(2026, 5, 6),
                OriginalLanguage = "nl",
                OriginCountries = ["BE"],
                TmdbScore = 8.8,
                TmdbVoteCount = 300,
                RuntimeMinutes = 45
            }
        };
        var filters = new CalendarFilters
        {
            ShowSeries = false,
            ShowMovies = true,
            MinScore = 5,
            MinVoteCount = 50,
            SortMode = PremiereSortMode.Score,
            SortDirection = SortDirection.Descending
        };

        Assert.Equal(PremiereFilter.Apply(items, filters).Count, PremiereFilter.CountMatches(items, filters));
    }

    [Fact]
    public void Apply_UsesSelectedScoreSourceForScoresAndVotes()
    {
        var items = new[]
        {
            new PremiereItem
            {
                CanonicalId = "movie:imdb",
                Title = "IMDb Qualified",
                MediaType = PremiereMediaType.Movie,
                Type = PremiereItemType.MovieFirstRelease,
                TmdbId = 1,
                PremiereDate = new DateOnly(2026, 5, 4),
                OriginalLanguage = "en",
                TmdbScore = 4.0,
                TmdbVoteCount = 1_000,
                ImdbScore = 8.4,
                ImdbVoteCount = 250
            },
            new PremiereItem
            {
                CanonicalId = "movie:tmdb",
                Title = "TMDb Only",
                MediaType = PremiereMediaType.Movie,
                Type = PremiereItemType.MovieFirstRelease,
                TmdbId = 2,
                PremiereDate = new DateOnly(2026, 5, 5),
                OriginalLanguage = "en",
                TmdbScore = 9.2,
                TmdbVoteCount = 1_000
            },
            new PremiereItem
            {
                CanonicalId = "movie:lowvotes",
                Title = "Low IMDb Votes",
                MediaType = PremiereMediaType.Movie,
                Type = PremiereItemType.MovieFirstRelease,
                TmdbId = 3,
                PremiereDate = new DateOnly(2026, 5, 6),
                OriginalLanguage = "en",
                TmdbScore = 9.0,
                TmdbVoteCount = 1_000,
                ImdbScore = 8.7,
                ImdbVoteCount = 50
            }
        };
        var filters = new CalendarFilters
        {
            ScoreSource = ScoreSource.Imdb,
            MinScore = 8,
            MaxScore = 9,
            IncludeUnknownScores = false,
            MinVoteCount = 100
        };

        var result = PremiereFilter.Apply(items, filters);

        var item = Assert.Single(result);
        Assert.Equal("IMDb Qualified", item.Title);
    }

    [Fact]
    public void Apply_UsesPercentScaleForRottenTomatoesAndMetacriticScores()
    {
        var items = new[]
        {
            new PremiereItem
            {
                CanonicalId = "movie:fresh",
                Title = "Fresh",
                MediaType = PremiereMediaType.Movie,
                Type = PremiereItemType.MovieFirstRelease,
                TmdbId = 1,
                PremiereDate = new DateOnly(2026, 5, 4),
                OriginalLanguage = "en",
                RottenTomatoesScore = 82,
                MetacriticScore = 68
            },
            new PremiereItem
            {
                CanonicalId = "movie:outside",
                Title = "Outside Range",
                MediaType = PremiereMediaType.Movie,
                Type = PremiereItemType.MovieFirstRelease,
                TmdbId = 2,
                PremiereDate = new DateOnly(2026, 5, 5),
                OriginalLanguage = "en",
                RottenTomatoesScore = 62,
                MetacriticScore = 91
            }
        };

        var rottenTomatoes = PremiereFilter.Apply(items, new CalendarFilters
        {
            ScoreSource = ScoreSource.RottenTomatoes,
            MinScore = 8,
            MaxScore = 9,
            IncludeUnknownScores = false
        });
        var metacritic = PremiereFilter.Apply(items, new CalendarFilters
        {
            ScoreSource = ScoreSource.Metacritic,
            MinScore = 6,
            MaxScore = 7,
            IncludeUnknownScores = false
        });

        Assert.Equal("Fresh", Assert.Single(rottenTomatoes).Title);
        Assert.Equal("Fresh", Assert.Single(metacritic).Title);
    }

    [Fact]
    public void Apply_CombinesGlobalAndMediaSpecificFilters()
    {
        var items = new[]
        {
            new PremiereItem
            {
                CanonicalId = "series:match",
                Title = "Dutch Series",
                MediaType = PremiereMediaType.Series,
                Type = PremiereItemType.SeriesPremiere,
                TmdbId = 1,
                PremiereDate = new DateOnly(2026, 5, 4),
                OriginalLanguage = "nl",
                OriginCountries = ["BE"],
                SourceNames = ["VRT"],
                TvStatus = "returning",
                TvType = "scripted",
                RuntimeMinutes = 45,
                TmdbScore = 7.8,
                TmdbVoteCount = 100
            },
            new PremiereItem
            {
                CanonicalId = "series:episode",
                Title = "Later Episode",
                MediaType = PremiereMediaType.Series,
                Type = PremiereItemType.SeriesEpisode,
                TmdbId = 2,
                PremiereDate = new DateOnly(2026, 5, 5),
                OriginalLanguage = "nl",
                OriginCountries = ["BE"],
                SourceNames = ["VRT"],
                TvStatus = "returning",
                TvType = "scripted",
                RuntimeMinutes = 45,
                TmdbScore = 8.0,
                TmdbVoteCount = 100
            },
            new PremiereItem
            {
                CanonicalId = "movie:match",
                Title = "Streaming Movie",
                MediaType = PremiereMediaType.Movie,
                Type = PremiereItemType.MovieFirstRelease,
                TmdbId = 3,
                PremiereDate = new DateOnly(2026, 5, 6),
                OriginalLanguage = "en",
                OriginCountries = ["US"],
                SourceNames = ["Netflix"],
                Sources = [new PremiereSource { Id = 8, Name = "Netflix", Kind = "flatrate" }],
                MovieReleaseTypes = [4],
                Certifications = ["US:PG-13"],
                RuntimeMinutes = 105,
                TmdbScore = 8.2,
                TmdbVoteCount = 100
            },
            new PremiereItem
            {
                CanonicalId = "movie:rent",
                Title = "Rental Movie",
                MediaType = PremiereMediaType.Movie,
                Type = PremiereItemType.MovieFirstRelease,
                TmdbId = 4,
                PremiereDate = new DateOnly(2026, 5, 7),
                OriginalLanguage = "en",
                OriginCountries = ["US"],
                SourceNames = ["Store"],
                Sources = [new PremiereSource { Id = 9, Name = "Store", Kind = "rent" }],
                MovieReleaseTypes = [4],
                Certifications = ["US:PG-13"],
                RuntimeMinutes = 105,
                TmdbScore = 8.2,
                TmdbVoteCount = 100
            }
        };
        var filters = new CalendarFilters
        {
            MinScore = 7,
            MinVoteCount = 50,
            RuntimeMinMinutes = 40,
            RuntimeMaxMinutes = 120,
            SeriesFilters = new MediaFilterSet
            {
                SeriesDateMode = SeriesDateMode.NewSeriesOnly,
                OriginalLanguages = ["nl"],
                TvStatuses = ["returning"],
                TvTypes = ["scripted"]
            },
            MovieFilters = new MediaFilterSet
            {
                MovieReleaseTypes = [4],
                MonetizationTypes = ["flatrate"],
                Certifications = ["US:PG-13"],
                CertificationCountry = "US"
            }
        };

        var result = PremiereFilter.Apply(items, filters);

        Assert.Equal(["Dutch Series", "Streaming Movie"], result.Select(item => item.Title));
    }
}
