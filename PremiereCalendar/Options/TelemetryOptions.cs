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
    public bool PerformanceDiagnosticsEnabled { get; set; } = true;
    // Empty follows LogDirectory, including deployment-specific overrides.
    public string PerformanceDiagnosticsDirectory { get; set; } = "";
    public int PerformanceFileSizeLimitMegabytes { get; set; } = 25;
    public int PerformanceRetainedFileCount { get; set; } = 14;
    public int PerformanceFlushIntervalSeconds { get; set; } = 2;
    public int PerformanceQueueCapacity { get; set; } = 4096;
    public int PerformanceFlushTimeoutSeconds { get; set; } = 5;
    public int PerformanceShutdownTimeoutSeconds { get; set; } = 5;
}
