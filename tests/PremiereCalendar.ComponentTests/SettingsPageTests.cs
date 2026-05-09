using Bunit;
using Microsoft.Extensions.DependencyInjection;
using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.ComponentTests;

public sealed class SettingsPageTests : BunitContext
{
    [Fact]
    public void SettingsPage_LoadsQualityProfileNamesOnOpenWhenIntegrationsAreConfigured()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var store = new FakeIntegrationSettingsStore
        {
            Settings = new IntegrationSettings
            {
                Sonarr = new SonarrIntegrationSettings
                {
                    Enabled = true,
                    BaseUrl = "http://sonarr.test/",
                    ApiKey = "sonarr-key",
                    QualityProfileId = 4
                },
                Radarr = new RadarrIntegrationSettings
                {
                    Enabled = true,
                    BaseUrl = "http://radarr.test/",
                    ApiKey = "radarr-key",
                    QualityProfileId = 5
                }
            }
        };
        Services.AddSingleton<IIntegrationSettingsStore>(store);
        Services.AddSingleton<IArrIntegrationService>(new FakeArrIntegrationService());
        Services.AddSingleton<ISimklClient>(new FakeSimklClient());

        var component = Render<PremiereCalendar.Components.Pages.Settings>();

        component.WaitForAssertion(() =>
        {
            var sonarrSelect = component.Find("select[aria-label='Sonarr quality profile']");
            var radarrSelect = component.Find("select[aria-label='Radarr quality profile']");

            Assert.Contains("HD-1080p", sonarrSelect.TextContent);
            Assert.Contains("Ultra-HD", radarrSelect.TextContent);
            Assert.Equal("4", sonarrSelect.GetAttribute("value"));
            Assert.Equal("5", radarrSelect.GetAttribute("value"));
            Assert.Empty(component.FindAll("input[aria-label='Sonarr quality profile ID']"));
            Assert.Empty(component.FindAll("input[aria-label='Radarr quality profile ID']"));
        });
    }

    [Fact]
    public void SettingsPage_SelectsQualityProfilesByNameAfterOptionsLoad()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var store = new FakeIntegrationSettingsStore
        {
            Settings = new IntegrationSettings
            {
                Sonarr = new SonarrIntegrationSettings
                {
                    Enabled = true,
                    BaseUrl = "http://sonarr.test/",
                    ApiKey = "sonarr-key",
                    QualityProfileId = 4
                },
                Radarr = new RadarrIntegrationSettings
                {
                    Enabled = true,
                    BaseUrl = "http://radarr.test/",
                    ApiKey = "radarr-key",
                    QualityProfileId = 5
                },
                Sources = new SourceIntegrationSettings
                {
                    Tmdb = new TmdbSourceSettings { BearerToken = "tmdb-token" },
                    Tvmaze = new TvmazeSourceSettings
                    {
                        Enabled = true,
                        EnableScheduleDiscovery = true,
                        ScheduleCountries = ["BE"]
                    },
                    Trakt = new TraktSourceSettings { Enabled = true },
                    Omdb = new OmdbSourceSettings { Enabled = false },
                    Fanart = new FanartSourceSettings { Enabled = false },
                    TheTvdb = new TheTvdbSourceSettings { Enabled = false },
                    Wikimedia = new WikimediaSourceSettings { Enabled = true }
                }
            }
        };
        var arrService = new FakeArrIntegrationService();
        Services.AddSingleton<IIntegrationSettingsStore>(store);
        Services.AddSingleton<IArrIntegrationService>(arrService);
        Services.AddSingleton<ISimklClient>(new FakeSimklClient());

        var component = Render<PremiereCalendar.Components.Pages.Settings>();

        component.Find("button[aria-label='Load Sonarr quality profiles and root folders']").Click();
        component.Find("button[aria-label='Load Radarr quality profiles and root folders']").Click();

        component.WaitForAssertion(() =>
        {
            var sonarrSelect = component.Find("select[aria-label='Sonarr quality profile']");
            var radarrSelect = component.Find("select[aria-label='Radarr quality profile']");

            Assert.Contains("HD-1080p", sonarrSelect.TextContent);
            Assert.Contains("Ultra-HD", radarrSelect.TextContent);
            Assert.Equal("4", sonarrSelect.GetAttribute("value"));
            Assert.Equal("5", radarrSelect.GetAttribute("value"));
            Assert.Empty(component.FindAll(".settings-options-list"));
        });

        component.Find("select[aria-label='Sonarr quality profile']").Change("7");
        component.Find("select[aria-label='Radarr quality profile']").Change("6");
        component.Find("input[aria-label='TVmaze schedule countries']").Change("be, us, BE");
        component.Find("input[aria-label='Trakt client ID']").Change("trakt-client");
        component.Find("input[aria-label='OMDb API key']").Change("omdb-key");
        component.Find("button[title='Save integration settings']").Click();

        component.WaitForAssertion(() =>
        {
            Assert.Equal(7, store.Settings.Sonarr.QualityProfileId);
            Assert.Equal(6, store.Settings.Radarr.QualityProfileId);
            Assert.Equal(["BE", "US"], store.Settings.Sources.Tvmaze.ScheduleCountries);
            Assert.Equal("trakt-client", store.Settings.Sources.Trakt.ClientId);
            Assert.Equal("omdb-key", store.Settings.Sources.Omdb.ApiKey);
        });
    }

    [Fact]
    public void SettingsPage_RendersClearSimklConnectionFlowAndRecommendedActivityInterval()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var store = new FakeIntegrationSettingsStore
        {
            Settings = new IntegrationSettings
            {
                Sources = new SourceIntegrationSettings
                {
                    Simkl = new SimklSourceSettings
                    {
                        Enabled = true,
                        ClientId = "simkl-client-id",
                        ClientSecret = "simkl-client-secret"
                    }
                }
            }
        };
        Services.AddSingleton<IIntegrationSettingsStore>(store);
        Services.AddSingleton<IArrIntegrationService>(new FakeArrIntegrationService());
        Services.AddSingleton<ISimklClient>(new FakeSimklClient());

        var component = Render<PremiereCalendar.Components.Pages.Settings>();

        component.WaitForAssertion(() =>
        {
            Assert.Empty(component.FindAll("button[aria-label='Request SIMKL PIN']"));
            Assert.Contains("Connect SIMKL", component.Find("button[aria-label='Connect SIMKL']").TextContent);
            Assert.Contains("Recommended: 30 minutes", component.Markup);
            Assert.Contains("not a background polling timer", component.Markup);
            Assert.Equal(
                "30",
                component.Find("input[aria-label='SIMKL activity check interval minutes']").GetAttribute("value"));
        });
    }

    [Fact]
    public void SettingsPage_GroupsProvidersByKindAndShowsAvailabilityStatus()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var store = new FakeIntegrationSettingsStore
        {
            Settings = new IntegrationSettings
            {
                Sonarr = new SonarrIntegrationSettings
                {
                    Enabled = true,
                    BaseUrl = "http://sonarr.test/",
                    ApiKey = "sonarr-key"
                },
                Radarr = new RadarrIntegrationSettings
                {
                    Enabled = true,
                    BaseUrl = "http://radarr.test/",
                    ApiKey = "radarr-key"
                },
                Sources = new SourceIntegrationSettings
                {
                    Tmdb = new TmdbSourceSettings { BearerToken = "tmdb-token" },
                    Tvmaze = new TvmazeSourceSettings { Enabled = true, EnableScheduleDiscovery = true },
                    Trakt = new TraktSourceSettings { Enabled = true },
                    Watchmode = new WatchmodeSourceSettings
                    {
                        Enabled = true,
                        ApiKey = "watchmode-key",
                        EnableReleaseDiscovery = true,
                        EnableAvailabilityEnrichment = true
                    },
                    Omdb = new OmdbSourceSettings { Enabled = false },
                    Fanart = new FanartSourceSettings { Enabled = true },
                    TheTvdb = new TheTvdbSourceSettings { Enabled = false },
                    Wikimedia = new WikimediaSourceSettings { Enabled = true }
                }
            }
        };
        Services.AddSingleton<IIntegrationSettingsStore>(store);
        Services.AddSingleton<IArrIntegrationService>(new FakeArrIntegrationService());
        Services.AddSingleton<ISimklClient>(new FakeSimklClient());

        var component = Render<PremiereCalendar.Components.Pages.Settings>();

        component.WaitForAssertion(() =>
        {
            Assert.NotEmpty(component.FindAll("[aria-label='Library app integration status']"));
            Assert.NotEmpty(component.FindAll("[aria-label='Movie and series provider settings']"));
            Assert.NotEmpty(component.FindAll("[aria-label='Watch availability provider settings']"));
            Assert.NotEmpty(component.FindAll("[aria-label='Score provider settings']"));
            Assert.NotEmpty(component.FindAll("[aria-label='Artwork provider settings']"));

            var discovery = component.Find("[aria-label='Movie and series provider settings']").TextContent;
            Assert.Contains("TMDb: Configured", discovery);
            Assert.Contains("Trakt: Needs setup", discovery);
            Assert.DoesNotContain("Watchmode releases", discovery);

            var availability = component.Find("[aria-label='Watch availability provider settings']").TextContent;
            Assert.Contains("Watchmode availability: Configured", availability);
            Assert.Contains("Used only as a fallback for streaming availability", availability);

            var scores = component.Find("[aria-label='Score provider settings']").TextContent;
            Assert.Contains("OMDb: Disabled", scores);

            var artwork = component.Find("[aria-label='Artwork provider settings']").TextContent;
            Assert.Contains("Fanart.tv: Needs setup", artwork);
            Assert.Contains("Wikimedia: Configured", artwork);
        });
    }

    [Fact]
    public void SettingsPage_ConnectsSimklWithManualAuthorizedFallback()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var store = new FakeIntegrationSettingsStore
        {
            Settings = new IntegrationSettings
            {
                Sources = new SourceIntegrationSettings
                {
                    Simkl = new SimklSourceSettings
                    {
                        Enabled = true,
                        ClientId = "simkl-client-id",
                        ClientSecret = "simkl-client-secret"
                    }
                }
            }
        };
        var simklClient = new FakeSimklClient
        {
            PinCodeResult = new SimklPinCodeResult(
                Success: true,
                UserCode: "ABCD-EFGH",
                VerificationUrl: "https://simkl.com/pin/",
                ExpiresInSeconds: 600,
                IntervalSeconds: 30),
            PinStatusResult = new SimklPinStatusResult(
                SimklPinStatus.Authorized,
                AccessToken: "simkl-access-token")
        };
        Services.AddSingleton<IIntegrationSettingsStore>(store);
        Services.AddSingleton<IArrIntegrationService>(new FakeArrIntegrationService());
        Services.AddSingleton<ISimklClient>(simklClient);

        var component = Render<PremiereCalendar.Components.Pages.Settings>();

        component.Find("button[aria-label='Connect SIMKL']").Click();

        component.WaitForAssertion(() =>
        {
            Assert.Contains("ABCD-EFGH", component.Markup);
            Assert.Contains("https://simkl.com/pin/", component.Markup);
            Assert.Contains("Waiting for authorization", component.Markup);
            Assert.Contains("this page will save the token automatically", component.Markup);
            Assert.Equal(1, simklClient.RequestPinCodeCallCount);
        });

        component.Find("button[aria-label='Save SIMKL token after authorization']").Click();

        component.WaitForAssertion(() =>
        {
            Assert.Equal("simkl-access-token", store.Settings.Sources.Simkl.AccessToken);
            Assert.Equal(1, simklClient.CheckPinCodeCallCount);
            Assert.Equal("ABCD-EFGH", simklClient.LastCheckedUserCode);
        });
    }

    [Fact]
    public void SettingsPage_AutoPollsSimklAuthorizationAndSavesToken()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        var store = new FakeIntegrationSettingsStore
        {
            Settings = new IntegrationSettings
            {
                Sources = new SourceIntegrationSettings
                {
                    Simkl = new SimklSourceSettings
                    {
                        Enabled = true,
                        ClientId = "simkl-client-id",
                        ClientSecret = "simkl-client-secret"
                    }
                }
            }
        };
        var simklClient = new FakeSimklClient
        {
            PinCodeResult = new SimklPinCodeResult(
                Success: true,
                UserCode: "ABCD-EFGH",
                VerificationUrl: "https://simkl.com/pin/",
                ExpiresInSeconds: 10,
                IntervalSeconds: 1)
        };
        simklClient.PinStatusResults.Enqueue(new SimklPinStatusResult(
            SimklPinStatus.Authorized,
            AccessToken: "simkl-access-token"));
        Services.AddSingleton<IIntegrationSettingsStore>(store);
        Services.AddSingleton<IArrIntegrationService>(new FakeArrIntegrationService());
        Services.AddSingleton<ISimklClient>(simklClient);

        var component = Render<PremiereCalendar.Components.Pages.Settings>();

        component.Find("button[aria-label='Connect SIMKL']").Click();

        component.WaitForAssertion(() =>
        {
            Assert.Equal("simkl-access-token", store.Settings.Sources.Simkl.AccessToken);
            Assert.Equal("ABCD-EFGH", simklClient.LastCheckedUserCode);
            Assert.True(simklClient.CheckPinCodeCallCount >= 1);
        }, TimeSpan.FromSeconds(3));
    }

    private sealed class FakeIntegrationSettingsStore : IIntegrationSettingsStore
    {
        public IntegrationSettings Settings { get; set; } = new();

        public Task<IntegrationSettings> GetAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(Settings);
        }

        public Task SaveAsync(IntegrationSettings settings, CancellationToken cancellationToken = default)
        {
            Settings = settings;
            return Task.CompletedTask;
        }
    }

    private sealed class FakeArrIntegrationService : IArrIntegrationService
    {
        public Task<ArrAddResult> AddAsync(PremiereItem item, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ArrAddResult(true, false, ArrIntegrationTarget.Radarr, item.Title, "Added."));
        }

        public Task<ArrConnectionOptions> GetSonarrOptionsAsync(
            SonarrIntegrationSettings settings,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ArrConnectionOptions(
                [new ArrRootFolder(1, "/series", 1000)],
                [new ArrOption(4, "HD-1080p"), new ArrOption(7, "HD-720p")]));
        }

        public Task<ArrConnectionOptions> GetRadarrOptionsAsync(
            RadarrIntegrationSettings settings,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ArrConnectionOptions(
                [new ArrRootFolder(2, "/movies", 2000)],
                [new ArrOption(5, "Ultra-HD"), new ArrOption(6, "HD-1080p")]));
        }
    }

    private sealed class FakeSimklClient : ISimklClient
    {
        public SimklPinCodeResult PinCodeResult { get; set; } = new(
            Success: false,
            Error: "PIN exchange is not configured for this test.");

        public SimklPinStatusResult PinStatusResult { get; set; } = new(SimklPinStatus.Pending);
        public Queue<SimklPinStatusResult> PinStatusResults { get; } = new();

        public int RequestPinCodeCallCount { get; private set; }
        public int CheckPinCodeCallCount { get; private set; }
        public string? LastCheckedUserCode { get; private set; }

        public Task<SimklSyncResult> SyncLibraryAsync(CancellationToken cancellationToken, bool forceRefresh = false)
        {
            return Task.FromResult(new SimklSyncResult(SimklSyncStatus.Disabled));
        }

        public Task<SimklPinCodeResult> RequestPinCodeAsync(CancellationToken cancellationToken)
        {
            RequestPinCodeCallCount++;
            return Task.FromResult(PinCodeResult);
        }

        public Task<SimklPinStatusResult> CheckPinCodeAsync(string userCode, CancellationToken cancellationToken)
        {
            CheckPinCodeCallCount++;
            LastCheckedUserCode = userCode;
            return Task.FromResult(PinStatusResults.Count > 0
                ? PinStatusResults.Dequeue()
                : PinStatusResult);
        }
    }
}
