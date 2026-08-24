using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class SqliteHealthCheck(
    IOptions<AppDatabaseOptions> options,
    IWebHostEnvironment environment,
    DatabaseRecoveryState recoveryState) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var status = recoveryState.Snapshot;
        if (!status.IsHealthy)
        {
            return HealthCheckResult.Unhealthy(status.Message, data: new Dictionary<string, object>
            {
                ["databaseSchemaVersion"] = status.CurrentVersion,
                ["targetDatabaseSchemaVersion"] = status.TargetVersion
            });
        }

        try
        {
            await using var connection = DatabaseConnectionFactory.Create(options.Value, environment.ContentRootPath);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy("Database is ready.", new Dictionary<string, object>
            {
                ["databaseSchemaVersion"] = status.CurrentVersion
            });
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Database is unavailable.", ex);
        }
    }
}
