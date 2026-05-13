using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class CalendarFiltersTests
{
    [Theory]
    [InlineData("2026-05-04", "2026-05-04")]
    [InlineData("2026-05-10", "2026-05-04")]
    [InlineData("2026-01-01", "2025-12-29")]
    public void StartOfWeek_UsesMondayStart(string input, string expected)
    {
        var actual = CalendarFilters.StartOfWeek(DateOnly.Parse(input));

        Assert.Equal(DateOnly.Parse(expected), actual);
    }

    [Fact]
    public void Normalize_RestoresBothMediaTypesWhenQueryDisablesAllMedia()
    {
        var filters = new CalendarFilters
        {
            ShowSeries = false,
            ShowMovies = false
        };

        CalendarFilterState.Normalize(filters);

        Assert.True(filters.ShowSeries);
        Assert.True(filters.ShowMovies);
    }

    [Fact]
    public void Normalize_SwapsReversedScoreAndRuntimeRanges()
    {
        var filters = new CalendarFilters
        {
            MinScore = 9.5,
            MaxScore = 2.5,
            RuntimeMinMinutes = 180,
            RuntimeMaxMinutes = 45,
            SeriesFilters = new MediaFilterSet
            {
                RuntimeMinMinutes = 120,
                RuntimeMaxMinutes = 30
            },
            MovieFilters = new MediaFilterSet
            {
                RuntimeMinMinutes = 150,
                RuntimeMaxMinutes = 75
            }
        };

        CalendarFilterState.Normalize(filters);

        Assert.Equal(2.5, filters.MinScore);
        Assert.Equal(9.5, filters.MaxScore);
        Assert.Equal(45, filters.RuntimeMinMinutes);
        Assert.Equal(180, filters.RuntimeMaxMinutes);
        Assert.Equal(30, filters.SeriesFilters.RuntimeMinMinutes);
        Assert.Equal(120, filters.SeriesFilters.RuntimeMaxMinutes);
        Assert.Equal(75, filters.MovieFilters.RuntimeMinMinutes);
        Assert.Equal(150, filters.MovieFilters.RuntimeMaxMinutes);
    }

    [Fact]
    public void Normalize_DropsInvalidMovieReleaseTypes()
    {
        var filters = new MediaFilterSet
        {
            MovieReleaseTypes = [4, 99, 2, 4, -1]
        };

        CalendarFilterState.Normalize(filters);

        Assert.Equal([2, 4], filters.MovieReleaseTypes);
    }
}
