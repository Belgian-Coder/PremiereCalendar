using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class CalendarQueryCodecTests
{
    [Theory]
    [InlineData("https://calendar.test/", CalendarPageMode.All)]
    [InlineData("https://calendar.test/series?week=2026-05-04", CalendarPageMode.Series)]
    [InlineData("https://calendar.test/movies/", CalendarPageMode.Movies)]
    public void ResolvesBackwardCompatibleRoutes(string value, CalendarPageMode expected)
        => Assert.Equal(expected, CalendarQueryCodec.ResolvePageMode(new Uri(value)));

    [Fact]
    public void PreservesCanonicalPathAndQueryForViewSync()
    {
        var uri = new Uri("https://calendar.test/series?week=2026-05-04&score=imdb");
        Assert.Equal("/series?week=2026-05-04&score=imdb", CalendarQueryCodec.PathAndQuery(uri));
        Assert.Equal("/series?week=2026-05-04&score=imdb", CalendarViewSyncNavigationCoordinator.Normalize(uri));
    }
}
