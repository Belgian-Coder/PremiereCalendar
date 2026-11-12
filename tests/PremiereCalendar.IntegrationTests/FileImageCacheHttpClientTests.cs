using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed class FileImageCacheHttpClientTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"premiere-image-client-{Guid.NewGuid():N}");

    [Fact]
    public void ImageCacheHttpClientDoesNotFollowRedirectsItself()
    {
        var settings = new Dictionary<string, string?>
        {
            ["ProviderScheduler:Enabled"] = "false",
            ["CalendarWarmup:Enabled"] = "false",
            ["ProviderDeltaSync:Enabled"] = "false",
            ["ImdbDataset:Enabled"] = "false",
            ["AppDatabase:Path"] = Path.Combine(_root, "data", "calendar.db"),
            ["AppDatabase:MigrationBackupDirectory"] = Path.Combine(_root, "backups"),
            ["CalendarCache:Directory"] = Path.Combine(_root, "calendar-cache"),
            ["ImageCache:Directory"] = Path.Combine(_root, "image-cache"),
            ["Telemetry:LogDirectory"] = Path.Combine(_root, "logs")
        };
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings)));

        // The typed client's handler chain ends in the primary handler; redirects must be validated by the cache per hop.
        HttpMessageHandler handler = factory.Services.GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(typeof(FileImageCache).Name);
        while (handler is DelegatingHandler delegating) handler = delegating.InnerHandler!;

        var primary = Assert.IsType<HttpClientHandler>(handler);
        Assert.False(primary.AllowAutoRedirect);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
