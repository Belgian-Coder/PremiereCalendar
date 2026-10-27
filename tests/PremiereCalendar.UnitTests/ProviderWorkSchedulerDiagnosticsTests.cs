using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

[Collection(SqliteTestCollection.Name)]
public sealed class ProviderWorkSchedulerDiagnosticsTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "premiere-scheduler-diagnostics", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new(DateTimeOffset.Parse("2026-10-27T12:00:00Z"));
    private PerformanceDiagnosticStore _diagnostics = null!;
    private PremiereTelemetry _telemetry = null!;
    private ProviderWorkStore _store = null!;
    private ProviderWorkScheduler _scheduler = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        TestSqliteDatabase.Initialize(_root, "scheduler.db");
        _diagnostics = new(new TelemetryOptions { LogDirectory = "logs" }, _root);
        _telemetry = new(_diagnostics);
        _store = new(Microsoft.Extensions.Options.Options.Create(new AppDatabaseOptions { Path = "scheduler.db" }), new TestEnvironment(_root));
        _scheduler = NewScheduler();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task HostedPipelineReceivesJobContextAndFinishesBeforeDurableCompletion()
    {
        var pipeline = new RecordingPipeline(_telemetry);
        var payload = JsonSerializer.Serialize(new CalendarProviderWorkPayload(new(2026, 10, 26), new(2026, 11, 1), false, null));
        ProviderWorkHandle handle;
        using (_telemetry.UseDiagnosticContext(_telemetry.CreateDiagnosticContext(source: "foreground")))
            handle = await _scheduler.EnqueueAsync(new(ProviderWorkKind.CalendarForeground, "secret-dedupe", ProviderWorkPriority.Foreground, payload));
        _time.Advance(TimeSpan.FromMilliseconds(40));
        await using var services = new ServiceCollection().AddSingleton<IPremiereLoadPipeline>(pipeline).BuildServiceProvider();
        var recovery = new DatabaseRecoveryState();
        recovery.Set(new(DatabaseSchema.CurrentVersion, DatabaseSchema.CurrentVersion, true, "ready", null, null));
        using var worker = new ProviderWorkSchedulerHostedService(_scheduler, services.GetRequiredService<IServiceScopeFactory>(),
            recovery, Microsoft.Extensions.Options.Options.Create(new ProviderSchedulerOptions { LeaseSeconds = 30 }),
            NullLogger<ProviderWorkSchedulerHostedService>.Instance);
        await worker.StartAsync(CancellationToken.None);
        try
        {
            await UntilAsync(async () => (await _scheduler.GetAsync(handle.JobId))?.State == ProviderWorkState.Completed);
            await UntilAsync(() => Task.FromResult(Events().Any(e => Name(e) == "scheduler.job.completed")));
        }
        finally { await worker.StopAsync(CancellationToken.None); }
        Assert.Equal(handle.JobId, pipeline.Context?.JobId);
        Assert.Null(pipeline.Context?.LoadId);
        var events = Events();
        Assert.All(events, e =>
        {
            Assert.Equal(handle.JobId, e.GetProperty("jobId").GetString());
            Assert.False(e.TryGetProperty("loadId", out _));
        });
        var start = Assert.Single(events, e => Name(e) == "scheduler.execution.started");
        var finished = Assert.Single(events, e => Name(e) == "scheduler.execution.finished");
        var completed = Assert.Single(events, e => Name(e) == "scheduler.job.completed");
        Assert.True(start.GetProperty("eventSequence").GetInt64() < finished.GetProperty("eventSequence").GetInt64());
        Assert.True(finished.GetProperty("eventSequence").GetInt64() < completed.GetProperty("eventSequence").GetInt64());
        var claim = Assert.Single(events, e => Name(e) == "scheduler.claim.completed").GetProperty("details");
        Assert.Equal(40, claim.GetProperty("waitMilliseconds").GetDouble());
        var checkpoint = Assert.Single(events, e => Name(e) == "scheduler.checkpoint.completed").GetProperty("details");
        Assert.Equal(1, checkpoint.GetProperty("count").GetInt32());
        Assert.Equal(2, checkpoint.GetProperty("total").GetInt32());
        Assert.Contains(events, e => Name(e) == "scheduler.progress.published");
        var raw = string.Join("\n", events.Select(e => e.GetRawText()));
        foreach (var secret in new[] { "secret-dedupe", "secret-title", "secret-message", "PayloadJson", "CompletedSources", "LastError" })
            Assert.DoesNotContain(secret, raw, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ProviderWorkState.Completed, (await _scheduler.GetAsync(handle.JobId))!.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredOwnershipNeverEmitsDurableCompletionOrCheckpoint(bool checkpoint)
    {
        var job = await ClaimAsync();
        var execution = _scheduler.BeginExecution(job, CancellationToken.None);
        try
        {
            _time.Advance(TimeSpan.FromSeconds(31));
            _scheduler.RecordExecutionFinished(job, "success");
            if (checkpoint)
                await Assert.ThrowsAsync<ProviderWorkLeaseLostException>(() => _scheduler.PublishAsync(job,
                    new(job.JobId, ProviderWorkState.Running, "secret-message", 1, 2), CancellationToken.None));
            else await _scheduler.CompleteAsync(job, CancellationToken.None);
        }
        finally { _scheduler.EndExecution(job, execution); }
        var events = Events();
        Assert.DoesNotContain(events, e => Name(e) is "scheduler.job.completed" or "scheduler.checkpoint.completed");
        var lost = Assert.Single(events, e => Name(e) == "scheduler.lease.lost").GetProperty("details");
        Assert.Equal(checkpoint ? "checkpoint" : "complete", lost.GetProperty("stage").GetString());
        Assert.Equal("ownership_rejected", lost.GetProperty("outcome").GetString());
        Assert.NotEqual(ProviderWorkState.Completed, (await _scheduler.GetAsync(job.JobId))!.State);
    }

    [Theory]
    [InlineData("preempt", "scheduler.job.preempted", ProviderWorkState.Queued)]
    [InlineData("defer", "scheduler.job.deferred", ProviderWorkState.RetryWaiting)]
    [InlineData("retry", "scheduler.job.retry.scheduled", ProviderWorkState.RetryWaiting)]
    [InlineData("fail", "scheduler.job.failed", ProviderWorkState.Failed)]
    public async Task DurableTransitionsRecordAcceptedStateWithoutErrorText(string transition, string expectedEvent, ProviderWorkState expectedState)
    {
        if (transition == "fail") _scheduler = NewScheduler(maximumAttempts: 1);
        var job = await ClaimAsync();
        var execution = _scheduler.BeginExecution(job, CancellationToken.None);
        try
        {
            _time.Advance(TimeSpan.FromMilliseconds(50));
            _scheduler.RecordExecutionFinished(job, transition is "retry" or "fail" ? "failed" : "success");
            switch (transition)
            {
                case "preempt": await _scheduler.PreemptAsync(job, CancellationToken.None); break;
                case "defer":
                    await _scheduler.PublishAsync(job, new(job.JobId, ProviderWorkState.Running, "secret-message", 1, 2,
                        new PremiereLoadProgress("secret-title", 1, 1, [], IsFinal: true) { RetryAfterUtc = _time.GetUtcNow().AddMinutes(1) }), CancellationToken.None);
                    await _scheduler.CompleteAsync(job, CancellationToken.None);
                    break;
                default: await _scheduler.FailAsync(job, new HttpRequestException("https://private.test/?apikey=secret-error"), CancellationToken.None); break;
            }
        }
        finally { _scheduler.EndExecution(job, execution); }
        Assert.Equal(expectedState, (await _scheduler.GetAsync(job.JobId))!.State);
        var events = Events();
        var durable = Assert.Single(events, e => Name(e) == expectedEvent).GetProperty("details");
        // The clock advanced during execution; persistence itself did not advance it.
        Assert.Equal(0, durable.GetProperty("durationMilliseconds").GetDouble());
        var finished = Assert.Single(events, e => Name(e) == "scheduler.execution.finished").GetProperty("details");
        Assert.Equal(50, finished.GetProperty("durationMilliseconds").GetDouble());
        Assert.DoesNotContain(events, e => Name(e) == "scheduler.job.completed");
        if (transition is "retry" or "fail") Assert.Equal("HttpRequestException", durable.GetProperty("reason").GetString());
        var raw = string.Join("\n", events.Select(e => e.GetRawText()));
        Assert.DoesNotContain("secret-error", raw);
        Assert.DoesNotContain("private.test", raw);
        Assert.DoesNotContain("secret-message", raw);
        Assert.DoesNotContain("secret-title", raw);
    }

    private ProviderWorkScheduler NewScheduler(int maximumAttempts = 3)
        => new(_store, _time, Microsoft.Extensions.Options.Options.Create(new ProviderSchedulerOptions { LeaseSeconds = 30, MaximumAttempts = maximumAttempts }), _telemetry);
    private async Task<ProviderWorkSnapshot> ClaimAsync()
    {
        await _scheduler.EnqueueAsync(new(ProviderWorkKind.CalendarForeground, "secret-key", ProviderWorkPriority.Foreground, "{\"secret-payload\":true}"));
        return (await _scheduler.ClaimNextAsync(false, "owner", CancellationToken.None))!;
    }
    private JsonElement[] Events()
    {
        _telemetry.FlushDiagnostics();
        var directory = Path.Combine(_root, "logs", "performance");
        return Directory.Exists(directory) ? Directory.GetFiles(directory, "performance-*.ndjson")
            .SelectMany(ReadLines).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray() : [];
    }
    private static IEnumerable<string> ReadLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line) yield return line;
    }
    private static string? Name(JsonElement value) => value.GetProperty("event").GetString();
    private static async Task UntilAsync(Func<Task<bool>> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!await predicate()) await Task.Delay(10, timeout.Token);
    }
    public Task DisposeAsync()
    {
        _diagnostics.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }
    private sealed class RecordingPipeline(PremiereTelemetry telemetry) : IPremiereLoadPipeline
    {
        public PerformanceDiagnosticContext? Context { get; private set; }
        public async IAsyncEnumerable<PremiereLoadProgress> StreamCoreAsync(DateOnly start, DateOnly end, bool forceRefresh,
            CalendarFilters? filters, ProviderWorkResumeState? resumeState, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Context = telemetry.CaptureDiagnosticContext();
            telemetry.RecordDiagnostic("pipeline.entered", details: new() { Stage = "pipeline" });
            await Task.Yield();
            yield return new("secret-title", 1, 1, [], IsFinal: true, CompletedWork: 1, TotalWork: 2, ProgressText: "secret-message");
        }
    }
    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "PremiereCalendar.UnitTests";
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class ManualTimeProvider(DateTimeOffset startUtc) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow = startUtc;
        private long _timestamp;
        private int _createdTimerCount;

        public int CreatedTimerCount => Volatile.Read(ref _createdTimerCount);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate) return _utcNow;
        }

        public override long GetTimestamp()
        {
            lock (_gate) return _timestamp;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                var timer = new ManualTimer(this, callback, state);
                _timers.Add(timer);
                _createdTimerCount++;
                timer.ChangeUnderLock(dueTime, period);
                return timer;
            }
        }

        public void Advance(TimeSpan amount)
        {
            List<(TimerCallback Callback, object? State)> callbacks = [];
            lock (_gate)
            {
                _utcNow += amount;
                _timestamp += amount.Ticks;
                foreach (var timer in _timers)
                {
                    if (timer.TryFireUnderLock(_timestamp, out var callback, out var state)) callbacks.Add((callback, state));
                }
            }

            foreach (var (callback, state) in callbacks) callback(state);
        }

        private bool Change(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate) return timer.ChangeUnderLock(dueTime, period);
        }

        private bool Dispose(ManualTimer timer)
        {
            lock (_gate) return timer.DisposeUnderLock();
        }

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            private long _dueTimestamp = long.MaxValue;
            private TimeSpan _period = Timeout.InfiniteTimeSpan;
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period) => owner.Change(this, dueTime, period);
            public void Dispose() => owner.Dispose(this);
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public bool ChangeUnderLock(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed) return false;
                _period = period;
                _dueTimestamp = dueTime == Timeout.InfiniteTimeSpan
                    ? long.MaxValue
                    : owner._timestamp + Math.Max(0, dueTime.Ticks);
                return true;
            }

            public bool TryFireUnderLock(long timestamp, out TimerCallback timerCallback, out object? timerState)
            {
                if (_disposed || _dueTimestamp > timestamp)
                {
                    timerCallback = null!;
                    timerState = null;
                    return false;
                }

                if (_period <= TimeSpan.Zero || _period == Timeout.InfiniteTimeSpan)
                {
                    _dueTimestamp = long.MaxValue;
                }
                else
                {
                    _dueTimestamp = timestamp + _period.Ticks;
                }
                timerCallback = callback;
                timerState = state;
                return true;
            }

            public bool DisposeUnderLock()
            {
                if (_disposed) return false;
                _disposed = true;
                _dueTimestamp = long.MaxValue;
                return true;
            }
        }
    }
}
