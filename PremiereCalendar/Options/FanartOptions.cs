namespace PremiereCalendar.Options;

public sealed class FanartOptions
{
    public string BaseUrl { get; set; } = "https://webservice.fanart.tv/v3/";
    public bool Enabled { get; set; }
    public string? ApiKey { get; set; }
}
