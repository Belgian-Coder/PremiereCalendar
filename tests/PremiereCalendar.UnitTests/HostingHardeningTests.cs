using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using PremiereCalendar.Hosting;
using PremiereCalendar.Options;

namespace PremiereCalendar.UnitTests;

public sealed class HostingHardeningTests
{
    [Fact]
    public void Defaults_are_valid()
    {
        var services = new ServiceCollection();
        services.AddHostingHardening(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();

        AssertStartupValidationSucceeds(provider);
    }

    [Fact]
    public void Blank_database_path_is_rejected_at_startup_validation()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["AppDatabase:Path"] = " " }).Build();
        var services = new ServiceCollection();
        services.AddHostingHardening(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => AssertStartupValidationSucceeds(provider));
    }

    [Theory]
    [InlineData("0", "4")]
    [InlineData("10", "0")]
    [InlineData("10", "4")]
    public void Enabled_image_cache_invalid_limits_or_hosts_are_rejected(string maxBytes, string concurrency)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ImageCache:Enabled"] = "true",
            ["ImageCache:MaxBytes"] = maxBytes,
            ["ImageCache:MaxConcurrentDownloads"] = concurrency,
            ["ImageCache:AllowedHosts:0"] = " "
        }).Build();
        var services = new ServiceCollection();
        services.AddHostingHardening(configuration);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => AssertStartupValidationSucceeds(provider));
    }

    [Fact]
    public void Disabled_image_cache_may_have_empty_hosts_and_limits()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ImageCache:Enabled"] = "false",
            ["ImageCache:MaxBytes"] = "0",
            ["ImageCache:MaxConcurrentDownloads"] = "0"
        }).Build();
        var services = new ServiceCollection();
        services.AddHostingHardening(configuration);
        using var provider = services.BuildServiceProvider();

        AssertStartupValidationSucceeds(provider);
    }

    [Fact]
    public void Enabled_image_cache_rejects_invalid_decode_concurrency()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ImageCache:Enabled"] = "true",
            ["ImageCache:MaxBytes"] = "1024",
            ["ImageCache:MaxConcurrentDownloads"] = "2",
            ["ImageCache:MaxConcurrentDecodes"] = "0",
            ["ImageCache:AllowedHosts:0"] = "image.tmdb.org"
        }).Build();
        var services = new ServiceCollection();
        services.AddHostingHardening(configuration);
        using var provider = services.BuildServiceProvider();
        Assert.Throws<OptionsValidationException>(() => AssertStartupValidationSucceeds(provider));
    }

    private static void AssertStartupValidationSucceeds(ServiceProvider provider) =>
        provider.GetRequiredService<IStartupValidator>().Validate();
}
