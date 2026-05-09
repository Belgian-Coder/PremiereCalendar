using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class PremiereIdentityTests
{
    [Theory]
    [InlineData(PremiereMediaType.Series, 123, "tv:123")]
    [InlineData(PremiereMediaType.Movie, 456, "movie:456")]
    public void CanonicalId_UsesMediaTypeAndTmdbId(PremiereMediaType mediaType, int tmdbId, string expected)
    {
        Assert.Equal(expected, PremiereIdentity.CanonicalId(mediaType, tmdbId));
    }

    [Theory]
    [InlineData(PremiereMediaType.Series, PremiereItemType.SeriesPremiere)]
    [InlineData(PremiereMediaType.Movie, PremiereItemType.MovieFirstRelease)]
    public void ItemType_MapsCalendarEventTypes(PremiereMediaType mediaType, PremiereItemType expected)
    {
        Assert.Equal(expected, PremiereIdentity.ItemType(mediaType));
    }
}
