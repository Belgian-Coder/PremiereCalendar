using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

[Collection(SqliteTestCollection.Name)]
public sealed class AdaptiveProviderPolicyReliabilityTests : IAsyncLifetime
{
    private static readonly DateTimeOffset StartUtc = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "premiere-provider-policy-tests", Guid.NewGuid().ToString("N"));
    private ManualTimeProvider _time = null!;
    private ProviderAdaptiveStateStore _store = null!;
    private IOptions<ProviderSchedulerOptions> _schedulerOptions = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        TestSqliteDatabase.Initialize(_root, "provider.db");
        var database = Microsoft.Extensions.Options.Options.Create(new AppDatabaseOptions { Path = "provider.db" });
        _store = new ProviderAdaptiveStateStore(database, new TestEnvironment(_root));
        _time = new ManualTimeProvider(StartUtc);
        _schedulerOptions = Microsoft.Extensions.Options.Options.Create(new ProviderSchedulerOptions
        {
            RetryBaseMilliseconds = 1_000,
            RetryMaximumSeconds = 10
        });
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CallerCancellationStormReleasesSlotsWithoutDamagingProviderHealth()
    {
        var policy = CreatePolicy();
        var inner = new BlockingHandler(2);
        using var client = new HttpClient(CreateAdaptiveHandler(
            policy, "cancel-storm", 2, TimeSpan.FromMinutes(1), inner))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        using var cancellation = new CancellationTokenSource();
        var requests = Enumerable.Range(0, 12)
            .Select(_ => Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync("https://example.test/", cancellation.Token)))
            .ToArray();

        await inner.RequestsStarted.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(5));

        var snapshot = Assert.Single(policy.GetSnapshots(), value => value.Provider == "cancel-storm");
        Assert.Equal(0, snapshot.ActiveRequests);
        Assert.Equal(0, snapshot.ConsecutiveFailures);
        Assert.Equal(0, snapshot.WindowFailureCount);
        Assert.Equal(2, snapshot.CurrentConcurrency);
        Assert.Null(snapshot.EwmaLatencyMilliseconds);
    }

    [Fact]
    public async Task HandlerOwnedHttpClientTimeoutCountsAsProviderFailure()
    {
        var policy = CreatePolicy();
        var inner = new BlockingHandler(1);
        using var client = new HttpClient(CreateAdaptiveHandler(
            policy, "transport-timeout", 2, TimeSpan.FromMilliseconds(100), inner))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        var request = client.GetAsync("https://example.test/");
        await inner.RequestsStarted.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => _time.CreatedTimerCount == 1);
        Assert.False(request.IsCompleted);
        _time.Advance(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => request);

        var snapshot = Assert.Single(policy.GetSnapshots(), value => value.Provider == "transport-timeout");
        Assert.Equal(0, snapshot.ActiveRequests);
        Assert.Equal(1, snapshot.ConsecutiveFailures);
        Assert.Equal(1, snapshot.WindowFailureCount);
    }

    [Fact]
    public async Task DefaultHttpClientBufferingKeepsTheDeadlineAfterImmediateHeaders()
    {
        var policy = CreatePolicy();
        using var body = new BlockingBodyContent();
        using var client = CreateBodyClient(policy, "buffered-body", body);
        var request = client.GetAsync("https://example.test/");
        await body.Started.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, Assert.Single(policy.GetSnapshots()).ActiveRequests);
        Assert.Equal(0, Assert.Single(policy.GetSnapshots()).ConsecutiveSuccesses);

        _time.Advance(TimeSpan.FromMilliseconds(100));
        var exception = await Assert.ThrowsAsync<TaskCanceledException>(() => request.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsType<TimeoutException>(exception.InnerException);
        Assert.Equal(0, Assert.Single(policy.GetSnapshots()).ActiveRequests);
        Assert.Equal(1, Assert.Single(policy.GetSnapshots()).ConsecutiveFailures);
        Assert.Equal(1, Assert.Single(policy.GetSnapshots()).WindowFailureCount);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task StreamedBodyReadsKeepTheDeadlineAndCallerCancellationIsNeutral(bool cancelCaller, bool tokenOnlyOnRead)
    {
        var policy = CreatePolicy();
        using var body = new ControlledBodyStream();
        using var client = CreateBodyClient(policy, "streamed-body", new StreamContent(body));
        using var caller = new CancellationTokenSource();
        using var response = await client.GetAsync("https://example.test/", HttpCompletionOption.ResponseHeadersRead, caller.Token);
        await using var stream = await response.Content.ReadAsStreamAsync(tokenOnlyOnRead ? CancellationToken.None : caller.Token);
        var read = stream.ReadAsync(new byte[1], tokenOnlyOnRead ? caller.Token : CancellationToken.None).AsTask();
        await body.Started.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, Assert.Single(policy.GetSnapshots()).ActiveRequests);
        Assert.Equal(0, Assert.Single(policy.GetSnapshots()).ConsecutiveSuccesses);

        if (cancelCaller) caller.Cancel();
        else _time.Advance(TimeSpan.FromMilliseconds(100));
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
        if (!cancelCaller) Assert.IsType<TimeoutException>(exception.InnerException);
        var snapshot = Assert.Single(policy.GetSnapshots());
        Assert.Equal(0, snapshot.ActiveRequests);
        Assert.Equal(cancelCaller ? 0 : 1, snapshot.ConsecutiveFailures);
        Assert.Equal(cancelCaller ? 0 : 1, snapshot.WindowFailureCount);
        if (cancelCaller) Assert.Null(snapshot.EwmaLatencyMilliseconds);
    }

    [Fact]
    public async Task BufferedBodyCallerCancellationDoesNotCountAsTimeoutOrSuccess()
    {
        var policy = CreatePolicy();
        using var body = new BlockingBodyContent();
        using var client = CreateBodyClient(policy, "buffered-cancel", body);
        using var caller = new CancellationTokenSource();
        var request = client.GetAsync("https://example.test/", caller.Token);
        await body.Started.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(TimeSpan.FromSeconds(5)));
        var snapshot = Assert.Single(policy.GetSnapshots());
        Assert.Equal(0, snapshot.ActiveRequests);
        Assert.Equal(0, snapshot.ConsecutiveFailures);
        Assert.Equal(0, snapshot.ConsecutiveSuccesses);
        Assert.Null(snapshot.EwmaLatencyMilliseconds);
    }

    [Fact]
    public async Task StreamingChunksDoNotRefreshTheTotalBodyBudget()
    {
        var policy = CreatePolicy();
        using var body = new ControlledBodyStream();
        body.AddChunk([42]);
        using var client = CreateBodyClient(policy, "slow-body", new StreamContent(body));
        using var response = await client.GetAsync("https://example.test/", HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync();
        _time.Advance(TimeSpan.FromMilliseconds(60));
        Assert.Equal(1, await stream.ReadAsync(new byte[1]));
        var read = stream.ReadAsync(new byte[1]).AsTask();
        _time.Advance(TimeSpan.FromMilliseconds(40));
        var exception = await Assert.ThrowsAsync<TaskCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsType<TimeoutException>(exception.InnerException);
        Assert.Equal(1, Assert.Single(policy.GetSnapshots()).ConsecutiveFailures);
    }

    [Fact]
    public async Task BodyIOExceptionCountsOnceWithoutRecordingHeaderSuccess()
    {
        var policy = CreatePolicy();
        using var body = new ControlledBodyStream();
        body.Fail(new IOException("Provider disconnected during the body."));
        using var client = CreateBodyClient(policy, "body-io", new StreamContent(body));
        using var response = await client.GetAsync("https://example.test/", HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync();
        await Assert.ThrowsAsync<IOException>(() => stream.ReadAsync(new byte[1]).AsTask());
        response.Dispose();
        var snapshot = Assert.Single(policy.GetSnapshots());
        Assert.Equal(0, snapshot.ActiveRequests);
        Assert.Equal(0, snapshot.ConsecutiveSuccesses);
        Assert.Equal(1, snapshot.ConsecutiveFailures);
        Assert.Equal(1, snapshot.WindowFailureCount);
    }

    [Fact]
    public async Task FinalHttpFailureBodyTimeoutDoesNotCountTheSameResponseTwice()
    {
        var policy = CreatePolicy();
        using var body = new BlockingBodyContent();
        var inner = new ScriptedHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError) { Content = body });
        using var client = new HttpClient(CreateAdaptiveHandler(policy, "failed-status-body", 1, TimeSpan.FromMilliseconds(100), inner))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, "https://example.test/");
        var request = client.SendAsync(message);
        await body.Started.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, Assert.Single(policy.GetSnapshots()).ConsecutiveFailures);
        _time.Advance(TimeSpan.FromMilliseconds(100));
        var exception = await Assert.ThrowsAsync<TaskCanceledException>(() => request.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsType<TimeoutException>(exception.InnerException);
        Assert.Equal(1, inner.SendCount);
        Assert.Equal(1, Assert.Single(policy.GetSnapshots()).ConsecutiveFailures);
        Assert.Equal(1, Assert.Single(policy.GetSnapshots()).WindowFailureCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisposingUnconsumedOrPendingBodyReleasesCapacityWithoutHealthDamage(bool pendingRead)
    {
        var policy = CreatePolicy();
        using var body = new ControlledBodyStream();
        using var client = CreateBodyClient(policy, "abandoned-body", new StreamContent(body));
        using var response = await client.GetAsync("https://example.test/", HttpCompletionOption.ResponseHeadersRead);
        Task<int>? read = null;
        if (pendingRead)
        {
            var stream = await response.Content.ReadAsStreamAsync();
            read = stream.ReadAsync(new byte[1]).AsTask();
            await body.Started.WaitAsync(TimeSpan.FromSeconds(5));
            stream.Dispose();
        }
        else response.Dispose();
        if (read is not null) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
        var snapshot = Assert.Single(policy.GetSnapshots());
        Assert.Equal(0, snapshot.ActiveRequests);
        Assert.Equal(0, snapshot.ConsecutiveFailures);
        Assert.Equal(0, snapshot.ConsecutiveSuccesses);
        Assert.Null(snapshot.EwmaLatencyMilliseconds);
        await using var available = await policy.AcquireAsync("abandoned-body", 1, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        await available.CompleteAsync(ProviderExecutionResult.Cancelled, null, CancellationToken.None);
    }

    [Fact]
    public async Task CompleteBodyPreservesGzipHeadersAndReleasesTheLeaseAtEof()
    {
        var policy = CreatePolicy();
        var expected = "Dataset payload containing gzip data.";
        using var compressed = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(compressed, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new StreamWriter(gzip)) writer.Write(expected);
        var content = new ByteArrayContent(compressed.ToArray());
        content.Headers.ContentEncoding.Add("gzip");
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        content.Headers.TryAddWithoutValidation("X-Provider-Body", "preserved");
        using var client = CreateBodyClient(policy, "gzip-body", content);
        using var response = await client.GetAsync("https://example.test/", HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal("gzip", Assert.Single(response.Content.Headers.ContentEncoding));
        Assert.Equal(content.Headers.ContentLength, response.Content.Headers.ContentLength);
        Assert.Equal("application/octet-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("preserved", Assert.Single(response.Content.Headers.GetValues("X-Provider-Body")));
        await using var stream = await response.Content.ReadAsStreamAsync();
        await using var uncompressed = new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionMode.Decompress);
        using var reader = new StreamReader(uncompressed);
        Assert.Equal(expected, await reader.ReadToEndAsync());
        Assert.Equal(1, Assert.Single(policy.GetSnapshots()).ConsecutiveSuccesses);
        // A consumer may stop exactly at the gzip trailer; explicitly observe the transport EOF.
        Assert.Equal(0, await stream.ReadAsync(new byte[1]));
        var snapshot = Assert.Single(policy.GetSnapshots());
        Assert.Equal(0, snapshot.ActiveRequests);
        Assert.Equal(1, snapshot.ConsecutiveSuccesses);
        Assert.Equal(0, snapshot.ConsecutiveFailures);
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, Assert.Single(policy.GetSnapshots()).ConsecutiveSuccesses);
    }

    [Fact]
    public async Task SynchronousBodyReadUsesTheSameCancellableDeadline()
    {
        var policy = CreatePolicy();
        using var body = new ControlledBodyStream();
        using var client = CreateBodyClient(policy, "sync-body", new StreamContent(body));
        using var response = await client.GetAsync("https://example.test/", HttpCompletionOption.ResponseHeadersRead);
        using var stream = response.Content.ReadAsStream();
        var read = Task.Run(() => stream.Read(new byte[1], 0, 1));
        await body.Started.WaitAsync(TimeSpan.FromSeconds(5));
        _time.Advance(TimeSpan.FromMilliseconds(100));
        var exception = await Assert.ThrowsAsync<TaskCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsType<TimeoutException>(exception.InnerException);
        Assert.Equal(1, Assert.Single(policy.GetSnapshots()).ConsecutiveFailures);
    }

    [Theory]
    [InlineData("HEAD", System.Net.HttpStatusCode.OK)]
    [InlineData("GET", System.Net.HttpStatusCode.NoContent)]
    [InlineData("GET", System.Net.HttpStatusCode.NotModified)]
    [InlineData("GET", System.Net.HttpStatusCode.OK)]
    public async Task KnownEmptyBodiesCompleteWithoutRequiringARead(string method, System.Net.HttpStatusCode status)
    {
        var policy = CreatePolicy();
        var inner = new ScriptedHandler(_ => new HttpResponseMessage(status) { Content = new ByteArrayContent([]) });
        using var client = new HttpClient(CreateAdaptiveHandler(policy, "empty-body", 1, TimeSpan.FromMilliseconds(100), inner))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        using var request = new HttpRequestMessage(new HttpMethod(method), "https://example.test/");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(0, Assert.Single(policy.GetSnapshots()).ActiveRequests);
        Assert.Equal(1, Assert.Single(policy.GetSnapshots()).ConsecutiveSuccesses);
    }

    [Fact]
    public async Task RetryDelayConsumesTheSameTotalRequestTimeoutBudget()
    {
        var policy = CreatePolicy(new ProviderSchedulerOptions { RetryBaseMilliseconds = 1_000, RetryMaximumSeconds = 10 });
        var inner = new ScriptedHandler(
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError),
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        using var client = new HttpClient(CreateAdaptiveHandler(
            policy, "retry-timeout", 2, TimeSpan.FromMilliseconds(500), inner))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        var request = client.GetAsync("https://example.test/");

        await WaitUntilAsync(() => inner.SendCount == 1 && _time.CreatedTimerCount >= 2);
        _time.Advance(TimeSpan.FromMilliseconds(499));
        await FlushContinuationsAsync();
        Assert.Equal(1, inner.SendCount);
        Assert.False(request.IsCompleted);

        _time.Advance(TimeSpan.FromMilliseconds(1));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, inner.SendCount);
        var snapshot = Assert.Single(policy.GetSnapshots(), value => value.Provider == "retry-timeout");
        Assert.Equal(0, snapshot.ActiveRequests);
        Assert.Equal(1, snapshot.ConsecutiveFailures);
    }

    [Fact]
    public async Task CooldownBeyondRemainingRequestBudgetPreservesRetryDeadlineInsteadOfTimingOut()
    {
        var policy = CreatePolicy(new ProviderSchedulerOptions { RetryBaseMilliseconds = 1_000, RetryMaximumSeconds = 10 });
        var inner = new ScriptedHandler(
            _ =>
            {
                var response = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
                return response;
            },
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        using var client = new HttpClient(CreateAdaptiveHandler(
            policy, "cooldown-timeout", 2, TimeSpan.FromMilliseconds(500), inner))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        var request = client.GetAsync("https://example.test/");

        var deferred = await Assert.ThrowsAsync<ProviderRetryAfterException>(() => request.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(StartUtc.AddSeconds(2), deferred.RetryAtUtc);
        Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, deferred.StatusCode);
        Assert.Equal(1, _time.CreatedTimerCount);
        Assert.Equal(1, inner.SendCount);
        var snapshot = Assert.Single(policy.GetSnapshots(), value => value.Provider == "cooldown-timeout");
        Assert.Equal(0, snapshot.ActiveRequests);
        Assert.Equal(1, snapshot.ConsecutiveFailures);
        Assert.Equal(StartUtc.AddSeconds(2), snapshot.CooldownUntilUtc);
    }

    [Fact]
    public async Task PersistedCooldownIsSharedByConcurrentAcquisitionsAndWaiterCancellationIsIsolated()
    {
        var initial = CreatePolicy();
        await using (var lease = await initial.AcquireAsync("persisted-cooldown", 4, CancellationToken.None))
        {
            await lease.CompleteAsync(ProviderExecutionResult.Throttled, TimeSpan.FromSeconds(5), CancellationToken.None);
        }

        var restarted = CreatePolicy();
        using var canceledWaiter = new CancellationTokenSource();
        var canceled = restarted.AcquireAsync("persisted-cooldown", 4, canceledWaiter.Token);
        canceledWaiter.Cancel();
        var first = restarted.AcquireAsync("persisted-cooldown", 4, CancellationToken.None);
        var second = restarted.AcquireAsync("persisted-cooldown", 4, CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        await WaitUntilAsync(() => _time.CreatedTimerCount == 1);
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        _time.Advance(TimeSpan.FromSeconds(5) - TimeSpan.FromMilliseconds(1));
        await FlushContinuationsAsync();
        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        _time.Advance(TimeSpan.FromMilliseconds(1));
        var leases = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, Assert.Single(restarted.GetSnapshots(), value => value.Provider == "persisted-cooldown").CurrentConcurrency);
        foreach (var lease in leases)
        {
            await lease.CompleteAsync(ProviderExecutionResult.Success, null, CancellationToken.None);
        }
    }

    [Fact]
    public async Task FailedPersistedInitializationCanBeRetried()
    {
        var database = new AppDatabaseOptions { Path = "not-created/provider.db" };
        var store = new ProviderAdaptiveStateStore(Microsoft.Extensions.Options.Options.Create(database), new TestEnvironment(_root));
        var policy = new AdaptiveProviderPolicy(store, _time, new PremiereTelemetry(), _schedulerOptions);

        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(
            () => policy.AcquireAsync("retry-load", 2, CancellationToken.None));

        database.Path = "provider.db";
        await using var lease = await policy.AcquireAsync("retry-load", 2, CancellationToken.None);
        await lease.CompleteAsync(ProviderExecutionResult.Success, null, CancellationToken.None);
        Assert.Equal(0, Assert.Single(policy.GetSnapshots(), value => value.Provider == "retry-load").ConsecutiveFailures);
    }

    [Fact]
    public async Task NoHeader429UsesOneSharedJitteredCooldownAndDoesNotRetryEarly()
    {
        var policy = CreatePolicy(new ProviderSchedulerOptions { RetryBaseMilliseconds = 1_000, RetryMaximumSeconds = 3 });
        var inner = new ScriptedHandler(
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests),
            _ => new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        using var client = new HttpClient(new AdaptiveProviderHandler("shared-429", 4, policy) { InnerHandler = inner });

        var send = client.GetAsync("https://example.test/");
        await WaitUntilAsync(() => inner.SendCount == 1
            && Assert.Single(policy.GetSnapshots(), value => value.Provider == "shared-429").CooldownUntilUtc is not null);
        var snapshot = Assert.Single(policy.GetSnapshots(), value => value.Provider == "shared-429");
        var cooldownUntil = Assert.IsType<DateTimeOffset>(snapshot.CooldownUntilUtc);
        var delay = cooldownUntil - StartUtc;
        Assert.InRange(delay.TotalMilliseconds, 750, 1_250);

        using var canceledWaiter = new CancellationTokenSource();
        var canceled = policy.AcquireAsync("shared-429", 4, canceledWaiter.Token);
        var sharedWaiter = policy.AcquireAsync("shared-429", 4, CancellationToken.None);
        await WaitUntilAsync(() => _time.CreatedTimerCount == 1);
        canceledWaiter.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);

        _time.Advance(delay - TimeSpan.FromMilliseconds(1));
        await FlushContinuationsAsync();
        Assert.Equal(1, inner.SendCount);
        Assert.False(send.IsCompleted);
        Assert.False(sharedWaiter.IsCompleted);

        _time.Advance(TimeSpan.FromMilliseconds(1));
        using var response = await send.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(2, inner.SendCount);
        var lease = await sharedWaiter.WaitAsync(TimeSpan.FromSeconds(5));
        await lease.CompleteAsync(ProviderExecutionResult.Success, null, CancellationToken.None);
    }

    [Fact]
    public async Task LongRetryAfterAndCircuitDeadlinesAreExposedWithoutClampingPersistedTime()
    {
        var policy = CreatePolicy(new ProviderSchedulerOptions { RetryBaseMilliseconds = 100, RetryMaximumSeconds = 1 });
        var expected = StartUtc.AddHours(2);
        await using (var lease = await policy.AcquireAsync("long-retry", 4, CancellationToken.None))
        {
            await lease.CompleteAsync(ProviderExecutionResult.Throttled, TimeSpan.FromHours(2), CancellationToken.None);
        }

        var retry = await Assert.ThrowsAsync<ProviderRetryAfterException>(
            () => policy.AcquireAsync("long-retry", 4, CancellationToken.None));
        Assert.Equal(expected, retry.RetryAtUtc);
        Assert.Equal(expected, Assert.Single(policy.GetSnapshots(), value => value.Provider == "long-retry").CooldownUntilUtc);
        Assert.Equal(0, _time.CreatedTimerCount);

        for (var index = 0; index < 5; index++)
        {
            await using var failed = await policy.AcquireAsync("open-circuit", 4, CancellationToken.None);
            await failed.CompleteAsync(ProviderExecutionResult.Failed, null, CancellationToken.None);
        }
        var circuit = await Assert.ThrowsAsync<ProviderCircuitOpenException>(
            () => policy.AcquireAsync("open-circuit", 4, CancellationToken.None));
        Assert.Equal(StartUtc.AddSeconds(60), circuit.RetryAtUtc);
    }

    [Fact]
    public async Task CooldownUsesBudgetRemainingAfterHeadersRatherThanWholeRequestTimeout()
    {
        var policy = CreatePolicy(new ProviderSchedulerOptions { RetryMaximumSeconds = 30 });
        var inner = new ScriptedHandler(_ =>
        {
            _time.Advance(TimeSpan.FromSeconds(9));
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(3));
            return response;
        });
        using var client = new HttpClient(CreateAdaptiveHandler(policy, "remaining-budget", 1, TimeSpan.FromSeconds(10), inner))
        { Timeout = Timeout.InfiniteTimeSpan };
        var deferred = await Assert.ThrowsAsync<ProviderRetryAfterException>(() => client.GetAsync("https://example.test/"));
        Assert.Equal(StartUtc.AddSeconds(12), deferred.RetryAtUtc);
        Assert.Equal(1, inner.SendCount);
        Assert.Equal(1, _time.CreatedTimerCount);
        Assert.Equal(0, Assert.Single(policy.GetSnapshots()).ActiveRequests);
    }

    [Fact]
    public async Task CooldownThatFitsRemainingBudgetStillRetriesInline()
    {
        var policy = CreatePolicy();
        var inner = new ScriptedHandler(_ =>
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(TimeSpan.FromMilliseconds(200));
            return response;
        }, _ => new HttpResponseMessage(System.Net.HttpStatusCode.NoContent));
        using var client = new HttpClient(CreateAdaptiveHandler(policy, "fitting-budget", 1, TimeSpan.FromMilliseconds(500), inner))
        { Timeout = Timeout.InfiniteTimeSpan };
        var request = client.GetAsync("https://example.test/");
        await WaitUntilAsync(() => _time.CreatedTimerCount == 2);
        Assert.False(request.IsCompleted);
        _time.Advance(TimeSpan.FromMilliseconds(200));
        using var response = await request.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(System.Net.HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(2, inner.SendCount);
        Assert.Equal(0, Assert.Single(policy.GetSnapshots()).ActiveRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Final429PreservesTypedDeadlineAndStatusWithoutExtraAttempts(bool post)
    {
        var policy = CreatePolicy();
        var inner = new ScriptedHandler(_ =>
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(TimeSpan.FromMilliseconds(100));
            return response;
        });
        using var client = new HttpClient(new AdaptiveProviderHandler("final-429", 1, policy, deferFinalThrottle: true) { InnerHandler = inner });
        using var message = new HttpRequestMessage(post ? HttpMethod.Post : HttpMethod.Get, "https://example.test/");
        var request = client.SendAsync(message);
        var attempts = post ? 1 : 3;
        for (var attempt = 1; attempt < attempts; attempt++)
        {
            var expectedTimers = attempt;
            await WaitUntilAsync(() => _time.CreatedTimerCount == expectedTimers);
            _time.Advance(TimeSpan.FromMilliseconds(100));
        }
        var deferred = await Assert.ThrowsAsync<ProviderRetryAfterException>(() => request.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(StartUtc.AddMilliseconds(attempts * 100), deferred.RetryAtUtc);
        Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, deferred.StatusCode);
        Assert.Equal(attempts, inner.SendCount);
        var state = Assert.Single(policy.GetSnapshots());
        Assert.Equal(0, state.ActiveRequests);
        Assert.Equal(attempts, state.ConsecutiveFailures);
    }

    [Fact]
    public async Task CallerCancellationDuring429DoesNotBecomeRetryDeadlineFailure()
    {
        var policy = CreatePolicy();
        using var cancellation = new CancellationTokenSource();
        var inner = new ScriptedHandler(_ =>
        {
            cancellation.Cancel();
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(25));
            return response;
        });
        using var client = new HttpClient(CreateAdaptiveHandler(policy, "cancel-throttle", 1, TimeSpan.FromSeconds(20), inner))
        { Timeout = Timeout.InfiniteTimeSpan };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetAsync("https://example.test/", cancellation.Token));
        Assert.Equal(1, inner.SendCount);
        Assert.Equal(0, Assert.Single(policy.GetSnapshots()).ActiveRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DefaultFinal429StillReturnsStatusHeadersAndBody(bool post)
    {
        var policy = CreatePolicy();
        var inner = new ScriptedHandler(_ =>
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests) { Content = new StringContent("provider quota body") };
            response.Headers.RetryAfter = new(TimeSpan.FromMilliseconds(100));
            return response;
        });
        using var client = new HttpClient(new AdaptiveProviderHandler("default-429", 1, policy) { InnerHandler = inner });
        using var message = new HttpRequestMessage(post ? HttpMethod.Post : HttpMethod.Get, "https://example.test/");
        var request = client.SendAsync(message);
        var attempts = post ? 1 : 3;
        for (var attempt = 1; attempt < attempts; attempt++)
        {
            var expectedTimers = attempt;
            await WaitUntilAsync(() => _time.CreatedTimerCount == expectedTimers);
            _time.Advance(TimeSpan.FromMilliseconds(100));
        }
        using var response = await request.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(System.Net.HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(TimeSpan.FromMilliseconds(100), response.Headers.RetryAfter!.Delta);
        Assert.Equal("provider quota body", await response.Content.ReadAsStringAsync());
        Assert.Equal(attempts, inner.SendCount);
    }

    [Fact]
    public async Task DefaultFinal429LetsOmdbPersistRateLimitFromResponseBodyAndDeadline()
    {
        var policy = CreatePolicy();
        var inner = RateLimitedClientHandler();
        using var http = new HttpClient(new AdaptiveProviderHandler("omdb", 1, policy) { InnerHandler = inner })
        { BaseAddress = new Uri("https://example.test/") };
        using var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var store = new RecordingOmdbCacheStore();
        var client = new OmdbClient(http, cache, Microsoft.Extensions.Options.Options.Create(new OmdbOptions { Enabled = true, ApiKey = "test" }),
            cacheStore: store, timeProvider: _time);
        var lookup = client.GetByImdbIdAsync("tt001", CancellationToken.None);
        await AdvanceClientRetryDelaysAsync();
        var error = await Assert.ThrowsAsync<ExternalApiException>(() => lookup);
        Assert.Contains("Request limit reached", error.Message);
        Assert.Equal(_time.GetUtcNow().AddSeconds(30), store.State.RateLimitedUntilUtc);
        Assert.Contains("Request limit reached", store.State.LastError);
        Assert.Null(await client.GetByImdbIdAsync("tt002", CancellationToken.None));
        Assert.Equal(3, inner.SendCount);
    }

    [Fact]
    public async Task DefaultFinal429LetsWatchmodeRememberCooldownAndSkipNextRequest()
    {
        var policy = CreatePolicy();
        var inner = RateLimitedClientHandler();
        using var http = new HttpClient(new AdaptiveProviderHandler("watchmode", 1, policy) { InnerHandler = inner })
        { BaseAddress = new Uri("https://example.test/") };
        using var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        var client = new WatchmodeClient(http, cache, Microsoft.Extensions.Options.Options.Create(new WatchmodeOptions
        { Enabled = true, ApiKey = "test", EnableReleaseDiscovery = true }));
        var lookup = client.GetReleaseCandidatesAsync(new(2026, 10, 26), new(2026, 11, 1), CancellationToken.None);
        await AdvanceClientRetryDelaysAsync();
        Assert.Empty(await lookup);
        Assert.Empty(await client.GetReleaseCandidatesAsync(new(2026, 11, 2), new(2026, 11, 8), CancellationToken.None));
        Assert.Equal(3, inner.SendCount);
    }

    private static ScriptedHandler RateLimitedClientHandler()
        => new(attempt =>
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.TooManyRequests)
            { Content = new StringContent("{\"Response\":\"False\",\"Error\":\"Request limit reached!\"}") };
            response.Headers.RetryAfter = new(attempt == 3 ? TimeSpan.FromSeconds(30) : TimeSpan.FromMilliseconds(50));
            return response;
        });

    private async Task AdvanceClientRetryDelaysAsync()
    {
        for (var attempt = 1; attempt < 3; attempt++)
        {
            var expectedTimers = attempt;
            await WaitUntilAsync(() => _time.CreatedTimerCount == expectedTimers);
            _time.Advance(TimeSpan.FromMilliseconds(50));
        }
    }

    private sealed class RecordingOmdbCacheStore : IOmdbCacheStore
    {
        public OmdbProviderCacheState State { get; private set; } = new(null, null, null);
        public Task<OmdbCacheEntry?> GetAsync(string imdbId, CancellationToken cancellationToken) => Task.FromResult<OmdbCacheEntry?>(null);
        public Task SetAsync(string imdbId, PremiereCalendar.Models.OmdbItem item, DateTimeOffset cachedAtUtc, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<OmdbProviderCacheState> GetProviderStateAsync(CancellationToken cancellationToken) => Task.FromResult(State);
        public Task MarkRateLimitedAsync(DateTimeOffset untilUtc, string error, CancellationToken cancellationToken)
        { State = new(untilUtc, error, null); return Task.CompletedTask; }
        public Task MarkFailureAsync(string error, CancellationToken cancellationToken)
        { State = new(null, error, null); return Task.CompletedTask; }
    }

    public Task DisposeAsync()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }

    private AdaptiveProviderPolicy CreatePolicy(ProviderSchedulerOptions? schedulerOptions = null)
        => new(_store, _time, new PremiereTelemetry(), Microsoft.Extensions.Options.Options.Create(
            schedulerOptions ?? _schedulerOptions.Value));

    private static HttpClient CreateBodyClient(AdaptiveProviderPolicy policy, string provider, HttpContent content)
        => new(CreateAdaptiveHandler(policy, provider, 1, TimeSpan.FromMilliseconds(100),
            new ScriptedHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content })))
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

    private static DelegatingHandler CreateAdaptiveHandler(
        AdaptiveProviderPolicy policy,
        string provider,
        int concurrency,
        TimeSpan timeout,
        HttpMessageHandler inner)
    {
        var handler = (DelegatingHandler)new AdaptiveProviderHandlerFactory(policy).Create(provider, concurrency, timeout);
        handler.InnerHandler = inner;
        return handler;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Condition did not become true in time.");
            await Task.Delay(1);
        }
    }

    private static async Task FlushContinuationsAsync()
    {
        for (var index = 0; index < 10; index++) await Task.Yield();
    }

    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "PremiereCalendar.UnitTests";
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class BlockingHandler(int requiredRequests) : HttpMessageHandler
    {
        private readonly TaskCompletionSource _requestsStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _sendCount;

        public Task RequestsStarted => _requestsStarted.Task;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _sendCount) >= requiredRequests) _requestsStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Canceled request unexpectedly completed.");
        }
    }

    private sealed class BlockingBodyContent : HttpContent
    {
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Started => _started.Task;

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }

        protected override Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
            => SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context, CancellationToken cancellationToken)
        {
            _started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }
    }

    private sealed class ControlledBodyStream : Stream
    {
        private readonly System.Threading.Channels.Channel<byte[]> _chunks = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private byte[]? _chunk;
        private int _offset;

        public Task Started => _started.Task;
        public void AddChunk(byte[] bytes) => _chunks.Writer.TryWrite(bytes);
        public void Fail(Exception exception) => _chunks.Writer.TryComplete(exception);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("Use the cancellable asynchronous transport path.");
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _started.TrySetResult();
            if (buffer.Length == 0) return 0;
            if (_chunk is null || _offset == _chunk.Length)
            {
                if (!await _chunks.Reader.WaitToReadAsync(cancellationToken)) return 0;
                if (!_chunks.Reader.TryRead(out _chunk)) throw new InvalidOperationException("Expected queued body bytes.");
                _offset = 0;
            }
            var count = Math.Min(buffer.Length, _chunk.Length - _offset);
            _chunk.AsMemory(_offset, count).CopyTo(buffer);
            _offset += count;
            return count;
        }
    }

    private sealed class ScriptedHandler(params Func<int, HttpResponseMessage>[] responses) : HttpMessageHandler
    {
        private int _sendCount;
        public int SendCount => Volatile.Read(ref _sendCount);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var attempt = Interlocked.Increment(ref _sendCount);
            return Task.FromResult(responses[Math.Min(attempt - 1, responses.Length - 1)](attempt));
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset startUtc) : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private DateTimeOffset _utcNow = startUtc;
        private long _timestamp;
        private int _createdTimerCount;

        public int CreatedTimerCount => Volatile.Read(ref _createdTimerCount);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override DateTimeOffset GetUtcNow()
        {
            lock (_gate) return _utcNow;
        }

        public override long GetTimestamp()
        {
            lock (_gate) return _timestamp;
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate)
            {
                var timer = new ManualTimer(this, callback, state);
                _timers.Add(timer);
                _createdTimerCount++;
                timer.ChangeUnderLock(dueTime, period);
                return timer;
            }
        }

        public void Advance(TimeSpan amount)
        {
            List<(TimerCallback Callback, object? State)> callbacks = [];
            lock (_gate)
            {
                _utcNow += amount;
                _timestamp += amount.Ticks;
                foreach (var timer in _timers)
                {
                    if (timer.TryFireUnderLock(_timestamp, out var callback, out var state)) callbacks.Add((callback, state));
                }
            }

            foreach (var (callback, state) in callbacks) callback(state);
        }

        private bool Change(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
        {
            lock (_gate) return timer.ChangeUnderLock(dueTime, period);
        }

        private bool Dispose(ManualTimer timer)
        {
            lock (_gate) return timer.DisposeUnderLock();
        }

        private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            private long _dueTimestamp = long.MaxValue;
            private TimeSpan _period = Timeout.InfiniteTimeSpan;
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period) => owner.Change(this, dueTime, period);
            public void Dispose() => owner.Dispose(this);
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public bool ChangeUnderLock(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed) return false;
                _period = period;
                _dueTimestamp = dueTime == Timeout.InfiniteTimeSpan
                    ? long.MaxValue
                    : owner._timestamp + Math.Max(0, dueTime.Ticks);
                return true;
            }

            public bool TryFireUnderLock(long timestamp, out TimerCallback timerCallback, out object? timerState)
            {
                if (_disposed || _dueTimestamp > timestamp)
                {
                    timerCallback = null!;
                    timerState = null;
                    return false;
                }

                if (_period <= TimeSpan.Zero || _period == Timeout.InfiniteTimeSpan)
                {
                    _dueTimestamp = long.MaxValue;
                }
                else
                {
                    _dueTimestamp = timestamp + _period.Ticks;
                }
                timerCallback = callback;
                timerState = state;
                return true;
            }

            public bool DisposeUnderLock()
            {
                if (_disposed) return false;
                _disposed = true;
                _dueTimestamp = long.MaxValue;
                return true;
            }
        }
    }
}
