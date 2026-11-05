using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed partial class PostgresPersistenceTests
{
    [PostgresFact]
    public async Task PostgreSqlStartupRecoversAfterConnectionRefusalAndStopsRetryingAfterSuccess()
    {
        await WithIsolatedDatabaseAsync(async (options, environment, connectionString) =>
        {
            options.Value.ConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
            {
                Host = "127.0.0.1", Port = 1, Pooling = false
            }.ConnectionString;
            options.Value.RecoveryRetrySeconds = 1;
            options.Value.RecoveryMaximumRetrySeconds = 1;
            var recovery = new DatabaseRecoveryState();
            var postgres = new PostgresDatabaseInitializer(options, environment, recovery, TimeProvider.System,
                NullLogger<PostgresDatabaseInitializer>.Instance, new PremiereTelemetry());
            var sqlite = new SqliteDatabaseInitializer(options, environment, NullLogger<SqliteDatabaseInitializer>.Instance);
            using var hosted = new DatabaseInitializerHostedService(options, sqlite, postgres);
            await hosted.StartAsync(default);
            try
            {
                Assert.False(recovery.Snapshot.IsHealthy);
                Assert.True(postgres.RetryPending);
                options.Value.ConnectionString = connectionString;
                await recovery.WaitUntilHealthyAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.False(postgres.RetryPending);
                Assert.Equal(DatabaseSchema.CurrentVersion, recovery.Snapshot.CurrentVersion);
            }
            finally { await hosted.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(3)); }
        });
    }

    [PostgresFact]
    public async Task PostgreSqlUnsupportedSchemaFailsWithoutBeingClassifiedAsTransient()
    {
        await WithIsolatedDatabaseAsync(async (options, environment, connectionString) =>
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var newer = new NpgsqlCommand("INSERT INTO SchemaMigrations (Version,Name,Checksum,AppliedUtc) VALUES (@version,'unsupported','unsupported','2026-11-05T00:00:00Z')", connection);
            newer.Parameters.AddWithValue("version", DatabaseSchema.CurrentVersion + 1);
            await newer.ExecuteNonQueryAsync();
            var recovery = new DatabaseRecoveryState();
            var postgres = new PostgresDatabaseInitializer(options, environment, recovery, TimeProvider.System,
                NullLogger<PostgresDatabaseInitializer>.Instance, new PremiereTelemetry());
            await Assert.ThrowsAsync<InvalidOperationException>(() => postgres.InitializeAsync());
            Assert.False(postgres.RetryPending);
            Assert.False(recovery.Snapshot.IsHealthy);
        });
    }

    [PostgresFact]
    public async Task PostgreSqlCorruptMigrationHistoryFailsWithoutRetry()
    {
        await WithIsolatedDatabaseAsync(async (options, environment, connectionString) =>
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var corrupt = new NpgsqlCommand("UPDATE SchemaMigrations SET Checksum='invalid' WHERE Version=1", connection);
            await corrupt.ExecuteNonQueryAsync();
            var postgres = new PostgresDatabaseInitializer(options, environment, new DatabaseRecoveryState(), TimeProvider.System,
                NullLogger<PostgresDatabaseInitializer>.Instance, new PremiereTelemetry());
            await Assert.ThrowsAsync<InvalidOperationException>(() => postgres.InitializeAsync());
            Assert.False(postgres.RetryPending);
        });
    }

    [PostgresFact]
    public async Task PostgreSqlMissingRequiredColumnFailsWithoutRetry()
    {
        await WithIsolatedDatabaseAsync(async (options, environment, connectionString) =>
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var corrupt = new NpgsqlCommand("ALTER TABLE ProviderWorkJobs DROP COLUMN ProgressJson", connection);
            await corrupt.ExecuteNonQueryAsync();
            var postgres = new PostgresDatabaseInitializer(options, environment, new DatabaseRecoveryState(), TimeProvider.System,
                NullLogger<PostgresDatabaseInitializer>.Instance, new PremiereTelemetry());
            var error = await Assert.ThrowsAsync<PostgresException>(() => postgres.InitializeAsync());
            Assert.Equal("42703", error.SqlState);
            Assert.False(postgres.RetryPending);
        });
    }
}
