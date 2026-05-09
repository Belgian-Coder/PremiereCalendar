using PremiereCalendar.Models;

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
}
