using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

/// <summary>Persists successful provider responses without extending their original freshness window.</summary>
public sealed class ProviderResponseCache(
    IProviderCacheStateStore store,
    ILogger<ProviderResponseCache> logger,
    TimeProvider? timeProvider = null,
    PremiereTelemetry? telemetry = null,
    IOptions<ItemEnrichmentCacheOptions>? itemCacheOptions = null)
{
    private const int EnvelopeVersion = 1;
    private const int MaximumPayloadBytes = 256 * 1024;
    private static readonly TimeSpan IoBudget = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly object _publicationLock = new();
    private readonly Dictionary<string, PublicationEntry> _publications = new(StringComparer.Ordinal);
    private long _tmdbBackoffUntilUtcTicks;
    private long _rottenTomatoesBackoffUntilUtcTicks;
    private long _cleanupBackoffUntilUtcTicks;
    private long _fanartBackoffUntilUtcTicks;
    private long _watchmodeBackoffUntilUtcTicks;
    private long _tvmazeBackoffUntilUtcTicks;

    public PublicationOperation BeginOperation(string provider, string requestKey, bool forceRefresh)
    {
        var key = $"{Namespace(provider)}:{Hash(requestKey)}";
        lock (_publicationLock)
        {
            if (!_publications.TryGetValue(key, out var entry))
            {
                entry = new PublicationEntry();
                _publications.Add(key, entry);
            }

            entry.References++;
            if (forceRefresh) Interlocked.Increment(ref entry.Generation);
            return new PublicationOperation(this, key, entry, entry.Generation, forceRefresh);
        }
    }

    private void ReleaseOperation(string key, PublicationEntry entry)
    {
        lock (_publicationLock)
        {
            if (--entry.References == 0)
            {
                _publications.Remove(key);
                entry.Gate.Dispose();
            }
        }
    }

    internal sealed class PublicationEntry
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public long Generation;
        public int References;
    }

    public sealed class PublicationOperation : IDisposable
    {
        private readonly ProviderResponseCache _owner;
        private readonly string _key;
        private readonly PublicationEntry _entry;
        private readonly long _generation;
        private readonly bool _forceRefresh;
        private int _disposed;

        internal PublicationOperation(ProviderResponseCache owner, string key, PublicationEntry entry, long generation, bool forceRefresh)
            => (_owner, _key, _entry, _generation, _forceRefresh) = (owner, key, entry, generation, forceRefresh);

        public async Task<T> PublishAsync<T>(Func<bool, Task<T>> publish, CancellationToken cancellationToken, bool successfulForceCommit = true)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await _entry.Gate.WaitAsync(cancellationToken);
            try
            {
                var current = Volatile.Read(ref _entry.Generation) == _generation;
                var result = await publish(current);
                // Fence normal operations that started while this refresh was fetching.
                // A newer refresh may already have advanced the generation itself.
                if (_forceRefresh && current && successfulForceCommit) Interlocked.CompareExchange(ref _entry.Generation, _generation + 1, _generation);
                return result;
            }
            finally
            {
                _entry.Gate.Release();
            }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _owner.ReleaseOperation(_key, _entry);
        }
    }

    // Compatibility callers retain their nullable cache-hit contract.
    public async Task<CachedProviderResponse<T>?> GetAsync<T>(
        string provider, string requestKey, TimeSpan lifetime, CancellationToken cancellationToken)
        => (await ReadAsync<T>(provider, requestKey, lifetime, cancellationToken)).Response;

    public async Task<ProviderResponseCacheRead<T>> ReadAsync<T>(
        string provider, string requestKey, TimeSpan lifetime, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var started = Stopwatch.GetTimestamp();
        var outcome = "miss";
        string? reason = null;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(IoBudget);
        try
        {
            if (BackoffDeadline(provider) is { } deadline)
            {
                outcome = "skipped";
                reason = "backoff_active";
                return new(ProviderResponseCacheReadStatus.StorageUnavailable, RetryAtUtc: deadline);
            }
            var state = await store.GetAsync(Namespace(provider), ProviderCacheScope.Item, Hash(requestKey), budget.Token);
            cancellationToken.ThrowIfCancellationRequested();
            budget.Token.ThrowIfCancellationRequested();
            if (state is null || string.IsNullOrWhiteSpace(state.MetadataJson)
                || Encoding.UTF8.GetByteCount(state.MetadataJson) > MaximumPayloadBytes)
            {
                return new(ProviderResponseCacheReadStatus.Miss);
            }

            var now = _timeProvider.GetUtcNow();
            DateTimeOffset expiresAt;
            try { expiresAt = state.LastCheckedUtc + lifetime; }
            catch (ArgumentOutOfRangeException) { return new(ProviderResponseCacheReadStatus.Miss); }
            if (state.LastCheckedUtc > now || expiresAt <= now)
            {
                return new(ProviderResponseCacheReadStatus.Miss);
            }

            var envelope = JsonSerializer.Deserialize<ResponseEnvelope<T>>(state.MetadataJson, JsonOptions);
            if (envelope?.FreshUntilUtc is { } explicitExpiry && explicitExpiry < expiresAt)
                expiresAt = explicitExpiry;
            if (expiresAt <= now) return new(ProviderResponseCacheReadStatus.Miss);
            var result = envelope is { Version: EnvelopeVersion, Value: not null } && envelope.Type == typeof(T).FullName
                ? new CachedProviderResponse<T>(envelope.Value, expiresAt)
                : null;
            outcome = result is null ? "miss" : "hit";
            return new(result is null ? ProviderResponseCacheReadStatus.Miss : ProviderResponseCacheReadStatus.Hit, result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = "canceled";
            throw;
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            outcome = "failed";
            reason = "budget_exceeded";
            return new(ProviderResponseCacheReadStatus.StorageUnavailable, RetryAtUtc: BackOff(provider));
        }
        catch (OperationCanceledException)
        {
            outcome = "canceled";
            throw;
        }
        catch (JsonException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            outcome = "miss";
            reason = "invalid_payload";
            return new(ProviderResponseCacheReadStatus.Miss);
        }
        catch (Exception)
        {
            outcome = "failed";
            cancellationToken.ThrowIfCancellationRequested();
            reason = budget.IsCancellationRequested ? "budget_exceeded" : "storage_unavailable";
            logger.LogWarning("Unable to read persistent provider response cache.");
            return new(ProviderResponseCacheReadStatus.StorageUnavailable, RetryAtUtc: BackOff(provider));
        }
        finally
        {
            RecordDiagnostic("read", provider, requestKey, started, outcome, reason);
        }
    }

    public async Task SaveAsync<T>(string provider, string requestKey, T value, CancellationToken cancellationToken,
        bool invalidateOversized = false, TimeSpan? lifetime = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (value is null)
        {
            return;
        }

        var started = Stopwatch.GetTimestamp();
        var outcome = "skipped";
        string? reason = null;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(IoBudget);
        try
        {
            if (IsBackingOff(provider))
            {
                reason = "backoff_active";
                return;
            }
            var now = _timeProvider.GetUtcNow();
            var json = JsonSerializer.Serialize(new ResponseEnvelope<T>(EnvelopeVersion, typeof(T).FullName!, value,
                lifetime is { } duration ? now.Add(duration) : null), JsonOptions);
            var oversized = Encoding.UTF8.GetByteCount(json) > MaximumPayloadBytes;
            if (oversized)
            {
                reason = "payload_too_large";
                if (!invalidateOversized) return;
                // A successful forced response supersedes the previous payload even
                // when it is too large to retain. Version zero is always a cache miss.
                json = "{\"version\":0}";
            }

            await store.SaveAsync(new ProviderCacheState(
                Namespace(provider), ProviderCacheScope.Item, Hash(requestKey), now,
                null, null, null, json), budget.Token);
            cancellationToken.ThrowIfCancellationRequested();
            budget.Token.ThrowIfCancellationRequested();
            outcome = oversized ? "invalidated" : "saved";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = "canceled";
            throw;
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            outcome = "failed";
            reason = "budget_exceeded";
            BackOff(provider);
        }
        catch (Exception)
        {
            outcome = "failed";
            cancellationToken.ThrowIfCancellationRequested();
            if (budget.IsCancellationRequested) reason = "budget_exceeded";
            BackOff(provider);
            logger.LogWarning("Unable to write persistent provider response cache.");
        }
        finally
        {
            RecordDiagnostic("write", provider, requestKey, started, outcome, reason);
        }
    }

    private static string Namespace(string provider) => $"responsecache-{provider}";
    private static string Hash(string requestKey) => $"v1:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestKey)))}";
    private sealed record ResponseEnvelope<T>(int Version, string Type, T Value, DateTimeOffset? FreshUntilUtc = null);

    public async Task<int> CleanupAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var started = Stopwatch.GetTimestamp();
        var outcome = "failed";
        string? reason = null;
        var removed = 0;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(IoBudget);
        try
        {
            if (IsBackingOff("cleanup"))
            {
                outcome = "skipped";
                reason = "backoff_active";
                return 0;
            }
            // Storage retention is independent of a provider response's freshness.
            // Calendar-month arithmetic preserves the requested three-month window.
            var months = Math.Clamp(itemCacheOptions?.Value.RetentionMonths ?? 3, 1, 24);
            removed = await store.CleanupResponseCacheAsync(_timeProvider.GetUtcNow().AddMonths(-months), budget.Token);
            cancellationToken.ThrowIfCancellationRequested();
            budget.Token.ThrowIfCancellationRequested();
            outcome = "completed";
            return removed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = "canceled";
            throw;
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            cancellationToken.ThrowIfCancellationRequested();
            reason = "budget_exceeded";
            BackOff("cleanup");
            return 0;
        }
        catch (Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (budget.IsCancellationRequested) reason = "budget_exceeded";
            BackOff("cleanup");
            logger.LogWarning("Unable to clean persistent provider response cache.");
            return 0;
        }
        finally
        {
            telemetry?.RecordDiagnostic("providerresponsecache", details: new()
            {
                Stage = "cleanup", Cache = "persistent_response", Outcome = outcome, Reason = reason,
                Count = removed, DurationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds
            });
        }
    }

    private bool IsBackingOff(string provider) => BackoffDeadline(provider) is not null;
    private DateTimeOffset? BackoffDeadline(string provider)
    {
        var ticks = Volatile.Read(ref BackoffSlot(provider));
        return ticks > _timeProvider.GetUtcNow().UtcTicks ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
    }
    private DateTimeOffset BackOff(string provider)
    {
        var until = _timeProvider.GetUtcNow().Add(FailureBackoff);
        Interlocked.Exchange(ref BackoffSlot(provider), until.UtcTicks);
        return until;
    }
    private ref long BackoffSlot(string provider)
    {
        if (provider == "tmdb") return ref _tmdbBackoffUntilUtcTicks;
        if (provider == "rottentomatoes") return ref _rottenTomatoesBackoffUntilUtcTicks;
        if (provider == "fanart") return ref _fanartBackoffUntilUtcTicks;
        if (provider == "watchmode") return ref _watchmodeBackoffUntilUtcTicks;
        if (provider == "tvmaze") return ref _tvmazeBackoffUntilUtcTicks;
        return ref _cleanupBackoffUntilUtcTicks;
    }

    private void RecordDiagnostic(string stage, string provider, string requestKey, long started, string outcome, string? reason)
        => telemetry?.RecordDiagnostic("providerresponsecache", details: new()
        {
            Stage = stage,
            Provider = provider is "tmdb" or "rottentomatoes" or "fanart" or "watchmode" or "tvmaze" ? provider : "other",
            Cache = "persistent_response",
            CacheKey = Hash(requestKey)[3..],
            Outcome = outcome,
            Reason = reason,
            FromCache = outcome == "hit",
            DurationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds
        });
}

public sealed record CachedProviderResponse<T>(T Value, DateTimeOffset ExpiresAtUtc);

public enum ProviderResponseCacheReadStatus { Hit, Miss, StorageUnavailable }

public sealed record ProviderResponseCacheRead<T>(ProviderResponseCacheReadStatus Status,
    CachedProviderResponse<T>? Response = null, DateTimeOffset? RetryAtUtc = null);
