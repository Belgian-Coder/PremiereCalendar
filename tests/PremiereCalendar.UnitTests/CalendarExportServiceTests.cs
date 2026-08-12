using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class CalendarExportServiceTests
{
    private static PremiereItem Item(string title = "A, Film") => new() { CanonicalId = "movie:1", MediaType = PremiereMediaType.Movie, TmdbId = 1, Title = title, PremiereDate = new DateOnly(2026, 8, 11), Overview = "line 1\nline 2" };

    [Fact]
    public void IcsUsesAllDayDatesAndEscapesText()
    {
        var ics = CalendarExportService.ToIcs([Item()], generatedAtUtc: new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero));
        Assert.Contains("DTSTART;VALUE=DATE:20260811", ics);
        Assert.Contains("DTEND;VALUE=DATE:20260812", ics);
        Assert.Contains("DTSTAMP:20260811T120000Z", ics);
        Assert.Contains("SUMMARY:A\\, Film", ics);
        Assert.Contains("DESCRIPTION:line 1\\nline 2", ics);
    }

    [Fact]
    public void CsvQuotesCommasAndNewlines()
    {
        var csv = CalendarExportService.ToCsv([Item()]);
        Assert.Contains("\"A, Film\"", csv);
        Assert.Contains("movie:1", csv);
    }

    [Fact]
    public void JsonContainsCanonicalIdentity()
    {
        Assert.Contains("movie:1", CalendarExportService.ToJson([Item()]));
    }

    [Theory]
    [InlineData("=cmd")]
    [InlineData("+SUM(A1:A2)")]
    [InlineData("-1+2")]
    [InlineData("@formula")]
    public void CsvNeutralizesSpreadsheetFormulas(string title)
    {
        var csv = CalendarExportService.ToCsv([Item(title)]);
        Assert.Contains($"'{title}", csv);
    }
}
