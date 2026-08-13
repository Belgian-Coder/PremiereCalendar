using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

/// <summary>Owns SQLite integrity validation, schema migrations, backups, and connection defaults.</summary>
public sealed class SqliteDatabaseInitializer(
    IOptions<AppDatabaseOptions> options,
    IWebHostEnvironment environment,
    DatabaseRecoveryState recoveryState,
    TimeProvider timeProvider,
    ILogger<SqliteDatabaseInitializer> logger,
    PremiereTelemetry? telemetry = null)
{
    private readonly AppDatabaseOptions _options = options.Value;

    public SqliteDatabaseInitializer(
        IOptions<AppDatabaseOptions> options,
        IWebHostEnvironment environment,
        ILogger<SqliteDatabaseInitializer> logger)
        : this(options, environment, new DatabaseRecoveryState(), TimeProvider.System, logger, null)
    {
    }

    public string ResolvePath() => SqliteDatabasePath.Resolve(_options.Path, environment.ContentRootPath);

    public string ResolveBackupDirectory()
    {
        var configured = string.IsNullOrWhiteSpace(_options.MigrationBackupDirectory)
            ? "App_Data/backups/database-migrations"
            : _options.MigrationBackupDirectory.Trim();
        return Path.GetFullPath(Path.IsPathFullyQualified(configured)
            ? configured
            : Path.Combine(environment.ContentRootPath, configured));
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var path = ResolvePath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string? backupPath = null;
        try
        {
            await using var connection = CreateConnection(path, readOnly: false, pooling: false);
            await connection.OpenAsync(cancellationToken);
            await ConfigureConnectionAsync(connection, cancellationToken);
            await EnsureIntegrityAsync(connection, cancellationToken);
            telemetry?.RecordDatabaseEvent("integrity", "passed");
            await EnsureMigrationHistoryAsync(connection, cancellationToken);

            var currentVersion = await ReadUserVersionAsync(connection, cancellationToken);
            if (currentVersion > DatabaseSchema.CurrentVersion)
            {
                throw new InvalidOperationException(
                    $"Database schema {currentVersion} is newer than supported schema {DatabaseSchema.CurrentVersion}.");
            }
            await ValidateMigrationHistoryAsync(connection, currentVersion, cancellationToken);

            var pending = DatabaseSchema.Migrations.Where(migration => migration.Version > currentVersion).ToArray();
            if (pending.Length > 0 && File.Exists(path) && new FileInfo(path).Length > 0)
            {
                await CheckpointAsync(connection, cancellationToken);
                backupPath = await CreateVerifiedBackupAsync(connection, currentVersion, cancellationToken);
            }

            string? lastMigration = null;
            foreach (var migration in pending)
            {
                var started = timeProvider.GetTimestamp();
                try
                {
                    await ApplyMigrationAsync(connection, migration, cancellationToken);
                    telemetry?.RecordMigration(migration.Version, "completed", timeProvider.GetElapsedTime(started));
                }
                catch
                {
                    telemetry?.RecordMigration(migration.Version, "failed", timeProvider.GetElapsedTime(started));
                    throw;
                }
                lastMigration = migration.Name;
                currentVersion = migration.Version;
            }

            await EnsureIntegrityAsync(connection, cancellationToken);
            await CheckpointAsync(connection, cancellationToken);
            recoveryState.Set(new DatabaseStatusSnapshot(
                currentVersion,
                DatabaseSchema.CurrentVersion,
                true,
                "SQLite schema and integrity checks passed.",
                lastMigration,
                backupPath));
            DeleteExpiredBackups();
            logger.LogInformation(
                "SQLite initialized at {DatabasePath} with schema {DatabaseSchemaVersion} and WAL journaling.",
                path,
                currentVersion);
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            telemetry?.RecordDatabaseException(ex);
            if (!string.IsNullOrWhiteSpace(backupPath))
            {
                try
                {
                    RestoreSnapshot(path, backupPath);
                    telemetry?.RecordDatabaseEvent("migration_restore", "completed");
                    logger.LogError(ex, "Database migration failed; restored {DatabaseBackupPath}.", backupPath);
                }
                catch (Exception restoreError) when (restoreError is IOException or UnauthorizedAccessException)
                {
                    telemetry?.RecordDatabaseEvent("migration_restore", "failed");
                    logger.LogCritical(restoreError, "Database migration and snapshot restoration both failed.");
                }
            }
            else
            {
                telemetry?.RecordDatabaseEvent("integrity", "failed");
                logger.LogCritical(ex, "SQLite initialization or integrity validation failed.");
            }

            recoveryState.Set(new DatabaseStatusSnapshot(
                await TryReadVersionAsync(path),
                DatabaseSchema.CurrentVersion,
                false,
                SanitizeFailure(ex),
                null,
                backupPath));
        }
    }

    public async Task<DatabaseStatusSnapshot> VerifyAsync(string? pathOverride, CancellationToken cancellationToken)
    {
        var path = string.IsNullOrWhiteSpace(pathOverride) ? ResolvePath() : Path.GetFullPath(pathOverride);
        await using var connection = CreateConnection(path, readOnly: true);
        await connection.OpenAsync(cancellationToken);
        await EnsureIntegrityAsync(connection, cancellationToken);
        var version = await ReadUserVersionAsync(connection, cancellationToken);
        return new DatabaseStatusSnapshot(
            version,
            DatabaseSchema.CurrentVersion,
            version <= DatabaseSchema.CurrentVersion,
            version <= DatabaseSchema.CurrentVersion
                ? "SQLite backup passed integrity and schema checks."
                : $"Database schema {version} is newer than supported schema {DatabaseSchema.CurrentVersion}.",
            null,
            null);
    }

    public async Task<DatabaseStatusSnapshot> CreateVerifiedSnapshotAsync(
        string outputPath,
        CancellationToken cancellationToken)
    {
        var sourcePath = ResolvePath();
        var destinationPath = Path.GetFullPath(outputPath);
        if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("The database snapshot output must differ from the live database path.");
        }

        var destinationDirectory = Path.GetDirectoryName(destinationPath)
            ?? throw new ArgumentException("The database snapshot output must have a parent directory.");
        Directory.CreateDirectory(destinationDirectory);
        if (File.Exists(destinationPath))
        {
            throw new IOException($"The database snapshot already exists: {destinationPath}");
        }

        await using var source = CreateConnection(sourcePath, readOnly: true);
        await source.OpenAsync(cancellationToken);
        await EnsureIntegrityAsync(source, cancellationToken);

        await using (var target = CreateConnection(destinationPath, readOnly: false, pooling: false))
        {
            await target.OpenAsync(cancellationToken);
            source.BackupDatabase(target);
            await FinalizePortableDatabaseAsync(target, cancellationToken);
        }

        try
        {
            return await VerifyAsync(destinationPath, cancellationToken);
        }
        catch
        {
            File.Delete(destinationPath);
            throw;
        }
    }

    public async Task RestoreAsync(string backupPath, CancellationToken cancellationToken)
    {
        var source = Path.GetFullPath(backupPath);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("Database backup was not found.", source);
        }

        var verification = await VerifyAsync(source, cancellationToken);
        if (!verification.IsHealthy)
        {
            throw new InvalidOperationException(verification.Message);
        }

        var target = ResolvePath();
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var damagedPath = target + $".damaged-{timeProvider.GetUtcNow():yyyyMMddHHmmss}";
        var stagedPath = target + $".restore-{Guid.NewGuid():N}";
        File.Copy(source, stagedPath, overwrite: false);

        try
        {
            await VerifyAsync(stagedPath, cancellationToken);
            DeleteSidecarFiles(stagedPath);
            SqliteConnection.ClearAllPools();
            if (File.Exists(target)) File.Move(target, damagedPath, overwrite: false);
            PreserveSidecar(target + "-wal", damagedPath + "-wal");
            PreserveSidecar(target + "-shm", damagedPath + "-shm");
            File.Move(stagedPath, target, overwrite: false);
            await VerifyAsync(target, cancellationToken);
            telemetry?.RecordDatabaseEvent("offline_restore", "completed");
        }
        catch
        {
            telemetry?.RecordDatabaseEvent("offline_restore", "failed");
            if (File.Exists(target)) File.Delete(target);
            if (File.Exists(damagedPath)) File.Move(damagedPath, target);
            if (File.Exists(damagedPath + "-wal")) File.Move(damagedPath + "-wal", target + "-wal");
            if (File.Exists(damagedPath + "-shm")) File.Move(damagedPath + "-shm", target + "-shm");
            if (File.Exists(stagedPath)) File.Delete(stagedPath);
            throw;
        }
    }

    private static void PreserveSidecar(string source, string destination)
    {
        if (File.Exists(source)) File.Move(source, destination, overwrite: false);
    }

    private static SqliteConnection CreateConnection(string path, bool readOnly, bool? pooling = null)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = pooling ?? !readOnly
        };
        return SqliteConnectionFactory.Create(builder.ToString());
    }

    private static async Task ConfigureConnectionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA foreign_keys = ON;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureIntegrityAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check";
        var result = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"SQLite quick_check failed: {result ?? "no result"}.");
        }
    }

    private static async Task EnsureMigrationHistoryAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS SchemaMigrations (
                Version INTEGER NOT NULL PRIMARY KEY,
                Name TEXT NOT NULL,
                Checksum TEXT NOT NULL,
                AppliedUtc TEXT NOT NULL
            )
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ValidateMigrationHistoryAsync(
        SqliteConnection connection,
        int currentVersion,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Version, Name, Checksum FROM SchemaMigrations WHERE Version <= $currentVersion";
        command.Parameters.AddWithValue("$currentVersion", currentVersion);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var version = reader.GetInt32(0);
            var expected = DatabaseSchema.Migrations.SingleOrDefault(migration => migration.Version == version)
                ?? throw new InvalidOperationException($"Database migration {version} is not recognized by this application.");
            if (!string.Equals(reader.GetString(1), expected.Name, StringComparison.Ordinal)
                || !string.Equals(reader.GetString(2), expected.Checksum, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"Database migration {version} does not match its authoritative checksum.");
            }
        }
    }

    private async Task ApplyMigrationAsync(
        SqliteConnection connection,
        DatabaseMigration migration,
        CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = migration.Sql;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            if (migration.AfterSql is not null)
            {
                await migration.AfterSql(connection, transaction, cancellationToken);
            }

            await using (var history = connection.CreateCommand())
            {
                history.Transaction = transaction;
                history.CommandText = """
                    INSERT INTO SchemaMigrations (Version, Name, Checksum, AppliedUtc)
                    VALUES ($version, $name, $checksum, $appliedUtc)
                    ON CONFLICT(Version) DO UPDATE SET
                        Name = excluded.Name,
                        Checksum = excluded.Checksum,
                        AppliedUtc = excluded.AppliedUtc
                    """;
                history.Parameters.AddWithValue("$version", migration.Version);
                history.Parameters.AddWithValue("$name", migration.Name);
                history.Parameters.AddWithValue("$checksum", migration.Checksum);
                history.Parameters.AddWithValue("$appliedUtc", timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
                await history.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var version = connection.CreateCommand())
            {
                version.Transaction = transaction;
                version.CommandText = $"PRAGMA user_version = {migration.Version}";
                await version.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation(
                "Applied database migration {MigrationVersion} {MigrationName} in {ElapsedMilliseconds} ms.",
                migration.Version,
                migration.Name,
                timeProvider.GetElapsedTime(started).TotalMilliseconds);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<string> CreateVerifiedBackupAsync(
        SqliteConnection source,
        int currentVersion,
        CancellationToken cancellationToken)
    {
        var directory = ResolveBackupDirectory();
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"pre-schema-{currentVersion}-to-{DatabaseSchema.CurrentVersion}-{timeProvider.GetUtcNow():yyyyMMddHHmmss}.db");
        await using (var target = CreateConnection(path, readOnly: false, pooling: false))
        {
            await target.OpenAsync(cancellationToken);
            source.BackupDatabase(target);
            await FinalizePortableDatabaseAsync(target, cancellationToken);
        }

        await VerifyAsync(path, cancellationToken);
        return path;
    }

    private static async Task<int> ReadUserVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task CheckpointAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task FinalizePortableDatabaseAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await CheckpointAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = DELETE";
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private static void RestoreSnapshot(string target, string backup)
    {
        SqliteConnection.ClearAllPools();
        DeleteSidecarFiles(target);
        File.Copy(backup, target, overwrite: true);
    }

    private static void DeleteSidecarFiles(string databasePath)
    {
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            var path = databasePath + suffix;
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private void DeleteExpiredBackups()
    {
        var directory = ResolveBackupDirectory();
        if (!Directory.Exists(directory)) return;
        foreach (var file in Directory.EnumerateFiles(directory, "pre-schema-*.db")
                     .Select(path => new FileInfo(path))
                     .OrderByDescending(file => file.CreationTimeUtc)
                     .Skip(Math.Clamp(_options.MigrationBackupRetentionCount, 1, 100)))
        {
            try { file.Delete(); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task<int> TryReadVersionAsync(string path)
    {
        try
        {
            await using var connection = CreateConnection(path, readOnly: true);
            await connection.OpenAsync();
            return await ReadUserVersionAsync(connection, CancellationToken.None);
        }
        catch
        {
            return 0;
        }
    }

    private static string SanitizeFailure(Exception error)
    {
        return error switch
        {
            UnauthorizedAccessException => "SQLite database access was denied.",
            IOException => "SQLite database files could not be read or written.",
            SqliteException => "SQLite reported an integrity or schema error.",
            _ => error.Message
        };
    }
}

internal static class SqliteDatabasePath
{
    public static string Resolve(string? configuredPath, string contentRootPath)
    {
        var path = string.IsNullOrWhiteSpace(configuredPath)
            ? "App_Data/data/premiere-calendar.db"
            : configuredPath.Trim();
        return Path.GetFullPath(Path.IsPathFullyQualified(path) ? path : Path.Combine(contentRootPath, path));
    }
}

public sealed class SqliteDatabaseInitializerHostedService(SqliteDatabaseInitializer initializer) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => initializer.InitializeAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
