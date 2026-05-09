namespace PremiereCalendar.Options;

public sealed class CalendarCacheOptions
{
    public bool Enabled { get; set; } = true;
    public string Directory { get; set; } = "App_Data/cache/calendar";
    public int WeekCacheHours { get; set; } = 6;
    public bool AdjacentWeekPrefetchEnabled { get; set; } = true;
    public int FuturePrefetchWeeks { get; set; } = 4;
    public int PastPrefetchWeeks { get; set; } = 2;
    public int AdjacentWeekPrefetchTimeoutSeconds { get; set; } = 30;
}
