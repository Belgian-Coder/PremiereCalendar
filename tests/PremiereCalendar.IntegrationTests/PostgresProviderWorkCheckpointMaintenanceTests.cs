using System.Data.Common;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed class PostgresProviderWorkCheckpointMaintenanceTests
{
    [ConfiguredPostgresFact]
    public async Task OfflineRewritePreservesAllColumnsAndRefusesLiveLeasesOrPartialMalformedConversion()
    {
        await WithDatabaseAsync(async (options, environment, connectionString) =>
        {
            var store = new ProviderWorkStore(options, environment);
            var handle = await store.EnqueueAsync(new(ProviderWorkKind.CalendarForeground, "checkpoint-maintenance", ProviderWorkPriority.Foreground, "{\"saved\":true}"), DateTimeOffset.UtcNow, default);
            var checkpoint = new ProviderWorkCheckpoint(new string('x', 6000), 2, 3, new("saved", 0, 0, []),
                new Dictionary<string, IReadOnlyList<PremiereCalendar.Models.PremiereItem>>());
            var encoded = ProviderWorkCheckpointCodec.Encode(checkpoint);
            using var decoded = ProviderWorkCheckpointCodec.Decode(encoded.Json);
            var legacy = decoded.RootElement.GetRawText();
            await ExecuteAsync(connectionString, "UPDATE ProviderWorkJobs SET State='Completed', AttemptCount=4, LastError='saved error', ProgressJson='{\"saved\":true}', CheckpointJson=@value WHERE JobId=@id", handle.JobId, encoded.Json);
            var before = await SnapshotAsync(connectionString);
            var maintenance = new ProviderWorkCheckpointMaintenance(options, environment, TimeProvider.System);
            Assert.Equal(1, (await maintenance.PrepareLegacyCheckpointsAsync()).ConvertedCheckpoints);
            before[0]["checkpointjson"] = legacy;
            Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await SnapshotAsync(connectionString)));
            Assert.Equal(0, (await maintenance.PrepareLegacyCheckpointsAsync()).ConvertedCheckpoints);

            // The first sorted row is rewritten before the malformed final row is
            // encountered; the entire transaction must roll back.
            await ExecuteAsync(connectionString, "UPDATE ProviderWorkJobs SET CheckpointJson=@value WHERE JobId=@id", handle.JobId, encoded.Json);
            await ExecuteAsync(connectionString, "INSERT INTO ProviderWorkJobs (JobId,Kind,DedupeKey,Priority,PayloadJson,CheckpointJson,State,AttemptCount,EnqueuedUtc) VALUES (@id,'CalendarForeground',@id,0,'{}',@value,'Completed',0,'2026-01-01T00:00:00Z')", "zz-malformed", "{\"Version\":4,\"Encoding\":\"invalid\"}");
            var malformedBefore = await SnapshotAsync(connectionString);
            await Assert.ThrowsAnyAsync<Exception>(() => maintenance.PrepareLegacyCheckpointsAsync());
            Assert.Equal(JsonSerializer.Serialize(malformedBefore), JsonSerializer.Serialize(await SnapshotAsync(connectionString)));
            await ExecuteAsync(connectionString, "DELETE FROM ProviderWorkJobs WHERE JobId=@id", "zz-malformed", null);

            await store.EnqueueAsync(new(ProviderWorkKind.CalendarForeground, "active-maintenance-refusal", ProviderWorkPriority.Foreground, "{}"), DateTimeOffset.UtcNow, default);
            var active = (await store.ClaimNextAsync(false, "live-worker", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), default))!;
            var activeBefore = await SnapshotAsync(connectionString);
            await Assert.ThrowsAsync<InvalidOperationException>(() => maintenance.PrepareLegacyCheckpointsAsync());
            Assert.Equal(JsonSerializer.Serialize(activeBefore), JsonSerializer.Serialize(await SnapshotAsync(connectionString)));
            Assert.Equal(active.LeaseOwner, (await store.GetAsync(active.JobId, default))!.LeaseOwner);
        });
    }

    [ConfiguredPostgresFact]
    public async Task TableLocksBlockNewClaimsForTheEntireOfflineRewrite()
    {
        await WithDatabaseAsync(async (options, environment, connectionString) =>
        {
            var store = new ProviderWorkStore(options, environment);
            var handle = await store.EnqueueAsync(new(ProviderWorkKind.CalendarForeground, "claim-lock", ProviderWorkPriority.Foreground, "{}"), DateTimeOffset.UtcNow, default);
            var encoded = ProviderWorkCheckpointCodec.Encode(new(new string('x', 6000), null, null, null,
                new Dictionary<string, IReadOnlyList<PremiereCalendar.Models.PremiereItem>>()));
            await ExecuteAsync(connectionString, "UPDATE ProviderWorkJobs SET CheckpointJson=@value WHERE JobId=@id", handle.JobId, encoded.Json);
            var clock = new BlockingTimeProvider();
            var maintenance = Task.Run(() => new ProviderWorkCheckpointMaintenance(options, environment, clock).PrepareLegacyCheckpointsAsync());
            Task<ProviderWorkSnapshot?>? claim = null;
            try
            {
                await clock.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                claim = store.ClaimNextAsync(false, "concurrent-worker", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), default);
                await using var observer = new NpgsqlConnection(connectionString);
                await observer.OpenAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                while (true)
                {
                    await using var blocked = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND cardinality(pg_blocking_pids(pid)) > 0", observer);
                    if (Convert.ToInt64(await blocked.ExecuteScalarAsync(timeout.Token)) > 0) break;
                    await Task.Delay(10, timeout.Token);
                }
                Assert.False(claim.IsCompleted);
            }
            finally { clock.Release.TrySetResult(); }
            Assert.Equal(1, (await maintenance.WaitAsync(TimeSpan.FromSeconds(5))).ConvertedCheckpoints);
            var claimed = (await claim!.WaitAsync(TimeSpan.FromSeconds(5)))!;
            Assert.Equal(handle.JobId, claimed.JobId);
            Assert.Equal(3, JsonDocument.Parse(claimed.CheckpointJson!).RootElement.GetProperty("Version").GetInt32());
        });
    }

    private static async Task ExecuteAsync(string connectionString, string sql, string id, string? value)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        if (value is not null) command.Parameters.AddWithValue("value", value);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<List<Dictionary<string, object?>>> SnapshotAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT * FROM ProviderWorkJobs ORDER BY JobId", connection);
        await using DbDataReader reader = await command.ExecuteReaderAsync();
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>();
            for (var column = 0; column < reader.FieldCount; column++) row[reader.GetName(column)] = reader.IsDBNull(column) ? null : reader.GetValue(column);
            rows.Add(row);
        }
        return rows;
    }

    private static async Task WithDatabaseAsync(Func<Microsoft.Extensions.Options.IOptions<AppDatabaseOptions>, TestEnvironment, string, Task> action)
    {
        var builder = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("PREMIERECALENDAR_TEST_POSTGRES")) { Database = "postgres" };
        var passwordFile = Environment.GetEnvironmentVariable("PREMIERECALENDAR_TEST_POSTGRES_PASSWORD_FILE");
        if (!string.IsNullOrWhiteSpace(passwordFile)) builder.Password = File.ReadAllText(passwordFile).TrimEnd('\r', '\n');
        var database = "premiere_checkpoint_maintenance_" + Guid.NewGuid().ToString("N");
        await using var admin = new NpgsqlConnection(builder.ConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            builder.Database = database;
            var environment = new TestEnvironment();
            var options = Microsoft.Extensions.Options.Options.Create(new AppDatabaseOptions { Provider = "PostgreSql", ConnectionString = builder.ConnectionString });
            var recovery = new DatabaseRecoveryState();
            await new PostgresDatabaseInitializer(options, environment, recovery, TimeProvider.System, NullLogger<PostgresDatabaseInitializer>.Instance, new PremiereTelemetry()).InitializeAsync();
            Assert.True(recovery.Snapshot.IsHealthy);
            await action(options, environment, builder.ConnectionString);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class ConfiguredPostgresFactAttribute : FactAttribute
    {
        public ConfiguredPostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PREMIERECALENDAR_TEST_POSTGRES")))
                Skip = "Configure PREMIERECALENDAR_TEST_POSTGRES for isolated PostgreSQL checkpoint maintenance coverage.";
        }
    }

    private sealed class BlockingTimeProvider : TimeProvider
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override DateTimeOffset GetUtcNow()
        {
            Entered.TrySetResult();
            Release.Task.GetAwaiter().GetResult();
            return DateTimeOffset.UtcNow;
        }
    }

    private sealed class TestEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "MaintenanceTests";
        public string EnvironmentName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
