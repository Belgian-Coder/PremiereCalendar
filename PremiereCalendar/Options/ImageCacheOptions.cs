namespace PremiereCalendar.Options;

public sealed class ImageCacheOptions
{
    public bool Enabled { get; init; } = true;
    public string Directory { get; init; } = "App_Data/cache/images";
    public int CacheDays { get; init; } = 30;
    public int MaxBytes { get; init; } = 5 * 1024 * 1024;
    public int MaxConcurrentDownloads { get; init; } = 4;
    public string[] AllowedHosts { get; init; } =
    [
        "image.tmdb.org",
        ".media-amazon.com",
        "static.tvmaze.com",
        "assets.fanart.tv",
        "webservice.fanart.tv",
        "artworks.thetvdb.com",
        "upload.wikimedia.org",
        "commons.wikimedia.org"
    ];
}
