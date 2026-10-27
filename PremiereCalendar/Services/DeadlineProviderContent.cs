using System.Net;

namespace PremiereCalendar.Services;

// HttpClient buffers ResponseContentRead bodies after the handler has returned.
// Keep the same request deadline and adaptive slot alive across that boundary,
// including when a caller instead consumes a response as a stream.
internal sealed class DeadlineProviderContent : HttpContent
{
    private readonly HttpContent _inner;
    private readonly string _provider;
    private readonly TimeSpan? _requestTimeout;
    private readonly CancellationTokenSource? _timeoutSource;
    private readonly CancellationTokenSource _requestSource;
    private readonly CancellationToken _requestToken;
    private readonly CancellationToken _callerToken;
    private readonly ProviderExecutionLease? _lease;
    private readonly long? _contentLength;
    private readonly Action<ProviderExecutionResult, string>? _onCompleted;
    private long _bytesRead;
    private int _completed;
    private int _disposed;

    public DeadlineProviderContent(
        HttpContent inner,
        string provider,
        TimeSpan? requestTimeout,
        CancellationTokenSource? timeoutSource,
        CancellationTokenSource requestSource,
        CancellationToken callerToken,
        ProviderExecutionLease? lease,
        Action<ProviderExecutionResult, string>? onCompleted = null)
    {
        _inner = inner;
        _provider = provider;
        _requestTimeout = requestTimeout;
        _timeoutSource = timeoutSource;
        _requestSource = requestSource;
        _requestToken = requestSource.Token;
        _callerToken = callerToken;
        _lease = lease;
        _onCompleted = onCompleted;
        _contentLength = inner.Headers.ContentLength;
        foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
    }

    internal static TaskCanceledException TimeoutException(
        string provider, TimeSpan? requestTimeout, Exception exception, CancellationToken token)
        => new(
            $"The {provider} provider request exceeded its total timeout of {requestTimeout}.",
            new TimeoutException($"The {provider} provider request timed out.", exception),
            token);

    protected override bool TryComputeLength(out long length)
    {
        length = _inner.Headers.ContentLength ?? 0;
        return _inner.Headers.ContentLength.HasValue;
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_requestToken, cancellationToken);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            await _inner.CopyToAsync(stream, context, linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            Complete(ProviderExecutionResult.Success, "consumed");
        }
        catch (Exception exception)
        {
            var failure = RecordFailure(exception, cancellationToken);
            if (ReferenceEquals(failure, exception)) throw;
            throw failure;
        }
    }

    protected override void SerializeToStream(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        => SerializeToStreamAsync(stream, context, cancellationToken).GetAwaiter().GetResult();

    protected override Task<Stream> CreateContentReadStreamAsync()
        => CreateContentReadStreamAsync(CancellationToken.None);

    protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_requestToken, cancellationToken);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            var stream = await _inner.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
            return new DeadlineReadStream(stream, this, cancellationToken);
        }
        catch (Exception exception)
        {
            var failure = RecordFailure(exception, cancellationToken);
            if (ReferenceEquals(failure, exception)) throw;
            throw failure;
        }
    }

    protected override Stream CreateContentReadStream(CancellationToken cancellationToken)
        => CreateContentReadStreamAsync(cancellationToken).GetAwaiter().GetResult();

    private Exception RecordFailure(Exception exception, CancellationToken readToken)
    {
        var cancelled = _callerToken.IsCancellationRequested || readToken.IsCancellationRequested
            || Volatile.Read(ref _disposed) != 0;
        var failure = !cancelled && _timeoutSource?.IsCancellationRequested == true
            ? TimeoutException(_provider, _requestTimeout, exception, _requestToken)
            : exception;
        Complete(cancelled ? ProviderExecutionResult.Cancelled : ProviderExecutionResult.Failed,
            Volatile.Read(ref _disposed) != 0 ? "disposed" : cancelled ? "cancelled"
                : _timeoutSource?.IsCancellationRequested == true ? "timeout" : "failed");
        return failure;
    }

    private void Complete(ProviderExecutionResult result, string reason)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0) return;
        // CompleteAsync releases capacity and updates in-memory health before its
        // first await. Persistence is observed without blocking a UI context from
        // synchronous Read/Dispose or starting an additional worker task.
        if (_lease is not null) _ = ObserveCompletionAsync(_lease.CompleteAsync(result, null, CancellationToken.None));
        // Diagnostics must never alter response completion or disposal behavior.
        try { _onCompleted?.Invoke(result, reason); }
        catch (Exception) { }
        _requestSource.Dispose();
        _timeoutSource?.Dispose();
    }

    private static async Task ObserveCompletionAsync(Task completion)
    {
        try { await completion.ConfigureAwait(false); }
        catch (Exception) { /* Response disposal must not surface persistence failures. */ }
    }

    protected override void Dispose(bool disposing)
    {
        if (!disposing)
        {
            base.Dispose(false);
            return;
        }
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            // Cancellation unblocks a pending network read. Mark abandonment first
            // so its resulting cancellation/IOException remains health-neutral.
            try { _requestSource.Cancel(); }
            catch (ObjectDisposedException) { }
            Complete(ProviderExecutionResult.Cancelled, "disposed");
            _inner.Dispose();
        }
        finally { base.Dispose(true); }
    }

    private sealed class DeadlineReadStream(Stream inner, DeadlineProviderContent owner, CancellationToken streamToken) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(owner._requestToken, streamToken, cancellationToken);
            try
            {
                linked.Token.ThrowIfCancellationRequested();
                var read = await inner.ReadAsync(buffer, linked.Token).ConfigureAwait(false);
                linked.Token.ThrowIfCancellationRequested();
                // A zero-sized caller buffer is not evidence that the body ended.
                if ((read == 0 && buffer.Length != 0)
                    || (read > 0 && owner._contentLength is { } length
                        && Interlocked.Add(ref owner._bytesRead, read) >= length))
                {
                    owner.Complete(ProviderExecutionResult.Success, "consumed");
                }
                return read;
            }
            catch (Exception exception)
            {
                var callerToken = streamToken.IsCancellationRequested ? streamToken : cancellationToken;
                var failure = owner.RecordFailure(exception, callerToken);
                if (ReferenceEquals(failure, exception)) throw;
                throw failure;
            }
        }

        public override int Read(byte[] buffer, int offset, int count)
            => ReadAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();

        public override int Read(Span<byte> buffer)
        {
            // Stream's span fallback routes through Read(byte[], ...), so sync
            // consumers use the cancellable async transport path as well.
            return base.Read(buffer);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) owner.Dispose();
            base.Dispose(disposing);
        }

        public override ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
