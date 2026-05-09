namespace PremiereCalendar.Options;

public sealed class TraktOptions
{
    public string BaseUrl { get; set; } = "https://api.trakt.tv/";
    public bool Enabled { get; set; } = true;
    public string? ClientId { get; set; }
    public string ApiVersion { get; set; } = "2";
}
