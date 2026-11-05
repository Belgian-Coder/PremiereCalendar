using System.Threading.Channels;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

/// <summary>Owns bounded, deduplicated stale-image refresh work for the application lifetime.</summary>
public sealed class ImageRefreshQueue : IHostedService, IDisposable
{
    private readonly object _gate = new();
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);
    private readonly Channel<Refresh> _queue;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task[] _workers;
    private readonly ILogger<ImageRefreshQueue> _logger;
    private Task? _stop;
    private bool _stopping;

    public ImageRefreshQueue(IOptions<ImageCacheOptions> options, ILogger<ImageRefreshQueue> logger)
    {
        _logger = logger;
        _queue = Channel.CreateBounded<Refresh>(new BoundedChannelOptions(
            Math.Clamp(options.Value.BackgroundRefreshQueueCapacity, 1, 4096))
        {
            FullMode = BoundedChannelFullMode.Wait, SingleWriter = false,
            SingleReader = options.Value.BackgroundRefreshConcurrency == 1,
            AllowSynchronousContinuations = false
        });
        _workers = Enumerable.Range(0, Math.Clamp(options.Value.BackgroundRefreshConcurrency, 1, 16))
            .Select(_ => Task.Run(RunAsync)).ToArray();
    }

    internal bool TryEnqueue(string key, Func<CancellationToken, Task> refresh)
    {
        lock (_gate)
        {
            if (_stopping || !_pending.Add(key)) return false;
            if (_queue.Writer.TryWrite(new(key, refresh))) return true;
            _pending.Remove(key);
            return false;
        }
    }

    private async Task RunAsync()
    {
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(_shutdown.Token).ConfigureAwait(false))
            {
                _shutdown.Token.ThrowIfCancellationRequested();
                try { await item.Action(_shutdown.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
                catch (Exception exception)
                {
                    // A failed refresh leaves the existing immutable body and metadata usable.
                    _logger.LogDebug(exception, "Background image refresh failed.");
                }
                finally { lock (_gate) _pending.Remove(item.Key); }
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Stop().WaitAsync(cancellationToken);

    private Task Stop()
    {
        lock (_gate)
        {
            if (_stop is not null) return _stop;
            _stopping = true;
            _queue.Writer.TryComplete();
            _shutdown.Cancel();
            return _stop = FinishAsync();
        }
    }

    private async Task FinishAsync()
    {
        await Task.WhenAll(_workers).ConfigureAwait(false);
        while (_queue.Reader.TryRead(out _)) { }
        lock (_gate) _pending.Clear();
        _shutdown.Dispose();
    }

    public void Dispose() => Stop().GetAwaiter().GetResult();

    private sealed record Refresh(string Key, Func<CancellationToken, Task> Action);
}
