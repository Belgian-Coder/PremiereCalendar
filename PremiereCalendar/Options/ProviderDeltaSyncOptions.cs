namespace PremiereCalendar.Options;

public sealed class ProviderDeltaSyncOptions
{
    public bool Enabled { get; set; } = true;
    public bool RunOnStartup { get; set; } = true;
    public int StartupDelaySeconds { get; set; } = 20;
    public int WakeIntervalMinutes { get; set; } = 60;
    public int TmdbLookbackDays { get; set; } = 14;
    public bool UseTmdbChanges { get; set; } = true;
    public bool UseTvmazeUpdates { get; set; } = true;
}
