using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

/// <summary>Creates the database directory and applies process-wide SQLite durability/concurrency defaults.</summary>
public sealed class SqliteDatabaseInitializer(
    IOptions<AppDatabaseOptions> options,
    IWebHostEnvironment environment,
    ILogger<SqliteDatabaseInitializer> logger)
{
    public string ResolvePath() => SqliteDatabasePath.Resolve(options.Value.Path, environment.ContentRootPath);

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var path = ResolvePath();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        };

        await using var connection = SqliteConnectionFactory.Create(builder.ToString());
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL; PRAGMA busy_timeout = 5000;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        logger.LogInformation("SQLite initialized at {DatabasePath} with WAL journaling.", path);
    }
}

internal static class SqliteDatabasePath
{
    public static string Resolve(string? configuredPath, string contentRootPath)
    {
        var path = string.IsNullOrWhiteSpace(configuredPath)
            ? "App_Data/data/premiere-calendar.db"
            : configuredPath.Trim();
        return Path.GetFullPath(Path.IsPathFullyQualified(path) ? path : Path.Combine(contentRootPath, path));
    }
}

public sealed class SqliteDatabaseInitializerHostedService(SqliteDatabaseInitializer initializer) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => initializer.InitializeAsync(cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
