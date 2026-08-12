using Microsoft.Data.Sqlite;

namespace PremiereCalendar.Services;

/// <summary>Applies consistent SQLite connection defaults for the application's concurrent stores.</summary>
internal static class SqliteConnectionFactory
{
    public const int DefaultCommandTimeoutSeconds = 30;
    public const int DefaultBusyTimeoutMilliseconds = 5_000;

    public static SqliteConnection Create(string connectionString)
    {
        var connection = new SqliteConnection(connectionString)
        {
            DefaultTimeout = DefaultCommandTimeoutSeconds
        };
        // Apply the busy timeout to every pooled connection. SQLite's busy timeout is
        // connection-local, so setting it only during startup would leave later store
        // connections vulnerable to transient SQLITE_BUSY errors.
        connection.StateChange += (_, args) =>
        {
            if (args.CurrentState != System.Data.ConnectionState.Open)
            {
                return;
            }

            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA busy_timeout = {DefaultBusyTimeoutMilliseconds};";
            command.ExecuteNonQuery();
        };
        return connection;
    }
}
