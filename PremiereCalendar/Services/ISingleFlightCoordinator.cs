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

            if (flight.TryAddWaiter())
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
            if (flight.ReleaseWaiter())
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
        private readonly object _gate = new();
        private int _waiterCount;
        private bool _isCanceling;
        private bool _isDisposed;

        public Flight(Func<CancellationToken, Task<object?>> factory)
        {
            _task = new Lazy<Task<object?>>(
                () => factory(_cancellation.Token),
                LazyThreadSafetyMode.ExecutionAndPublication);
        }

        public Task<object?> Task => _task.Value;

        public bool TryAddWaiter()
        {
            lock (_gate)
            {
                if (_isCanceling || _isDisposed)
                {
                    return false;
                }

                _waiterCount++;
                return true;
            }
        }

        public bool ReleaseWaiter()
        {
            lock (_gate)
            {
                _waiterCount--;
                if (_waiterCount > 0 || Task.IsCompleted)
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
