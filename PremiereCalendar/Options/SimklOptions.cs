namespace PremiereCalendar.Options;

public sealed class SimklOptions
{
    public string BaseUrl { get; set; } = "https://api.simkl.com/";
    public bool Enabled { get; set; } = true;
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public int RequestTimeoutSeconds { get; set; } = 20;
    public int MinimumActivityCheckMinutes { get; set; } = 30;
}
