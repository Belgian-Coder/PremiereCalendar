using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class SqliteProviderCacheStateStore : IProviderCacheStateStore
{
    private readonly AppDatabaseOptions _options;
    private readonly IWebHostEnvironment _environment;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    public SqliteProviderCacheStateStore(
        IOptions<AppDatabaseOptions> options,
        IWebHostEnvironment environment)
    {
        _options = options.Value;
        _environment = environment;
    }

    public async Task<ProviderCacheState?> GetAsync(
        string provider,
        ProviderCacheScope scope,
        string key,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(key))
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
                SELECT Provider, Scope, CacheKey, LastCheckedUtc, LastChangedUtc, Watermark, ItemCount, MetadataJson
                FROM ProviderCacheState
                WHERE Provider = $provider
                  AND Scope = $scope
                  AND CacheKey = $cacheKey
                """;
            command.Parameters.AddWithValue("$provider", Normalize(provider));
            command.Parameters.AddWithValue("$scope", scope.ToString());
            command.Parameters.AddWithValue("$cacheKey", key);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken)
                ? ReadState(reader)
                : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(ProviderCacheState state, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(state.Provider) || string.IsNullOrWhiteSpace(state.Key))
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
                INSERT INTO ProviderCacheState (
                    Provider,
                    Scope,
                    CacheKey,
                    LastCheckedUtc,
                    LastChangedUtc,
                    Watermark,
                    ItemCount,
                    MetadataJson
                )
                VALUES (
                    $provider,
                    $scope,
                    $cacheKey,
                    $lastCheckedUtc,
                    $lastChangedUtc,
                    $watermark,
                    $itemCount,
                    $metadataJson
                )
                ON CONFLICT(Provider, Scope, CacheKey) DO UPDATE SET
                    LastCheckedUtc = excluded.LastCheckedUtc,
                    LastChangedUtc = excluded.LastChangedUtc,
                    Watermark = excluded.Watermark,
                    ItemCount = excluded.ItemCount,
                    MetadataJson = excluded.MetadataJson
                """;
            command.Parameters.AddWithValue("$provider", Normalize(state.Provider));
            command.Parameters.AddWithValue("$scope", state.Scope.ToString());
            command.Parameters.AddWithValue("$cacheKey", state.Key);
            command.Parameters.AddWithValue("$lastCheckedUtc", state.LastCheckedUtc.ToString("O", CultureInfo.InvariantCulture));
            command.Parameters.AddWithValue("$lastChangedUtc", state.LastChangedUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "");
            command.Parameters.AddWithValue("$watermark", state.Watermark ?? "");
            command.Parameters.AddWithValue("$itemCount", state.ItemCount is { } count ? count : DBNull.Value);
            command.Parameters.AddWithValue("$metadataJson", state.MetadataJson ?? "");
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveManyAsync(IEnumerable<ProviderCacheState> states, CancellationToken cancellationToken)
    {
        var validStates = states
            .Where(state => !string.IsNullOrWhiteSpace(state.Provider) && !string.IsNullOrWhiteSpace(state.Key))
            .ToArray();
        if (validStates.Length == 0)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken);
            await using var connection = CreateConnection();
            await connection.OpenAsync(cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = UpsertCommandText;
            var providerParameter = command.Parameters.Add("$provider", SqliteType.Text);
            var scopeParameter = command.Parameters.Add("$scope", SqliteType.Text);
            var cacheKeyParameter = command.Parameters.Add("$cacheKey", SqliteType.Text);
            var lastCheckedUtcParameter = command.Parameters.Add("$lastCheckedUtc", SqliteType.Text);
            var lastChangedUtcParameter = command.Parameters.Add("$lastChangedUtc", SqliteType.Text);
            var watermarkParameter = command.Parameters.Add("$watermark", SqliteType.Text);
            var itemCountParameter = command.Parameters.Add("$itemCount", SqliteType.Integer);
            var metadataJsonParameter = command.Parameters.Add("$metadataJson", SqliteType.Text);

            foreach (var state in validStates)
            {
                cancellationToken.ThrowIfCancellationRequested();
                providerParameter.Value = Normalize(state.Provider);
                scopeParameter.Value = state.Scope.ToString();
                cacheKeyParameter.Value = state.Key;
                lastCheckedUtcParameter.Value = state.LastCheckedUtc.ToString("O", CultureInfo.InvariantCulture);
                lastChangedUtcParameter.Value = state.LastChangedUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "";
                watermarkParameter.Value = state.Watermark ?? "";
                itemCountParameter.Value = state.ItemCount is { } count ? count : DBNull.Value;
                metadataJsonParameter.Value = state.MetadataJson ?? "";
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

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
            )
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);

        _initialized = true;
    }

    private const string UpsertCommandText = """
        INSERT INTO ProviderCacheState (
            Provider,
            Scope,
            CacheKey,
            LastCheckedUtc,
            LastChangedUtc,
            Watermark,
            ItemCount,
            MetadataJson
        )
        VALUES (
            $provider,
            $scope,
            $cacheKey,
            $lastCheckedUtc,
            $lastChangedUtc,
            $watermark,
            $itemCount,
            $metadataJson
        )
        ON CONFLICT(Provider, Scope, CacheKey) DO UPDATE SET
            LastCheckedUtc = excluded.LastCheckedUtc,
            LastChangedUtc = excluded.LastChangedUtc,
            Watermark = excluded.Watermark,
            ItemCount = excluded.ItemCount,
            MetadataJson = excluded.MetadataJson
        """;

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

    private static ProviderCacheState ReadState(SqliteDataReader reader)
    {
        return new ProviderCacheState(
            reader.GetString(0),
            Enum.TryParse<ProviderCacheScope>(reader.GetString(1), out var scope) ? scope : ProviderCacheScope.Global,
            reader.GetString(2),
            DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            ParseDate(reader.GetString(4)),
            EmptyToNull(reader.GetString(5)),
            reader.IsDBNull(6) ? null : reader.GetInt32(6),
            EmptyToNull(reader.GetString(7)));
    }

    private static DateTimeOffset? ParseDate(string value)
    {
        return DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var result)
            ? result
            : null;
    }

    private static string? EmptyToNull(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static string Normalize(string value)
    {
        return value.Trim().ToLowerInvariant();
    }
}
