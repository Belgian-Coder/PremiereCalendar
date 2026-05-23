namespace PremiereCalendar.Options;

public sealed class RottenTomatoesOptions
{
    public string BaseUrl { get; set; } = "https://www.rottentomatoes.com/";
    public bool Enabled { get; set; } = true;
    public int CacheHours { get; set; } = 24;
    public int RequestTimeoutSeconds { get; set; } = 20;
}
