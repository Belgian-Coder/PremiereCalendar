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
        return GetDataSource(key).CreateConnection();
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
        if (!PostgreSqlDataSources.TryRemove(key, out var lazy) || !lazy.IsValueCreated) return;
        try { lazy.Value.Dispose(); }
        catch (Exception) { /* Shutdown must not fail because a pool could not close cleanly. */ }
    }

    private static readonly ConcurrentDictionary<PostgreSqlDataSourceKey, Lazy<NpgsqlDataSource>> PostgreSqlDataSources = new();
    private static int _shutdownHookRegistered;
    private static readonly ConcurrentDictionary<(string ConnectionString, string? PasswordFile), int> BuildCounts = new();

    /// <summary>
    /// Number of data sources built so far for one configuration (all command-timeout variants). Lets tests prove that
    /// concurrent callers do not build duplicates and that disposal removes the cached instance.
    /// </summary>
    internal static int BuiltFor(AppDatabaseOptions options)
        => BuildCounts.GetValueOrDefault((options.ConnectionString.Trim(), PasswordFileOf(options)));

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
        BuildCounts.AddOrUpdate((key.ConnectionString, key.PasswordFile), 1, static (_, count) => count + 1);
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
