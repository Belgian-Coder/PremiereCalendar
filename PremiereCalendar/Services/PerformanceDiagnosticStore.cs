using System.Globalization;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

/// <summary>Bounded nonblocking capture with independent single-writer persistence and archival workers.</summary>
public sealed partial class PerformanceDiagnosticStore : IDisposable, IHostedService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
    private const int MaximumEventBytes = 8192;
    private readonly bool _enabled;
    private readonly string _directory;
    private readonly long _fileSizeLimit;
    private readonly int _retainedFileCount;
    private readonly object _filesGate = new();
    private readonly HashSet<string> _artifactBlocked = new(StringComparer.Ordinal);
    private readonly PerformanceTraceArchiveWorker? _archiveWorker;
    private readonly Task _completion = Task.CompletedTask;
    private long _archiveMaintenanceFailures;
    private int _archiveReady;
    private readonly int _queueCapacity;
    private readonly long _queueByteCapacity;
    private readonly TimeSpan _flushInterval;
    private readonly TimeSpan _flushTimeout;
    private readonly TimeSpan _shutdownTimeout;
    private readonly Channel<QueueEntry>? _queue;
    private readonly CancellationTokenSource _abort = new();
    private readonly Task? _writerTask;
    private readonly Action? _beforeWrite;
    private readonly Action<string>? _beforeDelete;
    private readonly Action? _beforeFlush;
    private readonly Action? _beforeBarrier;
    private readonly object _lifecycleGate = new();
    private Task? _shutdownTask;
    private int _stopping;
    private int _writerRunning;
    private int _writerHealthy = 1;
    private int _flushTimedOut;
    private int _memoryFull;
    private int _shutdownTimedOut;
    private long _queuedEvents;
    private long _queuedBytes;
    private long _serializationFailures;
    private long _writtenEvents;
    private long _droppedEvents;
    private long _writerFailures;
    private long _flushTimeouts;
    private long _lastSuccessTicks;
    private long _lastFailureTicks;
    private long _activeIoStarted;
    // Only the writer task accesses the following fields.
    private FileStream? _stream;
    private FileStream? _streamLease;
    private long _currentLength;
    private long _currentFileLimit;
    private bool _dirty;
    private long _reportedDroppedEvents;
    private long _reportedWriterFailures;
    private int _fileSequence;

    internal bool Enabled => _enabled;
    internal string InstanceId { get; } = Guid.NewGuid().ToString("N");
    internal Task Completion => _completion;

    public PerformanceDiagnosticStore(IOptions<TelemetryOptions> options, IWebHostEnvironment environment)
        : this(options.Value, environment.ContentRootPath) { }

    internal PerformanceDiagnosticStore(TelemetryOptions options, string contentRoot, long? fileSizeLimitBytes = null,
        int? queueCapacity = null, Action? beforeWrite = null, TimeSpan? flushTimeout = null, TimeSpan? shutdownTimeout = null,
        Action<string>? beforeDelete = null, Action? beforeFlush = null, Action? beforeBarrier = null,
        Action<CancellationToken>? beforeArchive = null)
    {
        _enabled = options.PerformanceDiagnosticsEnabled;
        var directory = string.IsNullOrWhiteSpace(options.PerformanceDiagnosticsDirectory)
            ? Path.Combine(options.LogDirectory, "performance") : options.PerformanceDiagnosticsDirectory;
        _directory = Path.GetFullPath(Path.IsPathFullyQualified(directory) ? directory : Path.Combine(contentRoot, directory));
        _fileSizeLimit = fileSizeLimitBytes ?? Math.Clamp(options.PerformanceFileSizeLimitMegabytes, 1, 1024) * 1024L * 1024L;
        _retainedFileCount = Math.Clamp(options.PerformanceRetainedFileCount, 1, 100);
        _queueCapacity = queueCapacity ?? Math.Clamp(options.PerformanceQueueCapacity, 256, 32768);
        _queueByteCapacity = Math.Clamp(options.PerformanceQueueMemoryMegabytes, 1, 256) * 1024L * 1024L;
        _flushInterval = TimeSpan.FromSeconds(Math.Clamp(options.PerformanceFlushIntervalSeconds, 1, 60));
        _flushTimeout = flushTimeout ?? TimeSpan.FromSeconds(Math.Clamp(options.PerformanceFlushTimeoutSeconds, 1, 30));
        _shutdownTimeout = shutdownTimeout ?? TimeSpan.FromSeconds(Math.Clamp(options.PerformanceShutdownTimeoutSeconds, 1, 15));
        _beforeWrite = beforeWrite;
        _beforeDelete = beforeDelete;
        _beforeFlush = beforeFlush;
        _beforeBarrier = beforeBarrier;
        if (_fileSizeLimit < MaximumEventBytes) throw new ArgumentOutOfRangeException(nameof(fileSizeLimitBytes));
        if (_queueCapacity < 1) throw new ArgumentOutOfRangeException(nameof(queueCapacity));
        if (_enabled)
        {
            _queue = Channel.CreateBounded<QueueEntry>(new BoundedChannelOptions(_queueCapacity)
            {
                SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.Wait
            });
            if (options.PerformanceArchiveClosedFiles)
                _archiveWorker = new PerformanceTraceArchiveWorker(_directory, _fileSizeLimit,
                    TimeSpan.FromMinutes(Math.Clamp(options.PerformanceArchiveAfterMinutes, 0, 1440)), _filesGate, beforeArchive);
            Volatile.Write(ref _writerRunning, 1);
            _writerTask = Task.Run(WriteLoopAsync);
            _completion = Task.WhenAll(_writerTask, _archiveWorker?.Completion ?? Task.CompletedTask);
        }
    }

    public PerformanceDiagnosticHealth Health
    {
        get
        {
            var queued = Math.Max(0, Interlocked.Read(ref _queuedEvents));
            var full = _enabled && (Volatile.Read(ref _memoryFull) != 0 || (_queue?.Reader.Count ?? 0) >= _queueCapacity);
            var running = Volatile.Read(ref _writerRunning) != 0;
            var shutdownTimedOut = Volatile.Read(ref _shutdownTimedOut) != 0;
            var ioStarted = Interlocked.Read(ref _activeIoStarted);
            var activeIoMs = ioStarted == 0 ? 0 : Math.Clamp(Stopwatch.GetElapsedTime(ioStarted).TotalMilliseconds, 0, 86_400_000);
            var stalled = activeIoMs >= _flushTimeout.TotalMilliseconds;
            var healthy = !_enabled || (Volatile.Read(ref _writerHealthy) != 0 && Volatile.Read(ref _flushTimedOut) == 0
                && !shutdownTimedOut && !stalled && !full && (running || Volatile.Read(ref _stopping) != 0));
            return new(_enabled, healthy, Interlocked.Read(ref _writtenEvents), Interlocked.Read(ref _droppedEvents),
                Interlocked.Read(ref _writerFailures), ReadDate(ref _lastSuccessTicks), ReadDate(ref _lastFailureTicks),
                queued, _enabled ? _queueCapacity : 0, full, running, Interlocked.Read(ref _flushTimeouts), shutdownTimedOut, stalled, activeIoMs,
                Math.Max(0, Interlocked.Read(ref _queuedBytes)), _enabled ? _queueByteCapacity : 0,
                Interlocked.Read(ref _serializationFailures),
                Interlocked.Read(ref _archiveMaintenanceFailures) + (_archiveWorker?.Failures ?? 0),
                _archiveWorker?.Active ?? false, _archiveWorker?.Pending ?? 0, _archiveWorker?.Running ?? false);
        }
    }

    internal void Write(PerformanceDiagnosticEvent diagnosticEvent)
    {
        if (!_enabled) return;
        // The schema consists only of immutable records, scalar values and strings.
        // Reserve a conservative managed-memory estimate before retaining a snapshot;
        // serialization and its UTF-8 allocation belong to the single writer.
        var reservedBytes = diagnosticEvent.EstimatedSnapshotBytes;
        if (reservedBytes > 16 * 1024 || Volatile.Read(ref _stopping) != 0)
        {
            Interlocked.Increment(ref _droppedEvents);
            return;
        }
        if (Interlocked.Add(ref _queuedBytes, reservedBytes) > _queueByteCapacity)
        {
            Interlocked.Add(ref _queuedBytes, -reservedBytes);
            Volatile.Write(ref _memoryFull, 1);
            Interlocked.Increment(ref _droppedEvents);
            return;
        }
        Interlocked.Increment(ref _queuedEvents);
        if (!_queue!.Writer.TryWrite(new QueueEntry(diagnosticEvent, null, reservedBytes)))
        {
            Interlocked.Decrement(ref _queuedEvents);
            Interlocked.Add(ref _queuedBytes, -reservedBytes);
            Interlocked.Increment(ref _droppedEvents);
        }
    }

    /// <summary>Waits for a FIFO durability barrier, bounded by PerformanceFlushTimeoutSeconds.</summary>
    public void Flush()
    {
        if (!_enabled || Volatile.Read(ref _stopping) != 0) return;
        FlushAsync().GetAwaiter().GetResult();
    }

    private async Task FlushAsync()
    {
        using var timeout = new CancellationTokenSource(_flushTimeout);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await _queue!.Writer.WriteAsync(new QueueEntry(null, completion, 0), timeout.Token).ConfigureAwait(false);
            await completion.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            Interlocked.Increment(ref _flushTimeouts);
            Volatile.Write(ref _flushTimedOut, 1);
        }
        catch (ChannelClosedException) { }
    }

    private async Task WriteLoopAsync()
    {
        using var timer = new PeriodicTimer(_flushInterval);
        var token = _abort.Token;
        try
        {
            var readable = _queue!.Reader.WaitToReadAsync(token).AsTask();
            var tick = timer.WaitForNextTickAsync(token).AsTask();
            while (true)
            {
                await Task.WhenAny(readable, tick).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (tick.IsCompleted)
                {
                    if (!await tick.ConfigureAwait(false)) break;
                    FlushWriter();
                    ScheduleArchive(DateTimeOffset.UtcNow);
                    tick = timer.WaitForNextTickAsync(token).AsTask();
                }
                if (readable.IsCompleted)
                {
                    if (!await readable.ConfigureAwait(false)) break;
                    for (var count = 0; count < 256 && !token.IsCancellationRequested && _queue.Reader.TryRead(out var entry); count++)
                    {
                        if (entry.Event is { } diagnosticEvent)
                        {
                            Interlocked.Decrement(ref _queuedEvents);
                            Interlocked.Add(ref _queuedBytes, -entry.ReservedBytes);
                            Volatile.Write(ref _memoryFull, 0);
                            WriteEvent(diagnosticEvent);
                        }
                        else
                        {
                            _beforeBarrier?.Invoke();
                            entry.Completion!.TrySetResult(FlushWriter());
                        }
                    }
                    readable = _queue.Reader.WaitToReadAsync(token).AsTask();
                }
            }
            FlushWriter();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            // Secondary diagnostics must not fault provider/UI work. Never retain the exception.
            RecordFailure();
        }
        finally
        {
            _queue!.Writer.TryComplete();
            while (_queue.Reader.TryRead(out var entry))
            {
                if (entry.Event is not null)
                {
                    Interlocked.Decrement(ref _queuedEvents);
                    Interlocked.Add(ref _queuedBytes, -entry.ReservedBytes);
                    Interlocked.Increment(ref _droppedEvents);
                }
                else entry.Completion!.TrySetResult(false);
            }
            CloseStream();
            _archiveWorker?.RequestStop();
            Volatile.Write(ref _writerRunning, 0);
            _abort.Dispose();
        }
    }

    private void WriteEvent(PerformanceDiagnosticEvent diagnosticEvent)
    {
        try
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(diagnosticEvent, JsonOptions);
            if (bytes.Length + 1 > MaximumEventBytes || !TryWriteBytes(bytes))
                Interlocked.Increment(ref _droppedEvents);
        }
        catch (OperationCanceledException) when (_abort.IsCancellationRequested)
        {
            Interlocked.Increment(ref _droppedEvents);
            throw;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            Interlocked.Increment(ref _serializationFailures);
            Interlocked.Increment(ref _droppedEvents);
        }
    }

    private bool TryWriteBytes(byte[] bytes)
    {
        var io = BeginIo();
        try
        {
            _beforeWrite?.Invoke();
            if (_stream is null || _currentLength + bytes.Length + 1 > _currentFileLimit) Rotate(bytes.Length + 1);
            _stream!.Write(bytes);
            _stream.WriteByte((byte)'\n');
            _currentLength += bytes.Length + 1;
            _dirty = true;
            Interlocked.Increment(ref _writtenEvents);
            Interlocked.Exchange(ref _lastSuccessTicks, DateTimeOffset.UtcNow.UtcTicks);
            return true;
        }
        catch (Exception exception) when (IsWriterFailure(exception))
        {
            RecordFailure();
            CloseStream(flushDirty: false);
            return false;
        }
        finally { EndIo(io); }
    }

    private bool FlushWriter()
    {
        var dropped = Interlocked.Read(ref _droppedEvents);
        var failures = Interlocked.Read(ref _writerFailures);
        var markerWritten = true;
        if (dropped > _reportedDroppedEvents || failures > _reportedWriterFailures)
        {
            var marker = JsonSerializer.SerializeToUtf8Bytes(new
            {
                TimestampUtc = DateTimeOffset.UtcNow, Event = "diagnostics.loss", InstanceId,
                DroppedEvents = dropped, DroppedEventsSinceLastReport = dropped - _reportedDroppedEvents,
                WriterFailures = failures, WriterFailuresSinceLastReport = failures - _reportedWriterFailures,
                QueueCapacity = _queueCapacity, QueueByteCapacity = _queueByteCapacity,
                SerializationFailures = Interlocked.Read(ref _serializationFailures)
            }, JsonOptions);
            markerWritten = TryWriteBytes(marker);
            if (markerWritten) { _reportedDroppedEvents = dropped; _reportedWriterFailures = failures; }
        }
        if (_stream is null || !_dirty)
        {
            var alreadyDurable = markerWritten && Volatile.Read(ref _writerHealthy) != 0;
            if (alreadyDurable) Volatile.Write(ref _flushTimedOut, 0);
            return alreadyDurable;
        }
        if (!FlushDirtyStream())
        {
            CloseStream(flushDirty: false);
            return false;
        }
        if (markerWritten) Volatile.Write(ref _writerHealthy, 1);
        return markerWritten;
    }

    private bool FlushDirtyStream()
    {
        if (_stream is null || !_dirty) return true;
        var io = BeginIo();
        try
        {
            _beforeFlush?.Invoke();
            _stream.Flush(flushToDisk: true);
            _dirty = false;
            Volatile.Write(ref _flushTimedOut, 0);
            return true;
        }
        catch (Exception exception) when (IsWriterFailure(exception))
        {
            RecordFailure();
            return false;
        }
        finally { EndIo(io); }
    }

    private void Rotate(int requiredBytes)
    {
        lock (_filesGate) RotateCore(requiredBytes);
    }

    private void RotateCore(int requiredBytes)
    {
        CloseStream();
        Directory.CreateDirectory(_directory);
        CleanupInterruptedTraceArtifacts();
        var budget = _fileSizeLimit * _retainedFileCount;
        var attempted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in RetainedFiles().Where(file => file.BodyLength > _fileSizeLimit)) TryDelete(file);
        while (true)
        {
            var remaining = RetainedFiles();
            if (remaining.Length < _retainedFileCount && remaining.Sum(file => file.Length) <= budget - _fileSizeLimit) break;
            var candidate = remaining.FirstOrDefault(file => !attempted.Contains(file.FullName));
            if (candidate is null) break;
            TryDelete(candidate);
        }
        // Recount actual files after all attempts; undeletable files still consume slots and bytes.
        var actual = RetainedFiles();
        var availableBytes = budget - actual.Sum(file => file.Length);
        if (actual.Length >= _retainedFileCount || availableBytes < requiredBytes)
            throw new IOException("Diagnostic retention capacity is blocked.");
        _currentFileLimit = Math.Min(_fileSizeLimit, availableBytes);
        var name = $"performance-{DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffffZ", CultureInfo.InvariantCulture)}-{InstanceId}-{++_fileSequence:D6}.ndjson";
        var path = Path.Combine(_directory, name);
        _streamLease = PerformanceTraceArchive.AcquireLease(path);
        try { _stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 64 * 1024); }
        catch { _streamLease.Dispose(); _streamLease = null; throw; }
        _currentLength = 0;
        Volatile.Write(ref _archiveReady, 1);
        ScheduleArchive(DateTimeOffset.UtcNow);

        void TryDelete(TraceFile file)
        {
            attempted.Add(file.FullName);
            if (_artifactBlocked.Contains(file.FullName)) return;
            try
            {
                using var lease = PerformanceTraceArchive.TryAcquireLease(file.FullName);
                if (lease is null) return; // A live writer or archiver still owns this trace.
                // Re-read paths under the lease; archival may have published since enumeration.
                foreach (var path in TracePaths(file.FullName))
                {
                    if (!File.Exists(path)) continue;
                    try { _beforeDelete?.Invoke(path); File.Delete(path); }
                    catch (Exception exception) when (IsWriterFailure(exception)) { RecordFailure(); }
                }
            }
            catch (Exception exception) when (IsWriterFailure(exception)) { RecordFailure(); }
        }
    }

    private TraceFile[] RetainedFiles()
        => Directory.EnumerateFiles(_directory, "performance-*")
            .Select(path => (Path: path, Raw: TraceBasePath(path)))
            .Where(entry => entry.Raw is not null)
            .GroupBy(entry => entry.Raw!, StringComparer.Ordinal)
            .Select(group => new TraceFile(group.Key, group.Select(entry => entry.Path).ToArray()))
            .OrderBy(file => file.Name, StringComparer.Ordinal).ToArray();

    [System.Text.RegularExpressions.GeneratedRegex(@"^performance-\d{8}(?:-[a-z0-9]+)?\.ndjson$", System.Text.RegularExpressions.RegexOptions.CultureInvariant)]
    private static partial System.Text.RegularExpressions.Regex LegacyTraceNameRegex();

    private static string? TraceBasePath(string path)
    {
        var raw = path;
        foreach (var suffix in new[] { ".gz.index.json.pending", ".gz.index.json", ".gz.pending", ".gz" })
            if (raw.EndsWith(suffix, StringComparison.Ordinal)) { raw = raw[..^suffix.Length]; break; }
        // Retain the historical date/name convention as well as current process-qualified files.
        return PerformanceTraceArchive.IsTrace(raw)
            || LegacyTraceNameRegex().IsMatch(Path.GetFileName(raw))
            ? raw : null;
    }

    private void ScheduleArchive(DateTimeOffset nowUtc)
    {
        if (Volatile.Read(ref _archiveReady) != 0 && Volatile.Read(ref _stopping) == 0)
            _archiveWorker?.Schedule(nowUtc);
    }

    internal Task ArchiveClosedTracesAsync(DateTimeOffset nowUtc)
        => _archiveWorker?.RunOnceAsync(nowUtc) ?? Task.CompletedTask;

    private void CleanupInterruptedTraceArtifacts()
    {
        _artifactBlocked.Clear();
        foreach (var file in RetainedFiles().Where(file => file.HasInterruptedArtifacts))
        {
            try
            {
                using var lease = PerformanceTraceArchive.TryAcquireLease(file.FullName);
                if (lease is null) continue;
                PerformanceTraceArchive.CleanupInterruptedArtifacts(file.FullName, _beforeDelete);
            }
            catch (Exception exception) when (IsWriterFailure(exception))
            {
                // Preserve authoritative raw if its expendable companions cannot be removed.
                _artifactBlocked.Add(file.FullName);
                Interlocked.Increment(ref _archiveMaintenanceFailures);
            }
        }
    }

    private static IEnumerable<string> TracePaths(string rawPath)
    {
        yield return rawPath;
        yield return rawPath + ".gz";
        yield return rawPath + ".gz.index.json";
        yield return rawPath + ".gz.pending";
        yield return rawPath + ".gz.index.json.pending";
    }

    private sealed record TraceFile(string FullName, string[] Paths)
    {
        public string Name => Path.GetFileName(FullName);
        public long BodyLength => ReadLength(FullName) is var raw && raw >= 0 ? raw : Math.Max(0, ReadLength(FullName + ".gz"));
        // Count physical remnants against aggregate retention, but never mistake
        // raw plus staging for one oversized authoritative body.
        public long Length => Math.Max(Paths.Sum(path => Math.Max(0, ReadLength(path))),
            ReadLength(FullName) is var raw && raw >= 0 ? raw
                : Math.Max(0, ReadLength(FullName + ".gz")) + Math.Max(0, ReadLength(FullName + ".gz.index.json")));
        public bool HasInterruptedArtifacts => Paths.Any(path => path.EndsWith(".pending", StringComparison.Ordinal))
            || File.Exists(FullName) && Paths.Any(path => !string.Equals(path, FullName, StringComparison.Ordinal));
        private static long ReadLength(string path)
        {
            try { return new FileInfo(path).Length; }
            catch (FileNotFoundException) { return -1; }
            catch (DirectoryNotFoundException) { return -1; }
        }
    }

    private void RecordFailure()
    {
        Interlocked.Increment(ref _writerFailures);
        Interlocked.Exchange(ref _lastFailureTicks, DateTimeOffset.UtcNow.UtcTicks);
        Volatile.Write(ref _writerHealthy, 0);
    }

    private void CloseStream(bool flushDirty = true)
    {
        if (_stream is null) return;
        var io = BeginIo();
        try
        {
            if (flushDirty) FlushDirtyStream();
            _stream?.Dispose();
        }
        catch (Exception exception) when (IsWriterFailure(exception)) { RecordFailure(); }
        finally
        {
            _stream = null;
            _streamLease?.Dispose();
            _streamLease = null;
            _currentLength = 0;
            _currentFileLimit = 0;
            EndIo(io);
        }
    }

    private long BeginIo()
    {
        var started = Stopwatch.GetTimestamp();
        return Interlocked.CompareExchange(ref _activeIoStarted, started, 0) == 0 ? started : 0;
    }

    private void EndIo(long owner)
    {
        if (owner != 0) Interlocked.Exchange(ref _activeIoStarted, 0);
    }

    private static bool IsWriterFailure(Exception exception)
        => exception is IOException or UnauthorizedAccessException or System.Security.SecurityException;

    private static DateTimeOffset? ReadDate(ref long ticks)
        => Interlocked.Read(ref ticks) is var value && value != 0 ? new DateTimeOffset(value, TimeSpan.Zero) : null;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
        => GetShutdownTask().WaitAsync(cancellationToken);

    private Task GetShutdownTask()
    {
        lock (_lifecycleGate) return _shutdownTask ??= ShutdownAsync();
    }

    private async Task ShutdownAsync()
    {
        Interlocked.Exchange(ref _stopping, 1);
        _archiveWorker?.RequestStop();
        _queue?.Writer.TryComplete();
        if (_writerTask is null) { _abort.Dispose(); return; }
        try
        {
            await _completion.WaitAsync(_shutdownTimeout).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Volatile.Write(ref _shutdownTimedOut, 1);
            try { _abort.Cancel(); }
            catch (ObjectDisposedException) { /* The writer completed at the timeout boundary. */ }
        }
    }

    public void Dispose() => GetShutdownTask().GetAwaiter().GetResult();

    private sealed record QueueEntry(PerformanceDiagnosticEvent? Event, TaskCompletionSource<bool>? Completion, int ReservedBytes);
}
