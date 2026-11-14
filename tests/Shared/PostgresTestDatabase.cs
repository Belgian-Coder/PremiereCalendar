using System.Globalization;
using Microsoft.Extensions.Options;
using Npgsql;
using PremiereCalendar.Options;
#if POSTGRES_TEST_INPROCESS
using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.Services;
#endif

#if POSTGRES_TEST_INPROCESS
// One migrated template database per test process; every test database is a cheap clone of it (see PostgresTemplateDatabase).
[assembly: AssemblyFixture(typeof(PremiereCalendar.TestSupport.PostgresTemplateDatabase))]
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

    internal static readonly Lazy<ServerConfiguration> Server = new(ReadServerConfiguration);
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
        // A password file overrides any inline password (Npgsql rejects both together); without one the inline password stays.
        if (server.PasswordFile is not null) application.Remove("Password");
#if POSTGRES_TEST_INPROCESS
        // A migrated database is cloned from the template that the assembly fixture created once; creating it from
        // scratch and running every migration for each test is what dominated the set-up time before.
        var template = initializeSchema ? await PostgresTemplateDatabase.GetNameAsync() : null;
#else
        const string? template = null;
#endif
        await CreateDatabaseAsync(server, name, template);
        var database = new PostgresTestDatabase(name, application, server.PasswordFile, server.Password);
#if POSTGRES_TEST_INPROCESS
        if (template is not null)
        {
            // Replaying the migrations used to leave an open, authenticated connection in the application's pool, so the
            // first query of a test never paid for establishing one. Keep that: several tests give their first database
            // read a few hundred milliseconds.
            try { await using var warm = await database.OpenAsync(); }
            catch { await database.DisposeAsync(); throw; }
        }
#endif
        return database;
    }

    /// <summary>Wraps an existing database of the configured server (used for the template; nothing is created or dropped here).</summary>
    internal static PostgresTestDatabase Attach(ServerConfiguration server, string name)
    {
        var application = new NpgsqlConnectionStringBuilder(server.ConnectionString) { Database = name };
        if (server.PasswordFile is not null) application.Remove("Password");
        return new PostgresTestDatabase(name, application, server.PasswordFile, server.Password);
    }

    internal static async Task CreateDatabaseAsync(ServerConfiguration server, string name, string? template)
    {
        await using var admin = new NpgsqlConnection(server.AdminConnectionString);
        await admin.OpenAsync();
        for (var attempt = 1; ; attempt++)
        {
            await using var create = admin.CreateCommand();
            create.CommandText = template is null
                ? $"CREATE DATABASE \"{name}\""
                : $"CREATE DATABASE \"{name}\" TEMPLATE \"{template}\"";
            try { await create.ExecuteNonQueryAsync(); return; }
            // 55006: another session still touches the template (for instance a connection that is closing).
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.ObjectInUse && template is not null && attempt < 20)
            {
                await Task.Delay(50 * attempt);
            }
        }
    }

#if POSTGRES_TEST_INPROCESS
    /// <summary>
    /// Runs the application's migrations against this database and throws when they did not succeed. Pass the
    /// <see cref="DatabaseRecoveryState"/> that the code under test waits on to mark it healthy as well.
    /// </summary>
    public async Task InitializeSchemaAsync(DatabaseRecoveryState? recoveryState = null)
    {
        var recovery = recoveryState ?? new DatabaseRecoveryState();
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
        CloseConnections();
        await using var admin = new NpgsqlConnection(Server.Value.AdminConnectionString);
        await admin.OpenAsync();
        await using var drop = admin.CreateCommand();
        drop.CommandText = $"DROP DATABASE IF EXISTS \"{Name}\" WITH (FORCE)";
        await drop.ExecuteNonQueryAsync();
    }

    /// <summary>Closes every pooled connection to this database (data sources and plain Npgsql pools).</summary>
    internal void CloseConnections()
    {
#if POSTGRES_TEST_INPROCESS
        // ClearAllPools() does not touch NpgsqlDataSource pools; dispose this database's data sources explicitly.
        DatabaseConnectionFactory.Dispose(AppOptions);
#endif
        using var poolKey = new NpgsqlConnection(ConnectionString);
        NpgsqlConnection.ClearPool(poolKey);
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

    internal sealed record ServerConfiguration(string ConnectionString, string AdminConnectionString, string? PasswordFile, string? Password);
}

#if POSTGRES_TEST_INPROCESS
/// <summary>
/// Assembly fixture that owns the template database. xUnit creates it once, before the first test of the test
/// process: it migrates one database and keeps it (with no open connections) as the template that
/// <see cref="PostgresTestDatabase.CreateAsync"/> clones with <c>CREATE DATABASE ... TEMPLATE</c> (a file-level copy of a
/// few hundred kilobytes) instead of replaying every migration for each test. Tests that only need a plain empty
/// database, and runs without a configured server, never touch it; a configuration or connection failure is
/// reported by the first test that needs a migrated database.
/// </summary>
public sealed class PostgresTemplateDatabase : IAsyncLifetime
{
    private static Task<string> s_name = Task.FromException<string>(new InvalidOperationException("The PostgreSQL template database was not created."));
    private static string? s_created;

    internal static Task<string> GetNameAsync() => s_name;

    public async ValueTask InitializeAsync()
    {
        // Without a configured server nothing is created: PostgresTestDatabase.CreateAsync fails first, with its descriptive message.
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PostgresTestDatabase.ServerVariable))) return;
        try
        {
            var server = PostgresTestDatabase.Server.Value;
            var name = $"premierecalendar_template_{Guid.NewGuid():N}";
            await PostgresTestDatabase.CreateDatabaseAsync(server, name, template: null);
            s_created = name;
            var database = PostgresTestDatabase.Attach(server, name);
            try { await database.InitializeSchemaAsync(); }
            finally { database.CloseConnections(); }
            s_name = Task.FromResult(name);
        }
        catch (Exception ex)
        {
            s_name = Task.FromException<string>(ex);
        }
    }

    public async ValueTask DisposeAsync()
    {
        var name = Interlocked.Exchange(ref s_created, null);
        if (name is null) return;
        await using var admin = new NpgsqlConnection(PostgresTestDatabase.Server.Value.AdminConnectionString);
        await admin.OpenAsync();
        await using var drop = admin.CreateCommand();
        drop.CommandText = $"DROP DATABASE IF EXISTS \"{name}\" WITH (FORCE)";
        await drop.ExecuteNonQueryAsync();
    }
}
#endif
