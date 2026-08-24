using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed class PostgresPersistenceTests
{
    [Fact]
    public async Task PostgreSql_InitializesStoresAndImportsVerifiedSqliteSnapshot()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable("PREMIERECALENDAR_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(baseConnectionString)) return;

        var databaseName = $"premierecalendar_test_{Guid.NewGuid():N}";
        var root = Path.Combine(Path.GetTempPath(), "premierecalendar-postgres-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var environment = new TestEnvironment(root);
        var passwordFile = Environment.GetEnvironmentVariable("PREMIERECALENDAR_TEST_POSTGRES_PASSWORD_FILE");
        var password = string.IsNullOrWhiteSpace(passwordFile) ? null : File.ReadAllText(passwordFile).TrimEnd('\r', '\n');
        var adminBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = "postgres", Password = password };
        var appBuilder = new NpgsqlConnectionStringBuilder(baseConnectionString) { Database = databaseName, Password = password };

        await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE \"{databaseName}\"";
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            var postgresOptions = Microsoft.Extensions.Options.Options.Create(new AppDatabaseOptions
            {
                Provider = "PostgreSql",
                ConnectionString = appBuilder.ConnectionString
            });
            var recovery = new DatabaseRecoveryState();
            var initializer = new PostgresDatabaseInitializer(
                postgresOptions,
                environment,
                recovery,
                TimeProvider.System,
                NullLogger<PostgresDatabaseInitializer>.Instance,
                new PremiereTelemetry());

            await initializer.InitializeAsync();
            Assert.True(recovery.Snapshot.IsHealthy, recovery.Snapshot.Message);
            Assert.Equal(DatabaseSchema.CurrentVersion, recovery.Snapshot.CurrentVersion);

            var targetStore = new SqliteAppStateStore(postgresOptions, environment);
            await targetStore.SetValueAsync("integration.roundtrip", "ok", CancellationToken.None);
            Assert.Equal("ok", await targetStore.GetValueAsync("integration.roundtrip", CancellationToken.None));
            await targetStore.DeleteValueAsync("integration.roundtrip", CancellationToken.None);

            var sourceOptions = Microsoft.Extensions.Options.Options.Create(new AppDatabaseOptions { Path = "migration-source.db" });
            var sqlite = new SqliteDatabaseInitializer(sourceOptions, environment, NullLogger<SqliteDatabaseInitializer>.Instance);
            await sqlite.InitializeAsync();
            var sourceStore = new SqliteAppStateStore(sourceOptions, environment);
            await sourceStore.SetValueAsync("migration.marker", "copied", CancellationToken.None);
            var importedAt = DateTimeOffset.Parse("2026-08-24T10:00:00Z");
            var sourceRatings = new SqliteImdbRatingsStore(sourceOptions, environment);
            await sourceRatings.ReplaceAllAsync(
                [new ImdbRatingRecord("tt0000001", 7.25, 1234, importedAt)],
                importedAt,
                CancellationToken.None);

            var migrator = new SqliteToPostgresMigrator(postgresOptions, environment);
            var report = await migrator.MigrateAsync(sqlite.ResolvePath(), CancellationToken.None);
            Assert.Equal(1, report.TableRowCounts["AppParameters"]);
            Assert.Equal(1, report.TableRowCounts["ImdbRatings"]);
            Assert.Equal("copied", await targetStore.GetValueAsync("migration.marker", CancellationToken.None));
            var targetRatings = new SqliteImdbRatingsStore(postgresOptions, environment);
            var copiedRating = await targetRatings.GetByImdbIdAsync("tt0000001", CancellationToken.None);
            Assert.NotNull(copiedRating);
            Assert.Equal(7.25, copiedRating.AverageRating, precision: 2);
            Assert.Equal(64, report.SourceSha256.Length);

            var verification = await initializer.VerifyAsync();
            Assert.True(verification.IsHealthy, verification.Message);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
            await admin.OpenAsync();
            await using var drop = admin.CreateCommand();
            drop.CommandText = $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)";
            await drop.ExecuteNonQueryAsync();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PremiereCalendar.IntegrationTests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(root);
    }
}
