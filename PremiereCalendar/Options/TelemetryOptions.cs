namespace PremiereCalendar.Options;

public sealed class TelemetryOptions
{
    public bool Enabled { get; set; } = true;
    public string ServiceName { get; set; } = "PremiereCalendar";
    public string LogDirectory { get; set; } = "App_Data/logs";
    public int FileSizeLimitMegabytes { get; set; } = 50;
    public int RetainedFileCount { get; set; } = 14;
    public string OtlpEndpoint { get; set; } = "";
    public double TraceSampleRatio { get; set; } = 1.0;
}
