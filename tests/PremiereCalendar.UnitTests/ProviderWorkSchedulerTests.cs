using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
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
    private ProviderAdaptiveStateStore _providerStore = null!;

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
        _providerStore = new ProviderAdaptiveStateStore(database, environment);
        _providerPolicy = new AdaptiveProviderPolicy(_providerStore, TimeProvider.System, new PremiereTelemetry());
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
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_root, "scheduler.db")}");
        await connection.OpenAsync();
        await using var leases = connection.CreateCommand();
        leases.CommandText = "SELECT COUNT(*) FROM ProviderWorkLeases WHERE JobId=$id";
        leases.Parameters.AddWithValue("$id", claimed.JobId);
        Assert.Equal(1L, await leases.ExecuteScalarAsync());
    }

    [Fact]
    public async Task PersistedCheckpointRestoresCompletedSourceItemsAndAttachesLateSubscriber()
    {
        var handle = await _scheduler.EnqueueAsync(new ProviderWorkRequest(ProviderWorkKind.CalendarForeground, "resume", ProviderWorkPriority.Foreground, "{}"));
        var claimed = await _scheduler.ClaimNextAsync(false, "test", CancellationToken.None);
        Assert.NotNull(claimed);
        var item = new PremiereCalendar.Models.PremiereItem
        {
            MediaType = PremiereCalendar.Models.PremiereMediaType.Movie,
            Type = PremiereCalendar.Models.PremiereItemType.MovieFirstRelease,
            TmdbId = 42,
            Title = "Checkpoint movie",
            PremiereDate = new DateOnly(2026, 8, 13)
        };
        var calendar = new PremiereLoadProgress("TMDb movies", 1, 1, [item])
        {
            Phase = "complete",
            CheckpointKey = "tmdb:movies",
            SourceItems = [item]
        };
        await _scheduler.PublishAsync(claimed!, new ProviderWorkProgress(claimed.JobId, ProviderWorkState.Running, "source complete", 1, 2, calendar), CancellationToken.None);

        var persisted = await _scheduler.GetAsync(handle.JobId);
        var checkpoint = ProviderWorkScheduler.TryReadCheckpoint(persisted!.CheckpointJson);
        Assert.Equal("Checkpoint movie", Assert.Single(checkpoint!.CompletedSources["tmdb:movies"]).Title);
        await using var updates = _scheduler.WatchAsync(new ProviderWorkHandle(handle.JobId, false)).GetAsyncEnumerator();
        Assert.True(await updates.MoveNextAsync());
        Assert.Equal("source complete", updates.Current.Message);
    }

    [Fact]
    public async Task ForegroundEnqueuePreemptsBackgroundWithoutConsumingRetry()
    {
        var background = await _scheduler.EnqueueAsync(new ProviderWorkRequest(ProviderWorkKind.CalendarWarmup, "background", ProviderWorkPriority.Warmup, "{}"));
        var claimed = await _scheduler.ClaimNextAsync(false, "test", CancellationToken.None);
        Assert.NotNull(claimed);
        using var execution = _scheduler.BeginExecution(claimed!, CancellationToken.None);

        await _scheduler.EnqueueAsync(new ProviderWorkRequest(ProviderWorkKind.CalendarForeground, "foreground-preempt", ProviderWorkPriority.Foreground, "{}"));
        Assert.True(execution.IsCancellationRequested);
        await _scheduler.PreemptAsync(claimed, CancellationToken.None);

        var snapshot = await _scheduler.GetAsync(background.JobId);
        Assert.NotNull(snapshot);
        Assert.Equal(ProviderWorkState.Queued, snapshot!.State);
        Assert.Equal(0, snapshot.AttemptCount);
        _scheduler.EndExecution(claimed, execution);
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

    [Fact]
    public async Task OlderAdaptiveSnapshotCannotOverwriteNewerCircuitState()
    {
        var newer = DateTimeOffset.Parse("2026-08-13T18:00:01Z");
        var older = newer.AddSeconds(-1);
        await _providerStore.SaveAsync(new ProviderAdaptiveSnapshot(
            "watchmode", 1, 0, 0, 5, 5, older, 20_000, ProviderCircuitState.Open,
            newer.AddMinutes(1), newer, newer), CancellationToken.None);
        await _providerStore.SaveAsync(new ProviderAdaptiveSnapshot(
            "watchmode", 2, 0, 0, 1, 1, older, 50, ProviderCircuitState.Closed,
            null, null, older), CancellationToken.None);

        var persisted = await _providerStore.GetAsync("watchmode", CancellationToken.None);
        Assert.NotNull(persisted);
        Assert.Equal(ProviderCircuitState.Open, persisted!.CircuitState);
        Assert.Equal(5, persisted.ConsecutiveFailures);
        Assert.Equal(newer, persisted.UpdatedUtc);
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
