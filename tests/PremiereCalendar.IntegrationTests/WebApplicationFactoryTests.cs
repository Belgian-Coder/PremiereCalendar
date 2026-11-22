using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Text.Json;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

// Tests that only read from the default host (or the image-cache-disabled host) share one instance of each through
// the class fixture instead of starting two hosts per test case. Tests that need their own configuration or that
// stop their host build a private one.
public sealed class WebApplicationFactoryTests(WebApplicationFactoryTests.HostFixture hosts)
    : IClassFixture<WebApplicationFactoryTests.HostFixture>, IDisposable
{
    private readonly string _temporaryRoot = Path.Combine(Path.GetTempPath(), $"premiere-web-factory-{Guid.NewGuid():N}");
    private readonly List<PostgresTestDatabase> _databases = [];
    private HttpClient _client => hosts.Client;
    private HttpClient _imageCacheDisabledClient => hosts.ImageCacheDisabledClient;

    [Fact]
    public async Task HomePage_RendersShellWithoutBlockingWhenTmdbSecretIsAbsent()
    {
        await using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["Tmdb:BearerToken"] = "",
            ["CalendarCache:Enabled"] = "false"
        });
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/", TestContext.Current.CancellationToken);

        Assert.Contains("Premiere Calendar", html);
        Assert.Contains("Loading premieres", html);
    }

    [Fact]
    public async Task HealthEndpoint_ReturnsSuccess()
    {
        var response = await _client.GetAsync("/health", TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task ReadinessAndVersionEndpoints_ReturnOperationalEvidence()
    {
        using var readiness = await _client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        var versionJson = await _client.GetStringAsync("/health/version", TestContext.Current.CancellationToken);
        Assert.True(readiness.IsSuccessStatusCode, versionJson);
        using var versionDocument = JsonDocument.Parse(versionJson);

        var version = versionDocument.RootElement.GetProperty("version").GetString();
        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.Matches(@"^\d+\.\d+\.\d+", version);
        Assert.Equal(DatabaseSchema.CurrentVersion, versionDocument.RootElement.GetProperty("databaseSchemaVersion").GetInt32());
        Assert.Equal(
            DatabaseSchema.CurrentVersion,
            versionDocument.RootElement.GetProperty("database").GetProperty("currentSchemaVersion").GetInt32());
        Assert.True(versionDocument.RootElement.GetProperty("database").GetProperty("healthy").GetBoolean());
    }

    [Fact]
    public async Task HostShutdown_IsBoundedForContainerStops()
    {
        var options = hosts.Factory.Services.GetRequiredService<IOptions<HostOptions>>().Value;

        Assert.Equal(TimeSpan.FromSeconds(15), options.ShutdownTimeout);
    }

    [Fact]
    public async Task StoppingTheHostDisposesItsDatabaseDataSources()
    {
        DatabaseConnectionFactory.EnableBuildCounting();
        var factory = CreateFactory();
        var database = _databases[^1];
        using (var client = factory.CreateClient())
        {
            (await client.GetAsync("/health/ready", TestContext.Current.CancellationToken)).EnsureSuccessStatusCode();
        }
        var built = DatabaseConnectionFactory.BuiltFor(database.AppOptions);
        Assert.True(built >= 1);

        await factory.DisposeAsync();
        Assert.Equal(built, DatabaseConnectionFactory.DisposedFor(database.AppOptions));

        // The host removed its cached data source after its hosted services stopped, so the next use builds a new one.
        using var connection = DatabaseConnectionFactory.Create(database.AppOptions);
        Assert.Equal(built + 1, DatabaseConnectionFactory.BuiltFor(database.AppOptions));
    }

    [Fact]
    public async Task AboutPage_RendersSourceStatusWithoutSecretValues()
    {
        await using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["Tmdb:BearerToken"] = "secret-tmdb-token",
            ["Trakt:ClientId"] = "secret-trakt-client",
            ["Fanart:Enabled"] = "true",
            ["Fanart:ApiKey"] = "secret-fanart-key",
            ["Tvmaze:Enabled"] = "true",
            ["Tvmaze:EnableScheduleDiscovery"] = "true"
        });
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/about", TestContext.Current.CancellationToken);

        Assert.Contains("Source status", html);
        Assert.Contains("TMDb", html);
        Assert.Contains("TVmaze schedules", html);
        Assert.Contains("Trakt", html);
        Assert.Contains("Fanart.tv", html);
        Assert.Contains("Active", html);
        Assert.DoesNotContain("secret-tmdb-token", html);
        Assert.DoesNotContain("secret-trakt-client", html);
        Assert.DoesNotContain("secret-fanart-key", html);
    }

    [Theory]
    [InlineData("zstd")]
    [InlineData("br")]
    [InlineData("gzip")]
    public async Task HtmlResponses_UseNegotiatedCompression(string encoding)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/about");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue(encoding));

        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        Assert.Contains(response.Content.Headers.ContentEncoding, value => value == encoding);
        Assert.Equal("Accept-Encoding", response.Headers.Vary.Single());
    }


    [Fact]
    public async Task CachedImageEndpoint_RejectsDisallowedHosts()
    {
        var response = await _client.GetAsync("/cached-image?url=https%3A%2F%2Fexample.com%2Fposter.jpg", TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CachedImageEndpoint_WhenDisabledStillRejectsDisallowedRedirectHosts()
    {
        var response = await _imageCacheDisabledClient.GetAsync("/cached-image?url=https%3A%2F%2Fexample.com%2Fposter.jpg", TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CachedImageEndpoint_WhenDisabledRedirectsAllowedImageHosts()
    {
        var response = await _imageCacheDisabledClient.GetAsync("/cached-image?url=https%3A%2F%2Fimage.tmdb.org%2Ft%2Fp%2Fw342%2Fposter.jpg", TestContext.Current.CancellationToken);

        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("https://image.tmdb.org/t/p/w342/poster.jpg", response.Headers.Location?.AbsoluteUri);
    }

    private WebApplicationFactory<Program> CreateFactory(IReadOnlyDictionary<string, string?>? overrides = null)
    {
        var factory = CreateHostFactory(_temporaryRoot, out var database, overrides);
        _databases.Add(database);
        return factory;
    }

    private static WebApplicationFactory<Program> CreateHostFactory(
        string temporaryRoot,
        out PostgresTestDatabase database,
        IReadOnlyDictionary<string, string?>? overrides = null)
    {
        var instanceRoot = Path.Combine(temporaryRoot, Guid.NewGuid().ToString("N"));
        var settings = new Dictionary<string, string?>
        {
            ["ProviderScheduler:Enabled"] = "false",
            ["CalendarWarmup:Enabled"] = "false",
            ["ProviderDeltaSync:Enabled"] = "false",
            ["ImdbDataset:Enabled"] = "false",
            ["CalendarCache:Directory"] = Path.Combine(instanceRoot, "calendar-cache"),
            ["ImageCache:Directory"] = Path.Combine(instanceRoot, "image-cache"),
            ["Telemetry:LogDirectory"] = Path.Combine(instanceRoot, "logs")
        };
        database = PostgresTestDatabase.Create();
        foreach (var setting in database.AppSettings) settings[setting.Key] = setting.Value;
        if (overrides is not null)
        {
            foreach (var pair in overrides)
            {
                settings[pair.Key] = pair.Value;
            }
        }

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings)));
    }

    public sealed class HostFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"premiere-web-factory-shared-{Guid.NewGuid():N}");
        private readonly PostgresTestDatabase _database;
        private readonly PostgresTestDatabase _disabledDatabase;
        private readonly WebApplicationFactory<Program> _disabledFactory;

        public HostFixture()
        {
            Factory = CreateHostFactory(_root, out _database);
            Client = Factory.CreateClient();
            _disabledFactory = CreateHostFactory(_root, out _disabledDatabase, new Dictionary<string, string?>
            {
                ["ImageCache:Enabled"] = "false"
            });
            ImageCacheDisabledClient = _disabledFactory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });
        }

        public WebApplicationFactory<Program> Factory { get; }
        public HttpClient Client { get; }
        public HttpClient ImageCacheDisabledClient { get; }

        public void Dispose()
        {
            ImageCacheDisabledClient.Dispose();
            _disabledFactory.Dispose();
            Client.Dispose();
            Factory.Dispose();
            _database.Dispose();
            _disabledDatabase.Dispose();
            try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public void Dispose()
    {
        foreach (var database in _databases) database.Dispose();
        if (Directory.Exists(_temporaryRoot))
        {
            Directory.Delete(_temporaryRoot, recursive: true);
        }
    }
}
