namespace PremiereCalendar.Options;

public sealed class TvmazeOptions
{
    public string BaseUrl { get; set; } = "https://api.tvmaze.com/";
    public bool Enabled { get; set; } = true;
    public bool EnableScheduleDiscovery { get; set; } = true;
    public int ScheduleFetchConcurrency { get; set; } = 4;
    public int MaxConcurrentRequests { get; set; } = 4;
    public string[] ScheduleCountries { get; set; } = [];
}
