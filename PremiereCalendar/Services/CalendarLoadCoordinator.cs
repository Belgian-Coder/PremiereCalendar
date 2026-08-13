namespace PremiereCalendar.Services;

public class CalendarLoadCoordinator
{
    private readonly object _backgroundGate = new();
    private int _activeForegroundLoads;
    private int _activeBackgroundLoad;
    private CancellationTokenSource? _backgroundCancellation;

    public bool HasActiveForegroundLoad => Volatile.Read(ref _activeForegroundLoads) > 0;

    public IDisposable BeginForegroundLoad()
    {
        Interlocked.Increment(ref _activeForegroundLoads);
        CancelBackgroundLoad();
        return new ReleaseLease(() => Interlocked.Decrement(ref _activeForegroundLoads));
    }

    public Task<BackgroundLoadLease?> TryBeginBackgroundLoadAsync(
        bool skipWhenForegroundActive,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (skipWhenForegroundActive && HasActiveForegroundLoad)
        {
            return Task.FromResult<BackgroundLoadLease?>(null);
        }

        if (Interlocked.CompareExchange(ref _activeBackgroundLoad, 1, 0) != 0)
        {
            return Task.FromResult<BackgroundLoadLease?>(null);
        }

        CancellationTokenSource? backgroundCancellation = null;
        try
        {
            lock (_backgroundGate)
            {
                if (skipWhenForegroundActive && HasActiveForegroundLoad)
                {
                    Interlocked.Exchange(ref _activeBackgroundLoad, 0);
                    return Task.FromResult<BackgroundLoadLease?>(null);
                }

                backgroundCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                _backgroundCancellation = backgroundCancellation;
            }

            return Task.FromResult<BackgroundLoadLease?>(
                new BackgroundLoadLease(
                    backgroundCancellation.Token,
                    () => ReleaseBackgroundLoad(backgroundCancellation)));
        }
        catch
        {
            backgroundCancellation?.Dispose();
            Interlocked.Exchange(ref _activeBackgroundLoad, 0);
            throw;
        }
    }

    private void CancelBackgroundLoad()
    {
        lock (_backgroundGate)
        {
            _backgroundCancellation?.Cancel();
        }
    }

    private void ReleaseBackgroundLoad(CancellationTokenSource backgroundCancellation)
    {
        lock (_backgroundGate)
        {
            if (ReferenceEquals(_backgroundCancellation, backgroundCancellation))
            {
                _backgroundCancellation = null;
            }
        }

        backgroundCancellation.Dispose();
        Interlocked.Exchange(ref _activeBackgroundLoad, 0);
    }

    public sealed class BackgroundLoadLease(CancellationToken token, Action release) : IDisposable
    {
        private Action? _release = release;

        public CancellationToken Token { get; } = token;

        public void Dispose()
        {
            Interlocked.Exchange(ref _release, null)?.Invoke();
        }
    }

    private sealed class ReleaseLease(Action release) : IDisposable
    {
        private Action? _release = release;

        public void Dispose()
        {
            Interlocked.Exchange(ref _release, null)?.Invoke();
        }
    }
}

public sealed class CalendarPageCoordinator : CalendarLoadCoordinator
{
    private readonly object _stateGate = new();
    private CalendarPageState _state = CalendarPageState.Empty;

    public CalendarPageState Snapshot
    {
        get { lock (_stateGate) return _state; }
    }

    public void UpdatePageState(DateOnly weekStart, string route, bool loading, int cardCount)
    {
        lock (_stateGate)
        {
            _state = new CalendarPageState(weekStart, route, loading, Math.Max(0, cardCount));
        }
    }
}

public sealed record CalendarPageState(DateOnly? WeekStart, string Route, bool IsLoading, int CardCount)
{
    public static CalendarPageState Empty { get; } = new(null, "all", false, 0);
}
