namespace PremiereCalendar.Options;

public sealed class CalendarWarmupOptions
{
    public bool Enabled { get; set; } = true;
    public bool RunOnStartup { get; set; } = true;
    public int StartupDelaySeconds { get; set; } = 10;
    public int WakeIntervalMinutes { get; set; } = 15;
    public int MinimumRemoteRefreshMinutes { get; set; } = 60;
    public int MaximumProfilesPerWake { get; set; } = 5;
    public int MaximumRemoteWindowsPerWake { get; set; } = 4;
    public int TopFilterProfileCount { get; set; } = 4;
    public bool SkipWhenForegroundLoadActive { get; set; } = true;
    public int CleanupRetentionDays { get; set; } = 60;
    public int CycleBudgetSeconds { get; set; } = 600;
    public int WindowBudgetSeconds { get; set; } = 30;
    public bool StaleOnlyRemoteRefresh { get; set; } = true;
}
