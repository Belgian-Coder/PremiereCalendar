using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class SqliteDatabaseInitializerTests
{
    [Fact]
    public async Task InitializeAsync_CreatesDirectoryAndEnablesWalWithBusyTimeout()
    {
        var root = Path.Combine(Path.GetTempPath(), "premiere-calendar-sqlite-tests", Guid.NewGuid().ToString("N"));
        var relative = Path.Combine("nested", "calendar.db");
        try
        {
            var initializer = new SqliteDatabaseInitializer(
                Microsoft.Extensions.Options.Options.Create(new AppDatabaseOptions { Path = relative }),
                new TestEnvironment(root),
                NullLogger<SqliteDatabaseInitializer>.Instance);

            await initializer.InitializeAsync();

            Assert.True(File.Exists(Path.Combine(root, relative)));
            await using (var connection = SqliteConnectionFactory.Create(new SqliteConnectionStringBuilder
            {
                DataSource = initializer.ResolvePath(),
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Shared
            }.ToString()))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA journal_mode;";
                Assert.Equal("wal", (await command.ExecuteScalarAsync())?.ToString(), ignoreCase: true);
                command.CommandText = "PRAGMA busy_timeout;";
                Assert.Equal(5000L, Convert.ToInt64(await command.ExecuteScalarAsync()));
                command.CommandText = "PRAGMA user_version;";
                Assert.Equal(DatabaseSchema.CurrentVersion, Convert.ToInt32(await command.ExecuteScalarAsync()));
                command.CommandText = "SELECT COUNT(*) FROM SchemaMigrations;";
                Assert.Equal(DatabaseSchema.Migrations.Count, Convert.ToInt32(await command.ExecuteScalarAsync()));
            }

            SqliteConnection.ClearAllPools();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InitializeAsync_MigratesLegacyViewSyncWithoutLosingState()
    {
        var root = Path.Combine(Path.GetTempPath(), "premiere-calendar-sqlite-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "legacy.db");
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE ViewSyncGroups (GroupId TEXT NOT NULL PRIMARY KEY, Name TEXT NOT NULL, CreatedUtc TEXT NOT NULL);
                    INSERT INTO ViewSyncGroups VALUES ('group-a', 'Test group', '2026-05-01T00:00:00Z');
                    CREATE TABLE ViewSyncGroupState (
                        GroupId TEXT NOT NULL PRIMARY KEY,
                        RelativeUrl TEXT NOT NULL,
                        Revision INTEGER NOT NULL,
                        UpdatedUtc TEXT NOT NULL,
                        UpdatedByDeviceId TEXT NOT NULL,
                        UpdatedByDeviceName TEXT NOT NULL);
                    INSERT INTO ViewSyncGroupState VALUES (
                        'group-a', '/series?week=2026-05-04', 7, '2026-05-01T00:00:00Z', 'device-a', 'Test device');
                    PRAGMA user_version = 0;
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var state = new DatabaseRecoveryState();
            var initializer = Create(root, "legacy.db", state);
            await initializer.InitializeAsync();

            Assert.True(state.Snapshot.IsHealthy);
            await using var migrated = new SqliteConnection($"Data Source={path}");
            await migrated.OpenAsync();
            await using var verify = migrated.CreateCommand();
            verify.CommandText = "SELECT RelativeUrl || ':' || Revision FROM ViewSyncGroupState WHERE GroupId = 'group-a';";
            Assert.Equal("/series?week=2026-05-04:7", await verify.ExecuteScalarAsync());
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task InitializeAsync_LeavesReadinessUnhealthyForUnrelatedCorruption()
    {
        var root = Path.Combine(Path.GetTempPath(), "premiere-calendar-sqlite-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "corrupt.db"), "not a sqlite database");
        try
        {
            var state = new DatabaseRecoveryState();
            await Create(root, "corrupt.db", state).InitializeAsync();
            Assert.False(state.Snapshot.IsHealthy);
            Assert.Contains("integrity", state.Snapshot.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static SqliteDatabaseInitializer Create(string root, string path, DatabaseRecoveryState state) =>
        new(
            Microsoft.Extensions.Options.Options.Create(new AppDatabaseOptions
            {
                Path = path,
                MigrationBackupDirectory = Path.Combine(root, "backups")
            }),
            new TestEnvironment(root),
            state,
            TimeProvider.System,
            NullLogger<SqliteDatabaseInitializer>.Instance);

    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "PremiereCalendar.Tests";
        public string WebRootPath { get; set; } = root;
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = root;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
