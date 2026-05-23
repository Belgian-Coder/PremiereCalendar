namespace PremiereCalendar.Options;

public sealed class SimklOptions
{
    public string BaseUrl { get; set; } = "https://api.simkl.com/";
    public string CalendarBaseUrl { get; set; } = "https://data.simkl.in/";
    public string AppName { get; set; } = "premiere-calendar";
    public string AppVersion { get; set; } = "1.0";
    public bool Enabled { get; set; } = true;
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public int RequestTimeoutSeconds { get; set; } = 20;
    public int MinimumActivityCheckMinutes { get; set; } = 30;
}
