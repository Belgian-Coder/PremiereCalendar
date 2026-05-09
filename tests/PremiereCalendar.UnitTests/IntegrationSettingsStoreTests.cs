using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class IntegrationSettingsStoreTests
{
    [Fact]
    public async Task SqliteIntegrationSettingsStore_RoundTripsIntegrationParameters()
    {
        var root = Path.Combine(Path.GetTempPath(), $"premiere-calendar-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var store = new SqliteIntegrationSettingsStore(
                Microsoft.Extensions.Options.Options.Create(new AppDatabaseOptions { Path = "data/settings.db" }),
                new FakeWebHostEnvironment(root));

            var settings = new IntegrationSettings
            {
                Sonarr = new SonarrIntegrationSettings
                {
                    Enabled = true,
                    BaseUrl = "http://sonarr.local:8989/",
                    ApiKey = "sonarr-key",
                    RootFolderPath = "/series",
                    QualityProfileId = 4,
                    SeriesType = "standard",
                    Monitor = "future",
                    SeasonFolder = true,
                    SearchOnAdd = false,
                    TagOnAdd = "series-import"
                },
                Radarr = new RadarrIntegrationSettings
                {
                    Enabled = true,
                    BaseUrl = "http://radarr.local:7878/",
                    ApiKey = "radarr-key",
                    RootFolderPath = "/movies",
                    QualityProfileId = 5,
                    MinimumAvailability = "released",
                    Monitored = true,
                    SearchOnAdd = true,
                    TagOnAdd = "movie-import"
                },
                Sources = new SourceIntegrationSettings
                {
                    Tmdb = new TmdbSourceSettings { BearerToken = "tmdb-token" },
                    Tvmaze = new TvmazeSourceSettings
                    {
                        Enabled = true,
                        EnableScheduleDiscovery = true,
                        ScheduleCountries = ["be", "US", "be"]
                    },
                    Trakt = new TraktSourceSettings
                    {
                        Enabled = true,
                        ClientId = "trakt-client"
                    },
                    Omdb = new OmdbSourceSettings
                    {
                        Enabled = true,
                        ApiKey = "omdb-key"
                    },
                    Fanart = new FanartSourceSettings
                    {
                        Enabled = true,
                        ApiKey = "fanart-key"
                    },
                    TheTvdb = new TheTvdbSourceSettings
                    {
                        Enabled = true,
                        ApiKey = "tvdb-key"
                    },
                    Wikimedia = new WikimediaSourceSettings { Enabled = false },
                    Watchmode = new WatchmodeSourceSettings
                    {
                        Enabled = true,
                        ApiKey = "watchmode-key",
                        Regions = ["be", "NL", "be"],
                        EnableReleaseDiscovery = true,
                        EnableAvailabilityEnrichment = false,
                        CacheHours = 8
                    },
                    Simkl = new SimklSourceSettings
                    {
                        Enabled = true,
                        ClientId = "simkl-client",
                        ClientSecret = "simkl-secret",
                        AccessToken = "simkl-token",
                        MinimumActivityCheckMinutes = 30
                    }
                }
            };

            await store.SaveAsync(settings);
            var loaded = await store.GetAsync();

            Assert.True(loaded.Sonarr.Enabled);
            Assert.Equal("http://sonarr.local:8989/", loaded.Sonarr.BaseUrl);
            Assert.Equal("sonarr-key", loaded.Sonarr.ApiKey);
            Assert.Equal("/series", loaded.Sonarr.RootFolderPath);
            Assert.Equal(4, loaded.Sonarr.QualityProfileId);
            Assert.Equal("future", loaded.Sonarr.Monitor);
            Assert.False(loaded.Sonarr.SearchOnAdd);
            Assert.Equal("series-import", loaded.Sonarr.TagOnAdd);

            Assert.True(loaded.Radarr.Enabled);
            Assert.Equal("http://radarr.local:7878/", loaded.Radarr.BaseUrl);
            Assert.Equal("radarr-key", loaded.Radarr.ApiKey);
            Assert.Equal("/movies", loaded.Radarr.RootFolderPath);
            Assert.Equal(5, loaded.Radarr.QualityProfileId);
            Assert.Equal("movie-import", loaded.Radarr.TagOnAdd);
            Assert.Equal("tmdb-token", loaded.Sources.Tmdb.BearerToken);
            Assert.Equal(["BE", "US"], loaded.Sources.Tvmaze.ScheduleCountries);
            Assert.True(loaded.Sources.Trakt.Enabled);
            Assert.Equal("trakt-client", loaded.Sources.Trakt.ClientId);
            Assert.True(loaded.Sources.Omdb.Enabled);
            Assert.Equal("omdb-key", loaded.Sources.Omdb.ApiKey);
            Assert.True(loaded.Sources.Fanart.Enabled);
            Assert.Equal("fanart-key", loaded.Sources.Fanart.ApiKey);
            Assert.True(loaded.Sources.TheTvdb.Enabled);
            Assert.Equal("tvdb-key", loaded.Sources.TheTvdb.ApiKey);
            Assert.False(loaded.Sources.Wikimedia.Enabled);
            Assert.True(loaded.Sources.Watchmode.Enabled);
            Assert.Equal("watchmode-key", loaded.Sources.Watchmode.ApiKey);
            Assert.Equal(["BE", "NL"], loaded.Sources.Watchmode.Regions);
            Assert.True(loaded.Sources.Watchmode.EnableReleaseDiscovery);
            Assert.False(loaded.Sources.Watchmode.EnableAvailabilityEnrichment);
            Assert.Equal(8, loaded.Sources.Watchmode.CacheHours);
            Assert.True(loaded.Sources.Simkl.Enabled);
            Assert.Equal("simkl-client", loaded.Sources.Simkl.ClientId);
            Assert.Equal("simkl-secret", loaded.Sources.Simkl.ClientSecret);
            Assert.Equal("simkl-token", loaded.Sources.Simkl.AccessToken);
            Assert.Equal(30, loaded.Sources.Simkl.MinimumActivityCheckMinutes);
            Assert.True(File.Exists(Path.Combine(root, "data", "settings.db")));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SqliteIntegrationSettingsStore_DoesNotReadCredentialsFromConfigurationFallbacks()
    {
        var root = Path.Combine(Path.GetTempPath(), $"premiere-calendar-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);

        try
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Tmdb:BearerToken"] = "config-tmdb-token",
                    ["Trakt:ClientId"] = "config-trakt-client",
                    ["Omdb:ApiKey"] = "config-omdb-key",
                    ["Fanart:ApiKey"] = "config-fanart-key",
                    ["TheTvdb:ApiKey"] = "config-tvdb-key",
                    ["Watchmode:ApiKey"] = "config-watchmode-key",
                    ["Simkl:ClientId"] = "config-simkl-client",
                    ["Simkl:ClientSecret"] = "config-simkl-secret",
                    ["Simkl:AccessToken"] = "config-simkl-token"
                })
                .Build();
            var store = new SqliteIntegrationSettingsStore(
                Microsoft.Extensions.Options.Options.Create(new AppDatabaseOptions { Path = "data/settings.db" }),
                new FakeWebHostEnvironment(root),
                configuration);

            var loaded = await store.GetAsync();

            Assert.Empty(loaded.Sources.Tmdb.BearerToken);
            Assert.Empty(loaded.Sources.Trakt.ClientId);
            Assert.Empty(loaded.Sources.Omdb.ApiKey);
            Assert.Empty(loaded.Sources.Fanart.ApiKey);
            Assert.Empty(loaded.Sources.TheTvdb.ApiKey);
            Assert.Empty(loaded.Sources.Watchmode.ApiKey);
            Assert.Empty(loaded.Sources.Simkl.ClientId);
            Assert.Empty(loaded.Sources.Simkl.ClientSecret);
            Assert.Empty(loaded.Sources.Simkl.AccessToken);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public FakeWebHostEnvironment(string root)
        {
            ContentRootPath = root;
            WebRootPath = root;
            ContentRootFileProvider = new PhysicalFileProvider(root);
            WebRootFileProvider = new PhysicalFileProvider(root);
        }

        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "PremiereCalendar.Tests";
        public string WebRootPath { get; set; }
        public IFileProvider WebRootFileProvider { get; set; }
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
    }
}
