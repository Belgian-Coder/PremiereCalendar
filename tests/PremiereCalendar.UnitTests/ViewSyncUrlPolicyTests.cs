using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class ViewSyncUrlPolicyTests
{
    [Theory]
    [InlineData("/", "/")]
    [InlineData("/series?week=2026-05-04&day=2026-05-05&seriesLang=en,nl", "/series?week=2026-05-04&day=2026-05-05&seriesLang=en,nl")]
    [InlineData("/movies?week=2026-04-27&movieRuntimeMin=45", "/movies?week=2026-04-27&movieRuntimeMin=45")]
    public void TryNormalize_AllowsRelativeCalendarUrls(string value, string expected)
    {
        var accepted = ViewSyncUrlPolicy.TryNormalize(value, out var normalized);

        Assert.True(accepted);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("")]
    [InlineData("https://example.test/series?week=2026-05-04")]
    [InlineData("//example.test/series")]
    [InlineData("/settings")]
    [InlineData("/about")]
    [InlineData("/series/details")]
    [InlineData("/movies#today")]
    public void TryNormalize_RejectsNonSyncedOrUnsafeUrls(string value)
    {
        var accepted = ViewSyncUrlPolicy.TryNormalize(value, out var normalized);

        Assert.False(accepted);
        Assert.Null(normalized);
    }
}
