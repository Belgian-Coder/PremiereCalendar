using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class LocalReturnUrlTests
{
    [Theory]
    [InlineData("/movies?week=2026-05-04&score=imdb")]
    [InlineData("/series#week")]
    [InlineData("/")]
    public void IsSafe_AcceptsLocalPathAndQueryUrls(string returnUrl)
    {
        Assert.True(LocalReturnUrl.IsSafe(returnUrl));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("movies?week=2026-05-04")]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("\\\\evil.example")]
    [InlineData("/movies\nHost: evil.example")]
    [InlineData("/movies?next=https://evil.example")]
    public void IsSafe_RejectsExternalOrAmbiguousUrls(string returnUrl)
    {
        Assert.False(LocalReturnUrl.IsSafe(returnUrl));
    }
}
