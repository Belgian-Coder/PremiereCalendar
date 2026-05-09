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
    private readonly ConcurrentDictionary<string, Lazy<Task<object?>>> _flights = new(StringComparer.Ordinal);

    public async Task<T> RunAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(factory);

        var lazy = _flights.GetOrAdd(
            key,
            _ => new Lazy<Task<object?>>(
                () => RunBoxedAsync(key, factory, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));

        var value = await lazy.Value.WaitAsync(cancellationToken);
        return value is null ? default! : (T)value;
    }

    private async Task<object?> RunBoxedAsync<T>(
        string key,
        Func<CancellationToken, Task<T>> factory,
        CancellationToken cancellationToken)
    {
        try
        {
            return await factory(cancellationToken);
        }
        finally
        {
            _flights.TryRemove(key, out _);
        }
    }
}
