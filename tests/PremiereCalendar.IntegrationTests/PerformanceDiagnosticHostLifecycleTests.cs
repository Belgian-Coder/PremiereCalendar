using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed class PerformanceDiagnosticHostLifecycleTests
{
    [Fact]
    public void FactoryDisposalDrainsDiagnosticsAndReleasesEveryDiagnosticFile()
    {
        var root = Path.Combine(Path.GetTempPath(), "premiere-diagnostic-host-tests", Guid.NewGuid().ToString("N"));
        var logs = Path.Combine(root, "logs");
        try
        {
            using (var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                       builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
                       {
                           ["ProviderScheduler:Enabled"] = "false", ["CalendarWarmup:Enabled"] = "false",
                           ["ProviderDeltaSync:Enabled"] = "false", ["ImdbDataset:Enabled"] = "false",
                           ["AppDatabase:Path"] = Path.Combine(root, "data", "calendar.db"),
                           ["AppDatabase:MigrationBackupDirectory"] = Path.Combine(root, "backups"),
                           ["CalendarCache:Directory"] = Path.Combine(root, "calendar-cache"),
                           ["ImageCache:Directory"] = Path.Combine(root, "image-cache"),
                           ["Telemetry:LogDirectory"] = logs
                       }))))
            {
                using var client = factory.CreateClient();
                var telemetry = factory.Services.GetRequiredService<PremiereTelemetry>();
                for (var index = 0; index < 500; index++) telemetry.RecordDiagnostic("host.shutdown.pending");
            }
            var files = Directory.GetFiles(Path.Combine(logs, "performance"), "performance-*.ndjson");
            Assert.NotEmpty(files);
            var lines = new List<string>();
            foreach (var path in files)
            {
                using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                using var reader = new StreamReader(exclusive);
                lines.AddRange(reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries));
            }
            Assert.Equal(500, lines.Count(line => line.Contains("\"event\":\"host.shutdown.pending\"", StringComparison.Ordinal)));
            Directory.Delete(Path.Combine(logs, "performance"), recursive: true);
            Assert.False(Directory.Exists(Path.Combine(logs, "performance")));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
