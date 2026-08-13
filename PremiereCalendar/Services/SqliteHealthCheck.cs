using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class SqliteHealthCheck(
    SqliteDatabaseInitializer initializer,
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

        var path = initializer.ResolvePath();
        try
        {
            var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared };
            await using var connection = SqliteConnectionFactory.Create(builder.ToString());
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy("SQLite is ready.", new Dictionary<string, object>
            {
                ["databaseSchemaVersion"] = status.CurrentVersion
            });
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            return HealthCheckResult.Unhealthy("SQLite is unavailable.", ex);
        }
    }
}
