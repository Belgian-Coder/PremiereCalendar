using System.Globalization;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class PostgresDatabaseInitializer(
    IOptions<AppDatabaseOptions> options,
    IWebHostEnvironment environment,
    DatabaseRecoveryState recoveryState,
    TimeProvider timeProvider,
    ILogger<PostgresDatabaseInitializer> logger,
    PremiereTelemetry telemetry)
{
    private readonly AppDatabaseOptions _options = options.Value;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = DatabaseConnectionFactory.Create(_options, environment.ContentRootPath);
            await connection.OpenAsync(cancellationToken);
            await using (var history = connection.CreateCommand())
            {
                history.CommandText = """
                    CREATE TABLE IF NOT EXISTS SchemaMigrations (
                        Version INTEGER NOT NULL PRIMARY KEY,
                        Name TEXT NOT NULL,
                        Checksum TEXT NOT NULL,
                        AppliedUtc TEXT NOT NULL
                    )
                    """;
                await history.ExecuteNonQueryAsync(cancellationToken);
            }

            var currentVersion = await ReadVersionAsync(connection, cancellationToken);
            if (currentVersion > DatabaseSchema.CurrentVersion)
            {
                throw new InvalidOperationException($"Database schema {currentVersion} is newer than supported schema {DatabaseSchema.CurrentVersion}.");
            }

            string? lastMigration = null;
            foreach (var migration in DatabaseSchema.Migrations.Where(item => item.Version > currentVersion))
            {
                var started = timeProvider.GetTimestamp();
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                try
                {
                    await using (var command = connection.CreateCommand())
                    {
                        command.Transaction = transaction;
                        command.CommandText = ToPostgresSql(migration.Sql);
                        await command.ExecuteNonQueryAsync(cancellationToken);
                    }
                    await using (var history = connection.CreateCommand())
                    {
                        history.Transaction = transaction;
                        history.CommandText = """
                            INSERT INTO SchemaMigrations (Version, Name, Checksum, AppliedUtc)
                            VALUES (@version, @name, @checksum, @appliedUtc)
                            ON CONFLICT(Version) DO UPDATE SET
                                Name = excluded.Name,
                                Checksum = excluded.Checksum,
                                AppliedUtc = excluded.AppliedUtc
                            """;
                        DatabaseParameters.Add(history, "@version", migration.Version);
                        DatabaseParameters.Add(history, "@name", migration.Name);
                        DatabaseParameters.Add(history, "@checksum", migration.Checksum);
                        DatabaseParameters.Add(history, "@appliedUtc", timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
                        await history.ExecuteNonQueryAsync(cancellationToken);
                    }
                    await transaction.CommitAsync(cancellationToken);
                    telemetry.RecordMigration(migration.Version, "completed", timeProvider.GetElapsedTime(started));
                    currentVersion = migration.Version;
                    lastMigration = migration.Name;
                }
                catch
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    telemetry.RecordMigration(migration.Version, "failed", timeProvider.GetElapsedTime(started));
                    throw;
                }
            }

            await DatabaseSchema.AssertCurrentAsync(connection, cancellationToken);
            recoveryState.Set(new DatabaseStatusSnapshot(
                currentVersion,
                DatabaseSchema.CurrentVersion,
                true,
                "PostgreSQL schema and connectivity checks passed.",
                lastMigration,
                null));
            logger.LogInformation("PostgreSQL initialized with schema {DatabaseSchemaVersion}.", currentVersion);
        }
        catch (Exception exception)
        {
            telemetry.RecordDatabaseException(exception);
            logger.LogCritical(exception, "PostgreSQL initialization or schema validation failed.");
            recoveryState.Set(new DatabaseStatusSnapshot(0, DatabaseSchema.CurrentVersion, false,
                "PostgreSQL initialization or schema validation failed.", null, null));
        }
    }

    public async Task<DatabaseStatusSnapshot> VerifyAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = DatabaseConnectionFactory.Create(_options, environment.ContentRootPath);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(Version), 0) FROM SchemaMigrations";
        var version = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        await DatabaseSchema.AssertCurrentAsync(connection, cancellationToken);
        return new DatabaseStatusSnapshot(
            version,
            DatabaseSchema.CurrentVersion,
            version == DatabaseSchema.CurrentVersion,
            "PostgreSQL connectivity and schema checks passed.",
            null,
            null);
    }

    private static async Task<int> ReadVersionAsync(System.Data.Common.DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(Version), 0) FROM SchemaMigrations";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static string ToPostgresSql(string sql) => sql.Replace(
        "INSERT OR REPLACE INTO ProviderWorkLeases (JobId, LeaseOwner, LeaseExpiresUtc, UpdatedUtc)",
        "INSERT INTO ProviderWorkLeases (JobId, LeaseOwner, LeaseExpiresUtc, UpdatedUtc)",
        StringComparison.Ordinal).Replace(
        "WHERE State = 'Running' AND LeaseOwner IS NOT NULL AND LeaseExpiresUtc IS NOT NULL;",
        "WHERE State = 'Running' AND LeaseOwner IS NOT NULL AND LeaseExpiresUtc IS NOT NULL ON CONFLICT (JobId) DO UPDATE SET LeaseOwner = excluded.LeaseOwner, LeaseExpiresUtc = excluded.LeaseExpiresUtc, UpdatedUtc = excluded.UpdatedUtc;",
        StringComparison.Ordinal);
}

public sealed class DatabaseInitializerHostedService(
    IOptions<AppDatabaseOptions> options,
    SqliteDatabaseInitializer sqlite,
    PostgresDatabaseInitializer postgres) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) =>
        DatabaseConnectionFactory.IsPostgreSql(options.Value)
            ? postgres.InitializeAsync(cancellationToken)
            : sqlite.InitializeAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
