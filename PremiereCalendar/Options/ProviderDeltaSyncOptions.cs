namespace PremiereCalendar.Options;

public sealed class ProviderDeltaSyncOptions
{
    public bool Enabled { get; set; } = true;
    public bool RunOnStartup { get; set; } = true;
    public int StartupDelaySeconds { get; set; } = 20;
    public int WakeIntervalMinutes { get; set; } = 60;
    public int TmdbLookbackDays { get; set; } = 14;
    public int InitialLookbackDays { get; set; } = 2;
    public int ChangeHistoryConcurrency { get; set; } = 4;
    public int HistoryBatchSize { get; set; } = 100;
    public int HistoryMaximumItemsPerCycle { get; set; } = 500;
    public int ProviderBudgetSeconds { get; set; } = 90;
    public int ContinuationDelaySeconds { get; set; } = 60;
    public int TmdbIntervalMinutes { get; set; } = 60;
    public int TvmazeIntervalMinutes { get; set; } = 60;
    public bool UseTmdbChanges { get; set; } = true;
    public bool UseTvmazeUpdates { get; set; } = true;
}
