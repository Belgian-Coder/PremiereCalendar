using Microsoft.Data.Sqlite;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

[Collection(SqliteTestCollection.Name)]
public sealed class SqliteConnectionFactoryDurabilityTests : IDisposable
{
    private const long SynchronousFull = 2;
    private const long SynchronousNormal = 1;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "premiere-sqlite-durability-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task StoreConnectionsToAWalDatabaseUseSynchronousNormalAndKeepForeignKeysEnforced()
    {
        TestSqliteDatabase.Initialize(_root, "wal.db");
        await using var connection = Open(Path.Combine(_root, "wal.db"));

        Assert.Equal("wal", (await Scalar(connection, "PRAGMA journal_mode")).ToString(), ignoreCase: true);
        Assert.Equal(SynchronousNormal, Convert.ToInt64(await Scalar(connection, "PRAGMA synchronous")));
        Assert.Equal(1L, Convert.ToInt64(await Scalar(connection, "PRAGMA foreign_keys")));
        Assert.Equal(5000L, Convert.ToInt64(await Scalar(connection, "PRAGMA busy_timeout")));
    }

    [Fact]
    public async Task EveryNewUnpooledConnectionGetsTheRelaxedSetting()
    {
        TestSqliteDatabase.Initialize(_root, "wal.db");
        for (var index = 0; index < 3; index++)
        {
            await using var connection = Open(Path.Combine(_root, "wal.db"));
            Assert.Equal(SynchronousNormal, Convert.ToInt64(await Scalar(connection, "PRAGMA synchronous")));
        }
    }

    [Fact]
    public async Task RollbackJournalDatabasesKeepTheFullDefault()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "delete.db");
        await using (var seed = Open(path, SqliteOpenMode.ReadWriteCreate))
        {
            await Scalar(seed, "CREATE TABLE Probe (Id INTEGER)");
        }

        await using var connection = Open(path);
        Assert.Equal("delete", (await Scalar(connection, "PRAGMA journal_mode")).ToString(), ignoreCase: true);
        Assert.Equal(SynchronousFull, Convert.ToInt64(await Scalar(connection, "PRAGMA synchronous")));
    }

    [Fact]
    public async Task ReadOnlyConnectionsAndNonDatabaseFilesStillSurfaceTheirOwnErrors()
    {
        TestSqliteDatabase.Initialize(_root, "wal.db");
        await using (var readOnly = Open(Path.Combine(_root, "wal.db"), SqliteOpenMode.ReadOnly))
        {
            Assert.Equal(1L, Convert.ToInt64(await Scalar(readOnly, "SELECT 1")));
        }

        var garbage = Path.Combine(_root, "garbage.db");
        await File.WriteAllTextAsync(garbage, new string('x', 4096));
        await using var corrupt = Open(garbage);
        // The connection-opened hook must not swallow or relocate the failure the caller's first command reports.
        await Assert.ThrowsAsync<SqliteException>(() => Scalar(corrupt, "SELECT COUNT(*) FROM sqlite_master"));
    }

    private static SqliteConnection Open(string path, SqliteOpenMode mode = SqliteOpenMode.ReadWrite)
    {
        var connection = SqliteConnectionFactory.Create(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = mode,
            Cache = SqliteCacheMode.Private
        }.ToString());
        connection.Open();
        return connection;
    }

    private static async Task<object> Scalar(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (await command.ExecuteScalarAsync())!;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
