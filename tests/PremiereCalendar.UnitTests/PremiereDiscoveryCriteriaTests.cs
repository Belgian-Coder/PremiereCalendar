using System.Globalization;
using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class PremiereDiscoveryCriteriaTests
{
    [Fact]
    public void CacheKey_IncludesRawMediaSpecificSourceSelections()
    {
        var streamz = PremiereDiscoveryCriteria.FromFilters(new CalendarFilters
        {
            ShowSeries = true,
            ShowMovies = false,
            SeriesFilters =
            {
                SelectedSources = ["Streamz"]
            }
        }).CacheKey();
        var hbo = PremiereDiscoveryCriteria.FromFilters(new CalendarFilters
        {
            ShowSeries = true,
            ShowMovies = false,
            SeriesFilters =
            {
                SelectedSources = ["HBO Max"]
            }
        }).CacheKey();

        Assert.NotEqual(streamz, hbo);
    }

    [Fact]
    public void CacheKey_IncludesMediaSpecificSearchText()
    {
        var north = PremiereDiscoveryCriteria.FromFilters(new CalendarFilters
        {
            ShowSeries = true,
            ShowMovies = false,
            SeriesFilters =
            {
                SearchText = "north"
            }
        }).CacheKey();
        var south = PremiereDiscoveryCriteria.FromFilters(new CalendarFilters
        {
            ShowSeries = true,
            ShowMovies = false,
            SeriesFilters =
            {
                SearchText = "south"
            }
        }).CacheKey();

        Assert.NotEqual(north, south);
    }

    [Fact]
    public void CacheKey_IncludesMediaSpecificSourceText()
    {
        var cbs = PremiereDiscoveryCriteria.FromFilters(new CalendarFilters
        {
            ShowSeries = true,
            ShowMovies = false,
            SeriesFilters =
            {
                SourceText = "CBS"
            }
        }).CacheKey();
        var netflix = PremiereDiscoveryCriteria.FromFilters(new CalendarFilters
        {
            ShowSeries = true,
            ShowMovies = false,
            SeriesFilters =
            {
                SourceText = "Netflix"
            }
        }).CacheKey();

        Assert.NotEqual(cbs, netflix);
    }

    [Fact]
    public void CacheKey_UsesInvariantCultureForScoreValues()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var filters = new CalendarFilters
        {
            MinScore = 4.5,
            MaxScore = 8.5
        };

        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("en-US");
            var enUsKey = PremiereDiscoveryCriteria.FromFilters(filters).CacheKey();

            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("nl-BE");
            var nlBeKey = PremiereDiscoveryCriteria.FromFilters(filters).CacheKey();

            Assert.Equal(enUsKey, nlBeKey);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }
}
