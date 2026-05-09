using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class CachedImageUrlTests
{
    [Fact]
    public void Build_EncodesSourceUrlAndRefreshParameters()
    {
        var url = CachedImageUrl.Build(
            "https://image.tmdb.org/t/p/w342/poster path.jpg",
            "12345",
            refresh: true);

        Assert.Equal(
            "/cached-image?url=https%3A%2F%2Fimage.tmdb.org%2Ft%2Fp%2Fw342%2Fposter%20path.jpg&v=12345&refresh=true",
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
}
