namespace PremiereCalendar.Options;

public sealed class OmdbOptions
{
    public string BaseUrl { get; set; } = "https://www.omdbapi.com/";
    public string? ApiKey { get; set; }
    public bool Enabled { get; set; }
}
