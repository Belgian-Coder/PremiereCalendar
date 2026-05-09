using System.Collections.Concurrent;

namespace PremiereCalendar.Services;

public sealed class ProviderRequestThrottler
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    public async ValueTask<IDisposable> AcquireAsync(
        string providerKey,
        int maxConcurrentRequests,
        CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(maxConcurrentRequests, 1, 64);
        var key = $"{NormalizeProviderKey(providerKey)}:{limit}";
        var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(limit, limit));
        await gate.WaitAsync(cancellationToken);
        return new ReleaseLease(gate);
    }

    private static string NormalizeProviderKey(string providerKey)
    {
        return string.IsNullOrWhiteSpace(providerKey)
            ? "default"
            : providerKey.Trim().ToLowerInvariant();
    }

    private sealed class ReleaseLease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;

        public void Dispose()
        {
            Interlocked.Exchange(ref _gate, null)?.Release();
        }
    }
}
