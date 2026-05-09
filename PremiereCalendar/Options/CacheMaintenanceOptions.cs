namespace PremiereCalendar.Options;

public sealed class CacheMaintenanceOptions
{
    public bool Enabled { get; set; } = true;
    public int RetentionDays { get; set; } = 60;
    public int SweepIntervalHours { get; set; } = 24;
}
