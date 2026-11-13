using System.Globalization;
using Microsoft.Extensions.Options;
using Npgsql;
using PremiereCalendar.Options;
#if POSTGRES_TEST_INPROCESS
using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.Services;
#endif

namespace PremiereCalendar.TestSupport;

/// <summary>
/// A randomly named PostgreSQL database that belongs to exactly one test (or test class). Every test in this
/// repository runs against PostgreSQL: the server comes from <c>PREMIERECALENDAR_TEST_POSTGRES</c> (an Npgsql
/// connection string; its database is only used to issue CREATE/DROP DATABASE) and an optional
/// <c>PREMIERECALENDAR_TEST_POSTGRES_PASSWORD_FILE</c>. Use a throwaway server: <c>eng/Test-WithPostgres.ps1</c>
/// starts one. A missing configuration fails the test with a clear message; it is never skipped.
/// </summary>
internal sealed class PostgresTestDatabase : IDisposable, IAsyncDisposable
{
    public const string ServerVariable = "PREMIERECALENDAR_TEST_POSTGRES";
    public const string PasswordFileVariable = "PREMIERECALENDAR_TEST_POSTGRES_PASSWORD_FILE";

    private static readonly Lazy<ServerConfiguration> Server = new(ReadServerConfiguration);
    private int _disposed;

    private PostgresTestDatabase(string name, NpgsqlConnectionStringBuilder application, string? passwordFile, string? password)
    {
        Name = name;
        PasswordFile = passwordFile;
        var inline = new NpgsqlConnectionStringBuilder(application.ConnectionString);
        if (password is not null) inline.Password = password;
        ConnectionString = inline.ConnectionString;
        // Production style: no inline password, the password file is read by the data source.
        AppOptions = new AppDatabaseOptions
        {
            ConnectionString = application.ConnectionString,
            PasswordFile = passwordFile ?? ""
        };
        Options = Microsoft.Extensions.Options.Options.Create(AppOptions);
    }

    /// <summary>Database name, unique per instance.</summary>
    public string Name { get; }

    /// <summary>Connection string with the password inline, for tests that open raw <see cref="NpgsqlConnection"/>s.</summary>
    public string ConnectionString { get; }

    public string? PasswordFile { get; }

    /// <summary>Application options pointing at this database (connection string plus password file).</summary>
    public AppDatabaseOptions AppOptions { get; }

    public IOptions<AppDatabaseOptions> Options { get; }

    /// <summary>Configuration keys that make a hosted application use this database.</summary>
    public IReadOnlyDictionary<string, string?> AppSettings => new Dictionary<string, string?>
    {
        ["AppDatabase:ConnectionString"] = AppOptions.ConnectionString,
        ["AppDatabase:PasswordFile"] = PasswordFile ?? ""
    };

    /// <summary>Environment variables that make a launched application process use this database.</summary>
    public IReadOnlyDictionary<string, string> AppEnvironment
    {
        get
        {
            var values = new Dictionary<string, string> { ["AppDatabase__ConnectionString"] = AppOptions.ConnectionString };
            values["AppDatabase__PasswordFile"] = PasswordFile ?? "";
            return values;
        }
    }

    public static PostgresTestDatabase Create(bool initializeSchema = true)
        => CreateAsync(initializeSchema).GetAwaiter().GetResult();

    /// <param name="initializeSchema">Runs the application's migrations. Pass false for tests that exercise initialization itself.</param>
    public static async Task<PostgresTestDatabase> CreateAsync(bool initializeSchema = true)
    {
        var server = Server.Value;
        var name = $"premierecalendar_test_{Guid.NewGuid():N}";
        var application = new NpgsqlConnectionStringBuilder(server.ConnectionString) { Database = name };
        application.Remove("Password");
        await using (var admin = new NpgsqlConnection(server.AdminConnectionString))
        {
            await admin.OpenAsync();
            await using var create = admin.CreateCommand();
            create.CommandText = $"CREATE DATABASE \"{name}\"";
            await create.ExecuteNonQueryAsync();
        }

        var database = new PostgresTestDatabase(name, application, server.PasswordFile, server.Password);
#if POSTGRES_TEST_INPROCESS
        if (initializeSchema)
        {
            try { await database.InitializeSchemaAsync(); }
            catch { await database.DisposeAsync(); throw; }
        }
#endif
        return database;
    }

#if POSTGRES_TEST_INPROCESS
    /// <summary>Runs the application's migrations against this database and asserts that they succeeded.</summary>
    public async Task InitializeSchemaAsync()
    {
        var recovery = new DatabaseRecoveryState();
        await new PostgresDatabaseInitializer(Options, recovery, TimeProvider.System,
            NullLogger<PostgresDatabaseInitializer>.Instance, new PremiereTelemetry()).InitializeAsync();
        if (!recovery.Snapshot.IsHealthy) throw new InvalidOperationException(recovery.Snapshot.Message);
    }

    /// <summary>Opens a connection through the application's data source for this database.</summary>
    public async Task<NpgsqlConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = DatabaseConnectionFactory.Create(AppOptions);
        try { await connection.OpenAsync(cancellationToken); return connection; }
        catch { await connection.DisposeAsync(); throw; }
    }
#endif

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
#if POSTGRES_TEST_INPROCESS
        // ClearAllPools() does not touch NpgsqlDataSource pools; dispose this database's data sources explicitly.
        DatabaseConnectionFactory.Dispose(AppOptions);
#endif
        NpgsqlConnection.ClearPool(new NpgsqlConnection(ConnectionString));
        await using var admin = new NpgsqlConnection(Server.Value.AdminConnectionString);
        await admin.OpenAsync();
        await using var drop = admin.CreateCommand();
        drop.CommandText = $"DROP DATABASE IF EXISTS \"{Name}\" WITH (FORCE)";
        await drop.ExecuteNonQueryAsync();
    }

    private static ServerConfiguration ReadServerConfiguration()
    {
        var connectionString = Environment.GetEnvironmentVariable(ServerVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"{ServerVariable} is not set. All PremiereCalendar tests run against PostgreSQL and never skip. " +
                $"Start a throwaway server and set {ServerVariable} (and {PasswordFileVariable} when it needs a password), " +
                "or run eng/Test-WithPostgres.ps1. See docs/Testing.md.");
        }

        var passwordFile = Environment.GetEnvironmentVariable(PasswordFileVariable);
        passwordFile = string.IsNullOrWhiteSpace(passwordFile) ? null : Path.GetFullPath(passwordFile.Trim());
        var password = passwordFile is null ? null : File.ReadAllText(passwordFile).TrimEnd('\r', '\n');
        var admin = new NpgsqlConnectionStringBuilder(connectionString) { Database = "postgres" };
        if (password is not null) admin.Password = password;
        return new ServerConfiguration(connectionString, admin.ConnectionString, passwordFile, password);
    }

    private sealed record ServerConfiguration(string ConnectionString, string AdminConnectionString, string? PasswordFile, string? Password);
}
