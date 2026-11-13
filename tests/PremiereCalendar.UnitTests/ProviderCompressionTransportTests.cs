using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using PremiereCalendar.Hosting;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class ProviderCompressionTransportTests : IAsyncLifetime
{
    private AdaptiveProviderPolicy _policy = null!;
    private PostgresTestDatabase _database = null!;

    public async Task InitializeAsync()
    {
        _database = await PostgresTestDatabase.CreateAsync();
        _policy = new AdaptiveProviderPolicy(
            new ProviderAdaptiveStateStore(_database.Options),
            TimeProvider.System,
            new PremiereTelemetry(),
            Microsoft.Extensions.Options.Options.Create(new ProviderSchedulerOptions()));
    }

    [Fact]
    public void ProviderPrimaryHandlerNegotiatesGzipDeflateAndBrotliButNotZstd()
    {
        using var handler = ProviderClientServiceExtensions.CreateProviderPrimaryHandler();

        Assert.Equal(DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli, handler.AutomaticDecompression);
        Assert.Equal(TimeSpan.FromMinutes(5), handler.PooledConnectionLifetime);
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
    public async Task ProviderTransportKeepsCookiesBetweenRequestsLikeTheDefaultHandler()
    {
        await using var server = new LoopbackServer("identity", Encoding.UTF8.GetBytes("{}"), "session=abc123; Path=/");
        using var client = new HttpClient(ProviderClientServiceExtensions.CreateProviderPrimaryHandler());

        using (await client.GetAsync(server.Url)) { }
        using (await client.GetAsync(server.Url)) { }

        Assert.Equal(2, server.AllRequestHeaders.Count);
        Assert.DoesNotContain("Cookie:", server.AllRequestHeaders[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Cookie: session=abc123", server.AllRequestHeaders[1], StringComparison.OrdinalIgnoreCase);
    }

    private HttpClient CreateClient(string provider)
    {
        var handler = new AdaptiveProviderHandlerFactory(_policy).Create(provider, 1, TimeSpan.FromSeconds(30));
        handler.InnerHandler = ProviderClientServiceExtensions.CreateProviderPrimaryHandler();
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
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
