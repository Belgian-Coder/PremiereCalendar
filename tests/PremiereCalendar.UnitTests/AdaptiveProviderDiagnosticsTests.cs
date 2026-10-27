using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

[Collection(SqliteTestCollection.Name)]
public sealed class AdaptiveProviderDiagnosticsTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "premiere-provider-diagnostics", Guid.NewGuid().ToString("N"));
    private readonly ManualTimeProvider _time = new(DateTimeOffset.Parse("2026-10-27T12:00:00Z"));
    private PerformanceDiagnosticStore _diagnostics = null!;
    private PremiereTelemetry _telemetry = null!;
    private AdaptiveProviderPolicy _policy = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_root);
        TestSqliteDatabase.Initialize(_root, "provider.db");
        _diagnostics = new(new TelemetryOptions { LogDirectory = "logs" }, _root);
        _telemetry = new(_diagnostics);
        _policy = new(new ProviderAdaptiveStateStore(Microsoft.Extensions.Options.Options.Create(new AppDatabaseOptions { Path = "provider.db" }), new TestEnvironment(_root)),
            _time, _telemetry, Microsoft.Extensions.Options.Options.Create(new ProviderSchedulerOptions { RetryBaseMilliseconds = 50, RetryMaximumSeconds = 10 }));
        return Task.CompletedTask;
    }

    [Fact]
    public async Task DeferredBodyKeepsCorrelationAndSeparatesCapacitySendAndBodyTimeWithoutSecrets()
    {
        var context = _telemetry.CreateDiagnosticContext(jobId: Guid.NewGuid().ToString("N"), source: "calendar");
        await using var holder = await _policy.AcquireAsync("test", 1, CancellationToken.None);
        var body = new ControlledBodyStream();
        using var client = CreateClient(_ =>
        {
            _time.Advance(TimeSpan.FromMilliseconds(30));
            return new(HttpStatusCode.OK) { Content = new StreamContent(body) };
        });
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://example.test/private-title?apikey=secret-query");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer secret-header");
        Task<HttpResponseMessage> pending;
        using (_telemetry.UseDiagnosticContext(context))
            pending = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await UntilAsync(() => Events().Any(e => EventName(e) == "provider.attempt.started"));
        // The initialized policy reaches its capacity wait synchronously before yielding.
        _time.Advance(TimeSpan.FromMilliseconds(20));
        await holder.CompleteAsync(ProviderExecutionResult.Success, null, CancellationToken.None);
        using var response = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.DoesNotContain(Events(), e => EventName(e) == "provider.request.completed");
        using (_telemetry.UseDiagnosticContext(_telemetry.CreateDiagnosticContext(source: "unrelated")))
        {
            await using var stream = await response.Content.ReadAsStreamAsync();
            var bytes = new byte[64];
            var read = stream.ReadAsync(bytes.AsMemory()).AsTask();
            await body.Started.WaitAsync(TimeSpan.FromSeconds(5));
            _time.Advance(TimeSpan.FromMilliseconds(50));
            body.AddChunk(Encoding.UTF8.GetBytes("secret-body"));
            body.Finish();
            Assert.Equal("secret-body", Encoding.UTF8.GetString(bytes, 0, await read));
            Assert.Equal(0, await stream.ReadAsync(bytes.AsMemory()));
        }
        var events = Events();
        Assert.All(events, e =>
        {
            Assert.Equal(context.LoadId, e.GetProperty("loadId").GetString());
            Assert.Equal(context.JobId, e.GetProperty("jobId").GetString());
            Assert.Equal("calendar", e.GetProperty("sourceKey").GetString());
        });
        Assert.Single(events.Select(e => e.GetProperty("requestId").GetString()).Distinct());
        Assert.Equal(20, Number(events, "provider.concurrency.wait.completed", "waitMilliseconds"));
        Assert.Equal(30, Number(events, "provider.headers.completed", "durationMilliseconds"));
        Assert.Equal(50, Number(events, "provider.body.completed", "durationMilliseconds"));
        Assert.Equal(100, Number(events, "provider.request.completed", "durationMilliseconds"));
        var raw = string.Join("\n", events.Select(e => e.GetRawText()));
        foreach (var secret in new[] { "example.test", "private-title", "apikey", "secret-query", "secret-header", "secret-body", "Authorization" })
            Assert.DoesNotContain(secret, raw, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RetryAfterRecordsSharedCooldownAndBothAttemptsWithOneRequestId()
    {
        var sends = 0;
        using var client = CreateClient(_ =>
        {
            if (++sends != 1) return new(HttpStatusCode.NoContent);
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("secret-retry-body") };
            response.Headers.RetryAfter = new(TimeSpan.FromSeconds(1));
            return response;
        });
        Task<HttpResponseMessage> pending;
        var context = _telemetry.CreateDiagnosticContext();
        using (_telemetry.UseDiagnosticContext(context)) pending = client.GetAsync("https://example.test/?key=secret-retry");
        await UntilAsync(() => _time.CreatedTimerCount == 1);
        _time.Advance(TimeSpan.FromSeconds(1));
        using var response = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        var events = Events();
        Assert.Equal(2, sends);
        Assert.Equal(2, events.Count(e => EventName(e) == "provider.attempt.completed"));
        Assert.Single(events.Select(e => e.GetProperty("requestId").GetString()).Distinct());
        Assert.Equal(1_000, Number(events, "provider.cooldown.wait.completed", "waitMilliseconds"));
        var retry = Assert.Single(events, e => EventName(e) == "provider.retry.scheduled").GetProperty("details");
        Assert.Equal("http429", retry.GetProperty("reason").GetString());
        Assert.Equal(429, retry.GetProperty("statusCode").GetInt32());
        Assert.All(events, e => Assert.Equal(context.LoadId, e.GetProperty("loadId").GetString()));
    }

    [Fact]
    public async Task ServerFailureRecordsPlannedAndActualRetryDelaySeparately()
    {
        var sends = 0;
        using var client = CreateClient(_ => new(++sends == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NoContent));
        var pending = client.GetAsync("https://example.test/");
        await UntilAsync(() => _time.CreatedTimerCount == 1);
        _time.Advance(TimeSpan.FromSeconds(2));
        using var response = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        var events = Events();
        var retry = Assert.Single(events, e => EventName(e) == "provider.retry.scheduled").GetProperty("details");
        Assert.Equal("http5xx", retry.GetProperty("reason").GetString());
        Assert.InRange(retry.GetProperty("waitMilliseconds").GetDouble(), 37.5, 62.5);
        Assert.Equal(2_000, Number(events, "provider.retry.wait.completed", "waitMilliseconds"));
        Assert.Equal("success", Assert.Single(events, e => EventName(e) == "provider.request.completed").GetProperty("details").GetProperty("outcome").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DetachedBodyCancellationOrDisposalRecordsOnceAndKeepsHealthNeutral(bool dispose)
    {
        var context = _telemetry.CreateDiagnosticContext();
        var body = new ControlledBodyStream();
        using var client = CreateClient(_ => new(HttpStatusCode.OK) { Content = new StreamContent(body) });
        using var cancellation = new CancellationTokenSource();
        HttpResponseMessage response;
        using (_telemetry.UseDiagnosticContext(context))
            response = await client.GetAsync("https://example.test/", HttpCompletionOption.ResponseHeadersRead);
        using (response)
        {
            var read = response.Content.ReadAsByteArrayAsync(cancellation.Token);
            await body.Started.WaitAsync(TimeSpan.FromSeconds(5));
            _time.Advance(TimeSpan.FromMilliseconds(40));
            if (dispose) response.Dispose(); else cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
        }
        var events = Events();
        var completed = Assert.Single(events, e => EventName(e) == "provider.body.completed");
        Assert.Equal(context.LoadId, completed.GetProperty("loadId").GetString());
        Assert.Equal("cancelled", completed.GetProperty("details").GetProperty("outcome").GetString());
        Assert.Equal(dispose ? "disposed" : "cancelled", completed.GetProperty("details").GetProperty("reason").GetString());
        Assert.Equal(40, Number(events, "provider.body.completed", "durationMilliseconds"));
        Assert.Single(events, e => EventName(e) == "provider.request.completed");
        var state = Assert.Single(_policy.GetSnapshots());
        Assert.Equal(0, state.ActiveRequests);
        Assert.Equal(0, state.ConsecutiveFailures);
        Assert.Equal(0, state.WindowFailureCount);
    }

    [Fact]
    public async Task DeferredStreamingBodyDeadlineRecordsTimeoutAndProviderFailureWithoutRetry()
    {
        var context = _telemetry.CreateDiagnosticContext();
        var body = new ControlledBodyStream();
        var sends = 0;
        using var client = new HttpClient(new AdaptiveProviderHandler("test", 1, _policy, TimeSpan.FromMilliseconds(100))
        {
            InnerHandler = new ResponseHandler(_ =>
            {
                sends++;
                return new(HttpStatusCode.OK) { Content = new StreamContent(body) };
            })
        }) { Timeout = Timeout.InfiniteTimeSpan };
        HttpResponseMessage response;
        using (_telemetry.UseDiagnosticContext(context))
            response = await client.GetAsync("https://example.test/", HttpCompletionOption.ResponseHeadersRead);
        using (response)
        {
            await using var stream = await response.Content.ReadAsStreamAsync();
            var read = stream.ReadAsync(new byte[16].AsMemory()).AsTask();
            await body.Started.WaitAsync(TimeSpan.FromSeconds(5));
            _time.Advance(TimeSpan.FromMilliseconds(100));
            var error = await Assert.ThrowsAsync<TaskCanceledException>(() => read);
            Assert.IsType<TimeoutException>(error.InnerException);
        }
        var events = Events();
        var completed = Assert.Single(events, e => EventName(e) == "provider.body.completed");
        Assert.Equal(context.LoadId, completed.GetProperty("loadId").GetString());
        Assert.Equal("failed", completed.GetProperty("details").GetProperty("outcome").GetString());
        Assert.Equal("timeout", completed.GetProperty("details").GetProperty("reason").GetString());
        Assert.Equal(100, Number(events, "provider.body.completed", "durationMilliseconds"));
        Assert.Equal(1, sends);
        var state = Assert.Single(_policy.GetSnapshots());
        Assert.Equal(0, state.ActiveRequests);
        Assert.Equal(1, state.ConsecutiveFailures);
    }

    private HttpClient CreateClient(Func<HttpRequestMessage, HttpResponseMessage> send)
        => new(new AdaptiveProviderHandler("test", 1, _policy) { InnerHandler = new ResponseHandler(send) }) { Timeout = Timeout.InfiniteTimeSpan };
    private JsonElement[] Events()
    {
        _telemetry.FlushDiagnostics();
        var directory = Path.Combine(_root, "logs", "performance");
        return Directory.Exists(directory) ? Directory.GetFiles(directory, "performance-*.ndjson")
            .SelectMany(ReadLines).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray() : [];
    }
    private static IEnumerable<string> ReadLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line) yield return line;
    }
    private static string? EventName(JsonElement value) => value.GetProperty("event").GetString();
    private static double Number(JsonElement[] events, string name, string field)
        => Assert.Single(events, e => EventName(e) == name).GetProperty("details").GetProperty(field).GetDouble();
    private static async Task UntilAsync(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!predicate())
        {
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Condition did not become true.");
            await Task.Delay(1);
        }
    }
    public Task DisposeAsync()
    {
        _diagnostics.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        return Task.CompletedTask;
    }
    private sealed class ResponseHandler(Func<HttpRequestMessage, HttpResponseMessage> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(send(request));
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

    private sealed class ControlledBodyStream : Stream
    {
        private readonly System.Threading.Channels.Channel<byte[]> _chunks = System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private byte[]? _chunk;
        private int _offset;

        public Task Started => _started.Task;
        public void AddChunk(byte[] bytes) => _chunks.Writer.TryWrite(bytes);
        public void Finish() => _chunks.Writer.TryComplete();
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
