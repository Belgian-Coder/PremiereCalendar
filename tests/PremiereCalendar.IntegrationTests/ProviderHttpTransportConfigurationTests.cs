using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

// One host serves every case: the tests only read its service provider (handler pipelines, options), they never
// send requests or change state, so a host per theory row (about 8 s each, 22 hosts) proved nothing more.
public sealed class ProviderHttpTransportConfigurationTests(ProviderHttpTransportConfigurationTests.HostFixture host)
    : IClassFixture<ProviderHttpTransportConfigurationTests.HostFixture>
{
    private WebApplicationFactory<Program> Factory => host.Factory;

    public static TheoryData<string> CompressedProviderClients => new()
    {
        nameof(ITmdbClient),
        nameof(IOmdbClient),
        nameof(ITvmazeClient),
        nameof(IFanartClient),
        nameof(ITraktClient),
        nameof(ITheTvdbClient),
        nameof(WikimediaClient),
        nameof(IRottenTomatoesClient),
        nameof(IWatchmodeClient),
        nameof(ISimklClient)
    };

    [Theory]
    [MemberData(nameof(CompressedProviderClients))]
    public void ProviderClientsUseCompressingSocketsHandlerWithTheDefaultHandlerLifetimeAndCookies(string clientName)
    {
        var factory = Factory;

        var primary = PrimaryHandler(factory, clientName);

        var sockets = Assert.IsType<SocketsHttpHandler>(primary);
        Assert.Equal(DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli, sockets.AutomaticDecompression);
        // The same two minutes as the factory's handler lifetime, exactly like the default primary handler.
        Assert.Equal(TimeSpan.FromMinutes(2), sockets.PooledConnectionLifetime);
        // Cookies keep the SocketsHttpHandler default instead of being disabled, and the factory recycles the handler
        // (and its cookie container) at its default lifetime instead of keeping one container for the whole process.
        Assert.True(sockets.UseCookies);
        Assert.Equal(
            TimeSpan.FromMinutes(2),
            factory.Services.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>().Get(clientName).HandlerLifetime);
    }

    [Theory]
    [MemberData(nameof(CompressedProviderClients))]
    public void ProviderClientsCapDecodedResponsesBeforeTheAdaptiveHandlerSeesThem(string clientName)
    {
        var factory = Factory;

        // Buffered reads are capped by HttpClient itself.
        using var client = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(clientName);
        Assert.Equal(16L * 1024 * 1024, client.MaxResponseContentBufferSize);

        // Streamed reads are capped by the adaptive handler, which learns the limit from the outer limit handler.
        var chain = new List<Type>();
        var handler = factory.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(clientName);
        while (handler is DelegatingHandler delegating)
        {
            chain.Add(delegating.GetType());
            handler = delegating.InnerHandler!;
        }
        Assert.Contains(typeof(ProviderResponseLimitHandler), chain);
        Assert.Contains(typeof(AdaptiveProviderHandler), chain);
        Assert.True(chain.IndexOf(typeof(ProviderResponseLimitHandler)) < chain.IndexOf(typeof(AdaptiveProviderHandler)));
    }

    [Fact]
    public void ImageAndDatasetClientsKeepTheirRawTransport()
    {
        var factory = Factory;

        // The image cache validates redirects itself and the dataset is a .gz file that its importer inflates, so
        // neither may be given transparent decompression.
        var image = Assert.IsType<HttpClientHandler>(PrimaryHandler(factory, nameof(FileImageCache)));
        Assert.False(image.AllowAutoRedirect);
        Assert.Equal(DecompressionMethods.None, image.AutomaticDecompression);

        var dataset = PrimaryHandler(factory, nameof(IImdbDatasetImporter));
        var datasetDecompression = dataset switch
        {
            SocketsHttpHandler sockets => sockets.AutomaticDecompression,
            HttpClientHandler client => client.AutomaticDecompression,
            _ => throw new Xunit.Sdk.XunitException($"Unexpected primary handler {dataset.GetType()}.")
        };
        Assert.Equal(DecompressionMethods.None, datasetDecompression);
    }

    private static HttpMessageHandler PrimaryHandler(WebApplicationFactory<Program> factory, string clientName)
    {
        // Resolve the real pipeline: delegating handlers first, the primary transport at the end of the chain.
        var handler = factory.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(clientName);
        while (handler is DelegatingHandler delegating) handler = delegating.InnerHandler!;
        return handler;
    }

    public sealed class HostFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"premiere-provider-transport-{Guid.NewGuid():N}");
        private readonly PostgresTestDatabase _database;

        public HostFixture()
        {
            var settings = new Dictionary<string, string?>
            {
                ["ProviderScheduler:Enabled"] = "false",
                ["CalendarWarmup:Enabled"] = "false",
                ["ProviderDeltaSync:Enabled"] = "false",
                ["ImdbDataset:Enabled"] = "false",
                ["CalendarCache:Directory"] = Path.Combine(_root, "calendar-cache"),
                ["ImageCache:Directory"] = Path.Combine(_root, "image-cache"),
                ["Telemetry:LogDirectory"] = Path.Combine(_root, "logs")
            };
            _database = PostgresTestDatabase.Create();
            foreach (var setting in _database.AppSettings) settings[setting.Key] = setting.Value;
            Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings)));
        }

        public WebApplicationFactory<Program> Factory { get; }

        public void Dispose()
        {
            Factory.Dispose();
            _database.Dispose();
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
