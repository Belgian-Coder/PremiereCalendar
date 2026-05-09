using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class PremiereFilterTests
{
    [Fact]
    public void PassesScoreFilter_UsesTmdbAndImdbAsTenPointScores()
    {
        var item = Item with { TmdbScore = 7.2, ImdbScore = 8.1 };
        var filters = new CalendarFilters { ScoreSource = ScoreSource.Imdb, MinScore = 8, MaxScore = 9 };

        Assert.True(PremiereFilter.PassesScoreFilter(item, filters));
    }

    [Fact]
    public void PassesScoreFilter_NormalizesRottenTomatoesFromTenPointUiRange()
    {
        var item = Item with { RottenTomatoesScore = 83 };
        var filters = new CalendarFilters { ScoreSource = ScoreSource.RottenTomatoes, MinScore = 8, MaxScore = 9 };

        Assert.True(PremiereFilter.PassesScoreFilter(item, filters));
    }

    [Fact]
    public void PassesScoreFilter_NormalizesMetacriticFromTenPointUiRange()
    {
        var item = Item with { MetacriticScore = 74 };
        var filters = new CalendarFilters { ScoreSource = ScoreSource.Metacritic, MinScore = 7, MaxScore = 8 };

        Assert.True(PremiereFilter.PassesScoreFilter(item, filters));
    }

    [Fact]
    public void PassesScoreFilter_ObeysUnknownScoreToggle()
    {
        var filters = new CalendarFilters
        {
            ScoreSource = ScoreSource.Imdb,
            IncludeUnknownScores = false
        };

        Assert.False(PremiereFilter.PassesScoreFilter(Item with { ImdbScore = null }, filters));

        filters.IncludeUnknownScores = true;
        Assert.True(PremiereFilter.PassesScoreFilter(Item with { ImdbScore = null }, filters));
    }

    [Fact]
    public void Apply_FiltersByMediaTypeAndSearchText()
    {
        var items = new[]
        {
            Item with { MediaType = PremiereMediaType.Series, Title = "North Star" },
            Item with { MediaType = PremiereMediaType.Movie, Title = "South Point" }
        };
        var filters = new CalendarFilters
        {
            ShowSeries = false,
            SearchText = "south"
        };

        var filtered = PremiereFilter.Apply(items, filters);

        var onlyItem = Assert.Single(filtered);
        Assert.Equal("South Point", onlyItem.Title);
    }

    [Fact]
    public void Apply_FiltersByLanguageAndOriginGroup()
    {
        var items = new[]
        {
            Item with { OriginalLanguage = "en", OriginCountries = ["GB"], Title = "British Premiere" },
            Item with { OriginalLanguage = "en", OriginCountries = ["US"], Title = "American Premiere" },
            Item with { OriginalLanguage = "nl", OriginCountries = ["NL"], Title = "Dutch Premiere" }
        };
        var filters = new CalendarFilters
        {
            Language = LanguageFilter.English,
            OriginGroup = OriginGroupFilter.UnitedKingdom
        };

        var filtered = PremiereFilter.Apply(items, filters);

        var onlyItem = Assert.Single(filtered);
        Assert.Equal("British Premiere", onlyItem.Title);
    }

    [Fact]
    public void Apply_FiltersByFrenchLanguageAndBelgianOrigin()
    {
        var items = new[]
        {
            Item with { OriginalLanguage = "fr", OriginCountries = ["BE"], Title = "Belgian French Premiere" },
            Item with { OriginalLanguage = "nl", OriginCountries = ["BE"], Title = "Belgian Dutch Premiere" },
            Item with { OriginalLanguage = "fr", OriginCountries = ["FR"], Title = "French Premiere" }
        };
        var filters = new CalendarFilters
        {
            Language = LanguageFilter.French,
            OriginGroup = OriginGroupFilter.Belgium
        };

        var filtered = PremiereFilter.Apply(items, filters);

        var onlyItem = Assert.Single(filtered);
        Assert.Equal("Belgian French Premiere", onlyItem.Title);
    }


    [Fact]
    public void Apply_FiltersByGenreVotesRuntimeNetworkAndKeywords()
    {
        var items = new[]
        {
            Item with
            {
                Title = "Mystery Channel",
                GenreIds = [9648],
                Genres = ["Mystery"],
                SourceNames = ["HBO Max"],
                Keywords = ["missing person"],
                RuntimeMinutes = 55,
                TmdbVoteCount = 120
            },
            Item with
            {
                Title = "Comedy Short",
                GenreIds = [35],
                Genres = ["Comedy"],
                NetworkName = "Other",
                Keywords = ["stand-up"],
                RuntimeMinutes = 25,
                TmdbVoteCount = 10
            }
        };
        var filters = new CalendarFilters
        {
            GenreIds = [9648],
            SelectedSources = ["HBO Max"],
            NetworkText = "hbo",
            KeywordText = "missing",
            MinVoteCount = 100,
            RuntimeMinMinutes = 40,
            RuntimeMaxMinutes = 80
        };

        var filtered = PremiereFilter.Apply(items, filters);

        var onlyItem = Assert.Single(filtered);
        Assert.Equal("Mystery Channel", onlyItem.Title);
    }

    [Fact]
    public void Apply_FiltersMinimumVotesUsingSelectedScoreSource()
    {
        var items = new[]
        {
            Item with
            {
                Title = "IMDb Popular",
                TmdbVoteCount = 3,
                ImdbScore = 7.8,
                ImdbVoteCount = 15_000
            },
            Item with
            {
                Title = "IMDb Thin",
                TmdbVoteCount = 250,
                ImdbScore = 8.1,
                ImdbVoteCount = 20
            }
        };
        var filters = new CalendarFilters
        {
            ScoreSource = ScoreSource.Imdb,
            MinVoteCount = 1_000
        };

        var filtered = PremiereFilter.Apply(items, filters);

        var onlyItem = Assert.Single(filtered);
        Assert.Equal("IMDb Popular", onlyItem.Title);
    }

    [Fact]
    public void Apply_FiltersByLegacyProviderIdWithoutLabel()
    {
        var items = new[]
        {
            Item with
            {
                Title = "Netflix Movie",
                Sources = [new PremiereSource { Id = 8, Name = "Netflix", Kind = "flatrate" }],
                SourceNames = ["Netflix"]
            },
            Item with
            {
                Title = "Other Movie",
                Sources = [new PremiereSource { Id = 337, Name = "Disney Plus", Kind = "flatrate" }],
                SourceNames = ["Disney Plus"]
            }
        };
        var filters = new CalendarFilters
        {
            MovieFilters =
            {
                SelectedSources = ["provider:8"]
            }
        };

        var filtered = PremiereFilter.Apply(items, filters);

        var onlyItem = Assert.Single(filtered);
        Assert.Equal("Netflix Movie", onlyItem.Title);
    }

    [Fact]
    public void Apply_UsesMediaSpecificFiltersForAllView()
    {
        var items = new[]
        {
            Item with
            {
                MediaType = PremiereMediaType.Series,
                Title = "Belgian Streamz Series",
                OriginalLanguage = "nl",
                OriginCountries = ["BE"],
                Sources = [new PremiereSource { Name = "Streamz", Kind = "flatrate" }],
                SourceNames = ["Streamz"],
                TvStatus = "Returning Series",
                TvType = "Scripted"
            },
            Item with
            {
                MediaType = PremiereMediaType.Movie,
                Title = "Digital Movie",
                OriginalLanguage = "en",
                OriginCountries = ["US"],
                Sources = [new PremiereSource { Name = "Apple TV", Kind = "rent" }],
                SourceNames = ["Apple TV"],
                MovieReleaseTypes = [4],
                Certifications = ["US:PG-13"]
            },
            Item with
            {
                MediaType = PremiereMediaType.Movie,
                Title = "Theatrical Movie",
                OriginalLanguage = "en",
                OriginCountries = ["US"],
                Sources = [new PremiereSource { Name = "Cinema", Kind = "buy" }],
                SourceNames = ["Cinema"],
                MovieReleaseTypes = [3],
                Certifications = ["US:R"]
            }
        };
        var filters = new CalendarFilters
        {
            SeriesFilters =
            {
                OriginalLanguages = ["nl"],
                OriginCountries = ["BE"],
                SelectedSources = ["Streamz"],
                MonetizationTypes = ["flatrate"],
                TvStatuses = ["Returning Series"],
                TvTypes = ["Scripted"]
            },
            MovieFilters =
            {
                MovieReleaseTypes = [4],
                Certifications = ["US:PG-13"],
                MonetizationTypes = ["rent"]
            }
        };

        var filtered = PremiereFilter.Apply(items, filters);

        Assert.Equal(["Belgian Streamz Series", "Digital Movie"], filtered.Select(item => item.Title));
    }

    [Fact]
    public void Apply_FiltersByMultipleMediaSpecificLanguages()
    {
        var items = new[]
        {
            Item with { Title = "English Series", MediaType = PremiereMediaType.Series, OriginalLanguage = "en" },
            Item with { Title = "Dutch Series", MediaType = PremiereMediaType.Series, OriginalLanguage = "nl" },
            Item with { Title = "French Series", MediaType = PremiereMediaType.Series, OriginalLanguage = "fr" }
        };
        var filters = new CalendarFilters
        {
            SeriesFilters =
            {
                OriginalLanguages = ["en", "nl"]
            }
        };

        var filtered = PremiereFilter.Apply(items, filters);

        Assert.Equal(["Dutch Series", "English Series"], filtered.Select(item => item.Title).Order());
    }

    [Fact]
    public void Apply_SortsBySelectedScoreDescending()
    {
        var items = new[]
        {
            Item with { Title = "Lower", TmdbScore = 5.5 },
            Item with { Title = "Higher", TmdbScore = 9.1 }
        };
        var filters = new CalendarFilters
        {
            SortMode = PremiereSortMode.Score,
            SortDirection = SortDirection.Descending
        };

        var filtered = PremiereFilter.Apply(items, filters);

        Assert.Equal(["Higher", "Lower"], filtered.Select(item => item.Title));
    }

    private static PremiereItem Item => new()
    {
        MediaType = PremiereMediaType.Movie,
        TmdbId = 1,
        Title = "Premiere",
        PremiereDate = new DateOnly(2026, 5, 4),
        TmdbScore = 7.5
    };
}
