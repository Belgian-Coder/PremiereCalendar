using System.Text.Json;
using Microsoft.Extensions.Options;
using Npgsql;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public static class DatabaseCommandLine
{
    public static bool IsDatabaseCommand(string[] args) =>
        args.Length > 0 && string.Equals(args[0], "database", StringComparison.OrdinalIgnoreCase);

    public static async Task<int> RunAsync(
        string[] args,
        IConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        using var loggerFactory = LoggerFactory.Create(logging => logging.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
            options.UseUtcTimestamp = true;
        }));
        var databaseOptions = configuration.GetSection("AppDatabase").Get<AppDatabaseOptions>() ?? new AppDatabaseOptions();
        var configurationError = AppDatabaseOptionsValidator.Validate(configuration, databaseOptions);
        if (configurationError is not null)
        {
            Console.Error.WriteLine($"Database command failed: {configurationError}");
            return 2;
        }

        try
        {
            if (args.Length == 2 && string.Equals(args[1], "prepare-legacy-checkpoints", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var maintenance = new ProviderWorkCheckpointMaintenance(
                        Microsoft.Extensions.Options.Options.Create(databaseOptions), TimeProvider.System);
                    var result = await maintenance.PrepareLegacyCheckpointsAsync(cancellationToken);
                    Console.WriteLine(JsonSerializer.Serialize(result));
                    return 0;
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    // The rollback command must not print database exception text,
                    // connection strings, checkpoint contents or credentials.
                    Console.Error.WriteLine("Legacy checkpoint preparation failed. Keep the application stopped and verify its database and checkpoints before retrying.");
                    return 2;
                }
            }

            if (args.Length == 2 && string.Equals(args[1], "verify", StringComparison.OrdinalIgnoreCase))
            {
                var postgres = new PostgresDatabaseInitializer(
                    Microsoft.Extensions.Options.Options.Create(databaseOptions), new DatabaseRecoveryState(),
                    TimeProvider.System, loggerFactory.CreateLogger<PostgresDatabaseInitializer>(), new PremiereTelemetry());
                var result = await postgres.VerifyAsync(cancellationToken);
                Console.WriteLine(JsonSerializer.Serialize(result));
                return result.IsHealthy ? 0 : 2;
            }

            Console.Error.WriteLine("Usage: dotnet PremiereCalendar.dll database verify | database prepare-legacy-checkpoints");
            return 64;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NpgsqlException)
        {
            Console.Error.WriteLine($"Database command failed: {exception.Message}");
            return 2;
        }
        finally
        {
            DatabaseConnectionFactory.Dispose(databaseOptions);
        }
    }
}
