using System.Text.Json;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public static class DatabaseCommandLine
{
    public static bool IsDatabaseCommand(string[] args) =>
        args.Length > 0 && string.Equals(args[0], "database", StringComparison.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(
        string[] args,
        IConfiguration configuration,
        IWebHostEnvironment environment,
        CancellationToken cancellationToken = default)
    {
        using var loggerFactory = LoggerFactory.Create(logging => logging.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
            options.UseUtcTimestamp = true;
        }));
        var databaseOptions = configuration.GetSection("AppDatabase").Get<AppDatabaseOptions>() ?? new AppDatabaseOptions();
        var initializer = new SqliteDatabaseInitializer(
            Microsoft.Extensions.Options.Options.Create(databaseOptions),
            environment,
            new DatabaseRecoveryState(),
            TimeProvider.System,
            loggerFactory.CreateLogger<SqliteDatabaseInitializer>());

        try
        {
            if (args.Length == 2 && string.Equals(args[1], "verify", StringComparison.OrdinalIgnoreCase))
            {
                var result = await initializer.VerifyAsync(null, cancellationToken);
                Console.WriteLine(JsonSerializer.Serialize(result));
                return result.IsHealthy ? 0 : 2;
            }

            if (args.Length == 4
                && string.Equals(args[1], "snapshot", StringComparison.OrdinalIgnoreCase)
                && string.Equals(args[2], "--output", StringComparison.OrdinalIgnoreCase))
            {
                if (!Path.IsPathFullyQualified(args[3]))
                {
                    throw new ArgumentException("--output must be an absolute path.");
                }

                var result = await initializer.CreateVerifiedSnapshotAsync(args[3], cancellationToken);
                Console.WriteLine(JsonSerializer.Serialize(result));
                return result.IsHealthy ? 0 : 2;
            }

            if (args.Length == 4
                && string.Equals(args[1], "restore", StringComparison.OrdinalIgnoreCase)
                && string.Equals(args[2], "--backup", StringComparison.OrdinalIgnoreCase))
            {
                if (!Path.IsPathFullyQualified(args[3]))
                {
                    throw new ArgumentException("--backup must be an absolute path.");
                }

                EnsureDatabaseIsOffline(initializer.ResolvePath());
                await initializer.RestoreAsync(args[3], cancellationToken);
                var result = await initializer.VerifyAsync(null, cancellationToken);
                Console.WriteLine(JsonSerializer.Serialize(result));
                return result.IsHealthy ? 0 : 2;
            }

            Console.Error.WriteLine("Usage: PremiereCalendar.exe database verify | database snapshot --output <absolute-path> | database restore --backup <absolute-path>");
            return 64;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            Console.Error.WriteLine($"Database command failed: {exception.Message}");
            return 2;
        }
    }

    private static void EnsureDatabaseIsOffline(string databasePath)
    {
        if (!File.Exists(databasePath))
        {
            return;
        }

        try
        {
            using var stream = new FileStream(databasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException(
                "The database is in use. Stop the PremiereCalendar Windows Service before restoring.",
                exception);
        }
    }
}
