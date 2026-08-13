using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class ProviderWorkSchedulerTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "premiere-scheduler-tests", Guid.NewGuid().ToString("N"));
    private ProviderWorkStore _store = null!;
    private ProviderWorkScheduler _scheduler = null!;
    private AdaptiveProviderPolicy _providerPolicy = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        var environment = new TestEnvironment(_root);
        var database = Microsoft.Extensions.Options.Options.Create(new AppDatabaseOptions { Path = "scheduler.db" });
        await new SqliteDatabaseInitializer(database, environment, NullLogger<SqliteDatabaseInitializer>.Instance).InitializeAsync();
        _store = new ProviderWorkStore(database, environment);
        _scheduler = new ProviderWorkScheduler(
            _store,
            TimeProvider.System,
            Microsoft.Extensions.Options.Options.Create(new ProviderSchedulerOptions { LeaseSeconds = 30, MaximumAttempts = 3 }));
        _providerPolicy = new AdaptiveProviderPolicy(new ProviderAdaptiveStateStore(database, environment), TimeProvider.System, new PremiereTelemetry());
    }

    [Fact]
    public async Task DuplicateCallersAttachToOneActiveJob()
    {
        var request = new ProviderWorkRequest(ProviderWorkKind.CalendarForeground, "same-week", ProviderWorkPriority.Foreground, "{}");
        var first = await _scheduler.EnqueueAsync(request);
        var second = await _scheduler.EnqueueAsync(request);
        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.JobId, second.JobId);
    }

    [Fact]
    public async Task ClaimsForegroundBeforeMaintenanceAndCheckpointsWithoutExpiringLease()
    {
        await _scheduler.EnqueueAsync(new ProviderWorkRequest(ProviderWorkKind.ImdbDatasetRefresh, "maintenance", ProviderWorkPriority.Maintenance, "{}"));
        var foreground = await _scheduler.EnqueueAsync(new ProviderWorkRequest(ProviderWorkKind.CalendarForeground, "foreground", ProviderWorkPriority.Foreground, "{}"));

        var claimed = await _scheduler.ClaimNextAsync(false, "test", CancellationToken.None);
        Assert.Equal(foreground.JobId, claimed!.JobId);
        await _scheduler.PublishAsync(claimed, new ProviderWorkProgress(claimed.JobId, ProviderWorkState.Running, "page 2", 2, 5), CancellationToken.None);
        var persisted = await _scheduler.GetAsync(claimed.JobId);
        Assert.Contains("page 2", persisted!.CheckpointJson);
    }

    [Fact]
    public async Task ExpiredLeaseIsRequeuedWithoutConsumingAttempt()
    {
        var handle = await _scheduler.EnqueueAsync(new ProviderWorkRequest(ProviderWorkKind.CalendarWarmup, "warmup", ProviderWorkPriority.Warmup, "{}"));
        var now = DateTimeOffset.UtcNow.AddMinutes(-5);
        var claimed = await _store.ClaimNextAsync(false, "abandoned", now, TimeSpan.FromSeconds(30), CancellationToken.None);
        Assert.NotNull(claimed);
        await _store.RecoverExpiredLeasesAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        var recovered = await _scheduler.GetAsync(handle.JobId);
        Assert.Equal(ProviderWorkState.Queued, recovered!.State);
        Assert.Equal(0, recovered.AttemptCount);
    }

    [Fact]
    public async Task TransientFailureRetriesAndIncrementsAttempt()
    {
        var handle = await _scheduler.EnqueueAsync(new ProviderWorkRequest(ProviderWorkKind.ProviderDeltaSync, "delta", ProviderWorkPriority.Maintenance, "{}"));
        var claimed = await _scheduler.ClaimNextAsync(false, "test", CancellationToken.None);
        await _scheduler.FailAsync(claimed!, new HttpRequestException("secret URL should not persist"), CancellationToken.None);
        var retried = await _scheduler.GetAsync(handle.JobId);
        Assert.Equal(ProviderWorkState.RetryWaiting, retried!.State);
        Assert.Equal(1, retried.AttemptCount);
        Assert.Equal("A provider request failed.", retried.LastError);
    }

    [Fact]
    public async Task ThrottlingHalvesConcurrencyAndHonorsRetryAfter()
    {
        await using var lease = await _providerPolicy.AcquireAsync("throttled-provider", 4, CancellationToken.None);
        await lease.CompleteAsync(ProviderExecutionResult.Throttled, TimeSpan.FromSeconds(30), CancellationToken.None);
        var snapshot = Assert.Single(_providerPolicy.GetSnapshots(), value => value.Provider == "throttled-provider");
        Assert.Equal(2, snapshot.CurrentConcurrency);
        Assert.Equal(ProviderCircuitState.Closed, snapshot.CircuitState);
        await Assert.ThrowsAsync<ProviderRetryAfterException>(() => _providerPolicy.AcquireAsync("throttled-provider", 4, CancellationToken.None));
    }

    [Fact]
    public async Task FiveTransientFailuresOpenCircuitAndSuccessesRecoverConcurrency()
    {
        for (var index = 0; index < 2; index++)
        {
            await using var failed = await _providerPolicy.AcquireAsync("adaptive-provider", 4, CancellationToken.None);
            await failed.CompleteAsync(ProviderExecutionResult.Failed, null, CancellationToken.None);
        }
        Assert.Equal(2, Assert.Single(_providerPolicy.GetSnapshots(), value => value.Provider == "adaptive-provider").CurrentConcurrency);

        for (var index = 0; index < 20; index++)
        {
            await using var succeeded = await _providerPolicy.AcquireAsync("adaptive-provider", 4, CancellationToken.None);
            await succeeded.CompleteAsync(ProviderExecutionResult.Success, null, CancellationToken.None);
        }
        Assert.Equal(3, Assert.Single(_providerPolicy.GetSnapshots(), value => value.Provider == "adaptive-provider").CurrentConcurrency);

        for (var index = 0; index < 5; index++)
        {
            await using var failed = await _providerPolicy.AcquireAsync("circuit-provider", 4, CancellationToken.None);
            await failed.CompleteAsync(ProviderExecutionResult.Failed, null, CancellationToken.None);
        }
        Assert.Equal(ProviderCircuitState.Open, Assert.Single(_providerPolicy.GetSnapshots(), value => value.Provider == "circuit-provider").CircuitState);
        await Assert.ThrowsAsync<ProviderCircuitOpenException>(() => _providerPolicy.AcquireAsync("circuit-provider", 4, CancellationToken.None));
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "PremiereCalendar.UnitTests";
        public string WebRootPath { get; set; } = root;
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = root;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
