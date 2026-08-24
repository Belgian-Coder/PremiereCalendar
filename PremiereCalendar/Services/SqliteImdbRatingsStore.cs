using System.Data;
using System.Data.Common;
using System.Globalization;
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
                WHERE ImdbId = @imdbId
                """;
            DatabaseParameters.Add(command, "@imdbId", NormalizeImdbId(imdbId));

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

    public async Task<IReadOnlyDictionary<string, ImdbRatingRecord>> GetByImdbIdsAsync(
        IReadOnlyCollection<string> imdbIds,
        CancellationToken cancellationToken)
    {
        var normalizedIds = imdbIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(NormalizeImdbId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (normalizedIds.Length == 0)
        {
            return new Dictionary<string, ImdbRatingRecord>(StringComparer.OrdinalIgnoreCase);
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            var ratings = new Dictionary<string, ImdbRatingRecord>(normalizedIds.Length, StringComparer.OrdinalIgnoreCase);

            foreach (var batch in normalizedIds.Chunk(400))
            {
                await using var command = connection.CreateCommand();
                var parameterNames = new string[batch.Length];
                for (var index = 0; index < batch.Length; index++)
                {
                    parameterNames[index] = $"@id{index}";
                    DatabaseParameters.Add(command, parameterNames[index], batch[index]);
                }

                command.CommandText = $"""
                    SELECT ImdbId, AverageRating, VoteCount, ImportedAtUtc
                    FROM ImdbRatings
                    WHERE ImdbId IN ({string.Join(", ", parameterNames)})
                    """;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var rating = ReadRating(reader);
                    ratings[rating.ImdbId] = rating;
                }
            }

            return ratings;
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
        await ReplaceAllStreamingAsync(ToAsyncEnumerable(ratings, cancellationToken), importedAtUtc, cancellationToken);
    }

    public async Task<int> ReplaceAllStreamingAsync(
        IAsyncEnumerable<ImdbRatingRecord> ratings,
        DateTimeOffset importedAtUtc,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (DbTransaction)await connection.BeginTransactionAsync(cancellationToken);

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = "DELETE FROM ImdbRatings";
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            var count = 0;
            await using (var insert = connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO ImdbRatings (ImdbId, AverageRating, VoteCount, ImportedAtUtc)
                    VALUES (@imdbId, @averageRating, @voteCount, @importedAtUtc)
                    ON CONFLICT(ImdbId) DO UPDATE SET
                        AverageRating = excluded.AverageRating,
                        VoteCount = excluded.VoteCount,
                        ImportedAtUtc = excluded.ImportedAtUtc
                    """;
                var imdbIdParameter = DatabaseParameters.Add(insert, "@imdbId", DbType.String);
                var ratingParameter = DatabaseParameters.Add(insert, "@averageRating", DbType.Double);
                var voteCountParameter = DatabaseParameters.Add(insert, "@voteCount", DbType.Int64);
                var importedAtParameter = DatabaseParameters.Add(insert, "@importedAtUtc", DbType.String);
                var importedAt = importedAtUtc.ToString("O", CultureInfo.InvariantCulture);

                await foreach (var rating in ratings.WithCancellation(cancellationToken))
                {
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

            await UpsertStateAsync(connection, transaction, LastImportedUtcKey, importedAtUtc.ToString("O", CultureInfo.InvariantCulture), cancellationToken);
            await UpsertStateAsync(connection, transaction, RatingCountKey, count.ToString(CultureInfo.InvariantCulture), cancellationToken);
            await UpsertStateAsync(connection, transaction, LastErrorKey, "", cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return count;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static ImdbRatingRecord ReadRating(DbDataReader reader)
    {
        return new ImdbRatingRecord(
            reader.GetString(0),
            reader.GetDouble(1),
            reader.GetInt32(2),
            DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }

    private static async IAsyncEnumerable<ImdbRatingRecord> ToAsyncEnumerable(
        IEnumerable<ImdbRatingRecord> ratings,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var rating in ratings)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return rating;
        }

        await Task.CompletedTask;
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
            await using var transaction = (DbTransaction)await connection.BeginTransactionAsync(cancellationToken);
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

        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await DatabaseSchema.AssertCurrentAsync(connection, cancellationToken);
        _initialized = true;
    }

    private static async Task UpsertStateAsync(
        DbConnection connection,
        DbTransaction transaction,
        string key,
        string value,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO ImdbDatasetState (Key, Value, UpdatedUtc)
            VALUES (@key, @value, @updatedUtc)
            ON CONFLICT(Key) DO UPDATE SET
                Value = excluded.Value,
                UpdatedUtc = excluded.UpdatedUtc
            """;
        DatabaseParameters.Add(command, "@key", key);
        DatabaseParameters.Add(command, "@value", value);
        DatabaseParameters.Add(command, "@updatedUtc", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private DbConnection CreateConnection()
    {
        return DatabaseConnectionFactory.Create(_options, _environment.ContentRootPath);
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
