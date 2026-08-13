using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using PremiereCalendar.Options;
using PremiereCalendar.Services;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace PremiereCalendar.Hosting;

public static class TelemetryExtensions
{
    public static void AddPremiereTelemetry(this WebApplicationBuilder builder)
    {
        var section = builder.Configuration.GetSection("Telemetry");
        builder.Services.AddOptions<TelemetryOptions>().Bind(section)
            .Validate(options => options.FileSizeLimitMegabytes is >= 1 and <= 1024, "Telemetry file limit must be between 1 and 1024 MB")
            .Validate(options => options.RetainedFileCount is >= 1 and <= 100, "Telemetry retained file count must be between 1 and 100")
            .Validate(options => options.TraceSampleRatio is >= 0 and <= 1, "Telemetry trace sample ratio must be between 0 and 1")
            .ValidateOnStart();
        builder.Services.AddSingleton<PremiereTelemetry>();

        var options = section.Get<TelemetryOptions>() ?? new TelemetryOptions();
        var logDirectory = Path.GetFullPath(Path.IsPathFullyQualified(options.LogDirectory)
            ? options.LogDirectory
            : Path.Combine(builder.Environment.ContentRootPath, options.LogDirectory));
        Directory.CreateDirectory(logDirectory);
        builder.Host.UseSerilog((context, services, configuration) =>
        {
            configuration
                .MinimumLevel.Information()
                .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
                .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
                .Enrich.FromLogContext()
                .Enrich.WithProperty("service", options.ServiceName)
                .WriteTo.Console(new CompactJsonFormatter())
                .WriteTo.File(
                    new CompactJsonFormatter(),
                    Path.Combine(logDirectory, "premiere-calendar-.ndjson"),
                    rollingInterval: RollingInterval.Day,
                    fileSizeLimitBytes: Math.Clamp(options.FileSizeLimitMegabytes, 1, 1024) * 1024L * 1024L,
                    rollOnFileSizeLimit: true,
                    retainedFileCountLimit: Math.Clamp(options.RetainedFileCount, 1, 100),
                    shared: true,
                    flushToDiskInterval: TimeSpan.FromSeconds(2));
        });

        if (!options.Enabled) return;
        var openTelemetry = builder.Services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(options.ServiceName));
        openTelemetry.WithTracing(tracing =>
        {
            tracing
                .SetSampler(new TraceIdRatioBasedSampler(options.TraceSampleRatio))
                .AddSource(PremiereTelemetry.ActivitySourceName);
            if (TryGetOtlpEndpoint(options, out var endpoint))
            {
                tracing.AddOtlpExporter(exporter => exporter.Endpoint = endpoint);
            }
        });
        openTelemetry.WithMetrics(metrics =>
        {
            metrics.AddMeter(PremiereTelemetry.MeterName).AddRuntimeInstrumentation();
            if (TryGetOtlpEndpoint(options, out var endpoint))
            {
                metrics.AddOtlpExporter(exporter => exporter.Endpoint = endpoint);
            }
        });
    }

    private static bool TryGetOtlpEndpoint(TelemetryOptions options, out Uri endpoint)
        => Uri.TryCreate(options.OtlpEndpoint, UriKind.Absolute, out endpoint!);
}
