using System.Collections.Concurrent;

namespace PremiereCalendar.Services;

public interface ISingleFlightCoordinator
{
    Task<T> RunAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken);
}

public sealed class SingleFlightCoordinator : ISingleFlightCoordinator
{
    private readonly ConcurrentDictionary<string, Flight> _flights = new(StringComparer.Ordinal);

    public async Task<T> RunAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(factory);
        cancellationToken.ThrowIfCancellationRequested();

        Flight flight;
        long waiterId;
        var priorityAccessor = ProviderExecutionContext.CapturePriorityAccessor();
        while (true)
        {
            flight = _flights.GetOrAdd(
                key,
                _ =>
                {
                    Flight? createdFlight = null;
                    createdFlight = new Flight(token => RunBoxedAsync(key, createdFlight!, factory, token));
                    return createdFlight;
                });

            if (flight.TryAddWaiter(priorityAccessor, out waiterId))
            {
                break;
            }

            TryRemoveFlight(key, flight);
        }

        try
        {
            var value = await flight.Task.WaitAsync(cancellationToken);
            return value is null ? default! : (T)value;
        }
        finally
        {
            if (flight.ReleaseWaiter(waiterId))
            {
                TryRemoveFlight(key, flight);
                flight.Cancel();
            }
        }
    }

    private async Task<object?> RunBoxedAsync<T>(
        string key,
        Flight flight,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken)
    {
        try
        {
            return await factory(cancellationToken);
        }
        finally
        {
            TryRemoveFlight(key, flight);
            flight.Dispose();
        }
    }

    private bool TryRemoveFlight(string key, Flight flight)
    {
        return ((ICollection<KeyValuePair<string, Flight>>)_flights)
            .Remove(new KeyValuePair<string, Flight>(key, flight));
    }

    private sealed class Flight : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Lazy<Task<object?>> _task;
        private readonly Lock _gate = new();
        private readonly Dictionary<long, Func<ProviderWorkPriority>> _waiters = new();
        private long _nextWaiterId;
        private bool _isCanceling;
        private bool _isDisposed;

        public Flight(Func<CancellationToken, Task<object?>> factory)
        {
            _task = new Lazy<Task<object?>>(
                () => RunWithPriorityAsync(factory),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public Task<object?> Task => _task.Value;

        private async Task<object?> RunWithPriorityAsync(Func<CancellationToken, Task<object?>> factory)
        {
            using var scope = ProviderExecutionContext.Push(CurrentPriority);
            return await factory(_cancellation.Token);
        }

        private ProviderWorkPriority CurrentPriority()
        {
            lock (_gate)
            {
                var priority = ProviderWorkPriority.Maintenance;
                foreach (var accessor in _waiters.Values)
                {
                    var current = accessor();
                    if (current < priority) priority = current;
                }
                return priority;
            }
        }

        public bool TryAddWaiter(Func<ProviderWorkPriority> priorityAccessor, out long waiterId)
        {
            lock (_gate)
            {
                waiterId = 0;
                if (_isCanceling || _isDisposed)
                {
                    return false;
                }

                waiterId = ++_nextWaiterId;
                _waiters.Add(waiterId, priorityAccessor);
                return true;
            }
        }

        public bool ReleaseWaiter(long waiterId)
        {
            lock (_gate)
            {
                _waiters.Remove(waiterId);
                if (_waiters.Count > 0 || Task.IsCompleted)
                {
                    return false;
                }

                _isCanceling = true;
                return true;
            }
        }

        public void Cancel()
        {
            try
            {
                _cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _isDisposed = true;
            }

            _cancellation.Dispose();
        }
    }
}
