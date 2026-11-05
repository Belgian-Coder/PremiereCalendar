using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class PerformanceTraceArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "premiere-traces-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void ArchivePreservesEveryEventAndIndexesWallAndProcessTime()
    {
        var path = CreateTrace();
        var original = File.ReadAllText(path);
        PerformanceTraceArchive.Archive(path, 1024 * 1024);
        Assert.False(File.Exists(path));
        using var gzip = new GZipStream(File.OpenRead(path + ".gz"), CompressionMode.Decompress);
        using var reader = new StreamReader(gzip);
        Assert.Equal(original, reader.ReadToEnd());
        using var index = JsonDocument.Parse(File.ReadAllText(path + ".gz.index.json"));
        var value = index.RootElement;
        Assert.Equal(100, value.GetProperty("records").GetInt64());
        Assert.Equal(0, value.GetProperty("minimumProcessElapsedMilliseconds").GetDouble());
        Assert.Equal(99, value.GetProperty("maximumProcessElapsedMilliseconds").GetDouble());
        Assert.True(value.GetProperty("complete").GetBoolean());
        Assert.Equal(new FileInfo(path + ".gz").Length, value.GetProperty("compressedBytes").GetInt64());
    }

    [Fact]
    public void InterruptedIndexPublicationRetainsRawAndCanBeRetried()
    {
        var path = CreateTrace();
        var original = File.ReadAllText(path);
        Assert.Throws<IOException>(() => PerformanceTraceArchive.Archive(path, 1024 * 1024,
            beforePublishIndex: () => throw new IOException("simulated interruption")));
        Assert.Equal(original, File.ReadAllText(path));
        Assert.True(File.Exists(path + ".gz"));
        Assert.False(File.Exists(path + ".gz.index.json"));
        Assert.Empty(Directory.GetFiles(_root, "*.pending"));
        PerformanceTraceArchive.Archive(path, 1024 * 1024);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(path + ".gz.index.json"));
    }

    [Fact]
    public void UnterminatedTraceIsArchivedAsIncompleteEvidence()
    {
        var path = CreateTrace();
        File.AppendAllText(path, "{\"event\":");
        PerformanceTraceArchive.Archive(path, 1024 * 1024);
        using var index = JsonDocument.Parse(File.ReadAllText(path + ".gz.index.json"));
        Assert.False(index.RootElement.GetProperty("complete").GetBoolean());
        Assert.Equal(1, index.RootElement.GetProperty("malformedRecords").GetInt64());
    }

    [Fact]
    public void UnrelatedFileIsNeverArchived()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "performance-my-notes.ndjson");
        File.WriteAllText(path, "preserve");
        PerformanceTraceArchive.Archive(path, 1024 * 1024);
        Assert.Equal("preserve", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_root));
    }

    [Fact]
    public void ActiveWriterLeasePreventsArchivalUntilTheTraceCloses()
    {
        using var store = new PerformanceDiagnosticStore(new() { PerformanceArchiveClosedFiles = false }, _root);
        var telemetry = new PremiereTelemetry(store);
        for (var index = 0; index < 100; index++) telemetry.RecordDiagnostic("active.writer");
        store.Flush();
        var path = Assert.Single(Directory.GetFiles(Path.Combine(_root, "App_Data/logs/performance"), "*.ndjson"));
        Assert.False(PerformanceTraceArchive.Archive(path, 1024 * 1024));
        Assert.True(File.Exists(path));
        store.Dispose();
        PerformanceTraceArchive.Archive(path, 1024 * 1024);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(path + ".gz"));
    }

    [Fact]
    public async Task SmallUnarchivableTraceDoesNotStarveALaterCompressibleTrace()
    {
        Directory.CreateDirectory(_root);
        var small = Path.Combine(_root, $"performance-20260101T1800000000000Z-{Guid.NewGuid():N}-000001.ndjson");
        File.WriteAllText(small, "{}\n");
        var large = CreateTrace();
        var worker = new PerformanceTraceArchiveWorker(_root, 1024 * 1024, TimeSpan.Zero, new object());
        try
        {
            var now = DateTimeOffset.UtcNow.AddMinutes(1);
            for (var tick = 0; tick < 4 && File.Exists(large); tick++) await worker.RunOnceAsync(now.AddMinutes(tick));
            Assert.True(File.Exists(small));
            Assert.False(File.Exists(large));
            Assert.True(File.Exists(large + ".gz"));
        }
        finally { worker.RequestStop(); await worker.Completion.WaitAsync(TimeSpan.FromSeconds(3)); }
    }

    [Theory]
    [InlineData(".gz.pending")]
    [InlineData(".gz")]
    public void InterruptedArchiveNearFileLimitRemovesCompanionsAndPreservesAuthoritativeRaw(string suffix)
    {
        Directory.CreateDirectory(_root);
        var raw = Path.Combine(_root, $"performance-20260101T1800000000000Z-{Guid.NewGuid():N}-000001.ndjson");
        var original = new string('x', 8192);
        File.WriteAllText(raw, original);
        File.WriteAllText(raw + suffix, new string('y', 8192));
        File.WriteAllText(raw + ".gz.index.json.pending", "interrupted index");
        var unrelated = raw + ".gz.notes";
        File.WriteAllText(unrelated, "preserve unrelated notes");
        using var store = new PerformanceDiagnosticStore(new()
        {
            PerformanceDiagnosticsDirectory = _root, PerformanceArchiveClosedFiles = false, PerformanceRetainedFileCount = 2
        }, _root, fileSizeLimitBytes: 8192);
        new PremiereTelemetry(store).RecordDiagnostic("after.archive.crash");
        store.Flush();
        Assert.Equal(original, File.ReadAllText(raw));
        Assert.False(File.Exists(raw + suffix));
        Assert.False(File.Exists(raw + ".gz.index.json.pending"));
        Assert.Equal("preserve unrelated notes", File.ReadAllText(unrelated));
        Assert.Equal(0, store.Health.DroppedEvents);
        Assert.Equal(0, store.Health.WriterFailures);
        Assert.True(Directory.GetFiles(_root, "*.ndjson").Sum(path => new FileInfo(path).Length) <= 2 * 8192);
    }

    [Fact]
    public async Task SlowArchiveDoesNotBlockEventCaptureOrFlushAndShutdownJoinsIt()
    {
        CreateTrace();
        using var entered = new ManualResetEventSlim();
        using var store = new PerformanceDiagnosticStore(new()
        {
            PerformanceDiagnosticsDirectory = _root, PerformanceArchiveAfterMinutes = 0, PerformanceRetainedFileCount = 8
        }, _root, beforeArchive: token => { entered.Set(); token.WaitHandle.WaitOne(); token.ThrowIfCancellationRequested(); });
        var telemetry = new PremiereTelemetry(store);
        telemetry.RecordDiagnostic("starts.archive.worker");
        Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
        await Task.Run(() =>
        {
            for (var index = 0; index < 1000; index++) telemetry.RecordDiagnostic("during.slow.archive");
            store.Flush();
        }).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(0, store.Health.DroppedEvents);
        Assert.Equal(0, store.Health.WriterFailures);
        Assert.Equal(0, store.Health.FlushTimeouts);
        Assert.True(store.Health.ArchiveActive);
        Assert.InRange(store.Health.ArchivePending, 0, 1);
        Assert.False(store.Health.WriterStalled);
        await store.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        await store.Completion.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(store.Health.ArchiveWorkerRunning);
        Assert.False(store.Health.WriterRunning);
        Assert.Equal(0, store.Health.ArchiveFailures);
        Assert.Empty(Directory.GetFiles(_root, "*.pending"));
    }

    [Fact]
    public async Task AnotherWriterLeaseSkipsArchivalWithoutFailureOrLossMarkers()
    {
        var raw = CreateTrace();
        using var held = PerformanceTraceArchive.AcquireLease(raw);
        using var store = new PerformanceDiagnosticStore(new()
        {
            PerformanceDiagnosticsDirectory = _root, PerformanceArchiveAfterMinutes = 0
        }, _root);
        new PremiereTelemetry(store).RecordDiagnostic("other.writer.active");
        store.Flush();
        await store.ArchiveClosedTracesAsync(DateTimeOffset.UtcNow.AddMinutes(1));
        Assert.True(File.Exists(raw));
        Assert.Equal(0, store.Health.ArchiveFailures);
        Assert.Equal(0, store.Health.WriterFailures);
        Assert.Equal(0, store.Health.DroppedEvents);
        var path = Assert.Single(Directory.GetFiles(_root, $"*{store.InstanceId}*.ndjson"));
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        Assert.DoesNotContain("diagnostics.loss", reader.ReadToEnd());
    }

    [Fact]
    public async Task RetentionCannotResurrectAnArchiveAfterConcurrentPublicationAndEviction()
    {
        var raw = CreateTrace(20);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var store = new PerformanceDiagnosticStore(new()
        {
            PerformanceDiagnosticsDirectory = _root, PerformanceArchiveAfterMinutes = 0, PerformanceRetainedFileCount = 3
        }, _root, fileSizeLimitBytes: 8192, beforeArchive: token => { entered.Set(); release.Wait(token); });
        var telemetry = new PremiereTelemetry(store);
        telemetry.RecordDiagnostic("starts.concurrent.archive");
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            for (var index = 0; index < 200; index++) telemetry.RecordDiagnostic("retention.while.archiving");
            store.Flush();
            Assert.True(File.Exists(raw));
            release.Set();
            var timeout = Stopwatch.StartNew();
            while (store.Health.ArchiveActive && timeout.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(10);
            Assert.False(store.Health.ArchiveActive);
            for (var index = 0; index < 200; index++) telemetry.RecordDiagnostic("retention.after.publication");
            store.Flush();
            Assert.False(File.Exists(raw));
            Assert.False(File.Exists(raw + ".gz"));
            Assert.False(File.Exists(raw + ".gz.index.json"));
            Assert.Equal(0, store.Health.DroppedEvents);
            Assert.Equal(0, store.Health.WriterFailures);
        }
        finally { release.Set(); }
        await store.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Empty(Directory.GetFiles(_root, "*.pending"));
        Assert.True(Directory.GetFiles(_root, "performance-*").Sum(path => new FileInfo(path).Length) <= 3 * 8192);
    }

    [Fact]
    public void InvalidNumericSerializationDropsOnlyThatEventAndWriterContinues()
    {
        using var store = new PerformanceDiagnosticStore(new(), _root);
        store.Write(new(DateTimeOffset.UtcNow, double.NaN, store.InstanceId, 1, "invalid.number",
            null, null, null, null, null, null, null, null));
        new PremiereTelemetry(store).RecordDiagnostic("valid.after.serialization.failure");
        store.Flush();
        Assert.Equal(1, store.Health.SerializationFailures);
        Assert.Equal(1, store.Health.DroppedEvents);
        Assert.Equal(0, store.Health.WriterFailures);
        Assert.True(store.Health.Healthy);
        var content = string.Join("\n", Directory.GetFiles(Path.Combine(_root, "App_Data/logs/performance"))
            .Select(path => { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); using var reader = new StreamReader(stream); return reader.ReadToEnd(); }));
        Assert.Contains("valid.after.serialization.failure", content);
        Assert.Contains("diagnostics.loss", content);
        Assert.Contains("\"serializationFailures\":1", content);
    }

    [Fact]
    public async Task BlockedWriterBoundsSnapshotMemoryIndependentlyOfEntryCapacity()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var store = new PerformanceDiagnosticStore(new TelemetryOptions { PerformanceQueueMemoryMegabytes = 1 }, _root,
            queueCapacity: 4096, beforeWrite: () => { entered.Set(); release.Wait(); });
        var telemetry = new PremiereTelemetry(store);
        try
        {
            telemetry.RecordDiagnostic("blocked.storage");
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
            await Task.Run(() => { for (var index = 0; index < 4096; index++) telemetry.RecordDiagnostic("bounded.snapshot"); })
                .WaitAsync(TimeSpan.FromSeconds(3));
            Assert.InRange(store.Health.QueuedBytes, 1, 1024 * 1024);
            Assert.InRange(store.Health.QueuedEvents, 1, 1024);
            Assert.True(store.Health.DroppedEvents > 0);
        }
        finally { release.Set(); }
        store.Flush();
        Assert.Equal(0, store.Health.QueuedBytes);
    }

    private string CreateTrace(int records = 100)
    {
        Directory.CreateDirectory(_root);
        var instance = Guid.NewGuid().ToString("N");
        var path = Path.Combine(_root, $"performance-20261105T1800000000000Z-{instance}-000001.ndjson");
        var text = new StringBuilder();
        for (var index = 0; index < records; index++)
            text.AppendLine(JsonSerializer.Serialize(new { timestampUtc = new DateTimeOffset(2026, 11, 5, 18, 0, 0, TimeSpan.Zero).AddSeconds(index),
                instanceId = instance, processElapsedMilliseconds = index, eventSequence = index + 1, @event = "source.item.completed" }));
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
        return path;
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}
