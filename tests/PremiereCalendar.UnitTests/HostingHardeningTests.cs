using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using PremiereCalendar.Hosting;
using PremiereCalendar.Options;

namespace PremiereCalendar.UnitTests;

public sealed class HostingHardeningTests
{
    [Fact]
    public void Defaults_are_valid()
    {
        var services = new ServiceCollection();
        services.AddHostingHardening(Configure());
        using var provider = services.BuildServiceProvider();

        AssertStartupValidationSucceeds(provider);
    }

    [Fact]
    public void Configured_forwarded_proxy_is_trusted_exactly()
    {
        var configuration = Configure(new Dictionary<string, string?> { ["Hosting:ForwardedProxies:0"] = "192.168.68.22" });
        var services = new ServiceCollection();
        services.AddHostingHardening(configuration);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;

        Assert.Contains(System.Net.IPAddress.Parse("192.168.68.22"), options.KnownProxies);
        Assert.DoesNotContain(System.Net.IPAddress.Parse("192.168.68.23"), options.KnownProxies);
    }

    [Fact]
    public void Blank_database_path_is_ignored_at_startup_validation()
    {
        var configuration = Configure(new Dictionary<string, string?> { ["AppDatabase:Path"] = " " });
        var services = new ServiceCollection();
        services.AddHostingHardening(configuration);
        using var provider = services.BuildServiceProvider();

        AssertStartupValidationSucceeds(provider);
    }

    [Fact]
    public void Missing_database_connection_string_is_rejected_at_startup_validation()
    {
        var services = new ServiceCollection();
        services.AddHostingHardening(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(() => AssertStartupValidationSucceeds(provider));

        Assert.Contains("AppDatabase:ConnectionString is required", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Sqlite")]
    [InlineData("sqlite")]
    [InlineData("SQLite")]
    public void Removed_sqlite_database_provider_is_rejected_at_startup_validation(string provider)
    {
        var configuration = Configure(new Dictionary<string, string?> { ["AppDatabase:Provider"] = provider });
        var services = new ServiceCollection();
        services.AddHostingHardening(configuration);
        using var serviceProvider = services.BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(() => AssertStartupValidationSucceeds(serviceProvider));

        Assert.Contains("AppDatabase:Provider", exception.Message, StringComparison.Ordinal);
        Assert.Contains("SQLite was removed", exception.Message, StringComparison.Ordinal);
        Assert.Contains("PostgreSQL is the only database", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("data/app.db")]
    [InlineData("/data/premierecalendar.db")]
    public void Removed_sqlite_database_path_is_rejected_at_startup_validation(string path)
    {
        var configuration = Configure(new Dictionary<string, string?> { ["AppDatabase:Path"] = path });
        var services = new ServiceCollection();
        services.AddHostingHardening(configuration);
        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<OptionsValidationException>(() => AssertStartupValidationSucceeds(provider));

        Assert.Contains("AppDatabase:Path", exception.Message, StringComparison.Ordinal);
        Assert.Contains("SQLite was removed", exception.Message, StringComparison.Ordinal);
        Assert.Contains("AppDatabase:ConnectionString", exception.Message, StringComparison.Ordinal);
        // Names the release that can still import the data instead of the vague "last release that supports it".
        Assert.Contains("v1.2.19", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("PostgreSql")]
    [InlineData("postgresql")]
    public void Postgresql_database_provider_with_a_connection_string_is_accepted(string provider)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AppDatabase:Provider"] = provider,
            ["AppDatabase:ConnectionString"] = ValidConnectionString
        }).Build();
        var services = new ServiceCollection();
        services.AddHostingHardening(configuration);
        using var serviceProvider = services.BuildServiceProvider();

        AssertStartupValidationSucceeds(serviceProvider);
    }

    [Theory]
    [InlineData("0", "4")]
    [InlineData("10", "0")]
    [InlineData("10", "4")]
    public void Enabled_image_cache_invalid_limits_or_hosts_are_rejected(string maxBytes, string concurrency)
    {
        var configuration = Configure(new Dictionary<string, string?>
        {
            ["ImageCache:Enabled"] = "true",
            ["ImageCache:MaxBytes"] = maxBytes,
            ["ImageCache:MaxConcurrentDownloads"] = concurrency,
            ["ImageCache:AllowedHosts:0"] = " "
        });
        var services = new ServiceCollection();
        services.AddHostingHardening(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => AssertStartupValidationSucceeds(provider));
    }

    [Fact]
    public void Disabled_image_cache_may_have_empty_hosts_and_limits()
    {
        var configuration = Configure(new Dictionary<string, string?>
        {
            ["ImageCache:Enabled"] = "false",
            ["ImageCache:MaxBytes"] = "0",
            ["ImageCache:MaxConcurrentDownloads"] = "0"
        });
        var services = new ServiceCollection();
        services.AddHostingHardening(configuration);
        using var provider = services.BuildServiceProvider();

        AssertStartupValidationSucceeds(provider);
    }

    [Fact]
    public void Enabled_image_cache_rejects_invalid_decode_concurrency()
    {
        var configuration = Configure(new Dictionary<string, string?>
        {
            ["ImageCache:Enabled"] = "true",
            ["ImageCache:MaxBytes"] = "1024",
            ["ImageCache:MaxConcurrentDownloads"] = "2",
            ["ImageCache:MaxConcurrentDecodes"] = "0",
            ["ImageCache:AllowedHosts:0"] = "image.tmdb.org"
        });
        var services = new ServiceCollection();
        services.AddHostingHardening(configuration);
        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => AssertStartupValidationSucceeds(provider));
    }

    [Fact]
    public void A_relative_password_file_resolves_against_the_content_root_not_the_working_directory()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "premiere-content-root", Guid.NewGuid().ToString("N"), "PremiereCalendar");
        const string relative = "../.secrets/dev-postgres-password";
        var services = new ServiceCollection();
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostEnvironment>(
            new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { ContentRootPath = contentRoot });
        services.AddHostingHardening(Configure(new Dictionary<string, string?> { ["AppDatabase:PasswordFile"] = relative }));
        using var provider = services.BuildServiceProvider();

        var passwordFile = provider.GetRequiredService<IOptions<AppDatabaseOptions>>().Value.PasswordFile;

        Assert.Equal(Path.GetFullPath(Path.Combine(contentRoot, "..", ".secrets", "dev-postgres-password")), passwordFile);
        Assert.NotEqual(Path.GetFullPath(relative), passwordFile);
        AssertStartupValidationSucceeds(provider);
    }

    [Fact]
    public void An_absolute_or_blank_password_file_is_not_changed_by_the_content_root()
    {
        var contentRoot = Path.Combine(Path.GetTempPath(), "premiere-content-root", Guid.NewGuid().ToString("N"));
        var absolute = Path.Combine(Path.GetTempPath(), "secrets", "postgres_password");

        Assert.Equal(absolute, AppDatabasePaths.ResolvePasswordFile(absolute, contentRoot));
        Assert.Equal("", AppDatabasePaths.ResolvePasswordFile("", contentRoot));
        Assert.Equal("", AppDatabasePaths.ResolvePasswordFile("  ", contentRoot));
        // Without a content root (a bare options object) the value is only trimmed.
        Assert.Equal("relative/password", AppDatabasePaths.ResolvePasswordFile(" relative/password ", null));
    }

    private const string ValidConnectionString = "Host=localhost;Database=premierecalendar;Username=premierecalendar";

    // Every configuration carries a valid PostgreSQL connection string so that a rejection can only come from the
    // setting a test is exercising, never from the (required) database connection string being absent.
    private static IConfiguration Configure(IReadOnlyDictionary<string, string?>? values = null)
    {
        var settings = new Dictionary<string, string?> { ["AppDatabase:ConnectionString"] = ValidConnectionString };
        if (values is not null)
        {
            foreach (var pair in values) settings[pair.Key] = pair.Value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    private static void AssertStartupValidationSucceeds(ServiceProvider provider) =>
        provider.GetRequiredService<IAsyncStartupValidator>().ValidateAsync(CancellationToken.None).GetAwaiter().GetResult();
}
