namespace PremiereCalendar.Options;

public sealed class ProviderSchedulerOptions
{
    public bool Enabled { get; set; } = true;
    public int LeaseSeconds { get; set; } = 120;
    public int MaximumAttempts { get; set; } = 5;
    public int CompletedRetentionDays { get; set; } = 7;
}
