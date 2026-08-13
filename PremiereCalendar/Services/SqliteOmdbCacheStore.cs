using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class SqliteOmdbCacheStore : IOmdbCacheStore
{
    private const string RateLimitedUntilUtcKey = "RateLimitedUntilUtc";
    private const string LastErrorKey = "LastError";
    private const string LastFailureUtcKey = "LastFailureUtc";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly AppDatabaseOptions _options;
    private readonly IWebHostEnvironment _environment;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public SqliteOmdbCacheStore(
        IOptions<AppDatabaseOptions> options,
        IWebHostEnvironment environment,
        TimeProvider timeProvider)
    {
        _options = options.Value;
        _environment = environment;
        _timeProvider = timeProvider;
    }

    public async Task<OmdbCacheEntry?> GetAsync(string imdbId, CancellationToken cancellationToken)
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
                SELECT ImdbId, Json, CachedAtUtc
                FROM OmdbCache
                WHERE ImdbId = $imdbId
                """;
            command.Parameters.AddWithValue("$imdbId", NormalizeImdbId(imdbId));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            var item = JsonSerializer.Deserialize<OmdbItem>(reader.GetString(1), JsonOptions);
            if (item is null)
            {
                return null;
            }

            return new OmdbCacheEntry(
                reader.GetString(0),
                item,
                DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SetAsync(string imdbId, OmdbItem item, DateTimeOffset cachedAtUtc, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(imdbId))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO OmdbCache (ImdbId, Json, CachedAtUtc)
                VALUES ($imdbId, $json, $cachedAtUtc)
                ON CONFLICT(ImdbId) DO UPDATE SET
                    Json = excluded.Json,
                    CachedAtUtc = excluded.CachedAtUtc
                """;
            command.Parameters.AddWithValue("$imdbId", NormalizeImdbId(imdbId));
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(item, JsonOptions));
            command.Parameters.AddWithValue("$cachedAtUtc", cachedAtUtc.ToString("O", CultureInfo.InvariantCulture));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<OmdbProviderCacheState> GetProviderStateAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT Key, Value FROM OmdbProviderState";

            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                values[reader.GetString(0)] = reader.GetString(1);
            }

            return new OmdbProviderCacheState(
                GetDateTimeOffset(values, RateLimitedUntilUtcKey),
                GetString(values, LastErrorKey),
                GetDateTimeOffset(values, LastFailureUtcKey));
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task MarkRateLimitedAsync(DateTimeOffset untilUtc, string error, CancellationToken cancellationToken)
    {
        return SaveProviderStateAsync(
            new OmdbProviderCacheState(untilUtc, error, _timeProvider.GetUtcNow()),
            cancellationToken);
    }

    public Task MarkFailureAsync(string error, CancellationToken cancellationToken)
    {
        return SaveProviderStateAsync(
            new OmdbProviderCacheState(null, error, _timeProvider.GetUtcNow()),
            cancellationToken);
    }

    private async Task SaveProviderStateAsync(OmdbProviderCacheState state, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await UpsertStateAsync(connection, transaction, RateLimitedUntilUtcKey, state.RateLimitedUntilUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "", cancellationToken);
            await UpsertStateAsync(connection, transaction, LastErrorKey, state.LastError ?? "", cancellationToken);
            await UpsertStateAsync(connection, transaction, LastFailureUtcKey, state.LastFailureUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "", cancellationToken);
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
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        string value,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO OmdbProviderState (Key, Value, UpdatedUtc)
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
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Shared
        };

        return SqliteConnectionFactory.Create(builder.ToString());
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

    private static DateTimeOffset? GetDateTimeOffset(IReadOnlyDictionary<string, string> values, string key)
    {
        return values.TryGetValue(key, out var value)
            && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var result)
                ? result
                : null;
    }
}
