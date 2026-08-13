using Microsoft.Data.Sqlite;

namespace PremiereCalendar.Services;

/// <summary>Applies consistent SQLite connection defaults for the application's concurrent stores.</summary>
internal static class SqliteConnectionFactory
{
    public const int DefaultBusyTimeoutMilliseconds = 5_000;

    public static SqliteConnection Create(string connectionString)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString)
        {
            // The application opens short-lived store connections and relies on
            // clean process/file hand-off during signed updates. Native pooled
            // mappings can outlive the managed connection and block atomic restore.
            Pooling = false,
            DefaultTimeout = DefaultBusyTimeoutMilliseconds / 1_000
        };
        var connection = new SqliteConnection(builder.ToString())
        {
            DefaultTimeout = DefaultBusyTimeoutMilliseconds / 1_000
        };
        connection.StateChange += (_, args) =>
        {
            if (args.CurrentState == System.Data.ConnectionState.Open)
            {
                SQLitePCL.raw.sqlite3_busy_timeout(connection.Handle, DefaultBusyTimeoutMilliseconds);
            }
        };
        return connection;
    }
}
