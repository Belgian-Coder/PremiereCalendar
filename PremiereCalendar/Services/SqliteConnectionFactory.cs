using Microsoft.Data.Sqlite;

namespace PremiereCalendar.Services;

/// <summary>Applies consistent SQLite connection defaults for the application's concurrent stores.</summary>
internal static class SqliteConnectionFactory
{
    public const int DefaultBusyTimeoutMilliseconds = 5_000;

    public static SqliteConnection Create(string connectionString, bool relaxWalDurability = true)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString)
        {
            // WAL already provides concurrent readers without shared-cache's
            // process-local table locks. Shared cache can return SQLITE_LOCKED
            // immediately, bypassing the busy handler used for SQLITE_BUSY.
            Cache = SqliteCacheMode.Private,
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
                if (relaxWalDurability) RelaxWalDurability(connection);
            }
        };
        return connection;
    }

    /// <summary>
    /// The initializer switches the database to WAL with synchronous=NORMAL, but synchronous is a per-connection
    /// setting and the stores open unpooled connections that would otherwise run at the FULL default, fsyncing the
    /// WAL on every commit. NORMAL is corruption-safe in WAL mode (only the most recent commits can be lost on power
    /// failure, never on an application crash). The pragma only sets a flag, so it costs no I/O at open time.
    /// Callers that write rollback-journal databases (portable backups) opt out and keep FULL.
    /// </summary>
    private static void RelaxWalDurability(SqliteConnection connection)
    {
        // Best effort: an unreadable or busy database surfaces its error on the caller's first command, exactly
        // as before, rather than from the connection-opened hook.
        _ = SQLitePCL.raw.sqlite3_exec(connection.Handle, "PRAGMA synchronous = NORMAL");
    }
}
