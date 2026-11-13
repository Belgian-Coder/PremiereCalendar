using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed partial class PostgresPersistenceTests
{
    private static readonly string[] HistoricalLfChecksums =
    [
        "09bf8d563bf3afdf18c79cbf1931e22268a3b16e215202e420d7c971e3edb6e5",
        "d296a5952fccd5272d6ae79a837299ff5cff79c82bb40d5128220f9c9ba166d2",
        "ce2f893674ad83eb5f89d1d9eee95642dee7238fe2368bf23d3735b926910a63",
        "0e19b8ff266839912986b0e1c4f65c5a70ae973227fb1fa0032ac117a8088458"
    ];
    private static readonly string[] HistoricalCrlfChecksums =
    [
        "31dadbc76ca4ddb7e49ce001f87402bd43250e8e1e6441909e8a520072f0d758",
        "2ef1c4c3ff4c98102165b51ea201e9fe3aa43e4f38ea67c839c022ef27354131",
        "c99b9a092adff746c5969927b58de50a710d536af8d616ba47a9104612529f4c",
        "f55d950663157a8a11ccabac4ea25e685bb1db6170c82f0ddeca3874136fa3fd"
    ];

    [Fact]
    public async Task PostgreSqlHistoricalChecksumLineagesStartAndVerifyWithoutRewritingRows()
    {
        await SeedLineageUserDataAsync();
        foreach (var lineage in new[] { "lf", "crlf", "mixed" })
        {
            await using (var connection = await _database.OpenAsync())
            {
                for (var version = 1; version <= 4; version++)
                {
                    var hashes = lineage == "crlf" || lineage == "mixed" && version % 2 == 0
                        ? HistoricalCrlfChecksums : HistoricalLfChecksums;
                    await using var update = new NpgsqlCommand("UPDATE SchemaMigrations SET Checksum=@checksum WHERE Version=@version", connection);
                    update.Parameters.AddWithValue("checksum", hashes[version - 1]);
                    update.Parameters.AddWithValue("version", version);
                    Assert.Equal(1, await update.ExecuteNonQueryAsync());
                }
            }
            var before = await ReadMigrationLineageSnapshotAsync();
            var recovery = new DatabaseRecoveryState();
            var initializer = new PostgresDatabaseInitializer(_database.Options, recovery, TimeProvider.System,
                NullLogger<PostgresDatabaseInitializer>.Instance, new PremiereTelemetry());
            await initializer.InitializeAsync();
            Assert.True(recovery.Snapshot.IsHealthy, recovery.Snapshot.Message);
            Assert.False(initializer.RetryPending);
            Assert.True((await initializer.VerifyAsync()).IsHealthy);
            Assert.Equal(before, await ReadMigrationLineageSnapshotAsync());
        }
    }

    [Fact]
    public async Task PostgreSqlChangedMigrationNameOrRealAlteredSqlDigestIsStillRejected()
    {
        await SeedLineageUserDataAsync();
        var migration = DatabaseSchema.Migrations[0];
        var alteredChecksum = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{migration.Version}\n{migration.Name}\n{migration.Sql}\nSELECT 1;"))).ToLowerInvariant();
        foreach (var changeName in new[] { true, false })
        {
            await using (var connection = await _database.OpenAsync())
            {
                await using var corrupt = new NpgsqlCommand("UPDATE SchemaMigrations SET Name=@name,Checksum=@checksum WHERE Version=1", connection);
                corrupt.Parameters.AddWithValue("name", changeName ? "different-migration-name" : migration.Name);
                corrupt.Parameters.AddWithValue("checksum", changeName ? HistoricalLfChecksums[0] : alteredChecksum);
                await corrupt.ExecuteNonQueryAsync();
            }
            var before = await ReadMigrationLineageSnapshotAsync();
            var recovery = new DatabaseRecoveryState();
            var initializer = new PostgresDatabaseInitializer(_database.Options, recovery, TimeProvider.System,
                NullLogger<PostgresDatabaseInitializer>.Instance, new PremiereTelemetry());
            await Assert.ThrowsAsync<InvalidOperationException>(() => initializer.InitializeAsync());
            Assert.False(recovery.Snapshot.IsHealthy);
            Assert.False(initializer.RetryPending);
            await Assert.ThrowsAsync<InvalidOperationException>(() => initializer.VerifyAsync());
            Assert.Equal(before, await ReadMigrationLineageSnapshotAsync());
        }
    }

    [Fact]
    public async Task PostgreSqlHistoricalChecksumAllowanceDoesNotBypassActualSchemaValidation()
    {
        await using var connection = await _database.OpenAsync();
        await using var corrupt = new NpgsqlCommand("UPDATE SchemaMigrations SET Checksum=@checksum WHERE Version=1; ALTER TABLE ProviderWorkJobs DROP COLUMN ProgressJson;", connection);
        corrupt.Parameters.AddWithValue("checksum", HistoricalCrlfChecksums[0]);
        await corrupt.ExecuteNonQueryAsync();
        var initializer = new PostgresDatabaseInitializer(_database.Options, new DatabaseRecoveryState(), TimeProvider.System,
            NullLogger<PostgresDatabaseInitializer>.Instance, new PremiereTelemetry());
        var failure = await Assert.ThrowsAsync<PostgresException>(() => initializer.InitializeAsync());
        Assert.Equal("42703", failure.SqlState);
        Assert.False(initializer.RetryPending);
        await Assert.ThrowsAsync<PostgresException>(() => initializer.VerifyAsync());
    }

    [Fact]
    public async Task PostgreSqlMissingHistoricalMigrationRemainsRejected()
    {
        await using var connection = await _database.OpenAsync();
        await using var corrupt = new NpgsqlCommand("DELETE FROM SchemaMigrations WHERE Version=2", connection);
        await corrupt.ExecuteNonQueryAsync();
        var before = await ReadMigrationLineageSnapshotAsync();
        var initializer = new PostgresDatabaseInitializer(_database.Options, new DatabaseRecoveryState(), TimeProvider.System,
            NullLogger<PostgresDatabaseInitializer>.Instance, new PremiereTelemetry());
        await Assert.ThrowsAsync<InvalidOperationException>(() => initializer.InitializeAsync());
        Assert.False(initializer.RetryPending);
        await Assert.ThrowsAsync<InvalidOperationException>(() => initializer.VerifyAsync());
        Assert.Equal(before, await ReadMigrationLineageSnapshotAsync());
    }

    [Fact]
    public async Task PostgreSqlUpgradesSchemaTwoToCurrentAndBackfillsDurableLeasesFromRunningJobs()
    {
        await using (var connection = await _database.OpenAsync())
        {
            await using var downgrade = connection.CreateCommand();
            downgrade.CommandText = """
                DROP TABLE ItemEnrichments;
                DROP TABLE ProviderWorkLeases;
                DELETE FROM SchemaMigrations WHERE Version >= 3;
                INSERT INTO ProviderWorkJobs (JobId, Kind, DedupeKey, Priority, PayloadJson, State, AttemptCount,
                    EnqueuedUtc, StartedUtc, LeaseOwner, LeaseExpiresUtc)
                VALUES ('running-job', 'CalendarForeground', 'dedupe-running', 100, '{}', 'Running', 1,
                    '2026-05-01T09:00:00Z', '2026-05-01T09:05:00Z', 'worker-a', '2026-05-01T09:35:00Z'),
                    ('queued-job', 'CalendarForeground', 'dedupe-queued', 100, '{}', 'Queued', 0,
                    '2026-05-01T09:00:00Z', NULL, NULL, NULL);
                """;
            await downgrade.ExecuteNonQueryAsync();
        }

        var recovery = new DatabaseRecoveryState();
        var initializer = new PostgresDatabaseInitializer(_database.Options, recovery, TimeProvider.System,
            NullLogger<PostgresDatabaseInitializer>.Instance, new PremiereTelemetry());
        await initializer.InitializeAsync();

        Assert.True(recovery.Snapshot.IsHealthy, recovery.Snapshot.Message);
        Assert.Equal(DatabaseSchema.CurrentVersion, recovery.Snapshot.CurrentVersion);
        Assert.Equal("normalized-item-enrichment-cache", recovery.Snapshot.LastMigration);
        Assert.True((await initializer.VerifyAsync()).IsHealthy);
        // Migration 3 is stored with SQLite's INSERT OR REPLACE and translated to an upsert for PostgreSQL;
        // only Running jobs with a lease are copied into the lease table.
        await using var verify = await _database.OpenAsync();
        await using var leases = new NpgsqlCommand(
            "SELECT JobId, LeaseOwner, LeaseExpiresUtc, UpdatedUtc FROM ProviderWorkLeases ORDER BY JobId", verify);
        await using var reader = await leases.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal("running-job", reader.GetString(0));
        Assert.Equal("worker-a", reader.GetString(1));
        Assert.Equal("2026-05-01T09:35:00Z", reader.GetString(2));
        Assert.Equal("2026-05-01T09:05:00Z", reader.GetString(3));
        Assert.False(await reader.ReadAsync());
    }

    private async Task SeedLineageUserDataAsync()
    {
        await using var connection = await _database.OpenAsync();
        await using var seed = new NpgsqlCommand("""
            INSERT INTO AppParameters(Key,Value,UpdatedUtc) VALUES ('lineage-preference','manual value','2026-05-01T12:00:00Z');
            INSERT INTO ItemEnrichments(PartitionKey,MediaType,TmdbId,MetadataJson,RatingsJson,
                MetadataFetchedUtc,RatingsFetchedUtc,LastSeenUtc,Generation,TvmazeShowId,MetadataRevision)
            VALUES ('lineage-partition','Series',42,'{"title":"retained"}','{"score":7.5}',
                '2026-05-01T10:00:00Z','2026-05-01T11:00:00Z','2026-05-01T12:00:00Z',4294967296,7,8589934592);
            """, connection);
        await seed.ExecuteNonQueryAsync();
    }

    private async Task<string> ReadMigrationLineageSnapshotAsync()
    {
        await using var connection = await _database.OpenAsync();
        var rows = new List<string>();
        foreach (var sql in new[]
        {
            "SELECT to_jsonb(t)::text FROM SchemaMigrations t ORDER BY Version",
            "SELECT to_jsonb(t)::text FROM AppParameters t ORDER BY Key",
            "SELECT to_jsonb(t)::text FROM ItemEnrichments t ORDER BY PartitionKey,MediaType,TmdbId"
        })
        {
            await using var query = new NpgsqlCommand(sql, connection);
            await using var reader = await query.ExecuteReaderAsync();
            while (await reader.ReadAsync()) rows.Add(reader.GetString(0));
        }
        return string.Join("\n", rows);
    }
}
