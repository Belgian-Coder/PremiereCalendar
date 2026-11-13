using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class ProviderWorkCheckpointMaintenanceTests : IDisposable
{
    private readonly PostgresTestDatabase _database = PostgresTestDatabase.Create();
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;

    [Fact]
    public async Task OfflineConversionPreservesExactFullJsonAndEveryOtherJobAndLeaseColumn()
    {
        var legacy = LegacyJson();
        var compressed = Compress(legacy);
        await SeedAsync("001-completed", compressed);
        await SeedAsync("002-queued", compressed, "Queued");
        await SeedLeaseAsync("001-completed", _now.AddMinutes(-1));
        var before = await SnapshotAsync("ProviderWorkJobs");
        var leasesBefore = await SnapshotAsync("ProviderWorkLeases");

        var result = await Maintenance().PrepareLegacyCheckpointsAsync();

        Assert.True(result.Success);
        Assert.Equal(2, result.ConvertedCheckpoints);
        Assert.Equal(Encoding.UTF8.GetByteCount(compressed) * 2L, result.PreviousBytes);
        Assert.Equal(Encoding.UTF8.GetByteCount(legacy) * 2L, result.LegacyBytes);
        before[0]["CheckpointJson"] = legacy;
        before[1]["CheckpointJson"] = legacy;
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await SnapshotAsync("ProviderWorkJobs")));
        Assert.Equal(JsonSerializer.Serialize(leasesBefore), JsonSerializer.Serialize(await SnapshotAsync("ProviderWorkLeases")));
        Assert.Equal(0, (await Maintenance().PrepareLegacyCheckpointsAsync()).ConvertedCheckpoints);
    }

    [Fact]
    public async Task UnexpiredLeaseRefusesConversionWithoutChangingAnyRows()
    {
        await SeedAsync("active", Compress(LegacyJson()), "Running");
        await SeedLeaseAsync("active", _now.AddMinutes(5));
        var before = await SnapshotAsync("ProviderWorkJobs");
        var leases = await SnapshotAsync("ProviderWorkLeases");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Maintenance().PrepareLegacyCheckpointsAsync());
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await SnapshotAsync("ProviderWorkJobs")));
        Assert.Equal(JsonSerializer.Serialize(leases), JsonSerializer.Serialize(await SnapshotAsync("ProviderWorkLeases")));
    }

    [Theory]
    [InlineData("{\"Version\":4,\"Encoding\":\"br-base64\",\"UncompressedBytes\":20,\"Payload\":\"bad\"}")]
    [InlineData("{\"Version\":4,\"Encoding\":\"unsupported\"}")]
    [InlineData("{\"Version\":4,")]
    public async Task MalformedCompressedCheckpointRollsBackEarlierConversions(string malformed)
    {
        await SeedAsync("001-good", Compress(LegacyJson()));
        await SeedAsync("999-bad", malformed);
        var before = await SnapshotAsync("ProviderWorkJobs");
        await Assert.ThrowsAnyAsync<Exception>(() => Maintenance().PrepareLegacyCheckpointsAsync());
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await SnapshotAsync("ProviderWorkJobs")));
    }

    [Fact]
    public async Task LegacyVersionsNullAndEmptyCheckpointRowsRemainByteForByteUnchanged()
    {
        await SeedAsync("0", null);
        await SeedAsync("1", "");
        await SeedAsync("2", "{ \"Message\": \"legacy\", \"CompletedSources\": {} }");
        await SeedAsync("3", LegacyJson().Replace("\"Version\":3", "\"Version\":2", StringComparison.Ordinal));
        await SeedAsync("4", LegacyJson());
        var before = await SnapshotAsync("ProviderWorkJobs");
        var result = await Maintenance().PrepareLegacyCheckpointsAsync();
        Assert.Equal(new(true, 0, 0, 0), result);
        Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(await SnapshotAsync("ProviderWorkJobs")));
    }

    private ProviderWorkCheckpointMaintenance Maintenance() => new(_database.Options, new FixedTimeProvider(_now));

    private async Task SeedAsync(string id, string? checkpoint, string state = "Completed")
    {
        await using var connection = await _database.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ProviderWorkJobs (JobId, Kind, DedupeKey, Priority, PayloadJson, CheckpointJson, State, AttemptCount,
                EnqueuedUtc, StartedUtc, CompletedUtc, NextAttemptUtc, LeaseOwner, LeaseExpiresUtc, ProgressJson, LastError)
            VALUES (@id, 'CalendarForeground', @id, 200, '{"preserve":"payload"}', @checkpoint, @state, 7,
                '2026-01-01T00:00:00Z', '2026-01-01T00:01:00Z', '2026-01-01T00:02:00Z', '2026-01-01T00:03:00Z',
                'saved-owner', '2026-01-01T00:04:00Z', '{"preserve":"progress"}', 'saved error')
            """;
        command.Parameters.AddWithValue("id", id);
        command.Parameters.Add(new NpgsqlParameter("checkpoint", NpgsqlDbType.Text) { Value = (object?)checkpoint ?? DBNull.Value });
        command.Parameters.AddWithValue("state", state);
        await command.ExecuteNonQueryAsync();
    }

    private async Task SeedLeaseAsync(string id, DateTimeOffset expiry)
    {
        await using var connection = await _database.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO ProviderWorkLeases VALUES (@id, 'saved-owner', @expiry, '2026-01-01T00:00:00Z')";
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("expiry", expiry.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync();
    }

    private async Task<List<Dictionary<string, object?>>> SnapshotAsync(string table)
    {
        await using var connection = await _database.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM " + table + " ORDER BY JobId";
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync())
        {
            // PostgreSQL folds unquoted column names to lower case; keep lookups by the schema's spelling working.
            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            for (var column = 0; column < reader.FieldCount; column++)
                row[reader.GetName(column)] = reader.IsDBNull(column) ? null : reader.GetValue(column);
            rows.Add(row);
        }
        return rows;
    }

    private static string LegacyJson()
    {
        var checkpoint = new ProviderWorkCheckpoint(new string('x', 6000), 3, 5, new("saved", 0, 0, []),
            new Dictionary<string, IReadOnlyList<PremiereCalendar.Models.PremiereItem>>());
        var json = JsonSerializer.Serialize(checkpoint);
        return json[..^1] + ",\"UnknownMetadata\":{\"Keep\":[\"exact\",42]}}";
    }

    private static string Compress(string legacy)
    {
        var bytes = Encoding.UTF8.GetBytes(legacy);
        using var stream = new MemoryStream();
        using (var compression = new BrotliStream(stream, CompressionLevel.Fastest, true)) compression.Write(bytes);
        return JsonSerializer.Serialize(new { Version = 4, Encoding = "br-base64", UncompressedBytes = bytes.Length, Payload = Convert.ToBase64String(stream.ToArray()) });
    }

    public void Dispose() => _database.Dispose();

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
