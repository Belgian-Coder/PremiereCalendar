using Microsoft.AspNetCore.WebUtilities;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class FilterStorageQueryComposerTests
{
    [Fact]
    public void ComposeAllQuery_OverlaysSeparateSeriesAndMovieFiltersOntoAllQuery()
    {
        var result = FilterStorageQueryComposer.ComposeAllQuery(
            "week=2026-05-04&media=all&sort=score&seriesLang=en&seriesSources=HBO%20Max&movieReleaseTypes=3",
            "week=2026-05-11&seriesLang=nl&seriesSources=Streamz&seriesStatuses=Returning%20Series",
            "week=2026-05-18&movieSources=Apple%20TV&movieReleaseTypes=4");

        var query = QueryHelpers.ParseQuery($"?{result}");

        Assert.Equal("2026-05-04", query["week"].ToString());
        Assert.Equal("all", query["media"].ToString());
        Assert.Equal("score", query["sort"].ToString());
        Assert.Equal("nl", query["seriesLang"].ToString());
        Assert.Equal("Streamz", query["seriesSources"].ToString());
        Assert.Equal("Returning Series", query["seriesStatuses"].ToString());
        Assert.Equal("Apple TV", query["movieSources"].ToString());
        Assert.Equal("4", query["movieReleaseTypes"].ToString());
    }

    [Fact]
    public void ComposeAllQuery_CanBuildAllQueryFromSeparateMediaQueries()
    {
        var result = FilterStorageQueryComposer.ComposeAllQuery(
            allQuery: null,
            seriesQuery: "week=2026-05-11&sort=title&seriesLang=nl&seriesSources=VTM%20GO",
            movieQuery: "week=2026-05-18&movieSources=Netflix&movieReleaseTypes=4");

        var query = QueryHelpers.ParseQuery($"?{result}");

        Assert.Equal("2026-05-11", query["week"].ToString());
        Assert.False(query.ContainsKey("media"));
        Assert.Equal("title", query["sort"].ToString());
        Assert.Equal("nl", query["seriesLang"].ToString());
        Assert.Equal("VTM GO", query["seriesSources"].ToString());
        Assert.Equal("Netflix", query["movieSources"].ToString());
        Assert.Equal("4", query["movieReleaseTypes"].ToString());
    }

    [Fact]
    public void ComposeAllQuery_ReturnsNullWhenNoSavedQueriesExist()
    {
        var result = FilterStorageQueryComposer.ComposeAllQuery(
            allQuery: null,
            seriesQuery: "",
            movieQuery: " ");

        Assert.Null(result);
    }

    [Fact]
    public void HasMeaningfulFilterQuery_IgnoresWeekAndOldDefaultValues()
    {
        var result = FilterStorageQueryComposer.HasMeaningfulFilterQuery(
            "?week=2026-05-04&sort=date&dir=asc&score=tmdb&unknown=1&min=0.0&max=10.0&minVotes=0&lang=both&origin=all&runtimeMin=0&runtimeMax=360&seriesScope=episodes");

        Assert.False(result);
    }

    [Fact]
    public void HasMeaningfulFilterQuery_DetectsRealFilterValues()
    {
        Assert.True(FilterStorageQueryComposer.HasMeaningfulFilterQuery("?seriesLang=en,nl"));
        Assert.True(FilterStorageQueryComposer.HasMeaningfulFilterQuery("?movieRuntimeMin=45"));
        Assert.True(FilterStorageQueryComposer.HasMeaningfulFilterQuery("?seriesScope=new"));
    }

    [Fact]
    public void HasMeaningfulFilterQuery_IgnoresUnsupportedMediaSpecificKeys()
    {
        var result = FilterStorageQueryComposer.HasMeaningfulFilterQuery(
            "?movieScope=new&seriesReleaseTypes=3&seriesCertifications=US%3APG-13&seriesCertificationCountry=US&movieStatuses=Returning%20Series&movieTypes=Scripted");

        Assert.False(result);
    }

    [Fact]
    public void HasMeaningfulFilterQuery_UsesEarlierNonBlankDuplicateValues()
    {
        var result = FilterStorageQueryComposer.HasMeaningfulFilterQuery("?seriesLang=nl&seriesLang=");

        Assert.True(result);
    }

    [Fact]
    public void ComposeRestoredQuery_PreservesCurrentWeekButDropsStoredWeek()
    {
        var result = FilterStorageQueryComposer.ComposeRestoredQuery(
            "?week=2026-05-04&sort=date",
            "week=2026-04-20&seriesLang=nl&movieSources=Netflix");

        var query = QueryHelpers.ParseQuery($"?{result}");

        Assert.Equal("2026-05-04", query["week"].ToString());
        Assert.Equal("nl", query["seriesLang"].ToString());
        Assert.Equal("Netflix", query["movieSources"].ToString());
    }

    [Fact]
    public void ComposeRestoredQuery_DropsStoredDayWhenCurrentUrlHasNoDay()
    {
        var result = FilterStorageQueryComposer.ComposeRestoredQuery(
            "?week=2026-05-11",
            "week=2026-05-04&day=2026-05-05&seriesLang=nl");

        var query = QueryHelpers.ParseQuery($"?{result}");

        Assert.Equal("2026-05-11", query["week"].ToString());
        Assert.Equal("nl", query["seriesLang"].ToString());
        Assert.False(query.ContainsKey("day"));
    }

    [Fact]
    public void ComposeRestoredQuery_PreservesCurrentDayWhenCurrentUrlHasDay()
    {
        var result = FilterStorageQueryComposer.ComposeRestoredQuery(
            "?week=2026-05-11&day=2026-05-13",
            "week=2026-05-04&day=2026-05-05&seriesLang=nl");

        var query = QueryHelpers.ParseQuery($"?{result}");

        Assert.Equal("2026-05-11", query["week"].ToString());
        Assert.Equal("2026-05-13", query["day"].ToString());
        Assert.Equal("nl", query["seriesLang"].ToString());
    }

    [Fact]
    public void ComposeRestoredQuery_DropsUnsupportedMediaSpecificKeys()
    {
        var result = FilterStorageQueryComposer.ComposeRestoredQuery(
            "?week=2026-05-11",
            "week=2026-05-04&seriesLang=nl&movieScope=new&seriesReleaseTypes=3&movieStatuses=Returning%20Series");

        var query = QueryHelpers.ParseQuery($"?{result}");

        Assert.Equal("2026-05-11", query["week"].ToString());
        Assert.Equal("nl", query["seriesLang"].ToString());
        Assert.False(query.ContainsKey("movieScope"));
        Assert.False(query.ContainsKey("seriesReleaseTypes"));
        Assert.False(query.ContainsKey("movieStatuses"));
    }

    [Fact]
    public void HasMeaningfulFilterQuery_HandlesDuplicateKeysWithDifferentCasing()
    {
        var result = FilterStorageQueryComposer.HasMeaningfulFilterQuery(
            "?week=2026-05-04&WEEK=2026-05-11&sort=date&SORT=date");

        Assert.False(result);
    }
}
