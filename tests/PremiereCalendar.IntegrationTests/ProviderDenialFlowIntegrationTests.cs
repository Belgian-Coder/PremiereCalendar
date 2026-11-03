using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed class ProviderDenialFlowIntegrationTests
{
    [Theory]
    [InlineData("trakt", 401)]
    [InlineData("trakt", 403)]
    [InlineData("simkl", 403)]
    public Task SQLite_DenialSurvivesRestartRecheckAndRecovery(string provider, int status)
        => RunAsync(false, provider, status);

    [ConfiguredPostgresTheory]
    [InlineData("trakt", 401)]
    [InlineData("trakt", 403)]
    [InlineData("simkl", 403)]
    public Task PostgreSQL_DenialSurvivesRestartRecheckAndRecovery(string provider, int status)
        => RunAsync(true, provider, status);

    private static async Task RunAsync(bool postgres, string provider, int denied)
    {
        var root = Path.Combine(Path.GetTempPath(), "premiere-provider-denial-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var environment = new TestEnvironment(root);
        var database = "premiere_provider_denial_" + Guid.NewGuid().ToString("N");
        NpgsqlConnection? admin = null;
        try
        {
            IOptions<AppDatabaseOptions> options;
            if (postgres)
            {
                var builder = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("PREMIERECALENDAR_TEST_POSTGRES")) { Database = "postgres" };
                var passwordFile = Environment.GetEnvironmentVariable("PREMIERECALENDAR_TEST_POSTGRES_PASSWORD_FILE");
                if (!string.IsNullOrWhiteSpace(passwordFile)) builder.Password = File.ReadAllText(passwordFile).TrimEnd('\r', '\n');
                admin = new NpgsqlConnection(builder.ConnectionString);
                await admin.OpenAsync();
                await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin)) await create.ExecuteNonQueryAsync();
                builder.Database = database;
                options = Microsoft.Extensions.Options.Options.Create(new AppDatabaseOptions { Provider = "PostgreSql", ConnectionString = builder.ConnectionString });
                var recovery = new DatabaseRecoveryState();
                await new PostgresDatabaseInitializer(options, environment, recovery, TimeProvider.System, NullLogger<PostgresDatabaseInitializer>.Instance, new PremiereTelemetry()).InitializeAsync();
                Assert.True(recovery.Snapshot.IsHealthy, recovery.Snapshot.Message);
            }
            else
            {
                TestSqliteDatabase.Initialize(root, "provider.db");
                options = Microsoft.Extensions.Options.Options.Create(new AppDatabaseOptions { Path = "provider.db" });
            }
            var clock = new TestClock { Now = DateTimeOffset.UtcNow };
            var start = DateOnly.FromDateTime(DateTime.UtcNow);
            var first = await ProbeAsync(false);
            Assert.InRange(first.Sends, 1, 2);
            AssertDenials(first.Error);
            var stored = await NewStore().GetAsync(provider, CancellationToken.None);
            Assert.Equal(denied, stored!.AccessDenialStatusCode);
            var restarted = await ProbeAsync(false);
            Assert.Equal(0, restarted.Sends);
            var restartFailures = AssertDenials(restarted.Error);
            Assert.Contains(restartFailures, failure => failure.Failure.Reason == "local_circuit");
            clock.Now = clock.Now.AddMinutes(16);
            var rechecked = await ProbeAsync(false);
            Assert.InRange(rechecked.Sends, 1, 2);
            AssertDenials(rechecked.Error);
            clock.Now = clock.Now.AddMinutes(16);
            var recovered = await ProbeAsync(true);
            Assert.Null(recovered.Error);
            Assert.Equal(2, recovered.Sends);
            stored = await NewStore().GetAsync(provider, CancellationToken.None);
            Assert.Equal(ProviderCircuitState.Closed, stored!.CircuitState);
            Assert.Null(stored.AccessDenialStatusCode);

            ProviderAdaptiveStateStore NewStore() => new(options, environment);
            async Task<(Exception? Error, int Sends)> ProbeAsync(bool succeed)
            {
                var policy = new AdaptiveProviderPolicy(NewStore(), clock, new PremiereTelemetry(),
                    Microsoft.Extensions.Options.Options.Create(new ProviderSchedulerOptions { RetryMaximumSeconds = 1 }));
                var transport = new Transport(succeed ? HttpStatusCode.OK : (HttpStatusCode)denied);
                using var http = new HttpClient(new AdaptiveProviderHandler(provider, 2, policy) { InnerHandler = transport })
                    { BaseAddress = new Uri("https://provider.example/") };
                using var memory = new MemoryCache(new MemoryCacheOptions());
                INamedPremiereDiscoveryProvider discovery = provider == "trakt"
                    ? new TraktDiscoveryProvider(new TraktClient(http, memory,
                        Microsoft.Extensions.Options.Options.Create(new TraktOptions { Enabled = true, ClientId = "fixture-client" })))
                    : new SimklCalendarDiscoveryProvider(new SimklClient(http,
                        Microsoft.Extensions.Options.Options.Create(new SimklOptions { Enabled = true, ClientId = "fixture-client" }), new StateStore(), cache: memory));
                var error = await Record.ExceptionAsync(() => discovery.GetCandidatesAsync(start, start, CancellationToken.None, true));
                // Body completion releases the slot synchronously and observes
                // persistence asynchronously. Wait for this exact version, not
                // just for the HTTP tasks or a wall-clock delay.
                var expected = Assert.Single(policy.GetSnapshots());
                var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
                while (true)
                {
                    var durable = await NewStore().GetAsync(provider, CancellationToken.None);
                    if (durable?.UpdatedUtc >= expected.UpdatedUtc) break;
                    Assert.True(DateTimeOffset.UtcNow < deadline,
                        $"Provider state {expected.UpdatedUtc:O} was not persisted; latest durable version is {durable?.UpdatedUtc:O}.");
                    await Task.Delay(10);
                }
                return (error, transport.Sends);
            }
            IReadOnlyList<ProviderRequestException> AssertDenials(Exception? error)
            {
                var aggregate = Assert.IsType<AggregateException>(error);
                var failures = aggregate.Flatten().InnerExceptions.Select(item => Assert.IsType<ProviderRequestException>(item)).ToArray();
                Assert.Equal(2, failures.Length);
                Assert.All(failures, failure =>
                {
                    Assert.Equal(denied, failure.Failure.StatusCode);
                    Assert.True(failure.Failure.RetryAtUtc > DateTimeOffset.UtcNow);
                });
                var notice = new SourceUnavailableNotice(provider, "authentication_denied", denied,
                    failures.Min(failure => failure.Failure.RetryAtUtc)!.Value);
                Assert.True(notice.IsOptionalAccessDenial);
                return failures;
            }
        }
        finally
        {
            if (admin is not null)
            {
                await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", admin);
                await drop.ExecuteNonQueryAsync();
                await admin.DisposeAsync();
            }
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class Transport(HttpStatusCode status) : HttpMessageHandler
    {
        public int Sends { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sends++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("[]", Encoding.UTF8, "application/json") });
        }
    }
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class StateStore : ISimklSyncStateStore
    {
        public Task<SimklSyncState> GetAsync(CancellationToken cancellationToken) => Task.FromResult(new SimklSyncState());
        public Task SaveAsync(SimklSyncState state, CancellationToken cancellationToken) => Task.CompletedTask;
    }
    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "PremiereCalendar.IntegrationTests";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
    private sealed class ConfiguredPostgresTheoryAttribute : TheoryAttribute
    {
        public ConfiguredPostgresTheoryAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PREMIERECALENDAR_TEST_POSTGRES")))
                Skip = "Configure PREMIERECALENDAR_TEST_POSTGRES for isolated PostgreSQL denial coverage.";
        }
    }
}
