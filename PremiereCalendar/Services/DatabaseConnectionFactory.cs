using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Npgsql;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

internal static class DatabaseConnectionFactory
{
    public static bool IsPostgreSql(AppDatabaseOptions options) =>
        string.Equals(options.Provider, "PostgreSql", StringComparison.OrdinalIgnoreCase);

    public static DbConnection Create(AppDatabaseOptions options, string contentRootPath)
    {
        if (IsPostgreSql(options))
        {
            var builder = new NpgsqlConnectionStringBuilder(options.ConnectionString.Trim())
            {
                Pooling = true,
                Timeout = 15,
                CommandTimeout = 30,
                ApplicationName = "PremiereCalendar"
            };
            if (!string.IsNullOrWhiteSpace(options.PasswordFile))
            {
                builder.Password = File.ReadAllText(Path.GetFullPath(options.PasswordFile.Trim())).TrimEnd('\r', '\n');
            }
            return new NpgsqlConnection(builder.ConnectionString);
        }

        var configuredPath = string.IsNullOrWhiteSpace(options.Path)
            ? "App_Data/data/premiere-calendar.db"
            : options.Path.Trim();
        var path = Path.IsPathFullyQualified(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(contentRootPath, configuredPath));
        return SqliteConnectionFactory.Create(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private
        }.ToString());
    }

    public static bool IsPostgreSql(DbConnection connection) => connection is NpgsqlConnection;

    public static bool IsTransient(Exception exception) => exception switch
    {
        SqliteException { SqliteErrorCode: 5 or 6 } => true,
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
