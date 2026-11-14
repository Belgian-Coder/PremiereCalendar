using System.Globalization;
using System.Data.Common;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace PremiereCalendar.Services;

public static class DatabaseSchema
{
    public static int CurrentVersion { get; } = ReadCurrentVersion();

    internal static IReadOnlyList<DatabaseMigration> Migrations { get; } =
    [
        new(1, "baseline-current-schema", BaselineSql),
        new(2, "durable-provider-scheduler", SchedulerSql),
        new(3, "scheduler-leases-and-centralized-stores", SchedulerLeaseSql),
        new(4, "normalized-item-enrichment-cache", ItemEnrichmentSql)
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
        command.CommandText = "SELECT COALESCE(MAX(Version), 0) FROM SchemaMigrations";
        var version = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
        if (version != CurrentVersion)
        {
            throw new InvalidOperationException(
                $"Database schema {version} is not ready; startup migration must complete schema {CurrentVersion} before stores are used.");
        }
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

    private const string ItemEnrichmentSql = """
        CREATE TABLE IF NOT EXISTS ItemEnrichments (
            PartitionKey TEXT NOT NULL,
            MediaType TEXT NOT NULL,
            TmdbId INTEGER NOT NULL,
            MetadataJson TEXT NULL,
            RatingsJson TEXT NULL,
            MetadataFetchedUtc TEXT NULL,
            RatingsFetchedUtc TEXT NULL,
            LastSeenUtc TEXT NOT NULL,
            Generation BIGINT NOT NULL DEFAULT 0,
            TvmazeShowId INTEGER NULL,
            MetadataInvalidatedUtc TEXT NULL,
            TvmazeFenceUtc TEXT NULL,
            TmdbObservedUtc TEXT NULL,
            MetadataRevision BIGINT NOT NULL DEFAULT 0,
            PRIMARY KEY (PartitionKey, MediaType, TmdbId)
        );
        CREATE INDEX IF NOT EXISTS IX_ItemEnrichments_Tmdb
            ON ItemEnrichments (MediaType, TmdbId);
        CREATE INDEX IF NOT EXISTS IX_ItemEnrichments_Tvmaze
            ON ItemEnrichments (TvmazeShowId);
        CREATE INDEX IF NOT EXISTS IX_ItemEnrichments_LastSeen
            ON ItemEnrichments (LastSeenUtc);
        """;
}

internal sealed record DatabaseMigration(
    int Version,
    string Name,
    string Sql)
{
    public string Checksum { get; } = ComputeChecksum(Version, Name, Sql.Replace("\r\n", "\n", StringComparison.Ordinal));

    public bool MatchesChecksum(string checksum)
    {
        if (string.Equals(checksum, Checksum, StringComparison.OrdinalIgnoreCase)) return true;
        // Historical Windows archives embedded CRLF in these four SQL raw
        // strings. Accept only that demonstrated representation of the same
        // authoritative SQL; never rewrite SQL or the existing history row.
        return Version is >= 1 and <= 4 && string.Equals(checksum,
            ComputeChecksum(Version, Name, Sql.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace("\n", "\r\n", StringComparison.Ordinal)), StringComparison.OrdinalIgnoreCase);
    }

    private static string ComputeChecksum(int version, string name, string sql) => Convert.ToHexStringLower(
        SHA256.HashData(Encoding.UTF8.GetBytes($"{version.ToString(CultureInfo.InvariantCulture)}\n{name}\n{sql}")));
}

public sealed class DatabaseRecoveryState
{
    /// <summary>What an operator does when the database is unhealthy; shown by /health/version and the Settings page.</summary>
    public const string OperatorGuidance =
        "Run 'dotnet PremiereCalendar.dll database verify'. Restore only a checksum-verified pg_dump into an isolated PostgreSQL instance before production recovery.";

    private readonly Lock _gate = new();
    private TaskCompletionSource _healthy = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private DatabaseStatusSnapshot _snapshot = new(0, DatabaseSchema.CurrentVersion, false, "Database has not been initialized.", null);

    public DatabaseStatusSnapshot Snapshot
    {
        get { lock (_gate) return _snapshot; }
    }

    internal void Set(DatabaseStatusSnapshot snapshot)
    {
        lock (_gate)
        {
            _snapshot = snapshot;
            if (snapshot.IsHealthy) _healthy.TrySetResult();
            else if (_healthy.Task.IsCompleted)
                _healthy = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    internal Task WaitUntilHealthyAsync(CancellationToken cancellationToken)
    {
        lock (_gate) return _healthy.Task.WaitAsync(cancellationToken);
    }
}

public sealed record DatabaseStatusSnapshot(
    int CurrentVersion,
    int TargetVersion,
    bool IsHealthy,
    string Message,
    string? LastMigration);
