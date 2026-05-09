namespace PremiereCalendar.Options;

public sealed class TvmazeOptions
{
    public string BaseUrl { get; set; } = "https://api.tvmaze.com/";
    public bool Enabled { get; set; } = true;
    public bool EnableScheduleDiscovery { get; set; } = true;
    public int ScheduleFetchConcurrency { get; set; } = 20;
    public string[] ScheduleCountries { get; set; } = [];
}
