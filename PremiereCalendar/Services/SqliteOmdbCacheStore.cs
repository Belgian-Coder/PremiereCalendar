using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class SqliteOmdbCacheStore : IOmdbCacheStore
{
    private const string RateLimitedUntilUtcKey = "RateLimitedUntilUtc";
    private const string LastErrorKey = "LastError";
    private const string LastFailureUtcKey = "LastFailureUtc";
    private const string SuccessObservationIdProperty = "_cacheSuccessObservationId";
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
                WHERE ImdbId = @imdbId
                """;
            DatabaseParameters.Add(command, "@imdbId", NormalizeImdbId(imdbId));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            using var document = JsonDocument.Parse(reader.GetString(1));
            var item = document.RootElement.Deserialize<OmdbItem>(JsonOptions);
            if (item is null)
            {
                return null;
            }

            return new OmdbCacheEntry(
                reader.GetString(0),
                item,
                DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                document.RootElement.TryGetProperty(SuccessObservationIdProperty, out var observation)
                    && observation.ValueKind == JsonValueKind.String
                    && IsSuccessObservationId(observation.GetString()) ? observation.GetString() : null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task SetAsync(string imdbId, OmdbItem item, DateTimeOffset cachedAtUtc, CancellationToken cancellationToken)
        => SaveItemAsync(imdbId, item, cachedAtUtc, Guid.NewGuid().ToString("N"), cancellationToken);

    public Task SetWithObservationAsync(string imdbId, OmdbItem item, DateTimeOffset cachedAtUtc,
        string successObservationId, CancellationToken cancellationToken)
        => SaveItemAsync(imdbId, item, cachedAtUtc, successObservationId, cancellationToken);

    private async Task SaveItemAsync(string imdbId, OmdbItem item, DateTimeOffset cachedAtUtc,
        string successObservationId, CancellationToken cancellationToken)
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
                VALUES (@imdbId, @json, @cachedAtUtc)
                ON CONFLICT(ImdbId) DO UPDATE SET
                    Json = excluded.Json,
                    CachedAtUtc = excluded.CachedAtUtc
                """;
            DatabaseParameters.Add(command, "@imdbId", NormalizeImdbId(imdbId));
            // An opaque observation distinguishes successful writes even when their
            // clocks tie. Older readers ignore this optional JSON property.
            var payload = JsonSerializer.SerializeToNode(item, JsonOptions)!.AsObject();
            payload[SuccessObservationIdProperty] = successObservationId;
            DatabaseParameters.Add(command, "@json", payload.ToJsonString(JsonOptions));
            DatabaseParameters.Add(command, "@cachedAtUtc", cachedAtUtc.ToString("O", CultureInfo.InvariantCulture));
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
            await using var transaction = (DbTransaction)await connection.BeginTransactionAsync(cancellationToken);
            // An unrelated in-flight failure must not erase an accepted quota deadline.
            if (state.RateLimitedUntilUtc is { } until)
            {
                await using var readDeadline = connection.CreateCommand();
                readDeadline.Transaction = transaction;
                readDeadline.CommandText = "SELECT Value FROM OmdbProviderState WHERE Key = @key";
                DatabaseParameters.Add(readDeadline, "@key", RateLimitedUntilUtcKey);
                if (await readDeadline.ExecuteScalarAsync(cancellationToken) is string value
                    && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var existing)
                    && existing > until)
                    until = existing;
                await UpsertStateAsync(connection, transaction, RateLimitedUntilUtcKey, until.ToString("O", CultureInfo.InvariantCulture), cancellationToken);
            }
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
        DbConnection connection,
        DbTransaction transaction,
        string key,
        string value,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO OmdbProviderState (Key, Value, UpdatedUtc)
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

    private static bool IsSuccessObservationId(string? value)
        => Guid.TryParseExact(value, "N", out _)
            || value is { Length: 97 } && value[64] == ':'
                && value.Take(64).All(Uri.IsHexDigit) && Guid.TryParseExact(value[65..], "N", out _);

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
