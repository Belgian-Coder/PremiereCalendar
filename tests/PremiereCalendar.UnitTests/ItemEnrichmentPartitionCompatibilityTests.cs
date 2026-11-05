using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class PremiereServiceTests
{
    [Fact]
    public void IntroducingOrTuningPacingPreservesExistingItemDataPartition()
    {
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        string Partition(string endpoint, string? spacing)
        {
            var values = new Dictionary<string, string?>
            {
                ["Tvmaze:BaseUrl"] = endpoint,
                ["Tvmaze:MaxConcurrentRequests"] = "4"
            };
            if (spacing is not null) values["Tvmaze:MinimumRequestIntervalMilliseconds"] = spacing;
            return new ItemEnrichmentCacheCoordinator(store,
                Microsoft.Extensions.Options.Options.Create(new ItemEnrichmentCacheOptions()),
                Microsoft.Extensions.Options.Options.Create(new TmdbOptions()),
                NullLogger<ItemEnrichmentCacheCoordinator>.Instance,
                new ConfigurationBuilder().AddInMemoryCollection(values).Build(), clock).Partition;
        }
        var original = Partition("https://api.tvmaze.com/", null);
        Assert.Equal(original, Partition("https://api.tvmaze.com/", "510"));
        Assert.Equal(original, Partition("https://api.tvmaze.com/", "1000"));
        Assert.NotEqual(original, Partition("https://different.example/", "510"));
    }
}
