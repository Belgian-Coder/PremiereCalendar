namespace PremiereCalendar.Options;

public sealed class CalendarLoadOptions
{
    public int ForegroundLoadBudgetSeconds { get; set; } = 45;
    public int StaleCacheForegroundBudgetSeconds { get; set; } = 5;
    public int BackgroundCompletionBudgetSeconds { get; set; } = 300;
}
