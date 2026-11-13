namespace PremiereCalendar.Services;

// Presentation timeouts release foreground demand without cancelling durable work.
public sealed class CalendarObservationScope
{
    private static readonly AsyncLocal<CalendarObservationScope?> Ambient = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, Func<CancellationToken, Task>> _callbacks = [];
    private bool _passive;
    public static CalendarObservationScope? Current => Ambient.Value;
    public bool IsPassive { get { lock (_gate) return _passive; } }

    public IDisposable Enter()
    {
        var previous = Ambient.Value;
        Ambient.Value = this;
        return new ActionLease(() => Ambient.Value = previous);
    }

    public async Task<IDisposable> AttachAsync(Func<CancellationToken, Task> release, CancellationToken token)
    {
        var key = Guid.NewGuid();
        bool passive;
        lock (_gate) { _callbacks.Add(key, release); passive = _passive; }
        try { if (passive) await release(token); }
        catch { lock (_gate) _callbacks.Remove(key); throw; }
        return new ActionLease(() => { lock (_gate) _callbacks.Remove(key); });
    }

    public async Task ReleaseForegroundAsync(CancellationToken token)
    {
        Func<CancellationToken, Task>[] callbacks;
        lock (_gate) { if (_passive) return; _passive = true; callbacks = _callbacks.Values.ToArray(); }
        foreach (var callback in callbacks) await callback(token);
    }

    private sealed class ActionLease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
