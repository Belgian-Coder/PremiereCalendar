using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Extensions.Options;
using Microsoft.Data.Sqlite;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class SqliteProviderCacheStateStore : IProviderCacheStateStore
{
    private readonly AppDatabaseOptions _options;
    private readonly IWebHostEnvironment _environment;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _responseGate = new(4, 4);
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
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

        var operationGate = GetOperationGate(provider);
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            var responseCacheOperation = IsResponseCacheProvider(provider);
            await EnsureInitializedAsync(cancellationToken, responseCacheOperation);
            await using var connection = CreateConnection();
            ConfigureResponseCacheConnection(connection, responseCacheOperation);
            await connection.OpenAsync(cancellationToken);
            ConfigureResponseCacheConnection(connection, responseCacheOperation);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT Provider, Scope, CacheKey, LastCheckedUtc, LastChangedUtc, Watermark, ItemCount, MetadataJson
                FROM ProviderCacheState
                WHERE Provider = @provider
                  AND Scope = @scope
                  AND CacheKey = @cacheKey
                """;
            DatabaseParameters.Add(command, "@provider", Normalize(provider));
            DatabaseParameters.Add(command, "@scope", scope.ToString());
            DatabaseParameters.Add(command, "@cacheKey", key);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            return await reader.ReadAsync(cancellationToken)
                ? ReadState(reader)
                : null;
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task SaveAsync(ProviderCacheState state, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(state.Provider) || string.IsNullOrWhiteSpace(state.Key))
        {
            return;
        }

        var operationGate = GetOperationGate(state.Provider);
        await operationGate.WaitAsync(cancellationToken);
        try
        {
            var responseCacheOperation = IsResponseCacheProvider(state.Provider);
            await EnsureInitializedAsync(cancellationToken, responseCacheOperation);
            await using var connection = CreateConnection();
            ConfigureResponseCacheConnection(connection, responseCacheOperation);
            await connection.OpenAsync(cancellationToken);
            ConfigureResponseCacheConnection(connection, responseCacheOperation);
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
                    @provider,
                    @scope,
                    @cacheKey,
                    @lastCheckedUtc,
                    @lastChangedUtc,
                    @watermark,
                    @itemCount,
                    @metadataJson
                )
                ON CONFLICT(Provider, Scope, CacheKey) DO UPDATE SET
                    LastCheckedUtc = excluded.LastCheckedUtc,
                    LastChangedUtc = excluded.LastChangedUtc,
                    Watermark = excluded.Watermark,
                    ItemCount = excluded.ItemCount,
                    MetadataJson = excluded.MetadataJson
                """;
            DatabaseParameters.Add(command, "@provider", Normalize(state.Provider));
            DatabaseParameters.Add(command, "@scope", state.Scope.ToString());
            DatabaseParameters.Add(command, "@cacheKey", state.Key);
            DatabaseParameters.Add(command, "@lastCheckedUtc", state.LastCheckedUtc.ToString("O", CultureInfo.InvariantCulture));
            DatabaseParameters.Add(command, "@lastChangedUtc", state.LastChangedUtc?.ToString("O", CultureInfo.InvariantCulture) ?? "");
            DatabaseParameters.Add(command, "@watermark", state.Watermark ?? "");
            DatabaseParameters.Add(command, "@itemCount", state.ItemCount is { } count ? count : DBNull.Value);
            DatabaseParameters.Add(command, "@metadataJson", state.MetadataJson ?? "");
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            operationGate.Release();
        }
    }

    public async Task<IReadOnlyList<ProviderCacheState>> GetRecentAsync(int take, CancellationToken cancellationToken)
    {
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
                WHERE Provider NOT LIKE 'responsecache-%'
                ORDER BY LastCheckedUtc DESC
                LIMIT @take
                """;
            DatabaseParameters.Add(command, "@take", Math.Clamp(take, 1, 500));
            return await ReadStatesAsync(command, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<ProviderCacheState>> GetByProviderAsync(
        string provider,
        int take,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(provider))
        {
            return [];
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
                WHERE Provider = @provider
                ORDER BY LastCheckedUtc DESC
                LIMIT @take
                """;
            DatabaseParameters.Add(command, "@provider", Normalize(provider));
            DatabaseParameters.Add(command, "@take", Math.Clamp(take, 1, 500));
            return await ReadStatesAsync(command, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<int> CleanupResponseCacheAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureInitializedAsync(cancellationToken, responseCacheOperation: true);
            await using var connection = CreateConnection();
            ConfigureResponseCacheConnection(connection, responseCacheOperation: true);
            await connection.OpenAsync(cancellationToken);
            ConfigureResponseCacheConnection(connection, responseCacheOperation: true);
            await using var transaction = (DbTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                DELETE FROM ProviderCacheState
                WHERE Provider IN (@tmdb, @rottenTomatoes)
                  AND Scope = @scope
                  AND LastCheckedUtc < @cutoffUtc
                """;
            DatabaseParameters.Add(command, "@tmdb", "responsecache-tmdb");
            DatabaseParameters.Add(command, "@rottenTomatoes", "responsecache-rottentomatoes");
            DatabaseParameters.Add(command, "@scope", ProviderCacheScope.Item.ToString());
            DatabaseParameters.Add(command, "@cutoffUtc", cutoffUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
            var removed = await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return removed;
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
            await using var transaction = (DbTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = UpsertCommandText;
            var providerParameter = DatabaseParameters.Add(command, "@provider", DbType.String);
            var scopeParameter = DatabaseParameters.Add(command, "@scope", DbType.String);
            var cacheKeyParameter = DatabaseParameters.Add(command, "@cacheKey", DbType.String);
            var lastCheckedUtcParameter = DatabaseParameters.Add(command, "@lastCheckedUtc", DbType.String);
            var lastChangedUtcParameter = DatabaseParameters.Add(command, "@lastChangedUtc", DbType.String);
            var watermarkParameter = DatabaseParameters.Add(command, "@watermark", DbType.String);
            var itemCountParameter = DatabaseParameters.Add(command, "@itemCount", DbType.Int64);
            var metadataJsonParameter = DatabaseParameters.Add(command, "@metadataJson", DbType.String);

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

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken, bool responseCacheOperation = false)
    {
        if (Volatile.Read(ref _initialized))
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            await using var connection = CreateConnection();
            ConfigureResponseCacheConnection(connection, responseCacheOperation);
            await connection.OpenAsync(cancellationToken);
            ConfigureResponseCacheConnection(connection, responseCacheOperation);
            await DatabaseSchema.AssertCurrentAsync(connection, cancellationToken);
            Volatile.Write(ref _initialized, true);
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    // PostgreSQL can serve unrelated response keys independently. Keep bounded
    // database parallelism without queuing every cache hit behind one slow write.
    // SQLite retains the existing single writer/read gate.
    private SemaphoreSlim GetOperationGate(string provider)
        => DatabaseConnectionFactory.IsPostgreSql(_options) && IsResponseCacheProvider(provider) ? _responseGate : _gate;

    private static bool IsResponseCacheProvider(string provider)
        => Normalize(provider) is "responsecache-tmdb" or "responsecache-rottentomatoes";

    private static void ConfigureResponseCacheConnection(DbConnection connection, bool responseCacheOperation)
    {
        if (!responseCacheOperation || connection is not SqliteConnection sqlite) return;
        // SQLite async commands perform native work synchronously. Keep their managed
        // busy retries to one second and avoid an additional five-second native wait.
        sqlite.DefaultTimeout = 1;
        if (sqlite.State == ConnectionState.Open) SQLitePCL.raw.sqlite3_busy_timeout(sqlite.Handle, 0);
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
            @provider,
            @scope,
            @cacheKey,
            @lastCheckedUtc,
            @lastChangedUtc,
            @watermark,
            @itemCount,
            @metadataJson
        )
        ON CONFLICT(Provider, Scope, CacheKey) DO UPDATE SET
            LastCheckedUtc = excluded.LastCheckedUtc,
            LastChangedUtc = excluded.LastChangedUtc,
            Watermark = excluded.Watermark,
            ItemCount = excluded.ItemCount,
            MetadataJson = excluded.MetadataJson
        """;

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

    private static ProviderCacheState ReadState(DbDataReader reader)
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

    private static async Task<IReadOnlyList<ProviderCacheState>> ReadStatesAsync(
        DbCommand command,
        CancellationToken cancellationToken)
    {
        var states = new List<ProviderCacheState>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            states.Add(ReadState(reader));
        }

        return states;
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
