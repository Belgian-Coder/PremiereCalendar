using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class TelemetryContractTests
{
    [Fact]
    public void DefaultsKeepOtlpDisabledAndBoundRollingLogs()
    {
        var options = new TelemetryOptions();
        Assert.Equal("", options.OtlpEndpoint);
        Assert.Equal(50, options.FileSizeLimitMegabytes);
        Assert.Equal(14, options.RetainedFileCount);
    }

    [Fact]
    public void MetricsEmitStableLowCardinalitySignals()
    {
        var observed = new List<string>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == PremiereTelemetry.MeterName) meterListener.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, _, _) => observed.Add(instrument.Name));
        listener.SetMeasurementEventCallback<double>((instrument, _, _, _) => observed.Add(instrument.Name));
        listener.Start();

        var telemetry = new PremiereTelemetry();
        telemetry.RecordProviderRequest("tmdb", "Success", TimeSpan.FromMilliseconds(12), 2, ProviderCircuitState.Closed);
        telemetry.RecordJob("CalendarForeground", "completed", TimeSpan.FromMilliseconds(20));
        telemetry.RecordJobWait("CalendarForeground", TimeSpan.FromMilliseconds(5), resumed: true);
        telemetry.RecordProviderRetry("tmdb", "retry_after");
        telemetry.RecordDatabaseEvent("integrity", "passed");
        telemetry.RecordVersionValidation("database_schema", valid: true);

        Assert.Contains("premierecalendar.provider.requests", observed);
        Assert.Contains("premierecalendar.provider.request.duration", observed);
        Assert.Contains("premierecalendar.scheduler.job.outcomes", observed);
        Assert.Contains("premierecalendar.scheduler.job.wait.duration", observed);
        Assert.Contains("premierecalendar.provider.retries", observed);
        Assert.Contains("premierecalendar.database.events", observed);
        Assert.Contains("premierecalendar.version.validation.outcomes", observed);
    }

    [Fact]
    public async Task ProviderSpanNeverIncludesRawUrlQueryOrCredential()
    {
        Activity? captured = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == PremiereTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => captured = activity
        };
        ActivitySource.AddActivityListener(listener);
        var root = Path.Combine(Path.GetTempPath(), "premiere-telemetry-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var environment = new TestEnvironment(root);
            var databaseOptions = Microsoft.Extensions.Options.Options.Create(new AppDatabaseOptions { Path = "telemetry.db" });
            await new SqliteDatabaseInitializer(databaseOptions, environment, NullLogger<SqliteDatabaseInitializer>.Instance).InitializeAsync();
            var policy = new AdaptiveProviderPolicy(
                new ProviderAdaptiveStateStore(databaseOptions, environment),
                TimeProvider.System,
                new PremiereTelemetry());
            using var handler = new AdaptiveProviderHandler("tmdb", 2, policy) { InnerHandler = new OkHandler() };
            using var invoker = new HttpMessageInvoker(handler);
            using var response = await invoker.SendAsync(
                new HttpRequestMessage(HttpMethod.Get, "https://provider.test/title/secret-id?api_key=top-secret"),
                CancellationToken.None);

            Assert.NotNull(captured);
            var serialized = string.Join('|', captured!.TagObjects.Select(tag => $"{tag.Key}={tag.Value}"));
            Assert.Contains("provider=tmdb", serialized);
            Assert.Contains("operation=GET", serialized);
            Assert.DoesNotContain("provider.test", serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret-id", serialized, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("top-secret", serialized, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SharedProviderHandlerRetriesTransientGetAndHonorsRetryAfter()
    {
        var root = Path.Combine(Path.GetTempPath(), "premiere-provider-retry-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var environment = new TestEnvironment(root);
            var options = Microsoft.Extensions.Options.Options.Create(new AppDatabaseOptions { Path = "retry.db" });
            await new SqliteDatabaseInitializer(options, environment, NullLogger<SqliteDatabaseInitializer>.Instance).InitializeAsync();
            var policy = new AdaptiveProviderPolicy(
                new ProviderAdaptiveStateStore(options, environment),
                TimeProvider.System,
                new PremiereTelemetry(),
                Microsoft.Extensions.Options.Options.Create(new ProviderSchedulerOptions { RetryBaseMilliseconds = 50 }));
            var terminal = new RetryOnceHandler();
            using var handler = new AdaptiveProviderHandler("retry-provider", 2, policy) { InnerHandler = terminal };
            using var invoker = new HttpMessageInvoker(handler);

            using var response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "https://provider.test/items"), CancellationToken.None);

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(2, terminal.Calls);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    private sealed class RetryOnceHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (Calls == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMilliseconds(50));
                return Task.FromResult(response);
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "PremiereCalendar.UnitTests";
        public string WebRootPath { get; set; } = root;
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = root;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
