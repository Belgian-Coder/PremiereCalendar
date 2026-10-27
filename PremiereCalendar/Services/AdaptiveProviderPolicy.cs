using System.Data.Common;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class AdaptiveProviderPolicy(
    ProviderAdaptiveStateStore store,
    TimeProvider timeProvider,
    PremiereTelemetry telemetry,
    IOptions<ProviderSchedulerOptions>? schedulerOptions = null)
{
    private readonly ConcurrentDictionary<string, ProviderRuntimeState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly ProviderSchedulerOptions _schedulerOptions = schedulerOptions?.Value ?? new ProviderSchedulerOptions();

    public async Task<ProviderExecutionLease> AcquireAsync(
        string provider,
        int maximumConcurrency,
        CancellationToken cancellationToken)
        => await AcquireAsync(provider, maximumConcurrency, cancellationToken, null);

    internal async Task<ProviderExecutionLease> AcquireAsync(
        string provider,
        int maximumConcurrency,
        CancellationToken cancellationToken,
        ProviderAttemptDiagnostics? diagnostics,
        TimeSpan? cooldownWaitBudget = null,
        CancellationToken callerCancellationToken = default)
    {
        var acquisitionStarted = timeProvider.GetTimestamp();
        var state = _states.GetOrAdd(provider, key => new ProviderRuntimeState(key, Math.Max(1, maximumConcurrency)));
        await state.EnsureLoadedAsync(store, cancellationToken);
        while (true)
        {
            Task waitTask;
            var waitStage = "cooldown";
            lock (state.Gate)
            {
                var now = timeProvider.GetUtcNow();
                if (state.CircuitState == ProviderCircuitState.Closed && state.CooldownUntilUtc is { } retryAfter && retryAfter > now)
                {
                    var remaining = retryAfter - now;
                    var exceedsRequestBudget = cooldownWaitBudget is { } budget
                        && remaining >= budget - timeProvider.GetElapsedTime(acquisitionStarted);
                    if (exceedsRequestBudget) callerCancellationToken.ThrowIfCancellationRequested();
                    if (ExceedsInlineWaitLimit(state, retryAfter, remaining) || exceedsRequestBudget)
                    {
                        throw new ProviderRetryAfterException(provider, retryAfter);
                    }
                    waitTask = state.GetOrStartCooldownWait(retryAfter, remaining, timeProvider);
                }
                else
                {
                    if (state.CircuitState == ProviderCircuitState.Closed && state.CooldownUntilUtc is { } closedCooldown && closedCooldown <= now)
                    {
                        state.CooldownUntilUtc = null;
                        state.ClearCooldownWait();
                    }

                    if (state.CircuitState == ProviderCircuitState.Open)
                    {
                        if (state.CooldownUntilUtc is { } cooldown && cooldown > now)
                        {
                            var remaining = cooldown - now;
                            var exceedsRequestBudget = cooldownWaitBudget is { } budget
                                && remaining >= budget - timeProvider.GetElapsedTime(acquisitionStarted);
                            if (exceedsRequestBudget) callerCancellationToken.ThrowIfCancellationRequested();
                            if (remaining > MaximumInlineWait() || exceedsRequestBudget)
                            {
                                throw new ProviderCircuitOpenException(provider, cooldown);
                            }
                            waitTask = state.GetOrStartCooldownWait(cooldown, remaining, timeProvider);
                        }
                        else
                        {
                            state.CircuitState = ProviderCircuitState.HalfOpen;
                            state.CurrentConcurrency = 1;
                            state.ClearCooldownWait();
                            waitTask = Task.CompletedTask;
                        }
                    }
                    else
                    {
                        waitTask = Task.CompletedTask;
                    }

                    if (waitTask.IsCompleted)
                    {
                        var allowed = state.CircuitState == ProviderCircuitState.HalfOpen ? 1 : state.CurrentConcurrency;
                        if (state.ActiveRequests < allowed)
                        {
                            state.ActiveRequests++;
                            telemetry.SetProviderConcurrency(provider, state.CurrentConcurrency, state.ActiveRequests, state.CircuitState.ToString());
                            diagnostics?.Record("provider.acquire.completed", new()
                            {
                                Stage = "acquire", Outcome = "acquired",
                                DurationMilliseconds = timeProvider.GetElapsedTime(acquisitionStarted).TotalMilliseconds,
                                Active = state.ActiveRequests, Limit = allowed
                            });
                            return new ProviderExecutionLease(this, state, timeProvider.GetTimestamp());
                        }

                        waitTask = state.Signal.Task;
                        waitStage = "concurrency";
                    }
                }
            }
            var waitStarted = timeProvider.GetTimestamp();
            var waitOutcome = "completed";
            try { await waitTask.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { waitOutcome = "cancelled"; throw; }
            finally
            {
                diagnostics?.Record($"provider.{waitStage}.wait.completed", new()
                {
                    Stage = waitStage, Outcome = waitOutcome,
                    WaitMilliseconds = timeProvider.GetElapsedTime(waitStarted).TotalMilliseconds
                });
            }
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
            var now = timeProvider.GetUtcNow();

            if (result == ProviderExecutionResult.Cancelled)
            {
                // Caller cancellation says nothing about provider health or latency.
            }
            else if (result == ProviderExecutionResult.Success)
            {
                state.EwmaLatencyMilliseconds = state.EwmaLatencyMilliseconds is null
                    ? elapsed.TotalMilliseconds
                    : (state.EwmaLatencyMilliseconds.Value * 0.8) + (elapsed.TotalMilliseconds * 0.2);
                state.ConsecutiveFailures = 0;
                state.WindowFailureCount = 0;
                state.FailureWindowStartedUtc = null;
                state.ConsecutiveSuccesses++;
                if (state.CircuitState == ProviderCircuitState.HalfOpen)
                {
                    state.CircuitState = ProviderCircuitState.Closed;
                    state.CooldownUntilUtc = null;
                    state.ClearCooldownWait();
                }
                if (state.ConsecutiveSuccesses >= 20
                    && state.CurrentConcurrency < state.MaximumConcurrency
                    && state.EwmaLatencyMilliseconds <= Math.Clamp(_schedulerOptions.AcceptableLatencyMilliseconds, 100, 30_000))
                {
                    state.CurrentConcurrency++;
                    state.ConsecutiveSuccesses = 0;
                }
            }
            else
            {
                state.EwmaLatencyMilliseconds = state.EwmaLatencyMilliseconds is null
                    ? elapsed.TotalMilliseconds
                    : (state.EwmaLatencyMilliseconds.Value * 0.8) + (elapsed.TotalMilliseconds * 0.2);
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
                    state.CooldownUntilUtc = AddDelay(now, GetThrottleDelay(retryAfter));
                    state.ClearCooldownWait();
                }
                else if (state.ConsecutiveFailures >= 2)
                {
                    state.CurrentConcurrency = Math.Max(1, state.CurrentConcurrency / 2);
                }
                if (state.WindowFailureCount >= 5 || state.CircuitState == ProviderCircuitState.HalfOpen)
                {
                    state.CurrentConcurrency = Math.Max(1, state.CurrentConcurrency / 2);
                    state.CircuitState = ProviderCircuitState.Open;
                    var circuitCooldown = AddDelay(now, TimeSpan.FromSeconds(60));
                    state.CooldownUntilUtc = state.CooldownUntilUtc is { } existingCooldown && existingCooldown > circuitCooldown
                        ? existingCooldown
                        : circuitCooldown;
                    state.ClearCooldownWait();
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
        catch (Exception ex) when (ex is DbException or IOException) { }
    }

    public IReadOnlyList<ProviderAdaptiveSnapshot> GetSnapshots()
        => _states.Values.Select(state => state.ToSnapshot(timeProvider.GetUtcNow())).OrderBy(state => state.Provider).ToArray();

    internal DateTimeOffset GetUtcNow() => timeProvider.GetUtcNow();
    internal long GetTimestamp() => timeProvider.GetTimestamp();
    internal TimeSpan? RemainingRequestBudget(long started, TimeSpan? timeout)
        => timeout is null || timeout == Timeout.InfiniteTimeSpan ? null : timeout - timeProvider.GetElapsedTime(started);
    internal DateTimeOffset? GetCooldownUntilUtc(string provider)
    {
        if (!_states.TryGetValue(provider, out var state)) return null;
        lock (state.Gate) return state.CooldownUntilUtc;
    }
    internal double ElapsedMilliseconds(long started) => timeProvider.GetElapsedTime(started).TotalMilliseconds;
    internal ProviderRequestDiagnostics StartRequestDiagnostics(string provider)
        => new(this, telemetry, provider);
    internal PerformanceDiagnosticContext? CaptureDiagnosticContext() => telemetry.CaptureDiagnosticContext();

    internal CancellationTokenSource? CreateRequestTimeoutSource(TimeSpan? requestTimeout)
    {
        if (requestTimeout is null || requestTimeout == Timeout.InfiniteTimeSpan) return null;
        if (requestTimeout < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        return new CancellationTokenSource(requestTimeout.Value, timeProvider);
    }

    internal async Task DelayBeforeRetryAsync(string provider, int attempt, TimeSpan? retryAfter, CancellationToken cancellationToken,
        ProviderAttemptDiagnostics? diagnostics = null, string reason = "transient")
    {
        telemetry.RecordProviderRetry(provider, retryAfter is null ? "transient" : "retry_after");
        var maximum = MaximumInlineWait();
        var delay = retryAfter is { } declared
            ? NormalizeRetryAfter(declared)
            : TimeSpan.FromMilliseconds(
                Math.Clamp(_schedulerOptions.RetryBaseMilliseconds, 50, 30_000)
                * Math.Pow(2, Math.Max(0, attempt - 1))
                * (0.75 + (Random.Shared.NextDouble() * 0.5)));
        if (delay >= maximum)
        {
            if (retryAfter is not null)
            {
                diagnostics?.Record("provider.retry.scheduled", new()
                {
                    Stage = "retry", Outcome = "deferred", Reason = "retry_after", WaitMilliseconds = delay.TotalMilliseconds
                });
                throw new ProviderRetryAfterException(provider, AddDelay(timeProvider.GetUtcNow(), delay));
            }
            delay = maximum;
        }
        diagnostics?.Record("provider.retry.scheduled", new()
        {
            Stage = "retry", Outcome = "inline", Reason = retryAfter is null ? reason : "retry_after",
            WaitMilliseconds = delay.TotalMilliseconds
        });
        var started = timeProvider.GetTimestamp();
        var outcome = "completed";
        try { await Task.Delay(delay, timeProvider, cancellationToken); }
        catch (OperationCanceledException) { outcome = "cancelled"; throw; }
        finally
        {
            diagnostics?.Record("provider.retry.wait.completed", new()
            {
                Stage = "retry", Outcome = outcome, WaitMilliseconds = timeProvider.GetElapsedTime(started).TotalMilliseconds
            });
        }
    }

    private TimeSpan MaximumInlineWait()
        => TimeSpan.FromSeconds(Math.Clamp(_schedulerOptions.RetryMaximumSeconds, 1, 300));

    private bool ExceedsInlineWaitLimit(ProviderRuntimeState state, DateTimeOffset deadlineUtc, TimeSpan remaining)
    {
        var maximum = MaximumInlineWait();
        if (remaining > maximum) return true;
        return state.LastThrottledUtc is { } throttledUtc
            && deadlineUtc >= throttledUtc
            && deadlineUtc - throttledUtc >= maximum;
    }

    private TimeSpan GetThrottleDelay(TimeSpan? retryAfter)
    {
        if (retryAfter is { } declared) return NormalizeRetryAfter(declared);
        var baseMilliseconds = Math.Clamp(_schedulerOptions.RetryBaseMilliseconds, 50, 30_000);
        return TimeSpan.FromMilliseconds(baseMilliseconds * (0.75 + (Random.Shared.NextDouble() * 0.5)));
    }

    private static TimeSpan NormalizeRetryAfter(TimeSpan retryAfter)
        => retryAfter < TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : retryAfter;

    private static DateTimeOffset AddDelay(DateTimeOffset now, TimeSpan delay)
        => delay > DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now + delay;

    internal sealed class ProviderRuntimeState(string provider, int maximumConcurrency)
    {
        private Task? _loadTask;
        private DateTimeOffset? _cooldownWaitUntilUtc;
        private Task? _cooldownWaitTask;
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
            Task loadTask;
            TaskCompletionSource? startLoad = null;
            lock (Gate)
            {
                if (_loadTask is null)
                {
                    startLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    _loadTask = startLoad.Task;
                }
                loadTask = _loadTask;
            }

            if (startLoad is not null) _ = LoadAsync(store, startLoad);
            await loadTask.WaitAsync(cancellationToken);
        }

        private async Task LoadAsync(ProviderAdaptiveStateStore store, TaskCompletionSource completion)
        {
            try
            {
                var persisted = await store.GetAsync(Provider, CancellationToken.None);
                if (persisted is not null)
                {
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
                completion.TrySetResult();
            }
            catch (Exception exception)
            {
                lock (Gate)
                {
                    if (ReferenceEquals(_loadTask, completion.Task)) _loadTask = null;
                }
                completion.TrySetException(exception);
            }
        }

        public Task GetOrStartCooldownWait(DateTimeOffset untilUtc, TimeSpan remaining, TimeProvider timeProvider)
        {
            if (_cooldownWaitTask is { IsCompleted: false } existing && _cooldownWaitUntilUtc == untilUtc)
            {
                return existing;
            }

            _cooldownWaitUntilUtc = untilUtc;
            _cooldownWaitTask = Task.Delay(remaining, timeProvider, CancellationToken.None);
            return _cooldownWaitTask;
        }

        public void ClearCooldownWait()
        {
            _cooldownWaitUntilUtc = null;
            _cooldownWaitTask = null;
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

public enum ProviderExecutionResult { Success, Failed, Throttled, Cancelled }
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
    : HttpRequestException($"{provider} provider circuit is open until {retryUtc:O}.")
{
    public DateTimeOffset RetryAtUtc { get; } = retryUtc;
}

public sealed class ProviderRetryAfterException(string provider, DateTimeOffset retryUtc)
    : HttpRequestException($"{provider} provider is throttled until {retryUtc:O}.", null, HttpStatusCode.TooManyRequests)
{
    public DateTimeOffset RetryAtUtc { get; } = retryUtc;
}

public sealed class AdaptiveProviderHandler(
    string provider,
    int maximumConcurrency,
    AdaptiveProviderPolicy policy,
    TimeSpan? requestTimeout = null,
    bool deferFinalThrottle = false) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var diagnostics = policy.StartRequestDiagnostics(provider);
        var requestStarted = policy.GetTimestamp();
        var timeoutSource = policy.CreateRequestTimeoutSource(requestTimeout);
        var timeoutLink = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource?.Token ?? default);
        var requestToken = timeoutLink.Token;
        var responseOwnsDeadline = false;
        using var activity = PremiereTelemetry.ActivitySource.StartActivity("provider.request", ActivityKind.Client);
        activity?.SetTag("provider", provider);
        activity?.SetTag("operation", request.Method.Method);
        var canRetry = request.Method == HttpMethod.Get || request.Method == HttpMethod.Head;
        var maximumAttempts = canRetry ? 3 : 1;
        try
        {
            for (var attempt = 1; attempt <= maximumAttempts; attempt++)
            {
                var attemptDiagnostics = diagnostics.StartAttempt(attempt);
                ProviderExecutionLease? lease = null;
                var responseOwnsLease = false;
                try
                {
                    lease = await policy.AcquireAsync(provider, maximumConcurrency, requestToken, attemptDiagnostics,
                        policy.RemainingRequestBudget(requestStarted, requestTimeout), cancellationToken);
                    HttpResponseMessage response;
                    var requestSent = false;
                    var sendStarted = policy.GetTimestamp();
                    try
                    {
                        using var retryRequest = attempt == 1 ? null : await CloneAsync(request, requestToken);
                        requestToken.ThrowIfCancellationRequested();
                        requestSent = true;
                        sendStarted = policy.GetTimestamp();
                        response = await base.SendAsync(retryRequest ?? request, requestToken);
                        attemptDiagnostics.HeadersCompleted(sendStarted, (int)response.StatusCode, "completed");
                    }
                    catch (Exception) when (cancellationToken.IsCancellationRequested)
                    {
                        attemptDiagnostics.HeadersCompleted(sendStarted, null, "cancelled");
                        await lease.CompleteAsync(ProviderExecutionResult.Cancelled, null, CancellationToken.None);
                        throw;
                    }
                    catch (Exception exception) when (timeoutSource?.IsCancellationRequested == true)
                    {
                        attemptDiagnostics.HeadersCompleted(sendStarted, null, "timeout");
                        await lease.CompleteAsync(
                            requestSent ? ProviderExecutionResult.Failed : ProviderExecutionResult.Cancelled,
                            null,
                            CancellationToken.None);
                        if (requestSent)
                        {
                            throw DeadlineProviderContent.TimeoutException(provider, requestTimeout, exception, requestToken);
                        }
                        throw;
                    }
                    catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or TimeoutException)
                    {
                        attemptDiagnostics.HeadersCompleted(sendStarted, null, "failed");
                        await lease.CompleteAsync(ProviderExecutionResult.Failed, null, CancellationToken.None);
                        if (attempt == maximumAttempts) throw;
                        attemptDiagnostics.Complete(ProviderExecutionResult.Failed, "transport_error", false);
                        await policy.DelayBeforeRetryAsync(provider, attempt, null, requestToken, attemptDiagnostics, "transport_error");
                        continue;
                    }

                    var result = response.StatusCode == HttpStatusCode.TooManyRequests
                        ? ProviderExecutionResult.Throttled
                        : (int)response.StatusCode >= 500
                            ? ProviderExecutionResult.Failed
                            : ProviderExecutionResult.Success;
                    var retryAfter = RetryAfter(response, policy.GetUtcNow());
                    // Retryable HTTP failures are known at headers; a successful transport
                    // remains active until its body has been consumed or abandoned.
                    if (result != ProviderExecutionResult.Success)
                    {
                        await lease.CompleteAsync(result, retryAfter, CancellationToken.None);
                    }
                    if (deferFinalThrottle && result == ProviderExecutionResult.Throttled && attempt == maximumAttempts)
                    {
                        // Preserve provider recovery information for the durable scheduler even
                        // after the last HTTP attempt, including methods that cannot be retried.
                        response.Dispose();
                        attemptDiagnostics.BodyAbandonedForRetry(result, "retry_exhausted", true);
                        cancellationToken.ThrowIfCancellationRequested();
                        throw new ProviderRetryAfterException(provider, policy.GetCooldownUntilUtc(provider) ?? policy.GetUtcNow());
                    }
                    if (attempt == maximumAttempts || result == ProviderExecutionResult.Success)
                    {
                        if (request.Method == HttpMethod.Head
                            || response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.NotModified
                            || response.Content.Headers.ContentLength == 0)
                        {
                            await lease.CompleteAsync(result, retryAfter, CancellationToken.None);
                            attemptDiagnostics.BodyCompleted(result, "empty", result);
                        }
                        else
                        {
                            response.Content = new DeadlineProviderContent(
                                response.Content, provider, requestTimeout, timeoutSource, timeoutLink,
                                cancellationToken, result == ProviderExecutionResult.Success ? lease : null,
                                (bodyResult, reason) => attemptDiagnostics.BodyCompleted(bodyResult, reason, result));
                            responseOwnsDeadline = true;
                            responseOwnsLease = result == ProviderExecutionResult.Success;
                        }
                        return response;
                    }
                    response.Dispose();
                    attemptDiagnostics.BodyAbandonedForRetry(result);
                    if (result == ProviderExecutionResult.Throttled)
                    {
                        attemptDiagnostics.Record("provider.retry.scheduled", new()
                        {
                            Stage = "retry", Outcome = "shared_cooldown", Reason = "http429",
                            WaitMilliseconds = retryAfter?.TotalMilliseconds
                        });
                    }
                    if (result != ProviderExecutionResult.Throttled)
                    {
                        await policy.DelayBeforeRetryAsync(provider, attempt, retryAfter, requestToken, attemptDiagnostics, "http5xx");
                    }
                }
                catch (Exception)
                {
                    var result = cancellationToken.IsCancellationRequested ? ProviderExecutionResult.Cancelled : ProviderExecutionResult.Failed;
                    attemptDiagnostics.Complete(result, timeoutSource?.IsCancellationRequested == true ? "timeout" : "failed", true);
                    throw;
                }
                finally
                {
                    if (!responseOwnsLease && lease is not null) await lease.DisposeAsync();
                }
            }
            throw new UnreachableException();
        }
        catch (Exception)
        {
            diagnostics.Complete(cancellationToken.IsCancellationRequested ? ProviderExecutionResult.Cancelled : ProviderExecutionResult.Failed,
                timeoutSource?.IsCancellationRequested == true ? "timeout" : "failed");
            throw;
        }
        finally
        {
            if (!responseOwnsDeadline)
            {
                timeoutLink.Dispose();
                timeoutSource?.Dispose();
            }
        }
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response, DateTimeOffset now)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta) return delta;
        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var remaining = date - now;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
        return null;
    }

    private static async Task<HttpRequestMessage> CloneAsync(HttpRequestMessage source, CancellationToken cancellationToken)
    {
        var clone = new HttpRequestMessage(source.Method, source.RequestUri)
        {
            Version = source.Version,
            VersionPolicy = source.VersionPolicy
        };
        foreach (var header in source.Headers) clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        if (source.Content is not null)
        {
            var bytes = await source.Content.ReadAsByteArrayAsync(cancellationToken);
            clone.Content = new ByteArrayContent(bytes);
            foreach (var header in source.Content.Headers) clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        return clone;
    }
}

internal sealed class ProviderRequestDiagnostics
{
    private readonly AdaptiveProviderPolicy _policy;
    private readonly PremiereTelemetry _telemetry;
    private readonly string _provider;
    private readonly PerformanceDiagnosticContext _context;
    private readonly long _started;
    private int _completed;

    internal ProviderRequestDiagnostics(AdaptiveProviderPolicy policy, PremiereTelemetry telemetry, string provider)
    {
        _policy = policy;
        _telemetry = telemetry;
        _provider = provider;
        _context = (policy.CaptureDiagnosticContext() ?? new()) with { RequestId = Guid.NewGuid().ToString("N") };
        _started = policy.GetTimestamp();
        Record("provider.request.started", new() { Stage = "request" });
    }

    internal ProviderAttemptDiagnostics StartAttempt(int attempt) => new(_policy, this, attempt);
    internal void Record(string name, PerformanceDiagnosticDetails details)
        => _telemetry.RecordDiagnostic(name, _context, details with { Provider = _provider });
    internal void Complete(ProviderExecutionResult result, string reason)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0) return;
        Record("provider.request.completed", new()
        {
            Stage = "request", Outcome = result.ToString().ToLowerInvariant(), Reason = reason,
            DurationMilliseconds = _policy.ElapsedMilliseconds(_started), IsFinal = true
        });
    }
}

internal sealed class ProviderAttemptDiagnostics
{
    private readonly AdaptiveProviderPolicy _policy;
    private readonly ProviderRequestDiagnostics _request;
    private readonly int _attempt;
    private readonly long _started;
    private long _bodyStarted;
    private int? _statusCode;
    private int _completed;

    internal ProviderAttemptDiagnostics(AdaptiveProviderPolicy policy, ProviderRequestDiagnostics request, int attempt)
    {
        _policy = policy;
        _request = request;
        _attempt = attempt;
        _started = policy.GetTimestamp();
        Record("provider.attempt.started", new() { Stage = "attempt" });
    }
    internal void Record(string name, PerformanceDiagnosticDetails details)
        => _request.Record(name, details with { Attempt = _attempt, StatusCode = details.StatusCode ?? _statusCode });
    internal void HeadersCompleted(long sendStarted, int? statusCode, string outcome)
    {
        _statusCode = statusCode;
        _bodyStarted = _policy.GetTimestamp();
        Record("provider.headers.completed", new()
        {
            Stage = "headers", Outcome = outcome, DurationMilliseconds = _policy.ElapsedMilliseconds(sendStarted)
        });
    }
    internal void BodyAbandonedForRetry(ProviderExecutionResult result, string reason = "retry", bool isFinal = false)
    {
        Record("provider.body.completed", new()
        {
            Stage = "body", Outcome = "disposed", Reason = reason,
            DurationMilliseconds = _policy.ElapsedMilliseconds(_bodyStarted)
        });
        Complete(result, reason, isFinal);
    }
    internal void BodyCompleted(ProviderExecutionResult bodyResult, string reason, ProviderExecutionResult headerResult)
    {
        Record("provider.body.completed", new()
        {
            Stage = "body", Outcome = bodyResult.ToString().ToLowerInvariant(), Reason = reason,
            DurationMilliseconds = _policy.ElapsedMilliseconds(_bodyStarted)
        });
        Complete(bodyResult == ProviderExecutionResult.Success ? headerResult : bodyResult, reason, true);
    }
    internal void Complete(ProviderExecutionResult result, string reason, bool isFinal)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0) return;
        Record("provider.attempt.completed", new()
        {
            Stage = "attempt", Outcome = result.ToString().ToLowerInvariant(), Reason = reason,
            DurationMilliseconds = _policy.ElapsedMilliseconds(_started), IsFinal = isFinal
        });
        if (isFinal) _request.Complete(result, reason);
    }
}

public sealed class AdaptiveProviderHandlerFactory(AdaptiveProviderPolicy policy)
{
    public DelegatingHandler Create(string provider, int maximumConcurrency, TimeSpan? requestTimeout = null, bool deferFinalThrottle = false)
        => new AdaptiveProviderHandler(provider, Math.Max(1, maximumConcurrency), policy, requestTimeout, deferFinalThrottle);
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
            FROM ProviderAdaptiveState WHERE Provider = @provider
            """;
        DatabaseParameters.Add(command, "@provider", provider);
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
            VALUES (@provider, @concurrency, @successes, @failures, @windowFailures,
                @windowStarted, @latency, @circuit, @cooldown, @throttled, @updated)
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
            WHERE excluded.UpdatedUtc >= ProviderAdaptiveState.UpdatedUtc
            """;
        DatabaseParameters.Add(command, "@provider", state.Provider);
        DatabaseParameters.Add(command, "@concurrency", state.CurrentConcurrency);
        DatabaseParameters.Add(command, "@successes", state.ConsecutiveSuccesses);
        DatabaseParameters.Add(command, "@failures", state.ConsecutiveFailures);
        DatabaseParameters.Add(command, "@windowFailures", state.WindowFailureCount);
        DatabaseParameters.Add(command, "@windowStarted", DbDate(state.FailureWindowStartedUtc));
        DatabaseParameters.Add(command, "@latency", (object?)state.EwmaLatencyMilliseconds ?? DBNull.Value);
        DatabaseParameters.Add(command, "@circuit", state.CircuitState.ToString());
        DatabaseParameters.Add(command, "@cooldown", DbDate(state.CooldownUntilUtc));
        DatabaseParameters.Add(command, "@throttled", DbDate(state.LastThrottledUtc));
        DatabaseParameters.Add(command, "@updated", state.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private DbConnection CreateConnection()
    {
        return DatabaseConnectionFactory.Create(_options, environment.ContentRootPath);
    }

    private static object DbDate(DateTimeOffset? value)
        => value is null ? DBNull.Value : value.Value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ReadDate(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal)
            ? null
            : DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
