using System.Threading.Channels;

namespace PremiereCalendar.Services;

/// <summary>One bounded archive worker; capture and flush never wait for compression or archive fsync.</summary>
internal sealed class PerformanceTraceArchiveWorker
{
    private readonly string _directory;
    private readonly long _fileSizeLimit;
    private readonly TimeSpan _archiveAge;
    private readonly object _publicationGate;
    private readonly Action<CancellationToken>? _beforeArchive;
    private readonly Channel<Request> _requests = Channel.CreateBounded<Request>(new BoundedChannelOptions(1)
    {
        SingleReader = true, SingleWriter = false, AllowSynchronousContinuations = false,
        FullMode = BoundedChannelFullMode.Wait
    });
    private readonly CancellationTokenSource _shutdown = new();
    private IEnumerator<string>? _scan;
    private DateTimeOffset _nextArchiveUtc;
    private string? _cleanupRequired;
    private long _failures;
    private int _active;
    private int _running = 1;

    internal Task Completion { get; }
    internal long Failures => Interlocked.Read(ref _failures);
    internal bool Active => Volatile.Read(ref _active) != 0;
    internal bool Running => Volatile.Read(ref _running) != 0;
    internal int Pending => _requests.Reader.Count;

    internal PerformanceTraceArchiveWorker(string directory, long fileSizeLimit, TimeSpan archiveAge,
        object publicationGate, Action<CancellationToken>? beforeArchive = null)
    {
        _directory = directory;
        _fileSizeLimit = fileSizeLimit;
        _archiveAge = archiveAge;
        _publicationGate = publicationGate;
        _beforeArchive = beforeArchive;
        Completion = Task.Run(RunAsync);
    }

    internal void Schedule(DateTimeOffset nowUtc) => _requests.Writer.TryWrite(new(nowUtc, null));

    internal async Task RunOnceAsync(DateTimeOffset nowUtc)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _requests.Writer.WriteAsync(new(nowUtc, completion)).ConfigureAwait(false);
        await completion.Task.ConfigureAwait(false);
    }

    internal void RecordFailure() => Interlocked.Increment(ref _failures);

    internal void RequestStop()
    {
        _requests.Writer.TryComplete();
        try { _shutdown.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var request in _requests.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                try
                {
                    _shutdown.Token.ThrowIfCancellationRequested();
                    if (request.NowUtc >= _nextArchiveUtc)
                    {
                        _nextArchiveUtc = request.NowUtc.AddMinutes(1);
                        Volatile.Write(ref _active, 1);
                        ArchiveNext(request.NowUtc, _shutdown.Token);
                    }
                    request.Completion?.TrySetResult();
                }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
                { request.Completion?.TrySetCanceled(); throw; }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                    or System.Security.SecurityException or InvalidDataException)
                { RecordFailure(); request.Completion?.TrySetResult(); }
                finally { Volatile.Write(ref _active, 0); }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception) { RecordFailure(); }
        finally
        {
            _requests.Writer.TryComplete();
            while (_requests.Reader.TryRead(out var request)) request.Completion?.TrySetCanceled();
            _scan?.Dispose();
            _shutdown.Dispose();
            Volatile.Write(ref _running, 0);
        }
    }

    private void ArchiveNext(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_directory)) return;
        // One archiver per directory also bounds staging across application instances.
        using var directoryLease = PerformanceTraceArchive.TryAcquireLease(Path.Combine(_directory, "archive"));
        if (directoryLease is null) return;
        if (_cleanupRequired is { } previous)
        {
            using var lease = PerformanceTraceArchive.TryAcquireLease(previous);
            if (lease is null) return;
            lock (_publicationGate) PerformanceTraceArchive.CleanupInterruptedArtifacts(previous);
            _cleanupRequired = null;
        }
        var cutoff = nowUtc - _archiveAge;
        for (var examined = 0; examined < 16; examined++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _scan ??= Directory.EnumerateFiles(_directory, "performance-*.ndjson").GetEnumerator();
            if (!_scan.MoveNext()) { _scan.Dispose(); _scan = null; return; }
            var path = _scan.Current;
            if (!PerformanceTraceArchive.IsTrace(path) || File.GetLastWriteTimeUtc(path) > cutoff.UtcDateTime) continue;
            // Busy live traces cost one metadata probe, not this minute's compression slot.
            using (var probe = PerformanceTraceArchive.TryAcquireLease(path))
                if (probe is null) continue;
            try
            {
                PerformanceTraceArchive.Archive(path, _fileSizeLimit, cancellationToken: cancellationToken,
                    publicationGate: _publicationGate, beforeArchive: _beforeArchive);
            }
            catch (FileNotFoundException) { /* Retention won the lease before archival opened the raw trace. */ }
            catch
            {
                // A graceful failure can leave a published gzip beside authoritative raw.
                // Resolve that one trace before staging another; crashes are repaired by retention.
                _cleanupRequired = path;
                throw;
            }
            return;
        }
    }

    private sealed record Request(DateTimeOffset NowUtc, TaskCompletionSource? Completion);
}
