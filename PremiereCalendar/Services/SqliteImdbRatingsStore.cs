using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class SqliteImdbRatingsStore : IImdbRatingsStore
{
    private const string LastImportedUtcKey = "LastImportedUtc";
    private const string RatingCountKey = "RatingCount";
    private const string LastErrorKey = "LastError";

    private readonly AppDatabaseOptions _options;
    private readonly IWebHostEnvironment _environment;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public SqliteImdbRatingsStore(
        IOptions<AppDatabaseOptions> options,
        IWebHostEnvironment environment)
    {
        _options = options.Value;
        _environment = environment;
    }

    public async Task<ImdbRatingRecord?> GetByImdbIdAsync(string imdbId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(imdbId))
        {
            return null;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT ImdbId, AverageRating, VoteCount, ImportedAtUtc
                FROM ImdbRatings
                WHERE ImdbId = $imdbId
                """;
            command.Parameters.AddWithValue("$imdbId", NormalizeImdbId(imdbId));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            return new ImdbRatingRecord(
                reader.GetString(0),
                reader.GetDouble(1),
                reader.GetInt32(2),
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task ReplaceAllAsync(
        IEnumerable<ImdbRatingRecord> ratings,
        DateTimeOffset importedAtUtc,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    DROP TABLE IF EXISTS ImdbRatingsNew;
                    CREATE TABLE ImdbRatingsNew (
                        ImdbId TEXT NOT NULL PRIMARY KEY,
                        AverageRating REAL NOT NULL,
                        VoteCount INTEGER NOT NULL,
                        ImportedAtUtc TEXT NOT NULL
                    )
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            var count = 0;
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT OR REPLACE INTO ImdbRatingsNew (ImdbId, AverageRating, VoteCount, ImportedAtUtc)
                    VALUES ($imdbId, $averageRating, $voteCount, $importedAtUtc)
                    """;
                var imdbIdParameter = insert.Parameters.Add("$imdbId", SqliteType.Text);
                var ratingParameter = insert.Parameters.Add("$averageRating", SqliteType.Real);
                var voteCountParameter = insert.Parameters.Add("$voteCount", SqliteType.Integer);
                var importedAtParameter = insert.Parameters.Add("$importedAtUtc", SqliteType.Text);
                var importedAt = importedAtUtc.ToString("O", CultureInfo.InvariantCulture);

                foreach (var rating in ratings)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(rating.ImdbId))
                    {
                        continue;
                    }

                    imdbIdParameter.Value = NormalizeImdbId(rating.ImdbId);
                    ratingParameter.Value = rating.AverageRating;
                    voteCountParameter.Value = rating.VoteCount;
                    importedAtParameter.Value = importedAt;
                    await insert.ExecuteNonQueryAsync(cancellationToken);
                    count++;
                }
            }

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = """
                    DROP TABLE IF EXISTS ImdbRatings;
                    ALTER TABLE ImdbRatingsNew RENAME TO ImdbRatings
                    """;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await UpsertStateAsync(connection, transaction, LastImportedUtcKey, importedAtUtc.ToString("O", CultureInfo.InvariantCulture), cancellationToken);
            await UpsertStateAsync(connection, transaction, RatingCountKey, count.ToString(CultureInfo.InvariantCulture), cancellationToken);
            await UpsertStateAsync(connection, transaction, LastErrorKey, "", cancellationToken);

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ImdbDatasetState> GetStateAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT Key, Value FROM ImdbDatasetState";

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                values[reader.GetString(0)] = reader.GetString(1);
            }

            return new ImdbDatasetState(
                GetDateTimeOffset(values, LastImportedUtcKey),
                GetInt(values, RatingCountKey),
                GetString(values, LastErrorKey));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveStateAsync(ImdbDatasetState state, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await UpsertStateAsync(
                connection,
                transaction,
                LastImportedUtcKey,
                state.LastImportedUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "",
                cancellationToken);
            await UpsertStateAsync(
                connection,
                transaction,
                RatingCountKey,
                state.RatingCount.ToString(CultureInfo.InvariantCulture),
                cancellationToken);
            await UpsertStateAsync(connection, transaction, LastErrorKey, state.LastError ?? "", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        var databasePath = ResolveDatabasePath();
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
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
            )
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);

        _initialized = true;
    }

    private static async Task UpsertStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        string value,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ImdbDatasetState (Key, Value, UpdatedUtc)
            VALUES ($key, $value, $updatedUtc)
            ON CONFLICT(Key) DO UPDATE SET
                Value = excluded.Value,
                UpdatedUtc = excluded.UpdatedUtc
            """;
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.Parameters.AddWithValue("$updatedUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private SqliteConnection CreateConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = ResolveDatabasePath(),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };

        return new SqliteConnection(builder.ToString());
    }

    private string ResolveDatabasePath()
    {
        var configuredPath = string.IsNullOrWhiteSpace(_options.Path)
            ? "App_Data/data/premiere-calendar.db"
            : _options.Path.Trim();

        return Path.IsPathFullyQualified(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(_environment.ContentRootPath, configuredPath));
    }

    private static string NormalizeImdbId(string imdbId)
    {
        return imdbId.Trim().ToLowerInvariant();
    }

    private static string? GetString(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static int GetInt(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var value)
            && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
                ? result
                : 0;
    }

    private static DateTimeOffset? GetDateTimeOffset(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var value)
            && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var result)
                ? result
                : null;
    }
}
