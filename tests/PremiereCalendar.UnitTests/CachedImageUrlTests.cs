using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class CachedImageUrlTests
{
    [Fact]
    public void Build_EncodesSourceUrlAndVersion()
    {
        var url = CachedImageUrl.Build(
            "https://image.tmdb.org/t/p/w342/poster path.jpg",
            "12345");

        Assert.Equal(
            "/cached-image?url=https%3A%2F%2Fimage.tmdb.org%2Ft%2Fp%2Fw342%2Fposter%20path.jpg&v=12345",
            url);
    }

    [Fact]
    public void Build_AddsRequestedImageWidth()
    {
        var url = CachedImageUrl.Build(
            "https://static.tvmaze.com/uploads/images/original_untouched/1/1.jpg",
            width: 185);

        Assert.Equal(
            "/cached-image?url=https%3A%2F%2Fstatic.tvmaze.com%2Fuploads%2Fimages%2Foriginal_untouched%2F1%2F1.jpg&w=185",
            url);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0, null)]
    [InlineData(-5, null)]
    [InlineData(1, 185)]
    [InlineData(64, 185)]
    [InlineData(185, 185)]
    [InlineData(640, 185)]
    [InlineData(int.MaxValue, 185)]
    public void SnapWidth_MapsEveryPositiveWidthToTheNearestAllowedWidth(int? requested, int? expected)
    {
        Assert.Equal(expected, CachedImageUrl.SnapWidth(requested));
        Assert.Contains(CachedImageUrl.PosterWidth, CachedImageUrl.AllowedWidths);
    }

    [Theory]
    [InlineData("image/jpeg", "image/jpeg", true)]
    [InlineData("IMAGE/PNG; charset=binary", "image/png", true)]
    [InlineData("image/gif", "image/gif", true)]
    [InlineData("image/webp", "image/webp", true)]
    [InlineData("image/avif", "image/avif", true)]
    [InlineData("image/svg+xml", "image/svg+xml", false)]
    [InlineData("text/html", "text/html", false)]
    [InlineData("", "", false)]
    [InlineData(null, "", false)]
    public void TryGetRasterContentType_AcceptsOnlyRasterFormats(string? contentType, string expectedNormalized, bool expected)
    {
        Assert.Equal(expected, CachedImageUrl.TryGetRasterContentType(contentType, out var normalized));
        Assert.Equal(expectedNormalized, normalized);
    }
}
