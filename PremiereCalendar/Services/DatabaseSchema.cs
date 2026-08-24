using System.Globalization;
using System.Data.Common;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace PremiereCalendar.Services;

public static class DatabaseSchema
{
    public static int CurrentVersion { get; } = ReadCurrentVersion();

    internal static IReadOnlyList<DatabaseMigration> Migrations { get; } =
    [
        new(1, "baseline-current-schema", BaselineSql, ApplyLegacyViewSyncMigrationAsync),
        new(2, "durable-provider-scheduler", SchedulerSql),
        new(3, "scheduler-leases-and-centralized-stores", SchedulerLeaseSql)
    ];

    private static int ReadCurrentVersion()
    {
        var value = typeof(DatabaseSchema).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .SingleOrDefault(attribute => string.Equals(attribute.Key, "DatabaseSchemaVersion", StringComparison.Ordinal))?
            .Value;
        return int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            ? version
            : Migrations.Count;
    }

    internal static async Task AssertCurrentAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = DatabaseConnectionFactory.IsPostgreSql(connection)
            ? "SELECT COALESCE(MAX(Version), 0) FROM SchemaMigrations"
            : "PRAGMA user_version";
        var version = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        if (version != CurrentVersion)
        {
            throw new InvalidOperationException(
                $"Database schema {version} is not ready; startup migration must complete schema {CurrentVersion} before stores are used.");
        }
    }

    private static async Task ApplyLegacyViewSyncMigrationAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var info = connection.CreateCommand();
        info.Transaction = transaction;
        info.CommandText = "PRAGMA table_info(ViewSyncGroupState)";
        var hasRows = false;
        var hasRouteKey = false;
        await using (var reader = await info.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                hasRows = true;
                if (string.Equals(reader.GetString(1), "RouteKey", StringComparison.OrdinalIgnoreCase))
                {
                    hasRouteKey = true;
                }
            }
        }

        if (!hasRows || hasRouteKey)
        {
            return;
        }

        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = LegacyViewSyncSql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private const string BaselineSql = """
        CREATE TABLE IF NOT EXISTS AppParameters (
            Key TEXT NOT NULL PRIMARY KEY,
            Value TEXT NOT NULL,
            UpdatedUtc TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS ImdbRatings (
            ImdbId TEXT NOT NULL PRIMARY KEY,
            AverageRating REAL NOT NULL,
            VoteCount INTEGER NOT NULL,
            ImportedAtUtc TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS ImdbDatasetState (
            Key TEXT NOT NULL PRIMARY KEY,
            Value TEXT NOT NULL,
            UpdatedUtc TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS OmdbCache (
            ImdbId TEXT NOT NULL PRIMARY KEY,
            Json TEXT NOT NULL,
            CachedAtUtc TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS OmdbProviderState (
            Key TEXT NOT NULL PRIMARY KEY,
            Value TEXT NOT NULL,
            UpdatedUtc TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS ProviderCacheState (
            Provider TEXT NOT NULL,
            Scope TEXT NOT NULL,
            CacheKey TEXT NOT NULL,
            LastCheckedUtc TEXT NOT NULL,
            LastChangedUtc TEXT NOT NULL,
            Watermark TEXT NOT NULL,
            ItemCount INTEGER NULL,
            MetadataJson TEXT NOT NULL,
            PRIMARY KEY (Provider, Scope, CacheKey)
        );
        CREATE TABLE IF NOT EXISTS SimklSyncState (
            Key TEXT NOT NULL PRIMARY KEY,
            Value TEXT NOT NULL,
            UpdatedUtc TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS CalendarFilterUsage (
            ProfileKey TEXT NOT NULL PRIMARY KEY,
            PageMode TEXT NOT NULL,
            CacheKey TEXT NOT NULL,
            FilterJson TEXT NOT NULL,
            UseCount INTEGER NOT NULL,
            LastUsedUtc TEXT NOT NULL,
            LastWarmedUtc TEXT NULL,
            LastItemCount INTEGER NULL,
            LastFailure TEXT NULL,
            IsDefault INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE IF NOT EXISTS ViewSyncGroups (
            GroupId TEXT NOT NULL PRIMARY KEY,
            Name TEXT NOT NULL,
            CreatedUtc TEXT NOT NULL
        );
        CREATE TABLE IF NOT EXISTS ViewSyncDevices (
            DeviceId TEXT NOT NULL PRIMARY KEY,
            DisplayName TEXT NOT NULL,
            SyncEnabled INTEGER NOT NULL,
            GroupId TEXT NULL,
            LastSeenUtc TEXT NOT NULL,
            FOREIGN KEY (GroupId) REFERENCES ViewSyncGroups(GroupId) ON DELETE SET NULL
        );
        CREATE TABLE IF NOT EXISTS ViewSyncGroupState (
            GroupId TEXT NOT NULL,
            RouteKey TEXT NOT NULL,
            RelativeUrl TEXT NOT NULL,
            Revision INTEGER NOT NULL,
            UpdatedUtc TEXT NOT NULL,
            UpdatedByDeviceId TEXT NOT NULL,
            UpdatedByDeviceName TEXT NOT NULL,
            PRIMARY KEY (GroupId, RouteKey),
            FOREIGN KEY (GroupId) REFERENCES ViewSyncGroups(GroupId) ON DELETE CASCADE
        );
        """;

    private const string SchedulerSql = """
        CREATE TABLE IF NOT EXISTS ProviderWorkJobs (
            JobId TEXT NOT NULL PRIMARY KEY,
            Kind TEXT NOT NULL,
            DedupeKey TEXT NOT NULL,
            Priority INTEGER NOT NULL,
            PayloadJson TEXT NOT NULL,
            CheckpointJson TEXT NULL,
            State TEXT NOT NULL,
            AttemptCount INTEGER NOT NULL DEFAULT 0,
            EnqueuedUtc TEXT NOT NULL,
            StartedUtc TEXT NULL,
            CompletedUtc TEXT NULL,
            NextAttemptUtc TEXT NULL,
            LeaseOwner TEXT NULL,
            LeaseExpiresUtc TEXT NULL,
            ProgressJson TEXT NULL,
            LastError TEXT NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS IX_ProviderWorkJobs_ActiveDedupe
            ON ProviderWorkJobs (DedupeKey)
            WHERE State IN ('Queued', 'Running', 'RetryWaiting');
        CREATE INDEX IF NOT EXISTS IX_ProviderWorkJobs_Dequeue
            ON ProviderWorkJobs (State, Priority, NextAttemptUtc, EnqueuedUtc);
        CREATE TABLE IF NOT EXISTS ProviderAdaptiveState (
            Provider TEXT NOT NULL PRIMARY KEY,
            CurrentConcurrency INTEGER NOT NULL,
            ConsecutiveSuccesses INTEGER NOT NULL DEFAULT 0,
            ConsecutiveFailures INTEGER NOT NULL DEFAULT 0,
            WindowFailureCount INTEGER NOT NULL DEFAULT 0,
            FailureWindowStartedUtc TEXT NULL,
            EwmaLatencyMilliseconds REAL NULL,
            CircuitState TEXT NOT NULL DEFAULT 'Closed',
            CooldownUntilUtc TEXT NULL,
            LastThrottledUtc TEXT NULL,
            UpdatedUtc TEXT NOT NULL
        );
        """;

    private const string SchedulerLeaseSql = """
        CREATE TABLE IF NOT EXISTS ProviderWorkLeases (
            JobId TEXT NOT NULL PRIMARY KEY,
            LeaseOwner TEXT NOT NULL,
            LeaseExpiresUtc TEXT NOT NULL,
            UpdatedUtc TEXT NOT NULL,
            FOREIGN KEY (JobId) REFERENCES ProviderWorkJobs(JobId) ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS IX_ProviderWorkLeases_Expiry
            ON ProviderWorkLeases (LeaseExpiresUtc);
        INSERT OR REPLACE INTO ProviderWorkLeases (JobId, LeaseOwner, LeaseExpiresUtc, UpdatedUtc)
        SELECT JobId, LeaseOwner, LeaseExpiresUtc, COALESCE(StartedUtc, EnqueuedUtc)
        FROM ProviderWorkJobs
        WHERE State = 'Running' AND LeaseOwner IS NOT NULL AND LeaseExpiresUtc IS NOT NULL;
        """;

    private const string LegacyViewSyncSql = """
        ALTER TABLE ViewSyncGroupState RENAME TO ViewSyncGroupState_Old;
        CREATE TABLE ViewSyncGroupState (
            GroupId TEXT NOT NULL,
            RouteKey TEXT NOT NULL,
            RelativeUrl TEXT NOT NULL,
            Revision INTEGER NOT NULL,
            UpdatedUtc TEXT NOT NULL,
            UpdatedByDeviceId TEXT NOT NULL,
            UpdatedByDeviceName TEXT NOT NULL,
            PRIMARY KEY (GroupId, RouteKey),
            FOREIGN KEY (GroupId) REFERENCES ViewSyncGroups(GroupId) ON DELETE CASCADE
        );
        INSERT INTO ViewSyncGroupState (
            GroupId, RouteKey, RelativeUrl, Revision, UpdatedUtc, UpdatedByDeviceId, UpdatedByDeviceName)
        SELECT GroupId,
            CASE WHEN RelativeUrl LIKE '/series%' THEN 'series'
                 WHEN RelativeUrl LIKE '/movies%' THEN 'movies'
                 ELSE 'all' END,
            RelativeUrl, Revision, UpdatedUtc, UpdatedByDeviceId, UpdatedByDeviceName
        FROM ViewSyncGroupState_Old;
        DROP TABLE ViewSyncGroupState_Old;
        """;
}

internal sealed record DatabaseMigration(
    int Version,
    string Name,
    string Sql,
    Func<SqliteConnection, SqliteTransaction, CancellationToken, Task>? AfterSql = null)
{
    public string Checksum { get; } = Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes($"{Version}\n{Name}\n{Sql}"))).ToLowerInvariant();
}

public sealed class DatabaseRecoveryState
{
    private readonly object _gate = new();
    private DatabaseStatusSnapshot _snapshot = new(0, DatabaseSchema.CurrentVersion, false, "Database has not been initialized.", null, null);

    public DatabaseStatusSnapshot Snapshot
    {
        get { lock (_gate) return _snapshot; }
    }

    internal void Set(DatabaseStatusSnapshot snapshot)
    {
        lock (_gate) _snapshot = snapshot;
    }
}

public sealed record DatabaseStatusSnapshot(
    int CurrentVersion,
    int TargetVersion,
    bool IsHealthy,
    string Message,
    string? LastMigration,
    string? LastBackupPath);
