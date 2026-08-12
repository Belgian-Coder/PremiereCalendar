using Microsoft.Data.Sqlite;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class SqliteDatabaseInitializerTests
{
    [Fact]
    public async Task InitializeAsync_CreatesDirectoryAndEnablesWalWithBusyTimeout()
    {
        var root = Path.Combine(Path.GetTempPath(), "premiere-calendar-sqlite-tests", Guid.NewGuid().ToString("N"));
        var relative = Path.Combine("nested", "calendar.db");
        try
        {
            var initializer = new SqliteDatabaseInitializer(
                Microsoft.Extensions.Options.Options.Create(new AppDatabaseOptions { Path = relative }),
                new TestEnvironment(root),
                NullLogger<SqliteDatabaseInitializer>.Instance);

            await initializer.InitializeAsync();

            Assert.True(File.Exists(Path.Combine(root, relative)));
            await using (var connection = SqliteConnectionFactory.Create(new SqliteConnectionStringBuilder
            {
                DataSource = initializer.ResolvePath(),
                Mode = SqliteOpenMode.ReadWrite,
                Cache = SqliteCacheMode.Shared
            }.ToString()))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA journal_mode;";
                Assert.Equal("wal", (await command.ExecuteScalarAsync())?.ToString(), ignoreCase: true);
                command.CommandText = "PRAGMA busy_timeout;";
                Assert.Equal(5000L, Convert.ToInt64(await command.ExecuteScalarAsync()));
            }

            SqliteConnection.ClearAllPools();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "PremiereCalendar.Tests";
        public string WebRootPath { get; set; } = root;
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = root;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
