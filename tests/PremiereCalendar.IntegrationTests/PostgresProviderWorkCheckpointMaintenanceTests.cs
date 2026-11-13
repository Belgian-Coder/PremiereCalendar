using System.Data.Common;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Npgsql;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed class PostgresProviderWorkCheckpointMaintenanceTests : IDisposable
{
    private readonly PostgresTestDatabase _database = PostgresTestDatabase.Create();
    private readonly TestEnvironment _environment = new();

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task OfflineRewritePreservesAllColumnsAndRefusesLiveLeasesOrPartialMalformedConversion()
    {
        var store = new ProviderWorkStore(_database.Options, _environment);
        var handle = await store.EnqueueAsync(new(ProviderWorkKind.CalendarForeground, "checkpoint-maintenance", ProviderWorkPriority.Foreground, "{\"saved\":true}"), DateTimeOffset.UtcNow, default);
        var checkpoint = new ProviderWorkCheckpoint(new string('x', 6000), 2, 3, new("saved", 0, 0, []),
            new Dictionary<string, IReadOnlyList<PremiereCalendar.Models.PremiereItem>>());
        var encoded = ProviderWorkCheckpointCodec.Encode(checkpoint);
        using var decoded = ProviderWorkCheckpointCodec.Decode(encoded.Json);
        var legacy = decoded.RootElement.GetRawText();
        await ExecuteAsync("UPDATE ProviderWorkJobs SET State='Completed', AttemptCount=4, LastError='saved error', ProgressJson='{\"saved\":true}', CheckpointJson=@value WHERE JobId=@id", handle.JobId, encoded.Json);
        var before = await SnapshotAsync();
        var maintenance = new ProviderWorkCheckpointMaintenance(_database.Options, TimeProvider.System);
        Assert.Equal(1, (await maintenance.PrepareLegacyCheckpointsAsync()).ConvertedCheckpoints);
        before[0]["checkpointjson"] = legacy;
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await SnapshotAsync()));
        Assert.Equal(0, (await maintenance.PrepareLegacyCheckpointsAsync()).ConvertedCheckpoints);

        // The first sorted row is rewritten before the malformed final row is
        // encountered; the entire transaction must roll back.
        await ExecuteAsync("UPDATE ProviderWorkJobs SET CheckpointJson=@value WHERE JobId=@id", handle.JobId, encoded.Json);
        await ExecuteAsync("INSERT INTO ProviderWorkJobs (JobId,Kind,DedupeKey,Priority,PayloadJson,CheckpointJson,State,AttemptCount,EnqueuedUtc) VALUES (@id,'CalendarForeground',@id,0,'{}',@value,'Completed',0,'2026-01-01T00:00:00Z')", "zz-malformed", "{\"Version\":4,\"Encoding\":\"invalid\"}");
        var malformedBefore = await SnapshotAsync();
        await Assert.ThrowsAnyAsync<Exception>(() => maintenance.PrepareLegacyCheckpointsAsync());
        Assert.Equal(JsonSerializer.Serialize(malformedBefore), JsonSerializer.Serialize(await SnapshotAsync()));
        await ExecuteAsync("DELETE FROM ProviderWorkJobs WHERE JobId=@id", "zz-malformed", null);

        await store.EnqueueAsync(new(ProviderWorkKind.CalendarForeground, "active-maintenance-refusal", ProviderWorkPriority.Foreground, "{}"), DateTimeOffset.UtcNow, default);
        var active = (await store.ClaimNextAsync(false, "live-worker", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), default))!;
        var activeBefore = await SnapshotAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => maintenance.PrepareLegacyCheckpointsAsync());
        Assert.Equal(JsonSerializer.Serialize(activeBefore), JsonSerializer.Serialize(await SnapshotAsync()));
        Assert.Equal(active.LeaseOwner, (await store.GetAsync(active.JobId, default))!.LeaseOwner);
    }

    [Fact]
    public async Task TableLocksBlockNewClaimsForTheEntireOfflineRewrite()
    {
        var store = new ProviderWorkStore(_database.Options, _environment);
        var handle = await store.EnqueueAsync(new(ProviderWorkKind.CalendarForeground, "claim-lock", ProviderWorkPriority.Foreground, "{}"), DateTimeOffset.UtcNow, default);
        var encoded = ProviderWorkCheckpointCodec.Encode(new(new string('x', 6000), null, null, null,
            new Dictionary<string, IReadOnlyList<PremiereCalendar.Models.PremiereItem>>()));
        await ExecuteAsync("UPDATE ProviderWorkJobs SET CheckpointJson=@value WHERE JobId=@id", handle.JobId, encoded.Json);
        var clock = new BlockingTimeProvider();
        var maintenance = Task.Run(() => new ProviderWorkCheckpointMaintenance(_database.Options, clock).PrepareLegacyCheckpointsAsync());
        Task<ProviderWorkSnapshot?>? claim = null;
        try
        {
            await clock.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            claim = store.ClaimNextAsync(false, "concurrent-worker", DateTimeOffset.UtcNow, TimeSpan.FromSeconds(30), default);
            await using var observer = await _database.OpenAsync();
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
    }

    private async Task ExecuteAsync(string sql, string id, string? value)
    {
        await using var connection = await _database.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("id", id);
        if (value is not null) command.Parameters.AddWithValue("value", value);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<List<Dictionary<string, object?>>> SnapshotAsync()
    {
        await using var connection = await _database.OpenAsync();
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
