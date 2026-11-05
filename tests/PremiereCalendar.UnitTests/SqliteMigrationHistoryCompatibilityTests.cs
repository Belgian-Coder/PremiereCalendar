using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

[Collection(SqliteTestCollection.Name)]
public sealed class SqliteMigrationHistoryCompatibilityTests
{
    [Theory]
    [InlineData("lf")]
    [InlineData("crlf")]
    [InlineData("mixed")]
    public async Task HistoricalLineageStartsAndVerifiesWithoutRewritingHistoryOrUserData(string lineage)
    {
        await WithDatabaseAsync(async (initializer, recovery, path) =>
        {
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                foreach (var row in DatabaseMigrationChecksumTests.HistoricalChecksums)
                {
                    var version = (int)row[0];
                    var checksum = (string)row[lineage == "crlf" || lineage == "mixed" && version % 2 == 0 ? 2 : 1];
                    await using var update = connection.CreateCommand();
                    update.CommandText = "UPDATE SchemaMigrations SET Checksum=$checksum WHERE Version=$version";
                    update.Parameters.AddWithValue("$checksum", checksum);
                    update.Parameters.AddWithValue("$version", version);
                    Assert.Equal(1, await update.ExecuteNonQueryAsync());
                }
            }
            var before = await SnapshotAsync(path);
            await initializer.InitializeAsync();
            Assert.True(recovery.Snapshot.IsHealthy, recovery.Snapshot.Message);
            Assert.True((await initializer.VerifyAsync(null, default)).IsHealthy);
            Assert.Equal(before, await SnapshotAsync(path));
        });
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChangedNameOrRealAlteredSqlDigestStillRejectsStartupWithoutRewritingData(bool changeName)
    {
        await WithDatabaseAsync(async (initializer, recovery, path) =>
        {
            var migration = DatabaseSchema.Migrations[0];
            var changedSqlDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
                $"{migration.Version}\n{migration.Name}\n{migration.Sql}\nSELECT 1;"))).ToLowerInvariant();
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                await using var update = connection.CreateCommand();
                update.CommandText = changeName
                    ? "UPDATE SchemaMigrations SET Name='different-migration-name' WHERE Version=1"
                    : "UPDATE SchemaMigrations SET Checksum=$checksum WHERE Version=1";
                if (!changeName) update.Parameters.AddWithValue("$checksum", changedSqlDigest);
                await update.ExecuteNonQueryAsync();
            }
            var before = await SnapshotAsync(path);
            await initializer.InitializeAsync();
            Assert.False(recovery.Snapshot.IsHealthy);
            Assert.Equal(before, await SnapshotAsync(path));
        });
    }

    private static async Task WithDatabaseAsync(Func<SqliteDatabaseInitializer, DatabaseRecoveryState, string, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "premiere-migration-lineage", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var recovery = new DatabaseRecoveryState();
            var options = Microsoft.Extensions.Options.Options.Create(new AppDatabaseOptions { Path = "lineage.db" });
            var initializer = new SqliteDatabaseInitializer(options, new Environment(root), recovery, TimeProvider.System,
                NullLogger<SqliteDatabaseInitializer>.Instance);
            await initializer.InitializeAsync();
            Assert.True(recovery.Snapshot.IsHealthy);
            var path = Path.Combine(root, "lineage.db");
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                await using var seed = connection.CreateCommand();
                seed.CommandText = """
                    INSERT INTO AppParameters(Key,Value,UpdatedUtc) VALUES ('preserved-preference','manual value','2026-05-01T12:00:00Z');
                    INSERT INTO ItemEnrichments(PartitionKey,MediaType,TmdbId,MetadataJson,RatingsJson,
                        MetadataFetchedUtc,RatingsFetchedUtc,LastSeenUtc,Generation,TvmazeShowId,MetadataRevision)
                    VALUES ('preserved-partition','Series',42,'{"title":"retained"}','{"score":7.5}',
                        '2026-05-01T10:00:00Z','2026-05-01T11:00:00Z','2026-05-01T12:00:00Z',4294967296,7,8589934592);
                    """;
                await seed.ExecuteNonQueryAsync();
            }
            await action(initializer, recovery, path);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<string> SnapshotAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        var rows = new List<string>();
        foreach (var sql in new[]
        {
            "SELECT Version,Name,Checksum,AppliedUtc FROM SchemaMigrations ORDER BY Version",
            "SELECT Key,Value,UpdatedUtc FROM AppParameters ORDER BY Key",
            "SELECT * FROM ItemEnrichments ORDER BY PartitionKey,MediaType,TmdbId"
        })
        {
            await using var query = connection.CreateCommand();
            query.CommandText = sql;
            await using var reader = await query.ExecuteReaderAsync();
            while (await reader.ReadAsync())
                rows.Add(System.Text.Json.JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount)
                    .Select(index => reader.IsDBNull(index) ? null : reader.GetValue(index)).ToArray()));
        }
        return string.Join("\n", rows);
    }

    private sealed class Environment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PremiereCalendar.UnitTests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
