using Microsoft.Extensions.Options;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public interface IPosterWarmupQueue
{
    /// <summary>
    /// Requests best-effort server-side generation of the card poster variant, in the given order.
    /// Each call replaces all work that has not started yet.
    /// </summary>
    void Enqueue(IEnumerable<string> posterUrls);
}

/// <summary>
/// Generates the WebP poster variant requested by calendar cards before the browser asks for it.
/// Latest batch wins: a new batch replaces every pending poster, the item in flight may finish, and the
/// worker waits briefly after a new batch so the browser's own requests for visible cards go first.
/// One worker; never blocks or throws into the caller.
/// </summary>
public sealed class PosterWarmupQueue : IPosterWarmupQueue, IHostedService, IDisposable
{
    internal const int DefaultCapacity = 512;
    internal const int RecentlyWarmedCapacity = 2000;
    internal static readonly TimeSpan DefaultStartDelay = TimeSpan.FromMilliseconds(750);
    internal static readonly TimeSpan RecentlyWarmedLifetime = TimeSpan.FromMinutes(30);

    private readonly object _gate = new();
    private readonly List<string> _pending = [];
    private readonly Dictionary<string, DateTimeOffset> _recent = new(StringComparer.Ordinal);
    private readonly Queue<string> _recentOrder = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PosterWarmupQueue> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeProvider _time;
    private readonly TimeSpan _startDelay;
    private readonly int _capacity;
    private readonly bool _enabled;
    private readonly Task _worker;
    private CancellationTokenSource _batchCancellation = new();
    private long _generation;
    private int _pendingIndex;
    private Task? _stop;
    private bool _stopping;

    public PosterWarmupQueue(
        IServiceScopeFactory scopeFactory,
        IOptions<ImageCacheOptions> options,
        ILogger<PosterWarmupQueue> logger)
        : this(scopeFactory, options, logger, DefaultCapacity, DefaultStartDelay, Task.Delay, TimeProvider.System)
    {
    }

    internal PosterWarmupQueue(
        IServiceScopeFactory scopeFactory,
        IOptions<ImageCacheOptions> options,
        ILogger<PosterWarmupQueue> logger,
        int capacity,
        TimeSpan startDelay,
        Func<TimeSpan, CancellationToken, Task> delay,
        TimeProvider time)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _capacity = Math.Max(1, capacity);
        _startDelay = startDelay;
        _delay = delay;
        _time = time;
        _enabled = options.Value.Enabled;
        _worker = _enabled ? Task.Run(RunAsync) : Task.CompletedTask;
    }

    public void Enqueue(IEnumerable<string> posterUrls)
    {
        if (!_enabled || posterUrls is null)
        {
            return;
        }

        try
        {
            lock (_gate)
            {
                if (_stopping)
                {
                    return;
                }

                var batch = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var url in posterUrls)
                {
                    if (string.IsNullOrWhiteSpace(url)
                        || IsRecentlyWarmed(url)
                        || !seen.Add(url))
                    {
                        continue;
                    }

                    batch.Add(url);
                    if (batch.Count >= _capacity)
                    {
                        break;
                    }
                }

                _generation++;
                _pending.Clear();
                _pending.AddRange(batch);
                _pendingIndex = 0;
                var previous = _batchCancellation;
                _batchCancellation = new CancellationTokenSource();
                previous.Cancel();
                previous.Dispose();
                if (batch.Count > 0)
                {
                    _signal.Release();
                }
            }
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Poster warm-up enqueue failed.");
        }
    }

    private async Task RunAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            try
            {
                await ProcessNextBatchAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                // An unexpected failure must not silently end warm-up for the rest of the process.
                _logger.LogDebug(exception, "Poster warm-up worker iteration failed.");
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(50), _shutdown.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    private async Task ProcessNextBatchAsync()
    {
        await _signal.WaitAsync(_shutdown.Token).ConfigureAwait(false);

        long generation;
        CancellationToken batchToken;
        lock (_gate)
        {
            if (_pendingIndex >= _pending.Count)
            {
                return;
            }

            generation = _generation;
            batchToken = _batchCancellation.Token;
        }

        try
        {
            using var delayCancellation = CancellationTokenSource.CreateLinkedTokenSource(batchToken, _shutdown.Token);
            await _delay(_startDelay, delayCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!_shutdown.IsCancellationRequested)
        {
            // A newer batch arrived; its own signal restarts the wait.
            return;
        }

        while (TryTakeNext(generation, out var url))
        {
            await WarmAsync(url).ConfigureAwait(false);
        }
    }

    private bool IsRecentlyWarmed(string url)
    {
        lock (_gate)
        {
            return _recent.TryGetValue(url, out var warmedAt)
                && _time.GetUtcNow() - warmedAt < RecentlyWarmedLifetime;
        }
    }

    private bool TryTakeNext(long generation, out string url)
    {
        lock (_gate)
        {
            while (!_shutdown.IsCancellationRequested && generation == _generation && _pendingIndex < _pending.Count)
            {
                var candidate = _pending[_pendingIndex++];
                if (IsRecentlyWarmed(candidate))
                {
                    continue;
                }

                url = candidate;
                return true;
            }
        }

        url = "";
        return false;
    }

    private async Task WarmAsync(string url)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var cache = scope.ServiceProvider.GetRequiredService<IImageCache>();
            await cache.GetOrAddAsync(
                url,
                forceRefresh: false,
                _shutdown.Token,
                CachedImageUrl.PosterWidth,
                ImageCacheFormat.Webp).ConfigureAwait(false);
            RememberWarmed(url);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Poster warm-up failed.");
        }
    }

    private void RememberWarmed(string url)
    {
        lock (_gate)
        {
            var isNew = !_recent.ContainsKey(url);
            _recent[url] = _time.GetUtcNow();
            if (!isNew)
            {
                return;
            }

            _recentOrder.Enqueue(url);
            while (_recentOrder.Count > RecentlyWarmedCapacity)
            {
                _recent.Remove(_recentOrder.Dequeue());
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Stop().WaitAsync(cancellationToken);

    private Task Stop()
    {
        lock (_gate)
        {
            if (_stop is not null)
            {
                return _stop;
            }

            _stopping = true;
            _shutdown.Cancel();
            _batchCancellation.Cancel();
            return _stop = FinishAsync();
        }
    }

    private async Task FinishAsync()
    {
        await _worker.ConfigureAwait(false);
        lock (_gate)
        {
            _pending.Clear();
            _pendingIndex = 0;
            _recent.Clear();
            _recentOrder.Clear();
        }

        _batchCancellation.Dispose();
        _signal.Dispose();
        _shutdown.Dispose();
    }

    public void Dispose() => Stop().GetAwaiter().GetResult();
}
