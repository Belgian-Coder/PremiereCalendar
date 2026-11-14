using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using PremiereCalendar.Hosting;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class ProviderCompressionTransportTests : IAsyncLifetime
{
    private AdaptiveProviderPolicy _policy = null!;
    private PostgresTestDatabase _database = null!;
    private readonly CapturingLogger _logger = new();

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
        _policy = new AdaptiveProviderPolicy(
            new ProviderAdaptiveStateStore(_database.Options),
            TimeProvider.System,
            new PremiereTelemetry(),
            Microsoft.Extensions.Options.Options.Create(new ProviderSchedulerOptions()),
            logger: _logger);
    }

    [Fact]
    public void ProviderPrimaryHandlerNegotiatesGzipDeflateAndBrotliButNotZstd()
    {
        using var handler = ProviderClientServiceExtensions.CreateProviderPrimaryHandler();

        Assert.Equal(DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli, handler.AutomaticDecompression);
        // As the factory's default primary handler: connections live as long as the (two minute) handler.
        Assert.Equal(TimeSpan.FromMinutes(2), ProviderClientServiceExtensions.ProviderHandlerLifetime);
        Assert.Equal(ProviderClientServiceExtensions.ProviderHandlerLifetime, handler.PooledConnectionLifetime);
        // Cookies keep the SocketsHttpHandler default instead of being silently disabled.
        Assert.True(handler.UseCookies);
        Assert.True(handler.AllowAutoRedirect);
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("deflate")]
    [InlineData("br")]
    public async Task CompressedProviderBodyIsDecodedTransparentlyAndTheLeaseCompletesAtEndOfStream(string encoding)
    {
        var payload = JsonPayload();
        var compressed = Compress(payload, encoding);
        await using var server = new LoopbackServer(encoding, compressed);
        using var client = CreateClient("compressed-" + encoding);

        using var response = await client.GetAsync(server.Url, HttpCompletionOption.ResponseHeadersRead);

        Assert.Contains(encoding, server.LastRequestHeaders, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("zstd", server.LastRequestHeaders, StringComparison.OrdinalIgnoreCase);
        // The decoded response no longer advertises the wire encoding or the compressed length, so the adaptive
        // handler's body wrapper cannot complete by byte count and must complete when the stream ends.
        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Null(response.Content.Headers.ContentLength);
        Assert.Equal(1, Assert.Single(_policy.GetSnapshots()).ActiveRequests);

        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(payload, body);
        Assert.True(compressed.Length < Encoding.UTF8.GetByteCount(payload) / 4, "the fixture must actually compress");
        var snapshot = Assert.Single(_policy.GetSnapshots());
        Assert.Equal(0, snapshot.ActiveRequests);
        Assert.Equal(1, snapshot.ConsecutiveSuccesses);
        Assert.Equal(0, snapshot.ConsecutiveFailures);
    }

    [Fact]
    public async Task StreamedCompressedBodyReleasesItsSlotOnlyAfterTheFinalRead()
    {
        var payload = JsonPayload();
        await using var server = new LoopbackServer("gzip", Compress(payload, "gzip"));
        using var client = CreateClient("compressed-stream");
        using var response = await client.GetAsync(server.Url, HttpCompletionOption.ResponseHeadersRead);

        await using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[16];
        Assert.True(await stream.ReadAsync(buffer) > 0);
        Assert.Equal(1, Assert.Single(_policy.GetSnapshots()).ActiveRequests);

        using var rest = new MemoryStream();
        await stream.CopyToAsync(rest);

        Assert.Equal(0, Assert.Single(_policy.GetSnapshots()).ActiveRequests);
        Assert.Equal(payload.Length, 16 + rest.Length);
    }

    [Fact]
    public async Task ProviderTransportKeepsCookiesOnlyWithinOneHandlerAndTheFactoryRecyclesItLikeTheDefault()
    {
        await using var server = new LoopbackServer("identity", Encoding.UTF8.GetBytes("{}"), "session=abc123; Path=/");
        using (var client = new HttpClient(ProviderClientServiceExtensions.CreateProviderPrimaryHandler()))
        {
            using (await client.GetAsync(server.Url)) { }
            using (await client.GetAsync(server.Url)) { }
        }

        // The factory replaces the handler every ProviderHandlerLifetime, which discards its cookie container.
        using (var recycled = new HttpClient(ProviderClientServiceExtensions.CreateProviderPrimaryHandler()))
        {
            using (await recycled.GetAsync(server.Url)) { }
        }

        Assert.Equal(3, server.AllRequestHeaders.Count);
        Assert.DoesNotContain("Cookie:", server.AllRequestHeaders[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Cookie: session=abc123", server.AllRequestHeaders[1], StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Cookie:", server.AllRequestHeaders[2], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProviderClientsKeepTheFactoryDefaultHandlerLifetimeSoACookieContainerCannotOutliveItsHandler()
    {
        var services = new ServiceCollection();
        services.AddHttpClient("plain");
        services.AddHttpClient("provider").ConfigureProviderTransport();
        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<Microsoft.Extensions.Http.HttpClientFactoryOptions>>();

        var baseline = options.Get("plain").HandlerLifetime;

        Assert.Equal(TimeSpan.FromMinutes(2), baseline);
        Assert.Equal(baseline, options.Get("provider").HandlerLifetime);
        Assert.NotEqual(Timeout.InfiniteTimeSpan, options.Get("provider").HandlerLifetime);
    }

    [Fact]
    public void ConfiguredProviderClientsCapBufferedResponsesAtTheDefaultDecodedLimit()
    {
        var services = new ServiceCollection();
        services.AddHttpClient("limited").ConfigureProviderTransport();
        using var provider = services.BuildServiceProvider();

        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("limited");

        Assert.Equal(16L * 1024 * 1024, ProviderResponseLimits.DefaultMaxBytes);
        Assert.Equal(ProviderResponseLimits.DefaultMaxBytes, client.MaxResponseContentBufferSize);
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("br")]
    public async Task ConfiguredProviderClientRejectsADecompressionBombBeyondTheDefaultLimit(string encoding)
    {
        var bomb = CompressZeros(20 * 1024 * 1024, encoding);
        Assert.True(bomb.Length < 100 * 1024, "the fixture must be tiny on the wire");
        await using var server = new LoopbackServer(encoding, bomb);
        var services = new ServiceCollection();
        services.AddHttpClient("bomb").ConfigureProviderTransport();
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("bomb");

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync(server.Url));
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("br")]
    public async Task BufferedDecompressionBombFailsAtTheCapAndReleasesTheAdaptiveSlot(string encoding)
    {
        const long cap = 1024 * 1024;
        await using var server = new LoopbackServer(encoding, CompressZeros(8 * 1024 * 1024, encoding));
        using var client = CreateClient("bomb-buffered-" + encoding, cap);

        var failure = await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync(server.Url));

        Assert.Contains("limit", failure.Message, StringComparison.OrdinalIgnoreCase);
        var snapshot = Assert.Single(_policy.GetSnapshots());
        Assert.Equal(0, snapshot.ActiveRequests);
        Assert.Equal(1, snapshot.ConsecutiveFailures);
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("br")]
    public async Task StreamedDecompressionBombFailsAtTheCapAndReleasesTheAdaptiveSlot(string encoding)
    {
        const long cap = 1024 * 1024;
        await using var server = new LoopbackServer(encoding, CompressZeros(8 * 1024 * 1024, encoding));
        using var client = CreateClient("bomb-streamed-" + encoding, cap);
        using var response = await client.GetAsync(server.Url, HttpCompletionOption.ResponseHeadersRead);
        await using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[64 * 1024];
        long total = 0;

        var failure = await Assert.ThrowsAsync<ProviderResponseTooLargeException>(async () =>
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer);
                if (read == 0) return;
                total += read;
            }
        });

        Assert.Contains("limit", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(total <= cap, $"read {total} bytes before the cap stopped the body");
        var snapshot = Assert.Single(_policy.GetSnapshots());
        Assert.Equal(0, snapshot.ActiveRequests);
        Assert.Equal(1, snapshot.ConsecutiveFailures);
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("br")]
    public async Task ADecodedBodyBeyondTheCapIsRecordedAsOversizeAndLogsAWarningWithoutTheQueryString(string encoding)
    {
        const long cap = 64 * 1024;
        await using var server = new LoopbackServer(encoding, CompressZeros(512 * 1024, encoding));
        using var client = CreateClient("oversize-decoded-" + encoding, cap);

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync(server.Url + "titles/42?apikey=top-secret"));

        AssertOversizeWarning("oversize-decoded-" + encoding, "/titles/42", cap);
        var snapshot = Assert.Single(_policy.GetSnapshots());
        Assert.Equal(0, snapshot.ActiveRequests);
        Assert.Equal(1, snapshot.ConsecutiveFailures);
    }

    [Fact]
    public async Task AnUncompressedBodyWhoseContentLengthExceedsTheCapIsRecordedAsOversizeWhenBuffered()
    {
        const long cap = 64 * 1024;
        await using var server = new LoopbackServer("identity", new byte[256 * 1024]);
        using var client = CreateClient("oversize-declared-buffered", cap);

        // HttpClient used to reject this body itself, which disposed the content and recorded a cancelled "disposed"
        // request instead of the oversize failure that a decoded body gets.
        var failure = await Assert.ThrowsAnyAsync<HttpRequestException>(() => client.GetAsync(server.Url + "library?token=top-secret"));

        Assert.IsType<ProviderResponseTooLargeException>(failure);
        AssertOversizeWarning("oversize-declared-buffered", "/library", cap);
        var snapshot = Assert.Single(_policy.GetSnapshots());
        Assert.Equal(0, snapshot.ActiveRequests);
        Assert.Equal(1, snapshot.ConsecutiveFailures);
    }

    [Fact]
    public async Task AnUncompressedBodyWhoseContentLengthExceedsTheCapIsRecordedAsOversizeWhenStreamed()
    {
        const long cap = 64 * 1024;
        await using var server = new LoopbackServer("identity", new byte[256 * 1024]);
        using var client = CreateClient("oversize-declared-streamed", cap);
        using var response = await client.GetAsync(server.Url + "library", HttpCompletionOption.ResponseHeadersRead);
        // The oversized declared length is not exposed, so HttpClient cannot reject the body before the wrapper does.
        Assert.Null(response.Content.Headers.ContentLength);

        await Assert.ThrowsAsync<ProviderResponseTooLargeException>(() => response.Content.ReadAsStreamAsync());

        AssertOversizeWarning("oversize-declared-streamed", "/library", cap);
        var snapshot = Assert.Single(_policy.GetSnapshots());
        Assert.Equal(0, snapshot.ActiveRequests);
        Assert.Equal(1, snapshot.ConsecutiveFailures);
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("identity")]
    public async Task ARequestCanRaiseItsOwnLimitAboveTheClientDefault(string encoding)
    {
        const long clientCap = 64 * 1024;
        var body = encoding == "identity" ? new byte[256 * 1024] : CompressZeros(256 * 1024, encoding);
        await using var server = new LoopbackServer(encoding, body);
        using var client = CreateClient("raised-" + encoding, clientCap);
        using var request = new HttpRequestMessage(HttpMethod.Get, server.Url);
        // The outer limit handler only supplies the default; a request-specific limit (SIMKL library pulls) wins.
        request.Options.Set(ProviderRequestOptions.MaxResponseBytes, 1024 * 1024L);

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        await response.Content.CopyToAsync(Stream.Null);

        Assert.Equal(0, Assert.Single(_policy.GetSnapshots()).ActiveRequests);
        Assert.Equal(1, Assert.Single(_policy.GetSnapshots()).ConsecutiveSuccesses);
        Assert.Empty(_logger.Warnings);
    }

    private void AssertOversizeWarning(string provider, string path, long limit)
    {
        var warning = Assert.Single(_logger.Warnings);
        Assert.Contains(provider, warning, StringComparison.Ordinal);
        Assert.Contains(path, warning, StringComparison.Ordinal);
        Assert.Contains(limit.ToString(System.Globalization.CultureInfo.InvariantCulture), warning, StringComparison.Ordinal);
        Assert.DoesNotContain("top-secret", warning, StringComparison.Ordinal);
        Assert.DoesNotContain("apikey", warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ABodyWithinTheCapIsUnaffected()
    {
        var payload = JsonPayload();
        await using var server = new LoopbackServer("gzip", Compress(payload, "gzip"));
        using var client = CreateClient("within-cap", 1024 * 1024);

        Assert.Equal(payload, await client.GetStringAsync(server.Url));
        Assert.Equal(0, Assert.Single(_policy.GetSnapshots()).ActiveRequests);
    }

    private HttpClient CreateClient(string provider, long? maxResponseBytes = null)
    {
        var adaptive = new AdaptiveProviderHandlerFactory(_policy).Create(provider, 1, TimeSpan.FromSeconds(30));
        adaptive.InnerHandler = ProviderClientServiceExtensions.CreateProviderPrimaryHandler();
        if (maxResponseBytes is not { } cap) return new HttpClient(adaptive) { Timeout = Timeout.InfiniteTimeSpan };
        // Mirror ConfigureProviderTransport: the limit handler is outermost and HttpClient caps buffered reads.
        return new HttpClient(new ProviderResponseLimitHandler(cap) { InnerHandler = adaptive })
        {
            Timeout = Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = cap
        };
    }

    private static byte[] CompressZeros(int decodedBytes, string encoding)
    {
        using var output = new MemoryStream();
        using (Stream compressor = encoding switch
        {
            "gzip" => new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true),
            "br" => new BrotliStream(output, CompressionLevel.SmallestSize, leaveOpen: true),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding))
        })
        {
            compressor.Write(new byte[decodedBytes]);
        }
        return output.ToArray();
    }

    private static string JsonPayload()
    {
        var builder = new StringBuilder("[");
        for (var index = 0; index < 400; index++)
        {
            if (index > 0) builder.Append(',');
            builder.Append("{\"id\":").Append(index).Append(",\"name\":\"Premiere ").Append(index % 7).Append("\",\"genres\":[\"Drama\",\"Comedy\"]}");
        }
        return builder.Append(']').ToString();
    }

    private static byte[] Compress(string payload, string encoding)
    {
        using var output = new MemoryStream();
        using (Stream compressor = encoding switch
        {
            "gzip" => new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true),
            "deflate" => new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true),
            "br" => new BrotliStream(output, CompressionLevel.Fastest, leaveOpen: true),
            _ => throw new ArgumentOutOfRangeException(nameof(encoding))
        })
        {
            compressor.Write(Encoding.UTF8.GetBytes(payload));
        }
        return output.ToArray();
    }

    public async Task DisposeAsync()
    {
        if (_database is not null) await _database.DisposeAsync();
    }

    private sealed class CapturingLogger : Microsoft.Extensions.Logging.ILogger<AdaptiveProviderPolicy>
    {
        private readonly List<string> _warnings = [];

        public IReadOnlyList<string> Warnings
        {
            get { lock (_warnings) return [.. _warnings]; }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel != Microsoft.Extensions.Logging.LogLevel.Warning) return;
            lock (_warnings) _warnings.Add(formatter(state, exception));
        }
    }

    /// <summary>Single-purpose HTTP/1.1 peer that answers every request with a fixed pre-encoded body.</summary>
    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly string _encoding;
        private readonly byte[] _body;
        private readonly Task _loop;
        private readonly string? _setCookie;
        private readonly List<string> _allRequestHeaders = [];
        private volatile string _lastRequestHeaders = "";

        public LoopbackServer(string encoding, byte[] body, string? setCookie = null)
        {
            _encoding = encoding;
            _body = body;
            _setCookie = setCookie;
            _listener.Start();
            _loop = Task.Run(AcceptLoopAsync);
        }

        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/";
        public string LastRequestHeaders => _lastRequestHeaders;

        public IReadOnlyList<string> AllRequestHeaders
        {
            get { lock (_allRequestHeaders) return [.. _allRequestHeaders]; }
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    _ = Task.Run(() => RespondAsync(client));
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }

        private async Task RespondAsync(TcpClient client)
        {
            using (client)
            {
                try
                {
                    var stream = client.GetStream();
                    var request = new StringBuilder();
                    var single = new byte[1];
                    while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
                    {
                        if (await stream.ReadAsync(single, _stop.Token) == 0) return;
                        request.Append((char)single[0]);
                    }
                    _lastRequestHeaders = request.ToString();
                    lock (_allRequestHeaders) _allRequestHeaders.Add(_lastRequestHeaders);
                    var header = Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Encoding: {_encoding}\r\n"
                        + (_setCookie is null ? "" : $"Set-Cookie: {_setCookie}\r\n")
                        + $"Content-Length: {_body.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(header, _stop.Token);
                    // Trickle the body so a consumer cannot finish inside the first read.
                    for (var offset = 0; offset < _body.Length; offset += 64)
                    {
                        await stream.WriteAsync(_body.AsMemory(offset, Math.Min(64, _body.Length - offset)), _stop.Token);
                        await stream.FlushAsync(_stop.Token);
                    }
                }
                catch (OperationCanceledException) { }
                catch (IOException) { }
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            try { await _loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception) { }
            _stop.Dispose();
        }
    }
}
