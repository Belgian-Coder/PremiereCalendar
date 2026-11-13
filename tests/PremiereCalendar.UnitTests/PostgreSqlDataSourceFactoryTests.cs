using System.Net;
using System.Net.Sockets;
using System.Text;
using Npgsql;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class PostgreSqlDataSourceFactoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "premiere-pg-factory-tests", Guid.NewGuid().ToString("N"));

    public PostgreSqlDataSourceFactoryTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void ConnectionsApplyTheDocumentedDefaultsAndNeverStoreThePasswordInTheConnectionString()
    {
        var passwordFile = Path.Combine(_root, "password");
        File.WriteAllText(passwordFile, "initial-secret\n");
        var options = Options("Host=127.0.0.1;Port=1;Database=premiere;Username=app;Application Name=ignored", passwordFile);

        using var connection = Assert.IsType<NpgsqlConnection>(DatabaseConnectionFactory.Create(options));
        var settings = new NpgsqlConnectionStringBuilder(connection.ConnectionString);

        Assert.True(settings.Pooling);
        Assert.Equal(15, settings.Timeout);
        Assert.Equal(30, settings.CommandTimeout);
        Assert.Equal("PremiereCalendar", settings.ApplicationName);
        Assert.Equal("premiere", settings.Database);
        Assert.Null(settings.Password);
    }

    [Fact]
    public void RepeatedCallsShareOneDataSourceAndReturnIndependentConnections()
    {
        var options = Options("Host=127.0.0.1;Port=1;Database=shared;Username=app");

        using var first = DatabaseConnectionFactory.Create(options);
        using var second = DatabaseConnectionFactory.Create(options);

        Assert.NotSame(first, second);
        Assert.Equal(first.ConnectionString, second.ConnectionString);
        // A different configuration must not reuse the data source (and therefore the pool).
        using var other = DatabaseConnectionFactory.Create(Options("Host=127.0.0.1;Port=1;Database=other;Username=app"));
        Assert.NotEqual(first.ConnectionString, other.ConnectionString);
    }

    [Fact]
    public void BulkCopyConnectionsDisableTheCommandTimeoutWithoutAffectingRegularOnes()
    {
        var options = Options("Host=127.0.0.1;Port=1;Database=bulk;Username=app");

        using var regular = DatabaseConnectionFactory.Create(options);
        using var bulk = DatabaseConnectionFactory.Create(options, commandTimeoutSeconds: 0);

        Assert.Equal(30, new NpgsqlConnectionStringBuilder(regular.ConnectionString).CommandTimeout);
        Assert.Equal(0, new NpgsqlConnectionStringBuilder(bulk.ConnectionString).CommandTimeout);
        using var command = bulk.CreateCommand();
        Assert.Equal(0, command.CommandTimeout);
    }

    [Fact]
    public async Task PasswordFileIsReadWhenAPhysicalConnectionIsEstablishedSoRotationIsPickedUp()
    {
        var passwordFile = Path.Combine(_root, "rotating-password");
        await File.WriteAllTextAsync(passwordFile, "first-secret\r\n");
        using var server = new PasswordCapturingServer();
        var options = Options(
            $"Host=127.0.0.1;Port={server.Port};Database=premiere;Username=app;SSL Mode=Disable;GSS Encryption Mode=Disable;Timeout=5",
            passwordFile);

        await ExpectAuthenticationFailureAsync(options);
        await File.WriteAllTextAsync(passwordFile, "second-secret\n");
        await ExpectAuthenticationFailureAsync(options);

        Assert.Equal(["first-secret", "second-secret"], server.Passwords);
    }

    [Fact]
    public async Task APasswordFileOverridesAnInlinePasswordInsteadOfFailingToBuildTheDataSource()
    {
        var passwordFile = Path.Combine(_root, "file-password");
        await File.WriteAllTextAsync(passwordFile, "file-secret\n");
        using var server = new PasswordCapturingServer();
        var options = Options(
            $"Host=127.0.0.1;Port={server.Port};Database=premiere;Username=app;Password=inline-secret;SSL Mode=Disable;GSS Encryption Mode=Disable;Timeout=5",
            passwordFile);

        // Npgsql rejects a password provider combined with an inline password, so the factory must drop the latter.
        await ExpectAuthenticationFailureAsync(options);

        Assert.Equal(["file-secret"], server.Passwords);
    }

    [Fact]
    public void AMissingPasswordFileFailsFastWithTheRawIoExceptionWhenTheConnectionIsCreated()
    {
        var options = Options("Host=127.0.0.1;Port=1;Database=premiere;Username=app;Timeout=2",
            Path.Combine(_root, "absent-password"));

        var failure = Assert.ThrowsAny<IOException>(() => DatabaseConnectionFactory.Create(options));

        Assert.IsType<FileNotFoundException>(failure);
        // The initializer retries transient errors forever; a missing secret must stop the host instead.
        Assert.False(DatabaseConnectionFactory.IsTransient(failure));
    }

    [Fact]
    public void AMissingPasswordDirectoryFailsFastAndTheFailureIsNotCached()
    {
        var directory = Path.Combine(_root, "later");
        var passwordFile = Path.Combine(directory, "password");
        var options = Options("Host=127.0.0.1;Port=1;Database=premiere;Username=app;Timeout=2", passwordFile);

        var failure = Assert.ThrowsAny<IOException>(() => DatabaseConnectionFactory.Create(options));
        Assert.IsType<DirectoryNotFoundException>(failure);
        Assert.False(DatabaseConnectionFactory.IsTransient(failure));

        // Mounting the secret later recovers without restarting the process.
        Directory.CreateDirectory(directory);
        File.WriteAllText(passwordFile, "now-present");
        using var connection = DatabaseConnectionFactory.Create(options);
        Assert.Equal("premiere", new NpgsqlConnectionStringBuilder(connection.ConnectionString).Database);
    }

    [Fact]
    public async Task APasswordFileThatDisappearsAfterStartupIsPermanentNotTransientWhenOpeningAConnection()
    {
        var passwordFile = Path.Combine(_root, "vanishing-password");
        await File.WriteAllTextAsync(passwordFile, "secret");
        using var server = new PasswordCapturingServer();
        var options = Options(
            $"Host=127.0.0.1;Port={server.Port};Database=premiere;Username=app;SSL Mode=Disable;GSS Encryption Mode=Disable;Timeout=5",
            passwordFile);
        await using var connection = DatabaseConnectionFactory.Create(options);
        File.Delete(passwordFile);

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => connection.OpenAsync());

        Assert.False(DatabaseConnectionFactory.IsTransient(failure), failure.ToString());
    }

    [Fact]
    public void OnlyRealPasswordFileProblemsAreExcludedFromTransientClassification()
    {
        Assert.False(DatabaseConnectionFactory.IsTransient(new NpgsqlException("x", new FileNotFoundException())));
        Assert.False(DatabaseConnectionFactory.IsTransient(new NpgsqlException("x", new DirectoryNotFoundException())));
        Assert.False(DatabaseConnectionFactory.IsTransient(new NpgsqlException("x", new UnauthorizedAccessException())));
        Assert.True(DatabaseConnectionFactory.IsTransient(new NpgsqlException("x", new IOException("connection reset"))));
        Assert.True(DatabaseConnectionFactory.IsTransient(new NpgsqlException("x", new System.Net.Sockets.SocketException())));
    }

    [Fact]
    public async Task ConcurrentFirstCallsBuildExactlyOneDataSource()
    {
        var options = Options($"Host=127.0.0.1;Port=1;Database=concurrent_{Guid.NewGuid():N};Username=app");
        var before = DatabaseConnectionFactory.BuiltFor(options);
        using var gate = new ManualResetEventSlim();

        var tasks = Enumerable.Range(0, 32).Select(_ => Task.Run(() =>
        {
            gate.Wait();
            using var connection = DatabaseConnectionFactory.Create(options);
        })).ToArray();
        gate.Set();
        await Task.WhenAll(tasks);

        Assert.Equal(1, DatabaseConnectionFactory.BuiltFor(options) - before);
        DatabaseConnectionFactory.Dispose(options);
    }

    [Fact]
    public void DisposingOneConfigurationRemovesOnlyItsDataSources()
    {
        var disposed = Options($"Host=127.0.0.1;Port=1;Database=dispose_{Guid.NewGuid():N};Username=app");
        var kept = Options($"Host=127.0.0.1;Port=1;Database=keep_{Guid.NewGuid():N};Username=app");
        using (DatabaseConnectionFactory.Create(disposed)) { }
        using (DatabaseConnectionFactory.Create(disposed, commandTimeoutSeconds: 0)) { }
        using (DatabaseConnectionFactory.Create(kept)) { }
        var keptBuilt = DatabaseConnectionFactory.BuiltFor(kept);
        var disposedBuilt = DatabaseConnectionFactory.BuiltFor(disposed);
        Assert.Equal(1, keptBuilt);
        Assert.Equal(2, disposedBuilt);

        DatabaseConnectionFactory.Dispose(disposed);
        using (DatabaseConnectionFactory.Create(kept)) { }
        Assert.Equal(keptBuilt, DatabaseConnectionFactory.BuiltFor(kept));

        // Both timeout variants of the disposed configuration were removed, so each is rebuilt on next use.
        using (DatabaseConnectionFactory.Create(disposed)) { }
        using (DatabaseConnectionFactory.Create(disposed, commandTimeoutSeconds: 0)) { }
        Assert.Equal(disposedBuilt + 2, DatabaseConnectionFactory.BuiltFor(disposed));
        DatabaseConnectionFactory.Dispose(disposed);
        DatabaseConnectionFactory.Dispose(kept);
        // Disposing something that is not cached is a no-op, and so is disposing twice.
        DatabaseConnectionFactory.Dispose(kept);
    }

    private static async Task ExpectAuthenticationFailureAsync(AppDatabaseOptions options)
    {
        await using var connection = DatabaseConnectionFactory.Create(options);
        var failure = await Assert.ThrowsAsync<PostgresException>(() => connection.OpenAsync());
        Assert.Equal("28P01", failure.SqlState);
    }

    private static AppDatabaseOptions Options(string connectionString, string? passwordFile = null) => new()
    {
        ConnectionString = connectionString,
        PasswordFile = passwordFile ?? ""
    };

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Minimal PostgreSQL wire-protocol peer: requests a cleartext password, records it, then rejects it.</summary>
    private sealed class PasswordCapturingServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly List<string> _passwords = [];
        private readonly Task _loop;

        public PasswordCapturingServer()
        {
            _listener.Start();
            _loop = Task.Run(AcceptLoopAsync);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public IReadOnlyList<string> Passwords
        {
            get { lock (_passwords) return [.. _passwords]; }
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = Task.Run(() => HandleAsync(client));
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }

        private async Task HandleAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var length = await ReadInt32Async(stream);
                    var startup = await ReadExactAsync(stream, length - 4);
                    if (startup.Length >= 4 && System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(startup) is 80877103 or 80877104)
                    {
                        await stream.WriteAsync("N"u8.ToArray());
                        length = await ReadInt32Async(stream);
                        await ReadExactAsync(stream, length - 4);
                    }
                    // AuthenticationCleartextPassword
                    await stream.WriteAsync(new byte[] { (byte)'R', 0, 0, 0, 8, 0, 0, 0, 3 });
                    var type = new byte[1];
                    await ReadExactAsync(stream, type);
                    var passwordLength = await ReadInt32Async(stream);
                    var payload = await ReadExactAsync(stream, passwordLength - 4);
                    lock (_passwords) _passwords.Add(Encoding.UTF8.GetString(payload, 0, payload.Length - 1));
                    var fields = Encoding.UTF8.GetBytes("SFATAL\0VFATAL\0C28P01\0Mpassword authentication failed\0\0");
                    var error = new byte[5 + fields.Length];
                    error[0] = (byte)'E';
                    System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(error.AsSpan(1), fields.Length + 4);
                    fields.CopyTo(error, 5);
                    await stream.WriteAsync(error);
                    await stream.FlushAsync();
                }
                catch (Exception) when (_stop.IsCancellationRequested) { }
                catch (IOException) { }
            }
        }

        private static async Task<int> ReadInt32Async(Stream stream)
            => System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(await ReadExactAsync(stream, 4));

        private static async Task<byte[]> ReadExactAsync(Stream stream, int count)
        {
            var buffer = new byte[count];
            await ReadExactAsync(stream, buffer);
            return buffer;
        }

        private static async Task ReadExactAsync(Stream stream, byte[] buffer)
            => await stream.ReadExactlyAsync(buffer);

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            try { _loop.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
            _stop.Dispose();
        }
    }
}
