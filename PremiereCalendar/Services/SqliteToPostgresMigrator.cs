using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class SqliteToPostgresMigrator(
    IOptions<AppDatabaseOptions> options,
    IWebHostEnvironment environment)
{
    private static readonly string[] Tables =
    [
        "AppParameters", "ImdbRatings", "ImdbDatasetState", "OmdbCache", "OmdbProviderState",
        "ProviderCacheState", "SimklSyncState", "CalendarFilterUsage", "ViewSyncGroups",
        "ViewSyncDevices", "ViewSyncGroupState", "ProviderWorkJobs", "ProviderAdaptiveState",
        "ProviderWorkLeases"
    ];

    public async Task<DatabaseMigrationReport> MigrateAsync(string sourcePath, CancellationToken cancellationToken)
    {
        if (!DatabaseConnectionFactory.IsPostgreSql(options.Value))
        {
            throw new InvalidOperationException("AppDatabase:Provider must be PostgreSql for this migration.");
        }
        var fullSourcePath = Path.GetFullPath(sourcePath);
        if (!File.Exists(fullSourcePath)) throw new FileNotFoundException("SQLite migration source was not found.", fullSourcePath);

        await using var source = SqliteConnectionFactory.Create(new SqliteConnectionStringBuilder
        {
            DataSource = fullSourcePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString());
        await source.OpenAsync(cancellationToken);
        await using (var integrity = source.CreateCommand())
        {
            integrity.CommandText = "PRAGMA quick_check";
            if (!string.Equals(Convert.ToString(await integrity.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture), "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("SQLite migration source failed quick_check.");
            }
        }
        await DatabaseSchema.AssertCurrentAsync(source, cancellationToken);

        await using var destination = (NpgsqlConnection)DatabaseConnectionFactory.Create(options.Value, environment.ContentRootPath);
        destination.ConnectionString = new NpgsqlConnectionStringBuilder(destination.ConnectionString)
        {
            CommandTimeout = 0
        }.ConnectionString;
        await destination.OpenAsync(cancellationToken);
        await DatabaseSchema.AssertCurrentAsync(destination, cancellationToken);

        foreach (var table in Tables)
        {
            await using var count = destination.CreateCommand();
            count.CommandText = $"SELECT COUNT(*) FROM {table}";
            if (Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture) != 0)
            {
                throw new InvalidOperationException($"PostgreSQL target table {table} is not empty; refusing to merge or overwrite data.");
            }
        }

        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var transaction = await destination.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (var table in Tables)
            {
                var columns = await ReadColumnsAsync(source, table, cancellationToken);
                if (columns.Count == 0) throw new InvalidOperationException($"SQLite source table {table} is missing.");
                await using var read = source.CreateCommand();
                read.CommandText = $"SELECT {string.Join(", ", columns.Select(column => column.Name))} FROM {table}";
                await using var reader = await read.ExecuteReaderAsync(cancellationToken);

                long copied = 0;
                try
                {
                    await using var importer = await destination.BeginBinaryImportAsync(
                        $"COPY {table} ({string.Join(", ", columns.Select(column => column.Name))}) FROM STDIN (FORMAT BINARY)",
                        cancellationToken);
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        await importer.StartRowAsync(cancellationToken);
                        for (var index = 0; index < columns.Count; index++)
                        {
                            if (reader.IsDBNull(index))
                            {
                                await importer.WriteNullAsync(cancellationToken);
                                continue;
                            }

                            switch (columns[index].Type)
                            {
                                case "INTEGER":
                                    await importer.WriteAsync(Convert.ToInt32(reader.GetValue(index), CultureInfo.InvariantCulture), NpgsqlDbType.Integer, cancellationToken);
                                    break;
                                case "REAL":
                                    await importer.WriteAsync(Convert.ToSingle(reader.GetValue(index), CultureInfo.InvariantCulture), NpgsqlDbType.Real, cancellationToken);
                                    break;
                                default:
                                    await importer.WriteAsync(Convert.ToString(reader.GetValue(index), CultureInfo.InvariantCulture)!, NpgsqlDbType.Text, cancellationToken);
                                    break;
                            }
                        }
                        copied++;
                    }
                    await importer.CompleteAsync(cancellationToken);
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException($"PostgreSQL COPY failed for {table} after {copied} rows: {exception.Message}", exception);
                }
                counts[table] = copied;
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            try { await transaction.RollbackAsync(CancellationToken.None); }
            catch { }
            throw;
        }

        foreach (var entry in counts)
        {
            await using var count = destination.CreateCommand();
            count.CommandText = $"SELECT COUNT(*) FROM {entry.Key}";
            var actual = Convert.ToInt64(await count.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
            if (actual != entry.Value) throw new InvalidOperationException($"PostgreSQL row-count verification failed for {entry.Key}: expected {entry.Value}, found {actual}.");
        }

        await using var snapshotStream = File.OpenRead(fullSourcePath);
        return new DatabaseMigrationReport(
            Convert.ToHexString(await SHA256.HashDataAsync(snapshotStream, cancellationToken)).ToLowerInvariant(),
            counts,
            counts.Values.Sum());
    }

    private static async Task<IReadOnlyList<SourceColumn>> ReadColumnsAsync(SqliteConnection source, string table, CancellationToken cancellationToken)
    {
        await using var command = source.CreateCommand();
        command.CommandText = $"PRAGMA table_info({table})";
        var columns = new List<SourceColumn>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(new SourceColumn(reader.GetString(1), reader.GetString(2).ToUpperInvariant()));
        }
        return columns;
    }

    private sealed record SourceColumn(string Name, string Type);
}

public sealed record DatabaseMigrationReport(
    string SourceSha256,
    IReadOnlyDictionary<string, long> TableRowCounts,
    long TotalRows);
