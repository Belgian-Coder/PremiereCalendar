using System.Collections.Concurrent;

namespace PremiereCalendar.Services;

/// <summary>Reuses verified provider outcomes only within one calendar refresh generation.</summary>
public sealed class ProviderRefreshScope
{
    private const int MaximumEntries = 8192;
    private static readonly AsyncLocal<ProviderRefreshScope?> Ambient = new();
    private readonly ConcurrentDictionary<RequestKey, Entry> _entries = new();
    private readonly SingleFlightCoordinator _flights = new();
    private int _entryCount;

    private ProviderRefreshScope(bool forceDiscoveryRefresh) => ForceDiscoveryRefresh = forceDiscoveryRefresh;

    public bool ForceDiscoveryRefresh { get; }

    public static ProviderRefreshScope Create(bool forceDiscoveryRefresh = false) => new(forceDiscoveryRefresh);

    public static ProviderRefreshScope? Current => Ambient.Value;

    // Iterator producers must enter this around MoveNext/Dispose, never across a yield.
    public static IDisposable Use(ProviderRefreshScope scope)
    {
        var previous = Ambient.Value;
        Ambient.Value = scope;
        return new RestoreScope(previous);
    }

    internal async Task<T> RunAsync<T>(
        string requestKey,
        bool forceRefresh,
        Func<CancellationToken, Task<ProviderRefreshResult<T>>> factory,
        CancellationToken cancellationToken,
        Func<T, bool>? canReuse = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = new RequestKey(requestKey, typeof(T));
        if (!_entries.TryGetValue(key, out var entry))
        {
            if (Interlocked.Increment(ref _entryCount) > MaximumEntries)
            {
                Interlocked.Decrement(ref _entryCount);
                return (await factory(cancellationToken)).Value;
            }
            var candidate = new Entry();
            entry = _entries.GetOrAdd(key, candidate);
            if (!ReferenceEquals(entry, candidate)) Interlocked.Decrement(ref _entryCount);
        }

        lock (entry.Gate)
        {
            if ((forceRefresh ? entry.Forced : entry.Normal) is Memo<T> memo)
            {
                if (canReuse is null || canReuse(memo.Value)) return memo.Value;
                if (forceRefresh) entry.Forced = null; else entry.Normal = null;
            }
        }

        return await _flights.RunAsync(
            $"{requestKey.Length}:{requestKey}:{typeof(T).FullName}:{forceRefresh}",
            async token =>
            {
                long generation;
                lock (entry.Gate)
                {
                    if ((forceRefresh ? entry.Forced : entry.Normal) is Memo<T> memo)
                    {
                        if (canReuse is null || canReuse(memo.Value)) return memo.Value;
                        if (forceRefresh) entry.Forced = null; else entry.Normal = null;
                    }
                    generation = forceRefresh ? ++entry.ForceGeneration : entry.ForceGeneration;
                }

                var outcome = await factory(token);
                token.ThrowIfCancellationRequested();
                if (outcome.Successful)
                {
                    lock (entry.Gate)
                    {
                        if (forceRefresh)
                        {
                            entry.Forced = new Memo<T>(outcome.Value);
                            // A later ordinary lookup must not reuse its pre-refresh value.
                            entry.Normal = null;
                        }
                        else if (entry.ForceGeneration == generation)
                        {
                            entry.Normal = new Memo<T>(outcome.Value);
                        }
                    }
                }
                return outcome.Value;
            },
            cancellationToken);
    }

    private readonly record struct RequestKey(string Key, Type Type);
    private sealed record Memo<T>(T Value);
    private sealed class Entry
    {
        public object Gate { get; } = new();
        public object? Normal;
        public object? Forced;
        public long ForceGeneration;
    }

    private sealed class RestoreScope(ProviderRefreshScope? previous) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Ambient.Value = previous;
        }
    }
}

public readonly record struct ProviderRefreshResult<T>(T Value, bool Successful, ProviderFailure? Failure = null);
