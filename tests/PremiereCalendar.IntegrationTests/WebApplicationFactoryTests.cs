using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Net.Http.Headers;
using System.Text.Json;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed class WebApplicationFactoryTests
{
    [Fact]
    public async Task HomePage_RendersShellWithoutBlockingWhenTmdbSecretIsAbsent()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) =>
                {
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Tmdb:BearerToken"] = "",
                        ["CalendarCache:Enabled"] = "false"
                    });
                });
            });
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/");

        Assert.Contains("Premiere Calendar", html);
        Assert.Contains("Loading premieres", html);
    }

    [Fact]
    public async Task HealthEndpoint_ReturnsSuccess()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");

        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task ReadinessAndVersionEndpoints_ReturnOperationalEvidence()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        using var readiness = await client.GetAsync("/health/ready");
        readiness.EnsureSuccessStatusCode();
        using var versionDocument = JsonDocument.Parse(await client.GetStringAsync("/health/version"));

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
    public async Task HostShutdown_IsBoundedForWindowsServiceUpdates()
    {
        await using var factory = new WebApplicationFactory<Program>();

        var options = factory.Services.GetRequiredService<IOptions<HostOptions>>().Value;

        Assert.Equal(TimeSpan.FromSeconds(15), options.ShutdownTimeout);
    }

    [Fact]
    public async Task AboutPage_RendersSourceStatusWithoutSecretValues()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) =>
                {
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Tmdb:BearerToken"] = "secret-tmdb-token",
                        ["Trakt:ClientId"] = "secret-trakt-client",
                        ["Fanart:Enabled"] = "true",
                        ["Fanart:ApiKey"] = "secret-fanart-key",
                        ["Tvmaze:Enabled"] = "true",
                        ["Tvmaze:EnableScheduleDiscovery"] = "true"
                    });
                });
            });
        using var client = factory.CreateClient();

        var html = await client.GetStringAsync("/about");

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
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/about");
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue(encoding));

        using var response = await client.SendAsync(request);

        response.EnsureSuccessStatusCode();
        Assert.Contains(response.Content.Headers.ContentEncoding, value => value == encoding);
        Assert.Equal("Accept-Encoding", response.Headers.Vary.Single());
    }


    [Fact]
    public async Task CachedImageEndpoint_RejectsDisallowedHosts()
    {
        await using var factory = new WebApplicationFactory<Program>();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/cached-image?url=https%3A%2F%2Fexample.com%2Fposter.jpg");

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CachedImageEndpoint_WhenDisabledStillRejectsDisallowedRedirectHosts()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) =>
                {
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ImageCache:Enabled"] = "false"
                    });
                });
            });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/cached-image?url=https%3A%2F%2Fexample.com%2Fposter.jpg");

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task CachedImageEndpoint_WhenDisabledRedirectsAllowedImageHosts()
    {
        await using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureAppConfiguration((_, configuration) =>
                {
                    configuration.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["ImageCache:Enabled"] = "false"
                    });
                });
            });
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var response = await client.GetAsync("/cached-image?url=https%3A%2F%2Fimage.tmdb.org%2Ft%2Fp%2Fw342%2Fposter.jpg");

        Assert.Equal(System.Net.HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("https://image.tmdb.org/t/p/w342/poster.jpg", response.Headers.Location?.AbsoluteUri);
    }
}
