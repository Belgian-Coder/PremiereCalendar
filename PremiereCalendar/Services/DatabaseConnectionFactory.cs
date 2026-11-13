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
    /// rotated password is still picked up by the next physical connection exactly as before.
    /// </summary>
    public static NpgsqlConnection Create(AppDatabaseOptions options, int? commandTimeoutSeconds = null)
    {
        var connectionString = options.ConnectionString.Trim();
        var passwordFile = string.IsNullOrWhiteSpace(options.PasswordFile)
            ? null
            : Path.GetFullPath(options.PasswordFile.Trim());
        var key = new PostgreSqlDataSourceKey(connectionString, passwordFile, commandTimeoutSeconds);
        if (!PostgreSqlDataSources.TryGetValue(key, out var dataSource))
        {
            dataSource = PostgreSqlDataSources.GetOrAdd(key, static candidate => BuildPostgreSqlDataSource(candidate));
        }
        return dataSource.CreateConnection();
    }

    /// <summary>
    /// Disposes the cached data sources of one configuration (every command-timeout variant), closing their pooled
    /// connections. Hosts call it after their hosted services have stopped.
    /// </summary>
    internal static void Dispose(AppDatabaseOptions options)
    {
        var connectionString = options.ConnectionString.Trim();
        var passwordFile = string.IsNullOrWhiteSpace(options.PasswordFile)
            ? null
            : Path.GetFullPath(options.PasswordFile.Trim());
        foreach (var key in PostgreSqlDataSources.Keys)
        {
            if (!string.Equals(key.ConnectionString, connectionString, StringComparison.Ordinal)
                || !string.Equals(key.PasswordFile, passwordFile, StringComparison.Ordinal)
                || !PostgreSqlDataSources.TryRemove(key, out var dataSource))
            {
                continue;
            }
            try { dataSource.Dispose(); }
            catch (Exception) { /* Shutdown must not fail because a pool could not close cleanly. */ }
        }
    }

    /// <summary>Disposes every cached PostgreSQL data source, closing its pooled connections.</summary>
    internal static void DisposePostgreSqlDataSources()
    {
        foreach (var key in PostgreSqlDataSources.Keys)
        {
            if (!PostgreSqlDataSources.TryRemove(key, out var dataSource)) continue;
            try { dataSource.Dispose(); }
            catch (Exception) { /* Shutdown must not fail because a pool could not close cleanly. */ }
        }
    }

    private static readonly ConcurrentDictionary<PostgreSqlDataSourceKey, NpgsqlDataSource> PostgreSqlDataSources = new();
    private static int _shutdownHookRegistered;

    private readonly record struct PostgreSqlDataSourceKey(string ConnectionString, string? PasswordFile, int? CommandTimeoutSeconds);

    private static NpgsqlDataSource BuildPostgreSqlDataSource(PostgreSqlDataSourceKey key)
    {
        var builder = new NpgsqlConnectionStringBuilder(key.ConnectionString)
        {
            Pooling = true,
            Timeout = 15,
            CommandTimeout = key.CommandTimeoutSeconds ?? 30,
            ApplicationName = "PremiereCalendar"
        };
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(builder.ConnectionString);
        if (key.PasswordFile is { } passwordFile)
        {
            dataSourceBuilder.UsePasswordProvider(
                _ => ReadPassword(passwordFile),
                async (_, cancellationToken) => (await File.ReadAllTextAsync(passwordFile, cancellationToken)).TrimEnd('\r', '\n'));
        }
        if (Interlocked.Exchange(ref _shutdownHookRegistered, 1) == 0)
        {
            // Explicit data sources are not tracked by Npgsql's own process-exit pool cleanup.
            AppDomain.CurrentDomain.ProcessExit += (_, _) => DisposePostgreSqlDataSources();
        }
        return dataSourceBuilder.Build();
    }

    private static string ReadPassword(string passwordFile)
        => File.ReadAllText(passwordFile).TrimEnd('\r', '\n');

    public static bool IsTransient(Exception exception) => exception switch
    {
        PostgresException { SqlState: "40001" or "40P01" or "55P03" } => true,
        NpgsqlException { IsTransient: true } => true,
        _ => false
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
