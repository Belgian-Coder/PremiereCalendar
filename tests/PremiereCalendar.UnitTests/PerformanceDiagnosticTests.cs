using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Text.Json;
using PremiereCalendar.Options;
using PremiereCalendar.Services;
using Xunit.Abstractions;

namespace PremiereCalendar.UnitTests;

public sealed class PerformanceDiagnosticTests(ITestOutputHelper output)
{
    [Fact]
    public void DetailedEventBurstReportsPersistenceCost()
    {
        using var directory = new TestDirectory();
        using var store = new PerformanceDiagnosticStore(new() { PerformanceQueueCapacity = 16384 }, directory.Path);
        var telemetry = new PremiereTelemetry(store);
        var context = telemetry.CreateDiagnosticContext() with
        {
            JobId = Guid.NewGuid().ToString("N"), OperationId = Guid.NewGuid().ToString("N"),
            RequestId = Guid.NewGuid().ToString("N"), SourceKey = "tmdb"
        };
        const int warmupEvents = 100;
        const int measuredEvents = 10000;
        for (var index = 0; index < warmupEvents; index++) Write(index);
        telemetry.FlushDiagnostics();
        using var process = Process.GetCurrentProcess();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var cpuBefore = process.TotalProcessorTime;
        var started = Stopwatch.GetTimestamp();
        for (var index = 0; index < measuredEvents; index++) Write(index);
        var writeDuration = Stopwatch.GetElapsedTime(started);
        var cpuDuration = process.TotalProcessorTime - cpuBefore;
        var allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        started = Stopwatch.GetTimestamp();
        telemetry.FlushDiagnostics();
        var flushDuration = Stopwatch.GetElapsedTime(started);
        var bytes = Directory.GetFiles(directory.PerformancePath, "performance-*.ndjson").Sum(path => new FileInfo(path).Length);
        output.WriteLine($"Detailed diagnostics capacity16384: {measuredEvents} events, nonblocking capture={writeDuration.TotalMilliseconds:F2} ms, concurrent process CPU={cpuDuration.TotalMilliseconds:F2} ms, durable drain/flush={flushDuration.TotalMilliseconds:F2} ms, producer allocations={allocated / measuredEvents} bytes/event, saved={bytes} bytes including {warmupEvents} warmup events.");
        Assert.Equal(warmupEvents + measuredEvents, store.Health.WrittenEvents);
        Assert.Equal(0, store.Health.DroppedEvents);
        Assert.Equal(warmupEvents + measuredEvents, ReadEvents(directory.Path).Count);

        void Write(int index) => telemetry.RecordDiagnostic("calendar.stage.completed", context,
            new() { Stage = "movie.enrichment", Provider = "tmdb", Outcome = "completed", Sequence = index, DurationMilliseconds = 12.5, Count = 1 });
    }

    [Fact]
    public void DefaultCapacityBurstReportsEveryPersistedOrDroppedEvent()
    {
        using var directory = new TestDirectory();
        using var store = new PerformanceDiagnosticStore(new(), directory.Path);
        var telemetry = new PremiereTelemetry(store);
        var context = telemetry.CreateDiagnosticContext();
        const int count = 10000;
        var started = Stopwatch.GetTimestamp();
        for (var index = 0; index < count; index++) telemetry.RecordDiagnostic("calendar.stage.completed", context);
        var capture = Stopwatch.GetElapsedTime(started);
        started = Stopwatch.GetTimestamp();
        telemetry.FlushDiagnostics();
        var drain = Stopwatch.GetElapsedTime(started);
        var events = ReadEvents(directory.Path);
        var normalEvents = events.Count(entry => entry.GetProperty("event").GetString() != "diagnostics.loss");
        Assert.Equal(count, normalEvents + store.Health.DroppedEvents);
        Assert.Equal(4096, store.Health.QueueCapacity);
        Assert.Equal(0, store.Health.QueuedEvents);
        if (store.Health.DroppedEvents > 0)
            Assert.Contains(events, entry => entry.GetProperty("event").GetString() == "diagnostics.loss");
        output.WriteLine($"Default capacity4096: {count} events, capture={capture.TotalMilliseconds:F2} ms, durable drain/flush={drain.TotalMilliseconds:F2} ms, persisted={normalEvents}, dropped={store.Health.DroppedEvents}.");
    }

    [Fact]
    public void DefaultsPersistWithoutExporterOrSampledActivitiesAndSurviveRestart()
    {
        using var directory = new TestDirectory();
        var options = new TelemetryOptions { Enabled = false, TraceSampleRatio = 0 };
        var loadId = Guid.NewGuid().ToString("N");
        using (var store = new PerformanceDiagnosticStore(options, directory.Path))
        {
            var telemetry = new PremiereTelemetry(store);
            telemetry.RecordDiagnostic("calendar.load.started", new() { LoadId = loadId }, new() { WeekStart = new DateOnly(2026, 10, 26) });
            telemetry.RecordDiagnostic("calendar.load.completed", new() { LoadId = loadId }, new() { Count = 42, DurationMilliseconds = 1234.5 });
            telemetry.FlushDiagnostics();
            Assert.Equal(2, ReadEvents(directory.Path).Count);
            Assert.Equal(2, telemetry.DiagnosticsHealth.WrittenEvents);
        }
        using (var restarted = new PerformanceDiagnosticStore(options, directory.Path))
        {
            new PremiereTelemetry(restarted).RecordDiagnostic("calendar.load.started", new() { LoadId = loadId });
        }

        var events = ReadEvents(directory.Path);
        Assert.Equal(3, events.Count);
        Assert.All(events, diagnosticEvent => Assert.Equal(loadId, diagnosticEvent.GetProperty("loadId").GetString()));
        Assert.All(events, diagnosticEvent => Assert.True(diagnosticEvent.GetProperty("timestampUtc").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-1)));
        Assert.Equal(1, events[0].GetProperty("eventSequence").GetInt64());
        Assert.Equal(2, events[1].GetProperty("eventSequence").GetInt64());
        Assert.True(events[1].GetProperty("processElapsedMilliseconds").GetDouble() >= events[0].GetProperty("processElapsedMilliseconds").GetDouble());
        Assert.NotEqual(events[0].GetProperty("instanceId").GetString(), events[2].GetProperty("instanceId").GetString());
        Assert.Equal(42, events[1].GetProperty("details").GetProperty("count").GetInt32());
    }

    [Fact]
    public void RollingStoreEnforcesFileAndTotalVolumeBoundsAndKeepsNewestEvents()
    {
        using var directory = new TestDirectory();
        var options = new TelemetryOptions { PerformanceRetainedFileCount = 3 };
        using (var store = new PerformanceDiagnosticStore(options, directory.Path, fileSizeLimitBytes: 8192))
        {
            var telemetry = new PremiereTelemetry(store);
            var context = telemetry.CreateDiagnosticContext();
            for (var index = 1; index <= 1000; index++)
                telemetry.RecordDiagnostic("provider.attempt.completed", context,
                    new() { Provider = "tmdb", DurationMilliseconds = index, Sequence = index });
            telemetry.FlushDiagnostics();
            Assert.Equal(1000, store.Health.WrittenEvents);
            Assert.Equal(0, store.Health.DroppedEvents);
        }
        var files = Directory.GetFiles(directory.PerformancePath, "performance-*.ndjson");
        Assert.Equal(3, files.Length);
        Assert.All(files, path => Assert.InRange(new FileInfo(path).Length, 1, 8192));
        Assert.True(files.Sum(path => new FileInfo(path).Length) <= 3 * 8192);
        var events = ReadEvents(directory.Path);
        Assert.Equal(1000, events[^1].GetProperty("details").GetProperty("sequence").GetInt32());
        Assert.DoesNotContain(events, entry => entry.GetProperty("details").GetProperty("sequence").GetInt32() == 1);

        options.PerformanceRetainedFileCount = 2;
        using (var restarted = new PerformanceDiagnosticStore(options, directory.Path, fileSizeLimitBytes: 8192))
            new PremiereTelemetry(restarted).RecordDiagnostic("process.restarted");
        Assert.Equal(2, Directory.GetFiles(directory.PerformancePath, "performance-*.ndjson").Length);
    }

    [Fact]
    public void FixedSchemaRejectsUrlsFreeTextCredentialsAndUnhashedFilterKeys()
    {
        using var directory = new TestDirectory();
        using var store = new PerformanceDiagnosticStore(new(), directory.Path);
        var telemetry = new PremiereTelemetry(store);
        telemetry.RecordDiagnostic("https://private.example/api?api_key=top-secret",
            new() { LoadId = "secret-id", JobId = "credential", OperationId = "private-operation-id", RequestId = "private-id", SourceKey = "My Private Movie" },
            new()
            {
                Stage = "Bearer top-secret", Provider = "provider.test?api_key=hidden", Outcome = "exception private data",
                Reason = "user@example.test", Cache = "C:\\private\\cache", CacheKey = "private-filter-contents",
                SourceKey = "Title Including Spaces", JobKind = "sensitive payload",
                DurationMilliseconds = double.NaN, WaitMilliseconds = double.PositiveInfinity
            });
        telemetry.FlushDiagnostics();
        var raw = ReadActiveFile(Directory.GetFiles(directory.PerformancePath).Single());
        foreach (var secret in new[] { "private.example", "top-secret", "secret-id", "credential", "private-operation-id", "private-id", "My Private Movie", "hidden", "user@", "private-filter", "Title Including Spaces", "sensitive payload" })
            Assert.DoesNotContain(secret, raw, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("redacted", raw);
        Assert.DoesNotContain("NaN", raw);
        Assert.DoesNotContain("Infinity", raw);
    }

    [Fact]
    public void LoweredSizeLimitRemovesOversizedDiagnosticFilesWithoutTouchingOtherFiles()
    {
        using var directory = new TestDirectory();
        Directory.CreateDirectory(directory.PerformancePath);
        var oversized = System.IO.Path.Combine(directory.PerformancePath, "performance-20260101.ndjson");
        var unrelated = System.IO.Path.Combine(directory.PerformancePath, "operational.ndjson");
        File.WriteAllText(oversized, new string('x', 8193));
        File.WriteAllText(unrelated, "preserve");
        using var store = new PerformanceDiagnosticStore(new(), directory.Path, fileSizeLimitBytes: 8192);
        new PremiereTelemetry(store).RecordDiagnostic("process.restarted");
        store.Flush();
        Assert.False(File.Exists(oversized));
        Assert.Equal("preserve", File.ReadAllText(unrelated));
        Assert.Single(Directory.GetFiles(directory.PerformancePath, "performance-*.ndjson"));
    }

    [Fact]
    public async Task CorrelationFlowsAcrossAwaitsAndStaysIsolatedBetweenConcurrentLoads()
    {
        using var directory = new TestDirectory();
        using var store = new PerformanceDiagnosticStore(new(), directory.Path);
        var telemetry = new PremiereTelemetry(store);
        var first = telemetry.CreateDiagnosticContext() with { OperationId = Guid.NewGuid().ToString("N") };
        var second = telemetry.CreateDiagnosticContext() with { OperationId = Guid.NewGuid().ToString("N") };
        await Task.WhenAll(WriteLoad(first, "tmdb"), WriteLoad(second, "simkl"));
        Assert.Null(telemetry.CaptureDiagnosticContext());
        telemetry.FlushDiagnostics();
        var events = ReadEvents(directory.Path);
        Assert.Equal(4, events.Count);
        Assert.All(events.Where(entry => entry.GetProperty("loadId").GetString() == first.LoadId),
            entry =>
            {
                Assert.Equal("tmdb", entry.GetProperty("details").GetProperty("provider").GetString());
                Assert.Equal(first.OperationId, entry.GetProperty("operationId").GetString());
            });
        Assert.All(events.Where(entry => entry.GetProperty("loadId").GetString() == second.LoadId),
            entry =>
            {
                Assert.Equal("simkl", entry.GetProperty("details").GetProperty("provider").GetString());
                Assert.Equal(second.OperationId, entry.GetProperty("operationId").GetString());
            });
        Assert.Equal(2, events.Count(entry => entry.TryGetProperty("requestId", out _)));
        Assert.Equal(2, events.Count(entry => entry.GetProperty("details").TryGetProperty("itemId", out _)));

        async Task WriteLoad(PerformanceDiagnosticContext context, string provider)
        {
            using (telemetry.UseDiagnosticContext(context))
            {
                telemetry.RecordDiagnostic("source.started", details: new() { Provider = provider, ItemId = provider == "tmdb" ? 123 : 456 });
                await Task.Yield();
                await Task.Run(() =>
                {
                    using (telemetry.UseDiagnosticContext(telemetry.CaptureDiagnosticContext()! with { RequestId = Guid.NewGuid().ToString("N") }))
                        telemetry.RecordDiagnostic("source.completed", details: new() { Provider = provider });
                });
                Assert.Equal(context, telemetry.CaptureDiagnosticContext());
            }
        }
    }

    [Fact]
    public async Task IteratorContextCoversProducerAndDisposalWithoutLeakingToConsumer()
    {
        using var directory = new TestDirectory();
        using var store = new PerformanceDiagnosticStore(new(), directory.Path);
        var telemetry = new PremiereTelemetry(store);
        var consumer = telemetry.CreateDiagnosticContext();
        var producer = telemetry.CreateDiagnosticContext();
        using (telemetry.UseDiagnosticContext(consumer))
        {
            await foreach (var value in telemetry.WithDiagnosticContext(Produce(), producer))
            {
                Assert.Equal(1, value);
                Assert.Equal(consumer, telemetry.CaptureDiagnosticContext());
                break;
            }
            Assert.Equal(consumer, telemetry.CaptureDiagnosticContext());
        }
        Assert.Null(telemetry.CaptureDiagnosticContext());
        telemetry.FlushDiagnostics();
        var events = ReadEvents(directory.Path);
        Assert.Equal(2, events.Count);
        Assert.All(events, entry => Assert.Equal(producer.LoadId, entry.GetProperty("loadId").GetString()));

        async IAsyncEnumerable<int> Produce([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                await Task.Yield();
                Assert.Equal(producer, telemetry.CaptureDiagnosticContext());
                telemetry.RecordDiagnostic("source.produced");
                yield return 1;
                cancellationToken.ThrowIfCancellationRequested();
                yield return 2;
            }
            finally
            {
                Assert.Equal(producer, telemetry.CaptureDiagnosticContext());
                telemetry.RecordDiagnostic("source.disposed");
            }
        }
    }

    [Fact]
    public void DisabledDiagnosticsDoNotCreateFiles()
    {
        using var directory = new TestDirectory();
        using var store = new PerformanceDiagnosticStore(new() { PerformanceDiagnosticsEnabled = false }, directory.Path);
        var telemetry = new PremiereTelemetry(store);
        telemetry.RecordDiagnostic("calendar.load.started", telemetry.CreateDiagnosticContext());
        telemetry.FlushDiagnostics();
        Assert.False(Directory.Exists(directory.PerformancePath));
        Assert.False(telemetry.DiagnosticsHealth.Enabled);
        Assert.Equal(0, telemetry.DiagnosticsHealth.WrittenEvents);
        Assert.Equal(0, telemetry.DiagnosticsHealth.DroppedEvents);
    }

    [Fact]
    public void WriterFailuresAreVisibleAndLaterWritesRecoverWithoutFailingLoad()
    {
        using var directory = new TestDirectory();
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(directory.PerformancePath)!);
        File.WriteAllText(directory.PerformancePath, "block directory creation");
        using var store = new PerformanceDiagnosticStore(new(), directory.Path);
        var telemetry = new PremiereTelemetry(store);
        telemetry.RecordDiagnostic("calendar.load.started");
        telemetry.FlushDiagnostics();
        Assert.False(telemetry.DiagnosticsHealth.Healthy);
        Assert.Equal(1, telemetry.DiagnosticsHealth.DroppedEvents);
        Assert.True(telemetry.DiagnosticsHealth.WriterFailures >= 1);
        Assert.NotNull(telemetry.DiagnosticsHealth.LastFailureUtc);

        File.Delete(directory.PerformancePath);
        telemetry.RecordDiagnostic("calendar.load.completed");
        telemetry.FlushDiagnostics();
        Assert.True(telemetry.DiagnosticsHealth.Healthy);
        Assert.Equal(2, telemetry.DiagnosticsHealth.WrittenEvents);
        var events = ReadEvents(directory.Path);
        Assert.Contains(events, entry => entry.GetProperty("event").GetString() == "calendar.load.completed");
        var loss = Assert.Single(events, entry => entry.GetProperty("event").GetString() == "diagnostics.loss");
        Assert.Equal(1, loss.GetProperty("droppedEvents").GetInt64());
    }

    [Fact]
    public async Task BlockedWriterNeverBlocksCaptureOrHealthAndOverflowIsPersistedAfterRecovery()
    {
        using var directory = new TestDirectory();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var store = new PerformanceDiagnosticStore(new(), directory.Path, queueCapacity: 2,
            beforeWrite: () => { entered.Set(); release.Wait(); }, flushTimeout: TimeSpan.FromMilliseconds(100));
        var telemetry = new PremiereTelemetry(store);
        try
        {
            telemetry.RecordDiagnostic("writer.blocked");
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            var health = await Task.Run(() =>
            {
                for (var index = 0; index < 1000; index++) telemetry.RecordDiagnostic("provider.event");
                return store.Health;
            }).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(2, health.QueuedEvents);
            Assert.Equal(998, health.DroppedEvents);
            Assert.True(health.QueueFull);
            Assert.False(health.Healthy);
            var started = Stopwatch.GetTimestamp();
            store.Flush();
            Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(2));
            Assert.Equal(1, store.Health.FlushTimeouts);
        }
        finally { release.Set(); }
        store.Flush();
        for (var attempt = 0; attempt < 20 && !store.Health.Healthy; attempt++)
        {
            await Task.Delay(25);
            store.Flush();
        }
        Assert.True(store.Health.Healthy);
        Assert.Equal(0, store.Health.QueuedEvents);
        var events = ReadEvents(directory.Path);
        Assert.Equal(3, events.Count(entry => entry.GetProperty("event").GetString() != "diagnostics.loss"));
        var marker = Assert.Single(events, entry => entry.GetProperty("event").GetString() == "diagnostics.loss");
        Assert.Equal(998, marker.GetProperty("droppedEvents").GetInt64());
    }

    [Fact]
    public async Task HungWriterHasBoundedShutdownAndDropsQueuedEventsWhenIoReturns()
    {
        using var directory = new TestDirectory();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var store = new PerformanceDiagnosticStore(new(), directory.Path, queueCapacity: 2,
            beforeWrite: () => { entered.Set(); release.Wait(); }, shutdownTimeout: TimeSpan.FromMilliseconds(100));
        var telemetry = new PremiereTelemetry(store);
        try
        {
            telemetry.RecordDiagnostic("writer.blocked");
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            telemetry.RecordDiagnostic("queued.before.shutdown");
            var started = Stopwatch.GetTimestamp();
            store.Dispose();
            Assert.True(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(2));
            Assert.True(store.Health.ShutdownTimedOut);
            Assert.False(store.Health.Healthy);
        }
        finally { release.Set(); }
        await store.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(store.Health.WriterRunning);
        Assert.Equal(0, store.Health.QueuedEvents);
        Assert.Equal(1, store.Health.DroppedEvents);
        foreach (var path in Directory.GetFiles(directory.PerformancePath))
        {
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
    }

    [Fact]
    public async Task SingleHungWriteIsUnhealthyWithoutQueueOverflowAndRecoversAfterIoReturns()
    {
        using var directory = new TestDirectory();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var store = new PerformanceDiagnosticStore(new(), directory.Path,
            beforeWrite: () => { entered.Set(); release.Wait(); }, flushTimeout: TimeSpan.FromMilliseconds(200));
        var telemetry = new PremiereTelemetry(store);
        try
        {
            telemetry.RecordDiagnostic("writer.only.inflight.event");
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            await Task.Delay(250);
            var health = await Task.Run(() => store.Health).WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Equal(0, health.QueuedEvents);
            Assert.Equal(0, health.DroppedEvents);
            Assert.False(health.QueueFull);
            Assert.True(health.WriterStalled);
            Assert.InRange(health.ActiveIoMs, 200, 86_400_000);
            Assert.False(health.Healthy);
        }
        finally { release.Set(); }
        store.Flush();
        Assert.True(store.Health.Healthy);
        Assert.False(store.Health.WriterStalled);
        Assert.Equal(0, store.Health.ActiveIoMs);
    }

    [Fact]
    public async Task PeriodicFlushPersistsEventsAcrossIdleTimerTicksWithoutExplicitFlush()
    {
        using var directory = new TestDirectory();
        using var store = new PerformanceDiagnosticStore(new() { PerformanceFlushIntervalSeconds = 1 }, directory.Path);
        var telemetry = new PremiereTelemetry(store);
        await Task.Delay(1100);
        telemetry.RecordDiagnostic("after.first.idle.tick");
        await WaitForEvents(1);
        telemetry.RecordDiagnostic("after.second.idle.tick");
        await WaitForEvents(2);

        async Task WaitForEvents(int count)
        {
            var started = Stopwatch.GetTimestamp();
            while (Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5))
            {
                if (Directory.Exists(directory.PerformancePath) && ReadEvents(directory.Path).Count == count) return;
                await Task.Delay(25);
            }
            Assert.Fail($"Periodic writer did not persist {count} events after an idle timer tick.");
        }
    }

    [Fact]
    public void DisposalDrainsAcceptedEventsAndClosesFilesBeforeReturning()
    {
        using var directory = new TestDirectory();
        var store = new PerformanceDiagnosticStore(new(), directory.Path);
        var telemetry = new PremiereTelemetry(store);
        for (var index = 0; index < 1000; index++) telemetry.RecordDiagnostic("shutdown.drain");
        store.Dispose();
        Assert.Equal(1000, store.Health.WrittenEvents);
        Assert.Equal(0, store.Health.DroppedEvents);
        Assert.False(store.Health.WriterRunning);
        Assert.False(store.Health.ShutdownTimedOut);
        Assert.Equal(1000, ReadEvents(directory.Path).Count);
        foreach (var path in Directory.GetFiles(directory.PerformancePath))
        {
            using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        }
    }

    [Fact]
    public void OversizedRecordIsRejectedAndOnlyItsLossCountPersists()
    {
        using var directory = new TestDirectory();
        using var store = new PerformanceDiagnosticStore(new(), directory.Path);
        store.Write(new PerformanceDiagnosticEvent(DateTimeOffset.UtcNow, 0, Guid.NewGuid().ToString("N"), 1,
            new string('x', 8192), null, null, null, null, null, null, null, null));
        store.Flush();
        Assert.Equal(1, store.Health.DroppedEvents);
        var marker = Assert.Single(ReadEvents(directory.Path));
        Assert.Equal("diagnostics.loss", marker.GetProperty("event").GetString());
        Assert.Equal(1, marker.GetProperty("droppedEvents").GetInt64());
    }

    [Fact]
    public void LockedOldestFileDoesNotPreventUsingALaterDeletableRetentionSlot()
    {
        using var directory = new TestDirectory();
        Directory.CreateDirectory(directory.PerformancePath);
        var oldest = System.IO.Path.Combine(directory.PerformancePath, "performance-20000101-a.ndjson");
        var second = System.IO.Path.Combine(directory.PerformancePath, "performance-20000102-b.ndjson");
        var third = System.IO.Path.Combine(directory.PerformancePath, "performance-20000103-c.ndjson");
        foreach (var path in new[] { oldest, second, third }) File.WriteAllText(path, new string('x', 8192));
        var unrelated = System.IO.Path.Combine(directory.PerformancePath, "operational.ndjson");
        File.WriteAllText(unrelated, "preserve");
        using var oldestLock = new FileStream(oldest, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var store = new PerformanceDiagnosticStore(new() { PerformanceRetainedFileCount = 3 }, directory.Path,
            fileSizeLimitBytes: 8192, beforeDelete: path => SimulateUnixDeleteLock(path, oldest));
        new PremiereTelemetry(store).RecordDiagnostic("retention.recovered");
        store.Flush();
        Assert.True(File.Exists(oldest));
        Assert.False(File.Exists(second));
        Assert.True(File.Exists(third));
        Assert.Equal("preserve", File.ReadAllText(unrelated));
        var files = Directory.GetFiles(directory.PerformancePath, "performance-*.ndjson");
        Assert.Equal(3, files.Length);
        Assert.True(files.Sum(path => new FileInfo(path).Length) <= 3 * 8192);
        Assert.Equal(0, store.Health.DroppedEvents);
        Assert.True(store.Health.WriterFailures > 0);
        Assert.True(store.Health.Healthy);
        var recorded = string.Join('\n', files.Where(path => path.Contains(store.InstanceId, StringComparison.Ordinal)).Select(ReadActiveFile));
        Assert.Contains("\"event\":\"retention.recovered\"", recorded);
        Assert.Contains("\"event\":\"diagnostics.loss\"", recorded);
    }

    [Fact]
    public void AllLockedRetentionSlotsCauseVisibleDropsWithoutCreatingAdditionalFiles()
    {
        using var directory = new TestDirectory();
        Directory.CreateDirectory(directory.PerformancePath);
        var first = System.IO.Path.Combine(directory.PerformancePath, "performance-20000101-a.ndjson");
        var second = System.IO.Path.Combine(directory.PerformancePath, "performance-20000102-b.ndjson");
        foreach (var path in new[] { first, second }) File.WriteAllText(path, new string('x', 8192));
        var unrelated = System.IO.Path.Combine(directory.PerformancePath, "unrelated.txt");
        File.WriteAllText(unrelated, "preserve");
        using var firstLock = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var secondLock = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var store = new PerformanceDiagnosticStore(new() { PerformanceRetainedFileCount = 2 }, directory.Path,
            fileSizeLimitBytes: 8192, beforeDelete: path => SimulateUnixDeleteLock(path, first, second));
        new PremiereTelemetry(store).RecordDiagnostic("retention.blocked");
        store.Flush();
        var files = Directory.GetFiles(directory.PerformancePath, "performance-*.ndjson");
        Assert.Equal(2, files.Length);
        Assert.Equal(2 * 8192, files.Sum(path => new FileInfo(path).Length));
        Assert.Equal(1, store.Health.DroppedEvents);
        Assert.Equal(0, store.Health.WrittenEvents);
        Assert.True(store.Health.WriterFailures > 0);
        Assert.False(store.Health.Healthy);
        Assert.Equal("preserve", File.ReadAllText(unrelated));
    }

    [Fact]
    public void OversizedLockedFileReducesNewFileCapacityToKeepAggregateBudgetBounded()
    {
        using var directory = new TestDirectory();
        Directory.CreateDirectory(directory.PerformancePath);
        var oversized = System.IO.Path.Combine(directory.PerformancePath, "performance-20000101-a.ndjson");
        File.WriteAllText(oversized, new string('x', 20000));
        using var fileLock = new FileStream(oversized, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var store = new PerformanceDiagnosticStore(new() { PerformanceRetainedFileCount = 3 }, directory.Path,
            fileSizeLimitBytes: 8192, beforeDelete: path => SimulateUnixDeleteLock(path, oversized));
        var telemetry = new PremiereTelemetry(store);
        for (var index = 0; index < 100; index++) telemetry.RecordDiagnostic("partial.budget", details: new() { Sequence = index });
        store.Flush();
        var files = Directory.GetFiles(directory.PerformancePath, "performance-*.ndjson");
        Assert.InRange(files.Length, 2, 3);
        Assert.True(files.Sum(path => new FileInfo(path).Length) <= 3 * 8192);
        Assert.All(files.Where(path => path != oversized), path => Assert.InRange(new FileInfo(path).Length, 1, 3 * 8192 - 20000));
        Assert.Equal(20000, new FileInfo(oversized).Length);
        Assert.Equal(0, store.Health.DroppedEvents);
        Assert.True(store.Health.WriterFailures > 0);
    }

    [Fact]
    public void OversizedUndeletableFileCannotCauseAdditionalBudgetGrowth()
    {
        using var directory = new TestDirectory();
        Directory.CreateDirectory(directory.PerformancePath);
        var oversized = System.IO.Path.Combine(directory.PerformancePath, "performance-20000101-a.ndjson");
        File.WriteAllText(oversized, new string('x', 30000));
        using var fileLock = new FileStream(oversized, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var store = new PerformanceDiagnosticStore(new() { PerformanceRetainedFileCount = 3 }, directory.Path,
            fileSizeLimitBytes: 8192, beforeDelete: path => SimulateUnixDeleteLock(path, oversized));
        new PremiereTelemetry(store).RecordDiagnostic("no.remaining.budget");
        store.Flush();
        Assert.Equal(oversized, Assert.Single(Directory.GetFiles(directory.PerformancePath, "performance-*.ndjson")));
        Assert.Equal(30000, new FileInfo(oversized).Length);
        Assert.Equal(1, store.Health.DroppedEvents);
        Assert.False(store.Health.Healthy);
    }

    [Fact]
    public async Task IdlePeriodicAndExplicitFlushesDoNotFsyncAlreadyDurableData()
    {
        using var directory = new TestDirectory();
        var flushes = 0;
        using var store = new PerformanceDiagnosticStore(new() { PerformanceFlushIntervalSeconds = 1 }, directory.Path,
            beforeFlush: () => Interlocked.Increment(ref flushes));
        var telemetry = new PremiereTelemetry(store);
        telemetry.RecordDiagnostic("dirty.first");
        store.Flush();
        Assert.Equal(1, Volatile.Read(ref flushes));
        await Task.Delay(2200);
        Assert.Equal(1, Volatile.Read(ref flushes));
        store.Flush();
        Assert.Equal(1, Volatile.Read(ref flushes));
        telemetry.RecordDiagnostic("dirty.second");
        store.Flush();
        Assert.Equal(2, Volatile.Read(ref flushes));
        await Task.Delay(1100);
        store.Dispose();
        Assert.Equal(2, Volatile.Read(ref flushes));
        Assert.Equal(2, ReadEvents(directory.Path).Count);
    }

    [Fact]
    public async Task DelayedCleanBarrierClearsTimeoutHealthWithoutNewWritesOrFsync()
    {
        using var directory = new TestDirectory();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var delayNextBarrier = 0;
        var flushes = 0;
        using var store = new PerformanceDiagnosticStore(new(), directory.Path, flushTimeout: TimeSpan.FromMilliseconds(100),
            beforeFlush: () => Interlocked.Increment(ref flushes), beforeBarrier: () =>
            {
                if (Interlocked.Exchange(ref delayNextBarrier, 0) != 0) { entered.Set(); release.Wait(); }
            });
        new PremiereTelemetry(store).RecordDiagnostic("already.durable");
        store.Flush();
        await WaitForHealthy();
        Assert.Equal(1, Volatile.Read(ref flushes));
        var previousTimeouts = store.Health.FlushTimeouts;
        Interlocked.Exchange(ref delayNextBarrier, 1);
        try
        {
            var delayedFlush = Task.Run(store.Flush);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            await delayedFlush.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(previousTimeouts + 1, store.Health.FlushTimeouts);
            Assert.False(store.Health.Healthy);
            Assert.Equal(0, store.Health.ActiveIoMs);
        }
        finally { release.Set(); }
        await WaitForHealthy();
        Assert.Equal(1, Volatile.Read(ref flushes));
        Assert.Equal(1, store.Health.WrittenEvents);

        async Task WaitForHealthy()
        {
            var started = Stopwatch.GetTimestamp();
            while (!store.Health.Healthy && Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(3)) await Task.Delay(25);
            Assert.True(store.Health.Healthy);
        }
    }

    private static void SimulateUnixDeleteLock(string path, params string[] blocked)
    {
        // Windows uses the real FileShare.Read handles above; Unix unlink semantics need this fixture hook.
        if (!OperatingSystem.IsWindows() && blocked.Contains(path, StringComparer.Ordinal))
            throw new IOException("Fixture file is temporarily undeletable.");
    }

    private static List<JsonElement> ReadEvents(string root)
        => Directory.GetFiles(System.IO.Path.Combine(root, "App_Data", "logs", "performance"), "performance-*.ndjson")
            .OrderBy(path => path, StringComparer.Ordinal).SelectMany(path => ReadActiveFile(path).Split('\n', StringSplitOptions.RemoveEmptyEntries))
            .Select(line => { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); }).ToList();

    private static string ReadActiveFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "premiere-diagnostics-tests", Guid.NewGuid().ToString("N"));
        public string PerformancePath => System.IO.Path.Combine(Path, "App_Data", "logs", "performance");
        public TestDirectory() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
