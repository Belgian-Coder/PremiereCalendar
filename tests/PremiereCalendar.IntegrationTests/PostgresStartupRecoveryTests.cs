using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed partial class PostgresPersistenceTests
{
    [Fact]
    public async Task PostgreSqlStartupRecoversAfterConnectionRefusalAndStopsRetryingAfterSuccess()
    {
        // Schema is created by the recovery attempt itself, once the connection works again.
        using var database = await PostgresTestDatabase.CreateAsync(initializeSchema: false);
        var options = database.Options;
        var reachableConnectionString = options.Value.ConnectionString;
        options.Value.ConnectionString = new NpgsqlConnectionStringBuilder(reachableConnectionString)
        {
            Host = "127.0.0.1", Port = 1, Pooling = false
        }.ConnectionString;
        options.Value.RecoveryRetrySeconds = 1;
        options.Value.RecoveryMaximumRetrySeconds = 1;
        var recovery = new DatabaseRecoveryState();
        var postgres = new PostgresDatabaseInitializer(options, recovery, TimeProvider.System,
            NullLogger<PostgresDatabaseInitializer>.Instance, new PremiereTelemetry());
        using var hosted = new DatabaseInitializerHostedService(options, postgres);
        await hosted.StartAsync(default);
        try
        {
            Assert.False(recovery.Snapshot.IsHealthy);
            Assert.True(postgres.RetryPending);
            options.Value.ConnectionString = reachableConnectionString;
            await recovery.WaitUntilHealthyAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(postgres.RetryPending);
            Assert.Equal(DatabaseSchema.CurrentVersion, recovery.Snapshot.CurrentVersion);
        }
        finally { await hosted.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    [Fact]
    public async Task PostgreSqlUnsupportedSchemaFailsWithoutBeingClassifiedAsTransient()
    {
        await using var connection = await _database.OpenAsync();
        await using var newer = new NpgsqlCommand("INSERT INTO SchemaMigrations (Version,Name,Checksum,AppliedUtc) VALUES (@version,'unsupported','unsupported','2026-11-05T00:00:00Z')", connection);
        newer.Parameters.AddWithValue("version", DatabaseSchema.CurrentVersion + 1);
        await newer.ExecuteNonQueryAsync();
        var recovery = new DatabaseRecoveryState();
        var postgres = new PostgresDatabaseInitializer(_database.Options, recovery, TimeProvider.System,
            NullLogger<PostgresDatabaseInitializer>.Instance, new PremiereTelemetry());
        await Assert.ThrowsAsync<InvalidOperationException>(() => postgres.InitializeAsync());
        Assert.False(postgres.RetryPending);
        Assert.False(recovery.Snapshot.IsHealthy);
    }

    [Fact]
    public async Task PostgreSqlCorruptMigrationHistoryFailsWithoutRetry()
    {
        await using var connection = await _database.OpenAsync();
        await using var corrupt = new NpgsqlCommand("UPDATE SchemaMigrations SET Checksum='invalid' WHERE Version=1", connection);
        await corrupt.ExecuteNonQueryAsync();
        var postgres = new PostgresDatabaseInitializer(_database.Options, new DatabaseRecoveryState(), TimeProvider.System,
            NullLogger<PostgresDatabaseInitializer>.Instance, new PremiereTelemetry());
        await Assert.ThrowsAsync<InvalidOperationException>(() => postgres.InitializeAsync());
        Assert.False(postgres.RetryPending);
    }

    [Fact]
    public async Task PostgreSqlMissingRequiredColumnFailsWithoutRetry()
    {
        await using var connection = await _database.OpenAsync();
        await using var corrupt = new NpgsqlCommand("ALTER TABLE ProviderWorkJobs DROP COLUMN ProgressJson", connection);
        await corrupt.ExecuteNonQueryAsync();
        var postgres = new PostgresDatabaseInitializer(_database.Options, new DatabaseRecoveryState(), TimeProvider.System,
            NullLogger<PostgresDatabaseInitializer>.Instance, new PremiereTelemetry());
        var error = await Assert.ThrowsAsync<PostgresException>(() => postgres.InitializeAsync());
        Assert.Equal("42703", error.SqlState);
        Assert.False(postgres.RetryPending);
    }
}
