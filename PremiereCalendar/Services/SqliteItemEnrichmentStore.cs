using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

// Like the other application stores, this name preserves the original SQLite
// convention while DatabaseConnectionFactory selects SQLite or PostgreSQL.
public sealed class SqliteItemEnrichmentStore : IItemEnrichmentStore
{
    private const int ReadBatchSize = 250;
    private const int WriteBatchSize = 90;
    private readonly AppDatabaseOptions _options;
    private readonly IWebHostEnvironment _environment;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _operationGate;
    private bool _initialized;

    public SqliteItemEnrichmentStore(
        IOptions<AppDatabaseOptions> options,
        IWebHostEnvironment environment,
        TimeProvider? timeProvider = null)
    {
        _options = options.Value;
        _environment = environment;
        _timeProvider = timeProvider ?? TimeProvider.System;
        var concurrency = DatabaseConnectionFactory.IsPostgreSql(_options) ? 4 : 1;
        _operationGate = new SemaphoreSlim(concurrency, concurrency);
    }

    public async Task<IReadOnlyDictionary<ItemEnrichmentIdentity, ItemEnrichmentRecord>> ReadManyAsync(
        string partition,
        IReadOnlyCollection<ItemEnrichmentIdentity> identities,
        bool reserveMissing,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        var keys = identities.Where(IsValid).Distinct().OrderBy(identity => identity.MediaType.ToString(), StringComparer.Ordinal)
            .ThenBy(identity => identity.TmdbId).ToArray();
        var records = new Dictionary<ItemEnrichmentIdentity, ItemEnrichmentRecord>();
        if (keys.Length == 0) return records;

        await WithConnectionAsync(async connection =>
        {
            var seenUtc = Format(_timeProvider.GetUtcNow());
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            // Retention touches also take row locks. Use the same global order
            // as provider writes before their UPDATE/UPSERT statements run.
            if (DatabaseConnectionFactory.IsPostgreSql(_options))
                await LockWriteRowsAsync(connection, transaction, partition, keys, cancellationToken);
            foreach (var batch in keys.Chunk(ReadBatchSize))
            {
                await using (var touch = connection.CreateCommand())
                {
                    touch.Transaction = transaction;
                    Add(touch, "@partition", DbType.String, partition);
                    Add(touch, "@seenUtc", DbType.String, seenUtc);
                    var predicate = AddIdentities(touch, batch);
                    touch.CommandText = reserveMissing
                        ? $"""
                            INSERT INTO ItemEnrichments (PartitionKey, MediaType, TmdbId, LastSeenUtc)
                            VALUES {string.Join(", ", batch.Select((_, index) => $"(@partition, @media{index}, @id{index}, @seenUtc)"))}
                            ON CONFLICT (PartitionKey, MediaType, TmdbId) DO UPDATE SET
                                LastSeenUtc = CASE WHEN ItemEnrichments.LastSeenUtc < excluded.LastSeenUtc
                                    THEN excluded.LastSeenUtc ELSE ItemEnrichments.LastSeenUtc END
                            """
                        : $"""
                            UPDATE ItemEnrichments
                            SET LastSeenUtc = CASE WHEN LastSeenUtc < @seenUtc THEN @seenUtc ELSE LastSeenUtc END
                            WHERE PartitionKey = @partition AND {predicate}
                            """;
                    await touch.ExecuteNonQueryAsync(cancellationToken);
                }

                await using var read = connection.CreateCommand();
                read.Transaction = transaction;
                Add(read, "@partition", DbType.String, partition);
                var readPredicate = AddIdentities(read, batch);
                read.CommandText = $"""
                    SELECT MediaType, TmdbId, MetadataJson, RatingsJson, MetadataFetchedUtc,
                        RatingsFetchedUtc, LastSeenUtc, Generation, TvmazeShowId, MetadataInvalidatedUtc,
                        TmdbObservedUtc, MetadataRevision
                    FROM ItemEnrichments
                    WHERE PartitionKey = @partition AND {readPredicate}
                    """;
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var identity = new ItemEnrichmentIdentity(Enum.Parse<PremiereMediaType>(reader.GetString(0)), reader.GetInt32(1));
                    records.Add(identity, new ItemEnrichmentRecord(
                        identity, ReadString(reader, 2), ReadString(reader, 3), ReadDate(reader, 4),
                        ReadDate(reader, 5), ReadDate(reader, 6)!.Value, reader.GetInt64(7),
                        reader.IsDBNull(8) ? null : reader.GetInt32(8), ReadDate(reader, 9), ReadDate(reader, 10), reader.GetInt64(11)));
                }
            }
            await transaction.CommitAsync(cancellationToken);
            return 0;
        }, cancellationToken);
        return records;
    }

    public async Task<int> WriteManyAsync(
        string partition,
        IReadOnlyList<ItemEnrichmentWrite> writes,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(partition);
        var validWrites = writes.Where(write => IsValid(write.Identity) &&
            (HasComponent(write.MetadataJson, write.MetadataFetchedUtc) || HasComponent(write.RatingsJson, write.RatingsFetchedUtc)))
            .OrderBy(write => write.Identity.MediaType.ToString(), StringComparer.Ordinal).ThenBy(write => write.Identity.TmdbId).ToArray();
        if (validWrites.Length == 0) return 0;

        return await WithConnectionAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            if (DatabaseConnectionFactory.IsPostgreSql(_options))
                await LockWriteRowsAsync(connection, transaction, partition,
                    validWrites.Select(write => write.Identity).Distinct().ToArray(), cancellationToken);
            var updated = 0;
            var batch = new List<ItemEnrichmentWrite>(WriteBatchSize);
            var identities = new HashSet<ItemEnrichmentIdentity>();
            foreach (var write in validWrites)
            {
                // UPDATE FROM must see at most one source row per identity. Flush
                // duplicate identities in order instead of choosing an arbitrary row.
                if (batch.Count == WriteBatchSize || !identities.Add(write.Identity))
                {
                    updated += await WriteBatchAsync(connection, transaction, partition, batch, cancellationToken);
                    batch.Clear();
                    identities.Clear();
                    identities.Add(write.Identity);
                }
                batch.Add(write);
            }
            if (batch.Count > 0)
                updated += await WriteBatchAsync(connection, transaction, partition, batch, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return updated;
        }, cancellationToken);
    }

    public async Task<IReadOnlySet<int>> FindKnownTmdbIdsAsync(
        PremiereMediaType mediaType,
        IReadOnlyCollection<int> candidateIds,
        CancellationToken cancellationToken)
    {
        var ids = candidateIds.Where(id => id > 0).Distinct().ToArray();
        var known = new HashSet<int>();
        if (ids.Length == 0) return known;
        await WithConnectionAsync(async connection =>
        {
            foreach (var batch in ids.Chunk(ReadBatchSize))
            {
                await using var command = connection.CreateCommand();
                Add(command, "@mediaType", DbType.String, mediaType.ToString());
                for (var index = 0; index < batch.Length; index++)
                    Add(command, $"@id{index}", DbType.Int32, batch[index]);
                command.CommandText = $"""
                    SELECT DISTINCT TmdbId FROM ItemEnrichments
                    WHERE MediaType = @mediaType
                        AND TmdbId IN ({string.Join(", ", batch.Select((_, index) => $"@id{index}"))})
                    """;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) known.Add(reader.GetInt32(0));
            }
            return 0;
        }, cancellationToken);
        return known;
    }

    public Task<int> InvalidateTmdbAsync(
        PremiereMediaType mediaType,
        IReadOnlyCollection<int> tmdbIds,
        DateTimeOffset changedAtUtc,
        CancellationToken cancellationToken) =>
        InvalidateAsync("TmdbId", tmdbIds, mediaType, changedAtUtc, cancellationToken);

    public Task<int> InvalidateTvmazeAsync(
        IReadOnlyCollection<int> showIds,
        DateTimeOffset changedAtUtc,
        CancellationToken cancellationToken) =>
        InvalidateAsync("TvmazeShowId", showIds, PremiereMediaType.Series, changedAtUtc, cancellationToken);

    public Task<int> InvalidateTmdbManyAsync(
        PremiereMediaType mediaType,
        IReadOnlyDictionary<int, DateTimeOffset> changes,
        CancellationToken cancellationToken) =>
        InvalidateManyAsync("TmdbId", changes, mediaType, cancellationToken);

    public Task<int> InvalidateTvmazeManyAsync(
        IReadOnlyDictionary<int, DateTimeOffset> changes,
        CancellationToken cancellationToken) =>
        InvalidateManyAsync("TvmazeShowId", changes, PremiereMediaType.Series, cancellationToken);

    public Task<int> CleanupAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM ItemEnrichments WHERE LastSeenUtc < @cutoffUtc";
            Add(command, "@cutoffUtc", DbType.String, Format(cutoffUtc));
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    public Task<int> FenceUnmappedTvmazeReservationsAsync(DateTimeOffset epochUtc, CancellationToken cancellationToken) =>
        WithConnectionAsync(async connection =>
        {
            await using var command = connection.CreateCommand();
            command.CommandText = """
                UPDATE ItemEnrichments
                SET Generation = Generation + 1, TvmazeFenceUtc = @epochUtc,
                    MetadataInvalidatedUtc = CASE
                        WHEN MetadataJson IS NULL
                            AND (MetadataInvalidatedUtc IS NULL OR MetadataInvalidatedUtc < @epochUtc)
                        THEN @epochUtc ELSE MetadataInvalidatedUtc END
                WHERE MediaType = @mediaType AND TvmazeShowId IS NULL
                    AND (TvmazeFenceUtc IS NULL OR TvmazeFenceUtc < @epochUtc)
                """;
            Add(command, "@mediaType", DbType.String, PremiereMediaType.Series.ToString());
            Add(command, "@epochUtc", DbType.String, Format(epochUtc));
            return await command.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    private Task<int> InvalidateAsync(
        string idColumn,
        IReadOnlyCollection<int> ids,
        PremiereMediaType mediaType,
        DateTimeOffset changedAtUtc,
        CancellationToken cancellationToken)
        => InvalidateManyAsync(idColumn, ids.Where(id => id > 0).Distinct().ToDictionary(id => id, _ => changedAtUtc), mediaType, cancellationToken);

    private Task<int> InvalidateManyAsync(
        string idColumn,
        IReadOnlyDictionary<int, DateTimeOffset> changes,
        PremiereMediaType mediaType,
        CancellationToken cancellationToken)
    {
        var validChanges = changes.Where(change => change.Key > 0).OrderBy(change => change.Key).ToArray();
        if (validChanges.Length == 0) return Task.FromResult(0);
        return WithConnectionAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var updated = 0;
            foreach (var batch in validChanges.Chunk(ReadBatchSize))
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                Add(command, "@mediaType", DbType.String, mediaType.ToString());
                for (var index = 0; index < batch.Length; index++)
                {
                    Add(command, $"@id{index}", DbType.Int32, batch[index].Key);
                    Add(command, $"@changedAtUtc{index}", DbType.String, Format(batch[index].Value));
                }
                command.CommandText = $"""
                    WITH ChangedItems (ProviderId, ChangedAtUtc) AS (
                        VALUES {string.Join(", ", batch.Select((_, index) => $"(@id{index}, @changedAtUtc{index})"))}
                    )
                    UPDATE ItemEnrichments AS target
                    SET Generation = target.Generation + 1, MetadataInvalidatedUtc = incoming.ChangedAtUtc
                    FROM ChangedItems AS incoming
                    WHERE target.MediaType = @mediaType AND target.{idColumn} = incoming.ProviderId
                        AND (target.MetadataInvalidatedUtc IS NULL OR target.MetadataInvalidatedUtc < incoming.ChangedAtUtc)
                    """;
                updated += await command.ExecuteNonQueryAsync(cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
            return updated;
        }, cancellationToken);
    }

    private static async Task<int> WriteBatchAsync(
        DbConnection connection,
        DbTransaction transaction,
        string partition,
        IReadOnlyList<ItemEnrichmentWrite> batch,
        CancellationToken cancellationToken)
    {
        var current = await ReadWriteRowsAsync(connection, transaction, partition, batch.Select(write => write.Identity).ToArray(), cancellationToken);
        var prepared = batch.Select(write =>
        {
            if (!current.TryGetValue(write.Identity, out var record)) return (Write: write, RatingsRevision: write.ExpectedMetadataRevision);
            var merged = ItemEnrichmentRatingsMerger.Merge(record, write, out var authorized);
            return (Write: merged, RatingsRevision: authorized ? record.MetadataRevision : write.ExpectedMetadataRevision);
        }).ToArray();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        Add(command, "@partition", DbType.String, partition);
        for (var index = 0; index < batch.Count; index++)
        {
            var write = prepared[index].Write;
            var metadata = HasComponent(write.MetadataJson, write.MetadataFetchedUtc);
            var ratings = HasComponent(write.RatingsJson, write.RatingsFetchedUtc);
            Add(command, $"@media{index}", DbType.String, write.Identity.MediaType.ToString());
            Add(command, $"@id{index}", DbType.Int32, write.Identity.TmdbId);
            Add(command, $"@generation{index}", DbType.Int64, write.ExpectedGeneration);
            Add(command, $"@metadata{index}", DbType.String, metadata ? write.MetadataJson : null);
            Add(command, $"@metadataUtc{index}", DbType.String, metadata ? Format(write.MetadataFetchedUtc!.Value) : null);
            Add(command, $"@ratings{index}", DbType.String, ratings ? write.RatingsJson : null);
            Add(command, $"@ratingsUtc{index}", DbType.String, ratings ? Format(write.RatingsFetchedUtc!.Value) : null);
            Add(command, $"@tvmaze{index}", DbType.Int32, write.TvmazeShowId);
            Add(command, $"@tmdbObservedUtc{index}", DbType.String, metadata && write.TmdbObservedUtc.HasValue ? Format(write.TmdbObservedUtc.Value) : null);
            Add(command, $"@metadataRevision{index}", DbType.Int64, write.ExpectedMetadataRevision);
            Add(command, $"@ratingsRevision{index}", DbType.Int64, prepared[index].RatingsRevision);
        }
        const string metadataAccepted = """
            incoming.MetadataJson IS NOT NULL AND incoming.MetadataFetchedUtc IS NOT NULL
            AND (incoming.ExpectedMetadataRevision IS NULL OR target.MetadataRevision = incoming.ExpectedMetadataRevision)
            AND (target.MetadataFetchedUtc IS NULL OR target.MetadataFetchedUtc < incoming.MetadataFetchedUtc
                OR (target.MetadataFetchedUtc = incoming.MetadataFetchedUtc AND incoming.ExpectedMetadataRevision IS NOT NULL))
            AND (target.TmdbObservedUtc IS NULL OR
                (incoming.TmdbObservedUtc IS NOT NULL AND target.TmdbObservedUtc <= incoming.TmdbObservedUtc))
            AND (target.MetadataInvalidatedUtc IS NULL OR target.MetadataInvalidatedUtc <= incoming.MetadataFetchedUtc)
            """;
        const string ratingsAccepted = """
            incoming.RatingsJson IS NOT NULL AND incoming.RatingsFetchedUtc IS NOT NULL
            AND (incoming.ExpectedRatingsMetadataRevision IS NULL OR target.MetadataRevision = incoming.ExpectedRatingsMetadataRevision)
            AND (target.RatingsFetchedUtc IS NULL OR target.RatingsFetchedUtc <= incoming.RatingsFetchedUtc)
            """;
        command.CommandText = $"""
            WITH IncomingWrites (MediaType, TmdbId, ExpectedGeneration, MetadataJson,
                MetadataFetchedUtc, RatingsJson, RatingsFetchedUtc, TvmazeShowId, TmdbObservedUtc, ExpectedMetadataRevision, ExpectedRatingsMetadataRevision) AS (
                VALUES {string.Join(", ", batch.Select((_, index) => $"(@media{index}, @id{index}, @generation{index}, @metadata{index}, @metadataUtc{index}, @ratings{index}, @ratingsUtc{index}, @tvmaze{index}, @tmdbObservedUtc{index}, @metadataRevision{index}, @ratingsRevision{index})"))}
            )
            UPDATE ItemEnrichments AS target SET
                MetadataJson = CASE WHEN {metadataAccepted} THEN incoming.MetadataJson ELSE target.MetadataJson END,
                MetadataFetchedUtc = CASE WHEN {metadataAccepted} THEN incoming.MetadataFetchedUtc ELSE target.MetadataFetchedUtc END,
                TmdbObservedUtc = CASE WHEN {metadataAccepted} THEN incoming.TmdbObservedUtc ELSE target.TmdbObservedUtc END,
                MetadataRevision = CASE WHEN {metadataAccepted} THEN target.MetadataRevision + 1 ELSE target.MetadataRevision END,
                RatingsJson = CASE WHEN {ratingsAccepted} THEN incoming.RatingsJson ELSE target.RatingsJson END,
                RatingsFetchedUtc = CASE WHEN {ratingsAccepted} THEN incoming.RatingsFetchedUtc ELSE target.RatingsFetchedUtc END,
                TvmazeShowId = CASE WHEN {metadataAccepted} AND incoming.TvmazeShowId IS NOT NULL
                    THEN incoming.TvmazeShowId ELSE target.TvmazeShowId END
            FROM IncomingWrites AS incoming
            WHERE target.PartitionKey = @partition
                AND target.MediaType = incoming.MediaType AND target.TmdbId = incoming.TmdbId
                AND target.Generation = incoming.ExpectedGeneration
                AND (({metadataAccepted}) OR ({ratingsAccepted}))
            """;
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task LockWriteRowsAsync(DbConnection connection, DbTransaction transaction, string partition,
        IReadOnlyList<ItemEnrichmentIdentity> identities, CancellationToken cancellationToken)
    {
        // Every transaction acquires all of its keys in the same global order,
        // including across chunks and repeated writes for the same identity.
        foreach (var batch in identities.Chunk(ReadBatchSize))
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            Add(command, "@partition", DbType.String, partition);
            var predicate = AddIdentities(command, batch);
            command.CommandText = $"SELECT MediaType, TmdbId FROM ItemEnrichments WHERE PartitionKey = @partition AND {predicate} ORDER BY MediaType, TmdbId FOR UPDATE";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) { }
        }
    }

    private static async Task<IReadOnlyDictionary<ItemEnrichmentIdentity, ItemEnrichmentRecord>> ReadWriteRowsAsync(
        DbConnection connection, DbTransaction transaction, string partition, IReadOnlyList<ItemEnrichmentIdentity> identities,
        CancellationToken cancellationToken)
    {
        var result = new Dictionary<ItemEnrichmentIdentity, ItemEnrichmentRecord>();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        Add(command, "@partition", DbType.String, partition);
        var predicate = AddIdentities(command, identities);
        command.CommandText = $"""
            SELECT MediaType, TmdbId, MetadataJson, RatingsJson, MetadataFetchedUtc,
                RatingsFetchedUtc, LastSeenUtc, Generation, TvmazeShowId, MetadataInvalidatedUtc,
                TmdbObservedUtc, MetadataRevision
            FROM ItemEnrichments WHERE PartitionKey = @partition AND {predicate}
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var identity = new ItemEnrichmentIdentity(Enum.Parse<PremiereMediaType>(reader.GetString(0)), reader.GetInt32(1));
            result.Add(identity, new(identity, ReadString(reader, 2), ReadString(reader, 3), ReadDate(reader, 4),
                ReadDate(reader, 5), ReadDate(reader, 6)!.Value, reader.GetInt64(7), reader.IsDBNull(8) ? null : reader.GetInt32(8),
                ReadDate(reader, 9), ReadDate(reader, 10), reader.GetInt64(11)));
        }
        return result;
    }

    private async Task<int> WithConnectionAsync(Func<DbConnection, Task<int>> operation, CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = DatabaseConnectionFactory.Create(_options, _environment.ContentRootPath);
            if (connection is SqliteConnection sqlite) sqlite.DefaultTimeout = 1;
            await connection.OpenAsync(cancellationToken);
            if (connection is SqliteConnection openedSqlite) SQLitePCL.raw.sqlite3_busy_timeout(openedSqlite.Handle, 0);
            if (!Volatile.Read(ref _initialized))
            {
                await DatabaseSchema.AssertCurrentAsync(connection, cancellationToken);
                Volatile.Write(ref _initialized, true);
            }
            return await operation(connection);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private static string AddIdentities(DbCommand command, IReadOnlyList<ItemEnrichmentIdentity> batch)
    {
        for (var index = 0; index < batch.Count; index++)
        {
            Add(command, $"@media{index}", DbType.String, batch[index].MediaType.ToString());
            Add(command, $"@id{index}", DbType.Int32, batch[index].TmdbId);
        }
        return $"(MediaType, TmdbId) IN ({string.Join(", ", batch.Select((_, index) => $"(@media{index}, @id{index})"))})";
    }

    private static void Add(DbCommand command, string name, DbType type, object? value)
    {
        var parameter = DatabaseParameters.Add(command, name, type);
        parameter.Value = value ?? DBNull.Value;
    }

    private static bool IsValid(ItemEnrichmentIdentity identity) =>
        identity.TmdbId > 0 && Enum.IsDefined(identity.MediaType);

    private static bool HasComponent(string? json, DateTimeOffset? fetchedUtc) =>
        !string.IsNullOrWhiteSpace(json) && fetchedUtc.HasValue;

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static string? ReadString(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static DateTimeOffset? ReadDate(DbDataReader reader, int ordinal) => reader.IsDBNull(ordinal)
        ? null
        : DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
