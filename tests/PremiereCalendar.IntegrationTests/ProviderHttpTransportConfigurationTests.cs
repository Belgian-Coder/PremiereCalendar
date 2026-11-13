using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed class ProviderHttpTransportConfigurationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"premiere-provider-transport-{Guid.NewGuid():N}");
    private readonly List<PostgresTestDatabase> _databases = [];

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
    public void ProviderClientsUsePooledCompressingSocketsHandlerWithStableHandlerLifetime(string clientName)
    {
        using var factory = CreateFactory();

        var primary = PrimaryHandler(factory, clientName);

        var sockets = Assert.IsType<SocketsHttpHandler>(primary);
        Assert.Equal(DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli, sockets.AutomaticDecompression);
        Assert.Equal(TimeSpan.FromMinutes(5), sockets.PooledConnectionLifetime);
        Assert.False(sockets.UseCookies);
        Assert.Equal(
            Timeout.InfiniteTimeSpan,
            factory.Services.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>().Get(clientName).HandlerLifetime);
    }

    [Fact]
    public void ImageAndDatasetClientsKeepTheirRawTransport()
    {
        using var factory = CreateFactory();

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

    private WebApplicationFactory<Program> CreateFactory()
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
        var database = PostgresTestDatabase.Create();
        _databases.Add(database);
        foreach (var setting in database.AppSettings) settings[setting.Key] = setting.Value;
        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings)));
    }

    private static HttpMessageHandler PrimaryHandler(WebApplicationFactory<Program> factory, string clientName)
    {
        // Resolve the real pipeline: delegating handlers first, the primary transport at the end of the chain.
        var handler = factory.Services.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(clientName);
        while (handler is DelegatingHandler delegating) handler = delegating.InnerHandler!;
        return handler;
    }

    public void Dispose()
    {
        foreach (var database in _databases) database.Dispose();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
