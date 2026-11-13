using System.Globalization;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class PostgresDatabaseInitializer(
    IOptions<AppDatabaseOptions> options,
    DatabaseRecoveryState recoveryState,
    TimeProvider timeProvider,
    ILogger<PostgresDatabaseInitializer> logger,
    PremiereTelemetry telemetry)
{
    private readonly AppDatabaseOptions _options = options.Value;
    internal bool RetryPending { get; private set; }
    internal void MarkRecoveryTimeout() => RetryPending = true;

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        RetryPending = false;
        try
        {
            await using var connection = DatabaseConnectionFactory.Create(_options);
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
            await ValidateMigrationHistoryAsync(connection, currentVersion, cancellationToken);

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
            await ValidateSchemaShapeAsync(connection, cancellationToken);
            recoveryState.Set(new DatabaseStatusSnapshot(
                currentVersion,
                DatabaseSchema.CurrentVersion,
                true,
                "PostgreSQL schema and connectivity checks passed.",
                lastMigration));
            logger.LogInformation("PostgreSQL initialized with schema {DatabaseSchemaVersion}.", currentVersion);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (DatabaseConnectionFactory.IsTransient(exception))
        {
            RetryPending = true;
            logger.LogWarning(exception, "PostgreSQL is temporarily unavailable; initialization will retry.");
            recoveryState.Set(new DatabaseStatusSnapshot(0, DatabaseSchema.CurrentVersion, false,
                "PostgreSQL is temporarily unavailable; initialization will retry.", null));
        }
        catch (Exception exception)
        {
            recoveryState.Set(new DatabaseStatusSnapshot(0, DatabaseSchema.CurrentVersion, false,
                "PostgreSQL initialization or schema validation failed; operator intervention is required.", null));
            logger.LogCritical(exception, "PostgreSQL initialization failed with a non-transient error.");
            throw;
        }
    }

    public async Task<DatabaseStatusSnapshot> VerifyAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = DatabaseConnectionFactory.Create(_options);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(Version), 0) FROM SchemaMigrations";
        var version = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        await DatabaseSchema.AssertCurrentAsync(connection, cancellationToken);
        await ValidateMigrationHistoryAsync(connection, version, cancellationToken);
        await ValidateSchemaShapeAsync(connection, cancellationToken);
        return new DatabaseStatusSnapshot(
            version,
            DatabaseSchema.CurrentVersion,
            version == DatabaseSchema.CurrentVersion,
            "PostgreSQL connectivity and schema checks passed.",
            null);
    }

    private static async Task<int> ReadVersionAsync(System.Data.Common.DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(Version), 0) FROM SchemaMigrations";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task ValidateMigrationHistoryAsync(System.Data.Common.DbConnection connection, int version, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Version, Name, Checksum FROM SchemaMigrations ORDER BY Version";
        var seen = new HashSet<int>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var appliedVersion = reader.GetInt32(0);
            var expected = DatabaseSchema.Migrations.SingleOrDefault(migration => migration.Version == appliedVersion);
            if (expected is null || !string.Equals(reader.GetString(1), expected.Name, StringComparison.Ordinal)
                || !expected.MatchesChecksum(reader.GetString(2)))
                throw new InvalidOperationException($"Database migration {appliedVersion} does not match its authoritative checksum.");
            seen.Add(appliedVersion);
        }
        if (DatabaseSchema.Migrations.Any(migration => migration.Version <= version && !seen.Contains(migration.Version)))
            throw new InvalidOperationException("Database migration history is incomplete.");
    }

    private static async Task ValidateSchemaShapeAsync(System.Data.Common.DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT JobId, Kind, DedupeKey, Priority, PayloadJson, CheckpointJson, State,
                AttemptCount, EnqueuedUtc, StartedUtc, CompletedUtc, NextAttemptUtc,
                LastError, LeaseOwner, LeaseExpiresUtc, ProgressJson FROM ProviderWorkJobs WHERE FALSE;
            SELECT JobId, LeaseOwner, LeaseExpiresUtc, UpdatedUtc FROM ProviderWorkLeases WHERE FALSE;
            SELECT PartitionKey, MediaType, TmdbId, MetadataJson, RatingsJson, MetadataFetchedUtc,
                RatingsFetchedUtc, LastSeenUtc, Generation, TvmazeShowId, MetadataInvalidatedUtc,
                TvmazeFenceUtc, TmdbObservedUtc, MetadataRevision FROM ItemEnrichments WHERE FALSE;
            SELECT Provider, Scope, CacheKey, LastCheckedUtc, LastChangedUtc, Watermark,
                ItemCount, MetadataJson FROM ProviderCacheState WHERE FALSE;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // Migration 3 is immutable (its checksum is recorded in existing databases), so its SQL keeps the original
    // INSERT OR REPLACE statement and is translated to the PostgreSQL upsert here.
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
    PostgresDatabaseInitializer postgres) : BackgroundService
{
    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await postgres.InitializeAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = Math.Clamp(options.Value.RecoveryRetrySeconds, 1, 300);
        while (postgres.RetryPending && !stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(delay), stoppingToken);
            using var attempt = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            attempt.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.Value.RecoveryAttemptTimeoutSeconds, 5, 300)));
            try { await postgres.InitializeAsync(attempt.Token); }
            catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested && attempt.IsCancellationRequested)
            {
                // A timed-out attempt remains retryable; shutdown is never swallowed.
                postgres.MarkRecoveryTimeout();
            }
            delay = Math.Min(delay * 2, Math.Clamp(options.Value.RecoveryMaximumRetrySeconds, 1, 300));
        }
    }
}
