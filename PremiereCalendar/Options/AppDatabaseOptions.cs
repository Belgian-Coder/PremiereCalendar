using Microsoft.Extensions.Options;

namespace PremiereCalendar.Options;

/// <summary>PostgreSQL is the only supported database; there is no provider switch.</summary>
public sealed class AppDatabaseOptions
{
    public string ConnectionString { get; set; } = "";
    public string PasswordFile { get; set; } = "";
    public int RecoveryRetrySeconds { get; set; } = 5;
    public int RecoveryMaximumRetrySeconds { get; set; } = 60;
    public int RecoveryAttemptTimeoutSeconds { get; set; } = 60;
}

/// <summary>
/// Rejects configuration left over from the removed SQLite provider so a misconfigured container fails fast
/// instead of silently starting against an empty PostgreSQL database.
/// </summary>
public sealed class AppDatabaseOptionsValidator(IConfiguration configuration) : IValidateOptions<AppDatabaseOptions>
{
    public ValidateOptionsResult Validate(string? name, AppDatabaseOptions options)
    {
        var error = Validate(configuration, options);
        return error is null ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(error);
    }

    internal static string? Validate(IConfiguration configuration, AppDatabaseOptions options)
    {
        var provider = configuration["AppDatabase:Provider"];
        if (!string.IsNullOrWhiteSpace(provider) && !string.Equals(provider.Trim(), "PostgreSql", StringComparison.OrdinalIgnoreCase))
        {
            return $"AppDatabase:Provider '{provider}' is no longer supported: SQLite was removed and PostgreSQL is the only database. "
                + "Remove AppDatabase:Provider or set it to PostgreSql, and configure AppDatabase:ConnectionString.";
        }

        if (!string.IsNullOrWhiteSpace(configuration["AppDatabase:Path"]))
        {
            return "AppDatabase:Path configured a SQLite file, which is no longer supported: SQLite was removed and PostgreSQL is the only database. "
                + "Remove AppDatabase:Path and configure AppDatabase:ConnectionString. Migrate any old SQLite data with the last release that supports it before upgrading.";
        }

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return "AppDatabase:ConnectionString is required for PostgreSQL";
        }

        return null;
    }
}
