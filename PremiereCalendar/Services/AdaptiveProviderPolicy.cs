using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class AdaptiveProviderPolicy(
    ProviderAdaptiveStateStore store,
    TimeProvider timeProvider,
    PremiereTelemetry telemetry)
{
    private readonly ConcurrentDictionary<string, ProviderRuntimeState> _states = new(StringComparer.OrdinalIgnoreCase);

    public async Task<ProviderExecutionLease> AcquireAsync(
        string provider,
        int maximumConcurrency,
        CancellationToken cancellationToken)
    {
        var state = _states.GetOrAdd(provider, key => new ProviderRuntimeState(key, Math.Max(1, maximumConcurrency)));
        await state.EnsureLoadedAsync(store, cancellationToken);
        while (true)
        {
            Task waitTask;
            lock (state.Gate)
            {
                var now = timeProvider.GetUtcNow();
                if (state.CircuitState == ProviderCircuitState.Closed && state.CooldownUntilUtc is { } retryAfter && retryAfter > now)
                {
                    throw new ProviderRetryAfterException(provider, retryAfter);
                }
                if (state.CircuitState == ProviderCircuitState.Closed && state.CooldownUntilUtc <= now)
                {
                    state.CooldownUntilUtc = null;
                }
                if (state.CircuitState == ProviderCircuitState.Open)
                {
                    if (state.CooldownUntilUtc is { } cooldown && cooldown > now)
                    {
                        throw new ProviderCircuitOpenException(provider, cooldown);
                    }

                    state.CircuitState = ProviderCircuitState.HalfOpen;
                    state.CurrentConcurrency = 1;
                }

                var allowed = state.CircuitState == ProviderCircuitState.HalfOpen ? 1 : state.CurrentConcurrency;
                if (state.ActiveRequests < allowed)
                {
                    state.ActiveRequests++;
                    telemetry.SetProviderConcurrency(provider, state.CurrentConcurrency, state.ActiveRequests, state.CircuitState.ToString());
                    return new ProviderExecutionLease(this, state, timeProvider.GetTimestamp());
                }

                waitTask = state.Signal.Task;
            }
            await waitTask.WaitAsync(cancellationToken);
        }
    }

    internal async Task ReleaseAsync(
        ProviderRuntimeState state,
        long startedTimestamp,
        ProviderExecutionResult result,
        TimeSpan? retryAfter,
        CancellationToken cancellationToken)
    {
        ProviderAdaptiveSnapshot snapshot;
        var elapsed = timeProvider.GetElapsedTime(startedTimestamp);
        ProviderCircuitState previousCircuitState;
        lock (state.Gate)
        {
            previousCircuitState = state.CircuitState;
            state.ActiveRequests = Math.Max(0, state.ActiveRequests - 1);
            state.EwmaLatencyMilliseconds = state.EwmaLatencyMilliseconds is null
                ? elapsed.TotalMilliseconds
                : (state.EwmaLatencyMilliseconds.Value * 0.8) + (elapsed.TotalMilliseconds * 0.2);
            var now = timeProvider.GetUtcNow();

            if (result == ProviderExecutionResult.Success)
            {
                state.ConsecutiveFailures = 0;
                state.WindowFailureCount = 0;
                state.FailureWindowStartedUtc = null;
                state.ConsecutiveSuccesses++;
                if (state.CircuitState == ProviderCircuitState.HalfOpen)
                {
                    state.CircuitState = ProviderCircuitState.Closed;
                    state.CooldownUntilUtc = null;
                }
                if (state.ConsecutiveSuccesses >= 20 && state.CurrentConcurrency < state.MaximumConcurrency)
                {
                    state.CurrentConcurrency++;
                    state.ConsecutiveSuccesses = 0;
                }
            }
            else
            {
                state.ConsecutiveSuccesses = 0;
                state.ConsecutiveFailures++;
                if (state.FailureWindowStartedUtc is null || now - state.FailureWindowStartedUtc > TimeSpan.FromMinutes(2))
                {
                    state.FailureWindowStartedUtc = now;
                    state.WindowFailureCount = 0;
                }
                state.WindowFailureCount++;
                if (result == ProviderExecutionResult.Throttled)
                {
                    state.CurrentConcurrency = Math.Max(1, state.CurrentConcurrency / 2);
                    state.LastThrottledUtc = now;
                    state.CooldownUntilUtc = now.Add(ClampRetryAfter(retryAfter));
                }
                else if (state.ConsecutiveFailures >= 2)
                {
                    state.CurrentConcurrency = Math.Max(1, state.CurrentConcurrency / 2);
                }
                if (state.WindowFailureCount >= 5 || state.CircuitState == ProviderCircuitState.HalfOpen)
                {
                    state.CurrentConcurrency = Math.Max(1, state.CurrentConcurrency / 2);
                    state.CircuitState = ProviderCircuitState.Open;
                    state.CooldownUntilUtc = now.AddSeconds(60);
                }
            }

            snapshot = state.ToSnapshot(now);
            state.Pulse();
        }

        telemetry.RecordProviderRequest(state.Provider, result.ToString(), elapsed, snapshot.CurrentConcurrency, snapshot.CircuitState);
        telemetry.SetProviderConcurrency(state.Provider, snapshot.CurrentConcurrency, snapshot.ActiveRequests, snapshot.CircuitState.ToString());
        if (previousCircuitState != snapshot.CircuitState)
        {
            telemetry.RecordProviderCircuitEvent(state.Provider, snapshot.CircuitState.ToString());
        }
        try { await store.SaveAsync(snapshot, cancellationToken); }
        catch (Exception ex) when (ex is SqliteException or IOException) { }
    }

    public IReadOnlyList<ProviderAdaptiveSnapshot> GetSnapshots()
        => _states.Values.Select(state => state.ToSnapshot(timeProvider.GetUtcNow())).OrderBy(state => state.Provider).ToArray();

    private static TimeSpan ClampRetryAfter(TimeSpan? retryAfter)
    {
        var seconds = retryAfter?.TotalSeconds ?? 2;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 60));
    }

    internal sealed class ProviderRuntimeState(string provider, int maximumConcurrency)
    {
        private int _loaded;
        public object Gate { get; } = new();
        public string Provider { get; } = provider;
        public int MaximumConcurrency { get; } = maximumConcurrency;
        public int CurrentConcurrency { get; set; } = maximumConcurrency;
        public int ActiveRequests { get; set; }
        public int ConsecutiveSuccesses { get; set; }
        public int ConsecutiveFailures { get; set; }
        public int WindowFailureCount { get; set; }
        public DateTimeOffset? FailureWindowStartedUtc { get; set; }
        public double? EwmaLatencyMilliseconds { get; set; }
        public ProviderCircuitState CircuitState { get; set; }
        public DateTimeOffset? CooldownUntilUtc { get; set; }
        public DateTimeOffset? LastThrottledUtc { get; set; }
        public TaskCompletionSource Signal { get; private set; } = NewSignal();

        public async Task EnsureLoadedAsync(ProviderAdaptiveStateStore store, CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _loaded, 1) != 0) return;
            var persisted = await store.GetAsync(Provider, cancellationToken);
            if (persisted is null) return;
            lock (Gate)
            {
                CurrentConcurrency = Math.Clamp(persisted.CurrentConcurrency, 1, MaximumConcurrency);
                ConsecutiveSuccesses = persisted.ConsecutiveSuccesses;
                ConsecutiveFailures = persisted.ConsecutiveFailures;
                WindowFailureCount = persisted.WindowFailureCount;
                FailureWindowStartedUtc = persisted.FailureWindowStartedUtc;
                EwmaLatencyMilliseconds = persisted.EwmaLatencyMilliseconds;
                CircuitState = persisted.CircuitState;
                CooldownUntilUtc = persisted.CooldownUntilUtc;
                LastThrottledUtc = persisted.LastThrottledUtc;
            }
        }

        public void Pulse()
        {
            var signal = Signal;
            Signal = NewSignal();
            signal.TrySetResult();
        }

        public ProviderAdaptiveSnapshot ToSnapshot(DateTimeOffset now)
        {
            lock (Gate)
            {
                return new ProviderAdaptiveSnapshot(
                    Provider,
                    CurrentConcurrency,
                    ActiveRequests,
                    ConsecutiveSuccesses,
                    ConsecutiveFailures,
                    WindowFailureCount,
                    FailureWindowStartedUtc,
                    EwmaLatencyMilliseconds,
                    CircuitState,
                    CooldownUntilUtc,
                    LastThrottledUtc,
                    now);
            }
        }

        private static TaskCompletionSource NewSignal()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

public sealed class ProviderExecutionLease : IAsyncDisposable
{
    private readonly AdaptiveProviderPolicy _policy;
    private readonly AdaptiveProviderPolicy.ProviderRuntimeState _state;
    private readonly long _startedTimestamp;
    private int _released;

    internal ProviderExecutionLease(
        AdaptiveProviderPolicy policy,
        AdaptiveProviderPolicy.ProviderRuntimeState state,
        long startedTimestamp)
    {
        _policy = policy;
        _state = state;
        _startedTimestamp = startedTimestamp;
    }

    public Task CompleteAsync(
        ProviderExecutionResult result,
        TimeSpan? retryAfter,
        CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _released, 1) != 0) return Task.CompletedTask;
        return _policy.ReleaseAsync(_state, _startedTimestamp, result, retryAfter, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await CompleteAsync(ProviderExecutionResult.Failed, null, CancellationToken.None);
    }
}

public enum ProviderExecutionResult { Success, Failed, Throttled }
public enum ProviderCircuitState { Closed, Open, HalfOpen }

public sealed record ProviderAdaptiveSnapshot(
    string Provider,
    int CurrentConcurrency,
    int ActiveRequests,
    int ConsecutiveSuccesses,
    int ConsecutiveFailures,
    int WindowFailureCount,
    DateTimeOffset? FailureWindowStartedUtc,
    double? EwmaLatencyMilliseconds,
    ProviderCircuitState CircuitState,
    DateTimeOffset? CooldownUntilUtc,
    DateTimeOffset? LastThrottledUtc,
    DateTimeOffset UpdatedUtc);

public sealed class ProviderCircuitOpenException(string provider, DateTimeOffset retryUtc)
    : HttpRequestException($"{provider} provider circuit is open until {retryUtc:O}.");

public sealed class ProviderRetryAfterException(string provider, DateTimeOffset retryUtc)
    : HttpRequestException($"{provider} provider is throttled until {retryUtc:O}.");

public sealed class AdaptiveProviderHandler(
    string provider,
    int maximumConcurrency,
    AdaptiveProviderPolicy policy) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var activity = PremiereTelemetry.ActivitySource.StartActivity("provider.request", ActivityKind.Client);
        activity?.SetTag("provider", provider);
        activity?.SetTag("operation", request.Method.Method);
        var lease = await policy.AcquireAsync(provider, maximumConcurrency, cancellationToken);
        try
        {
            var response = await base.SendAsync(request, cancellationToken);
            var result = response.StatusCode == HttpStatusCode.TooManyRequests
                ? ProviderExecutionResult.Throttled
                : (int)response.StatusCode >= 500
                    ? ProviderExecutionResult.Failed
                    : ProviderExecutionResult.Success;
            var retryAfter = response.Headers.RetryAfter?.Delta;
            await lease.CompleteAsync(result, retryAfter, CancellationToken.None);
            return response;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            await lease.CompleteAsync(ProviderExecutionResult.Failed, null, CancellationToken.None);
            throw;
        }
    }
}

public sealed class AdaptiveProviderHandlerFactory(AdaptiveProviderPolicy policy)
{
    public DelegatingHandler Create(string provider, int maximumConcurrency)
        => new AdaptiveProviderHandler(provider, Math.Max(1, maximumConcurrency), policy);
}

public sealed class ProviderAdaptiveStateStore(
    IOptions<AppDatabaseOptions> options,
    IWebHostEnvironment environment)
{
    private readonly AppDatabaseOptions _options = options.Value;

    public async Task<ProviderAdaptiveSnapshot?> GetAsync(string provider, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT CurrentConcurrency, ConsecutiveSuccesses, ConsecutiveFailures,
                   WindowFailureCount, FailureWindowStartedUtc, EwmaLatencyMilliseconds,
                   CircuitState, CooldownUntilUtc, LastThrottledUtc, UpdatedUtc
            FROM ProviderAdaptiveState WHERE Provider = $provider
            """;
        command.Parameters.AddWithValue("$provider", provider);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new ProviderAdaptiveSnapshot(
            provider,
            reader.GetInt32(0),
            0,
            reader.GetInt32(1),
            reader.GetInt32(2),
            reader.GetInt32(3),
            ReadDate(reader, 4),
            reader.IsDBNull(5) ? null : reader.GetDouble(5),
            Enum.Parse<ProviderCircuitState>(reader.GetString(6), true),
            ReadDate(reader, 7),
            ReadDate(reader, 8),
            ReadDate(reader, 9) ?? DateTimeOffset.MinValue);
    }

    public async Task SaveAsync(ProviderAdaptiveSnapshot state, CancellationToken cancellationToken)
    {
        await using var connection = CreateConnection();
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO ProviderAdaptiveState (
                Provider, CurrentConcurrency, ConsecutiveSuccesses, ConsecutiveFailures,
                WindowFailureCount, FailureWindowStartedUtc, EwmaLatencyMilliseconds,
                CircuitState, CooldownUntilUtc, LastThrottledUtc, UpdatedUtc)
            VALUES ($provider, $concurrency, $successes, $failures, $windowFailures,
                $windowStarted, $latency, $circuit, $cooldown, $throttled, $updated)
            ON CONFLICT(Provider) DO UPDATE SET
                CurrentConcurrency = excluded.CurrentConcurrency,
                ConsecutiveSuccesses = excluded.ConsecutiveSuccesses,
                ConsecutiveFailures = excluded.ConsecutiveFailures,
                WindowFailureCount = excluded.WindowFailureCount,
                FailureWindowStartedUtc = excluded.FailureWindowStartedUtc,
                EwmaLatencyMilliseconds = excluded.EwmaLatencyMilliseconds,
                CircuitState = excluded.CircuitState,
                CooldownUntilUtc = excluded.CooldownUntilUtc,
                LastThrottledUtc = excluded.LastThrottledUtc,
                UpdatedUtc = excluded.UpdatedUtc
            """;
        command.Parameters.AddWithValue("$provider", state.Provider);
        command.Parameters.AddWithValue("$concurrency", state.CurrentConcurrency);
        command.Parameters.AddWithValue("$successes", state.ConsecutiveSuccesses);
        command.Parameters.AddWithValue("$failures", state.ConsecutiveFailures);
        command.Parameters.AddWithValue("$windowFailures", state.WindowFailureCount);
        command.Parameters.AddWithValue("$windowStarted", DbDate(state.FailureWindowStartedUtc));
        command.Parameters.AddWithValue("$latency", (object?)state.EwmaLatencyMilliseconds ?? DBNull.Value);
        command.Parameters.AddWithValue("$circuit", state.CircuitState.ToString());
        command.Parameters.AddWithValue("$cooldown", DbDate(state.CooldownUntilUtc));
        command.Parameters.AddWithValue("$throttled", DbDate(state.LastThrottledUtc));
        command.Parameters.AddWithValue("$updated", state.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private SqliteConnection CreateConnection()
    {
        var path = SqliteDatabasePath.Resolve(_options.Path, environment.ContentRootPath);
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true
        };
        return SqliteConnectionFactory.Create(builder.ToString());
    }

    private static object DbDate(DateTimeOffset? value)
        => value is null ? DBNull.Value : value.Value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ReadDate(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal)
            ? null
            : DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
