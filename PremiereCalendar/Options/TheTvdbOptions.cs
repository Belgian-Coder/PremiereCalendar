namespace PremiereCalendar.Options;

public sealed class TheTvdbOptions
{
    public string BaseUrl { get; set; } = "https://api4.thetvdb.com/v4/";
    public string ImageBaseUrl { get; set; } = "https://artworks.thetvdb.com/banners/";
    public bool Enabled { get; set; }
    public string? ApiKey { get; set; }
}
