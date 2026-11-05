using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Caching.Memory;

namespace PremiereCalendar.Services;

/// <summary>Shares optional-provider fetches and fences successful forced publications.</summary>
internal sealed class OptionalProviderResponseCache(IMemoryCache memory, ProviderResponseCache? persistent, TimeProvider? clock = null)
{
    private static readonly ConditionalWeakTable<IMemoryCache, SharedState> States = new();
    private readonly SharedState _state = States.GetValue(memory, static _ => new());
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    internal static string Partition(Uri? endpoint, string credential, string request)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{endpoint}|{credential}|{request}")));

    internal Task<ProviderRefreshResult<T>> GetAsync<T>(string provider, string partition, bool force,
        TimeSpan lifetime, Func<CancellationToken, Task<ProviderRefreshResult<T>>> fetch, CancellationToken cancellationToken,
        Func<T, TimeSpan>? responseLifetime = null, Action<T, DateTimeOffset>? publishMemory = null,
        T unavailableFallback = default!)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = $"optional-response:{provider}:{partition}";
        if (!force && memory.TryGetValue(key, out CachedProviderResponse<OptionalProviderValue<T>>? cached)
            && cached is not null && cached.ExpiresAtUtc > _clock.GetUtcNow())
            return Task.FromResult(new ProviderRefreshResult<T>(cached.Value.Value, true));

        return _state.Flights.RunAsync<ProviderRefreshResult<T>>($"{key}:{(force ? "refresh" : "normal")}", async token =>
        {
            using var operation = Begin(key, force);
            using var durableOperation = persistent?.BeginOperation(provider, partition, force);
            memory.TryGetValue(key, out CachedProviderResponse<OptionalProviderValue<T>>? previous);
            if (!force && previous is not null && previous.ExpiresAtUtc > _clock.GetUtcNow())
                return new(previous.Value.Value, true);

            var read = persistent is null ? null
                : await persistent.ReadAsync<OptionalProviderValue<T>>(provider, partition, lifetime, token);
            if (read?.Status == ProviderResponseCacheReadStatus.StorageUnavailable)
                return new(previous is null ? unavailableFallback : previous.Value.Value, false,
                    new ProviderFailure("storage_unavailable", RetryAtUtc: read.RetryAtUtc, IsLocal: true));
            var stored = read?.Response;
            previous ??= stored;
            if (!force && stored is not null)
            {
                await operation.PublishAsync(() =>
                {
                    memory.Set(key, stored, stored.ExpiresAtUtc);
                    publishMemory?.Invoke(stored.Value.Value, stored.ExpiresAtUtc);
                    return Task.CompletedTask;
                }, token, successfulForce: false);
                return new(stored.Value.Value, true);
            }

            var result = await fetch(token);
            token.ThrowIfCancellationRequested();
            if (!result.Successful)
                return previous is null ? result : result with { Value = previous.Value.Value };

            var value = new OptionalProviderValue<T>(result.Value);
            await operation.PublishAsync(async () =>
            {
                async Task<bool> Publish(bool current)
                {
                    if (!current) return false;
                    var duration = responseLifetime?.Invoke(result.Value) ?? lifetime;
                    var expires = _clock.GetUtcNow().Add(duration);
                    if (persistent is not null)
                        await persistent.SaveAsync(provider, partition, value, token, invalidateOversized: force, lifetime: duration);
                    token.ThrowIfCancellationRequested();
                    memory.Set(key, new CachedProviderResponse<OptionalProviderValue<T>>(value, expires), expires);
                    publishMemory?.Invoke(result.Value, expires);
                    return true;
                }
                if (durableOperation is null) await Publish(true);
                else await durableOperation.PublishAsync(Publish, token);
            }, token, successfulForce: force);
            return result;
        }, cancellationToken);
    }

    private Publication Begin(string key, bool force)
    {
        lock (_state.Gate)
        {
            if (!_state.Entries.TryGetValue(key, out var entry)) _state.Entries.Add(key, entry = new());
            entry.References++;
            if (force) entry.Generation++;
            return new(_state, key, entry, entry.Generation);
        }
    }

    private sealed class SharedState
    {
        internal readonly object Gate = new();
        internal readonly Dictionary<string, Entry> Entries = new(StringComparer.Ordinal);
        internal readonly SingleFlightCoordinator Flights = new();
    }

    private sealed class Entry
    {
        internal readonly SemaphoreSlim Gate = new(1, 1);
        internal int References;
        internal long Generation;
    }

    private sealed class Publication(SharedState state, string key, Entry entry, long generation) : IDisposable
    {
        internal async Task PublishAsync(Func<Task> publish, CancellationToken token, bool successfulForce)
        {
            await entry.Gate.WaitAsync(token);
            try
            {
                lock (state.Gate) if (entry.Generation != generation) return;
                await publish();
                if (successfulForce) lock (state.Gate)
                    if (entry.Generation == generation) entry.Generation++;
            }
            finally { entry.Gate.Release(); }
        }

        public void Dispose()
        {
            lock (state.Gate)
                if (--entry.References == 0)
                {
                    state.Entries.Remove(key);
                    entry.Gate.Dispose();
                }
        }
    }
}

internal sealed record OptionalProviderValue<T>(T Value);
