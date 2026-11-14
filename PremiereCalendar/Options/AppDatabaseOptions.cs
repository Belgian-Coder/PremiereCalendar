using Microsoft.Extensions.Options;

namespace PremiereCalendar.Options;

/// <summary>PostgreSQL is the only supported database; there is no provider switch.</summary>
public sealed class AppDatabaseOptions
{
    public string ConnectionString { get; set; } = "";
    /// <summary>
    /// File that holds the PostgreSQL password. A relative path is resolved against the application's content root (see
    /// <see cref="AppDatabasePaths.ResolvePasswordFile"/>), never against the working directory.
    /// </summary>
    public string PasswordFile { get; set; } = "";
    public int RecoveryRetrySeconds { get; set; } = 5;
    public int RecoveryMaximumRetrySeconds { get; set; } = 60;
    public int RecoveryAttemptTimeoutSeconds { get; set; } = 60;
}

/// <summary>Resolution of the password file path shared by the host and the database command line.</summary>
internal static class AppDatabasePaths
{
    /// <summary>
    /// A relative password file (for example the development default <c>../.secrets/dev-postgres-password</c>) resolves
    /// against the content root, like the other relative paths in the configuration, so it does not depend on the folder
    /// the process was started from. Absolute paths are kept; a blank value means no password file.
    /// </summary>
    public static string ResolvePasswordFile(string? passwordFile, string? contentRootPath)
    {
        if (string.IsNullOrWhiteSpace(passwordFile)) return "";
        var trimmed = passwordFile.Trim();
        return string.IsNullOrWhiteSpace(contentRootPath)
            ? trimmed
            : Path.GetFullPath(trimmed, Path.GetFullPath(contentRootPath));
    }
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
                + "Remove AppDatabase:Path and configure AppDatabase:ConnectionString. Migrate any old SQLite data with v1.2.19, the last release that supports it (database migrate-postgres), before upgrading.";
        }

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            return "AppDatabase:ConnectionString is required for PostgreSQL";
        }

        return null;
    }
}
