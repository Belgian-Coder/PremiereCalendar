using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using Npgsql;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

internal static class DatabaseConnectionFactory
{
    /// <summary>
    /// Opens a connection through a cached <see cref="NpgsqlDataSource"/>. The connection string is parsed and
    /// validated once per distinct configuration instead of per call, and the password file is read asynchronously
    /// by Npgsql only when a new physical connection is established (never while a pooled one is reused), so a
    /// rotated password is still picked up by the next physical connection.
    /// </summary>
    public static NpgsqlConnection Create(AppDatabaseOptions options, int? commandTimeoutSeconds = null)
    {
        var key = KeyFor(options, commandTimeoutSeconds);
        try
        {
            return GetDataSource(key).CreateConnection();
        }
        catch (ObjectDisposedException)
        {
            // Dispose(options) removed and disposed the data source between GetDataSource and CreateConnection.
            // Use after disposal rebuilds the data source, exactly as a call made just after Dispose would.
            return GetDataSource(key).CreateConnection();
        }
    }

    /// <summary>
    /// Disposes the cached data sources of one configuration (every command-timeout variant), closing their pooled
    /// connections. Hosts call it after their hosted services have stopped; tests call it before dropping a database.
    /// </summary>
    internal static void Dispose(AppDatabaseOptions options)
    {
        var connectionString = options.ConnectionString.Trim();
        var passwordFile = PasswordFileOf(options);
        foreach (var key in PostgreSqlDataSources.Keys)
        {
            if (!string.Equals(key.ConnectionString, connectionString, StringComparison.Ordinal)
                || !string.Equals(key.PasswordFile, passwordFile, StringComparison.Ordinal))
            {
                continue;
            }
            DisposeKey(key);
        }
    }

    /// <summary>Disposes every cached PostgreSQL data source. Last resort at process exit.</summary>
    internal static void DisposePostgreSqlDataSources()
    {
        foreach (var key in PostgreSqlDataSources.Keys) DisposeKey(key);
    }

    private static void DisposeKey(PostgreSqlDataSourceKey key)
    {
        // TryRemove hands each lazy to exactly one disposer, so host shutdown and process exit cannot dispose twice.
        if (!PostgreSqlDataSources.TryRemove(key, out var lazy)) return;
        // Reading Value waits for a build that is still running on another thread, so a data source that finishes
        // building after its key was removed is disposed here instead of being orphaned (checking IsValueCreated
        // would skip it). Every entry is read by the caller that added it right away, so no build is started just
        // to be disposed, and a failed build rethrows here and is swallowed.
        try
        {
            lazy.Value.Dispose();
            if (CounterFor(key.ConnectionString, key.PasswordFile) is { } counter) Interlocked.Increment(ref counter.Disposed);
        }
        catch (Exception) { /* Shutdown must not fail because a pool could not close cleanly. */ }
    }

    private static readonly ConcurrentDictionary<PostgreSqlDataSourceKey, Lazy<NpgsqlDataSource>> PostgreSqlDataSources = new();
    private static int _shutdownHookRegistered;

    // Test instrumentation only. Null (never allocated, never updated) until a test calls EnableBuildCounting, so the
    // application never accumulates an entry per configuration it has seen.
    private static ConcurrentDictionary<(string ConnectionString, string? PasswordFile), BuildCounter>? _buildCounters;

    private sealed class BuildCounter
    {
        public int Built;
        public int Disposed;
    }

    /// <summary>Test-only. Starts counting data source builds and disposals per configuration; idempotent.</summary>
    internal static void EnableBuildCounting() => Interlocked.CompareExchange(ref _buildCounters, new(), null);

    /// <summary>
    /// Test-only. Number of data sources built for one configuration (all command-timeout variants) since
    /// <see cref="EnableBuildCounting"/>. Lets tests prove that concurrent callers do not build duplicates and that
    /// disposal removes the cached instance.
    /// </summary>
    internal static int BuiltFor(AppDatabaseOptions options) => CounterFor(options.ConnectionString.Trim(), PasswordFileOf(options))?.Built ?? 0;

    /// <summary>Test-only. Number of data sources disposed for one configuration since <see cref="EnableBuildCounting"/>.</summary>
    internal static int DisposedFor(AppDatabaseOptions options) => CounterFor(options.ConnectionString.Trim(), PasswordFileOf(options))?.Disposed ?? 0;

    /// <summary>Test-only. Invoked with the connection string at the start of every data source build.</summary>
    internal static Action<string>? BuildInterceptor { get; set; }

    private static BuildCounter? CounterFor(string connectionString, string? passwordFile)
        => _buildCounters?.GetOrAdd((connectionString, passwordFile), static _ => new BuildCounter());

    private readonly record struct PostgreSqlDataSourceKey(string ConnectionString, string? PasswordFile, int? CommandTimeoutSeconds);

    private static string? PasswordFileOf(AppDatabaseOptions options) => string.IsNullOrWhiteSpace(options.PasswordFile)
        ? null
        : Path.GetFullPath(options.PasswordFile.Trim());

    private static PostgreSqlDataSourceKey KeyFor(AppDatabaseOptions options, int? commandTimeoutSeconds) =>
        new(options.ConnectionString.Trim(), PasswordFileOf(options), commandTimeoutSeconds);

    private static NpgsqlDataSource GetDataSource(PostgreSqlDataSourceKey key)
    {
        if (!PostgreSqlDataSources.TryGetValue(key, out var lazy))
        {
            // ExecutionAndPublication guarantees concurrent first callers build exactly one data source.
            lazy = PostgreSqlDataSources.GetOrAdd(key, static candidate => new Lazy<NpgsqlDataSource>(
                () => BuildPostgreSqlDataSource(candidate), LazyThreadSafetyMode.ExecutionAndPublication));
        }
        try
        {
            return lazy.Value;
        }
        catch
        {
            // A failed build (for example a missing password file) must not be cached: fixing the mount recovers.
            PostgreSqlDataSources.TryRemove(new KeyValuePair<PostgreSqlDataSourceKey, Lazy<NpgsqlDataSource>>(key, lazy));
            throw;
        }
    }

    private static NpgsqlDataSource BuildPostgreSqlDataSource(PostgreSqlDataSourceKey key)
    {
        BuildInterceptor?.Invoke(key.ConnectionString);
        var builder = new NpgsqlConnectionStringBuilder(key.ConnectionString)
        {
            Pooling = true,
            Timeout = 15,
            CommandTimeout = key.CommandTimeoutSeconds ?? 30,
            ApplicationName = "PremiereCalendar"
        };
        if (key.PasswordFile is not null)
        {
            // The password file overrides any inline password; Npgsql rejects a password provider combined with one.
            builder.Password = null;
            builder.Passfile = null;
        }
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(builder.ConnectionString);
        if (key.PasswordFile is { } passwordFile)
        {
            // Startup pre-check: fail fast with the raw IO exception (never wrapped in a retryable NpgsqlException) when
            // the secret is missing or unreadable. The exception is tagged so the database initializer can treat it as
            // a configuration error; at runtime IsTransient still classifies it as transient.
            try { _ = ReadPassword(passwordFile); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                exception.Data[PasswordFileCheckKey] = true;
                throw;
            }
            dataSourceBuilder.UsePasswordProvider(
                _ => ReadPassword(passwordFile),
                async (_, cancellationToken) => (await File.ReadAllTextAsync(passwordFile, cancellationToken)).TrimEnd('\r', '\n'));
        }
        var dataSource = dataSourceBuilder.Build();
        if (CounterFor(key.ConnectionString, key.PasswordFile) is { } counter) Interlocked.Increment(ref counter.Built);
        if (Interlocked.Exchange(ref _shutdownHookRegistered, 1) == 0)
        {
            // Explicit data sources are not tracked by Npgsql's own process-exit pool cleanup. Hosts dispose their own
            // data sources after stopping; this only covers processes that never ran a host shutdown.
            AppDomain.CurrentDomain.ProcessExit += (_, _) => DisposePostgreSqlDataSources();
        }
        return dataSource;
    }

    private static string ReadPassword(string passwordFile)
        => File.ReadAllText(passwordFile).TrimEnd('\r', '\n');

    private const string PasswordFileCheckKey = "PremiereCalendar.PasswordFileCheck";

    /// <summary>
    /// True for the raw IO exception that the startup pre-check throws when the password file is missing or unreadable.
    /// Only <see cref="PostgresDatabaseInitializer"/> turns it into a fail-fast configuration error.
    /// </summary>
    internal static bool IsPasswordFileCheckFailure(Exception exception) => exception.Data.Contains(PasswordFileCheckKey);

    /// <summary>
    /// Whether a database failure is worth retrying. The password provider runs for every new physical connection, so
    /// a password file that is briefly unreadable at runtime (a secret remount, a rotation) surfaces as an
    /// NpgsqlException with an IO inner exception and is transient: workers must keep retrying instead of faulting the
    /// host. Failing fast on a missing secret is the startup pre-check's job (see
    /// <see cref="IsPasswordFileCheckFailure"/>), not a property of the classification.
    /// </summary>
    public static bool IsTransient(Exception exception) => exception switch
    {
        PostgresException { SqlState: "40001" or "40P01" or "55P03" } => true,
        NpgsqlException { IsTransient: true } => true,
        // NpgsqlException.IsTransient covers IOException (missing file or directory) but not a permission error.
        NpgsqlException { InnerException: UnauthorizedAccessException } => true,
        // A fresh data source for another command-timeout variant runs the pre-check again while the host is running.
        _ => IsPasswordFileCheckFailure(exception)
    };
}

internal static class DatabaseParameters
{
    public static DbParameter Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
        return parameter;
    }

    public static DbParameter Add(DbCommand command, string name, DbType type)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        command.Parameters.Add(parameter);
        return parameter;
    }
}
