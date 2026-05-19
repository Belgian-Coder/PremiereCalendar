using Bunit;
using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.ComponentTests;

public sealed class SettingsPageTests : BunitContext
{
    private readonly FakeViewSyncService _viewSyncService = new();
    private readonly InMemoryAppStateStore _appStateStore = new();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"premiere-settings-tests-{Guid.NewGuid():N}");

    public SettingsPageTests()
    {
        Directory.CreateDirectory(_root);
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.Setup<string>("premiereViewSync.getOrCreateDeviceId").SetResult("device-a");
        Services.AddLogging();
        Services.AddSingleton<IViewSyncService>(_viewSyncService);
        Services.AddSingleton<IAppStateStore>(_appStateStore);
        Services.AddSingleton(TimeProvider.System);
        Services.AddSingleton(sp => new CacheInspectorService(
            Microsoft.Extensions.Options.Options.Create(new CalendarCacheOptions { Directory = "cache/calendar" }),
            Microsoft.Extensions.Options.Options.Create(new ImageCacheOptions { Directory = "cache/images" }),
            new FakeWebHostEnvironment(_root)));
        Services.AddSingleton<BackgroundJobTimelineService>();
        Services.AddSingleton<SettingsBackupService>();
        Services.AddSingleton(new ReleaseUpdateService(
            new HttpClient(new StaticHttpMessageHandler(
                """
                {
                  "tag_name": "v1.2.0",
                  "html_url": "https://github.com/Belgian-Coder/PremiereCalendar/releases/tag/v1.2.0",
                  "published_at": "2026-05-16T10:00:00Z",
                  "name": "Premiere Calendar 1.2.0"
                }
                """))
            {
                BaseAddress = new Uri("https://api.github.com/")
            },
            "1.0.0"));
    }

    [Fact]
    public void SettingsPage_ShowsRequiredTmdbNoticeWhenCalendarRedirectsForMissingToken()
    {
        var store = new FakeIntegrationSettingsStore();
        Services.AddSingleton<IIntegrationSettingsStore>(store);
        Services.AddSingleton<IArrIntegrationService>(new FakeArrIntegrationService());
        Services.AddSingleton<ISimklClient>(new FakeSimklClient());
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo("/settings?reason=tmdb&returnUrl=%2Fmovies%3Fweek%3D2026-05-04%26score%3Dimdb");

        var component = Render<PremiereCalendar.Components.Pages.Settings>();

        component.WaitForAssertion(() =>
        {
            var notice = component.Find("[data-testid='tmdb-required-notice']");
            Assert.Contains("TMDb token required", notice.TextContent);
            Assert.Contains("Save settings", notice.TextContent);
            Assert.Equal("alert", notice.GetAttribute("role"));
            Assert.Single(notice.QuerySelectorAll("a[href='#tmdb-token']"));
            Assert.Empty(notice.QuerySelectorAll("a[href='/movies?week=2026-05-04&score=imdb']"));
            Assert.Single(component.FindAll("#tmdb-token"));
            Assert.Contains("TMDb API read access token", component.Markup);
        });
    }

    [Fact]
    public void SettingsPage_HidesRequiredTmdbNoticeWhenTokenAlreadyExists()
    {
        var store = new FakeIntegrationSettingsStore
        {
            Settings = new IntegrationSettings
            {
                Sources = new SourceIntegrationSettings
                {
                    Tmdb = new TmdbSourceSettings { BearerToken = "tmdb-token" }
                }
            }
        };
        Services.AddSingleton<IIntegrationSettingsStore>(store);
        Services.AddSingleton<IArrIntegrationService>(new FakeArrIntegrationService());
        Services.AddSingleton<ISimklClient>(new FakeSimklClient());
        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>()
            .NavigateTo("/settings?reason=tmdb");

        var component = Render<PremiereCalendar.Components.Pages.Settings>();

        component.WaitForAssertion(() =>
        {
            Assert.Empty(component.FindAll("[data-testid='tmdb-required-notice']"));
        });
    }

    [Fact]
    public void SettingsPage_SaveAfterTmdbSetupReturnsToOriginalCalendarUrl()
    {
        var store = new FakeIntegrationSettingsStore();
        Services.AddSingleton<IIntegrationSettingsStore>(store);
        Services.AddSingleton<IArrIntegrationService>(new FakeArrIntegrationService());
        Services.AddSingleton<ISimklClient>(new FakeSimklClient());
        var navigation = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        navigation.NavigateTo("/settings?reason=tmdb&returnUrl=%2Fmovies%3Fweek%3D2026-05-04%26score%3Dimdb");

        var component = Render<PremiereCalendar.Components.Pages.Settings>();

        component.WaitForElement("input[aria-label='TMDb API read access token']");
        component.Find("input[aria-label='TMDb API read access token']").Change("tmdb-token");
        component.Find("button[title='Save integration settings']").Click();

        component.WaitForAssertion(() =>
        {
            Assert.Equal("tmdb-token", store.Settings.Sources.Tmdb.BearerToken);
            Assert.EndsWith("/movies?week=2026-05-04&score=imdb", new Uri(navigation.Uri).PathAndQuery);
        });
    }

    [Theory]
    [InlineData("%2F%5Cevil.example")]
    [InlineData("%5C%5Cevil.example")]
    [InlineData("%2Fmovies%0AHost%3A%20evil.example")]
    public void SettingsPage_SaveAfterTmdbSetupRejectsUnsafeReturnUrl(string encodedReturnUrl)
    {
        var store = new FakeIntegrationSettingsStore();
        Services.AddSingleton<IIntegrationSettingsStore>(store);
        Services.AddSingleton<IArrIntegrationService>(new FakeArrIntegrationService());
        Services.AddSingleton<ISimklClient>(new FakeSimklClient());
        var navigation = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        navigation.NavigateTo($"/settings?reason=tmdb&returnUrl={encodedReturnUrl}");

        var component = Render<PremiereCalendar.Components.Pages.Settings>();

        component.WaitForElement("input[aria-label='TMDb API read access token']");
        component.Find("input[aria-label='TMDb API read access token']").Change("tmdb-token");
        component.Find("button[title='Save integration settings']").Click();

        component.WaitForAssertion(() =>
        {
            Assert.Equal("tmdb-token", store.Settings.Sources.Tmdb.BearerToken);
            Assert.Equal("/settings", new Uri(navigation.Uri).AbsolutePath);
        });
    }

    [Fact]
    public void SettingsPage_SettingsLoadFailureShowsErrorPanel()
    {
        Services.AddSingleton<IIntegrationSettingsStore>(new ThrowingIntegrationSettingsStore());
        Services.AddSingleton<IArrIntegrationService>(new FakeArrIntegrationService());
        Services.AddSingleton<ISimklClient>(new FakeSimklClient());

        var component = Render<PremiereCalendar.Components.Pages.Settings>();

        component.WaitForAssertion(() =>
        {
            var error = component.Find("[data-testid='settings-load-error']");
            Assert.Equal("alert", error.GetAttribute("role"));
            Assert.Contains("Settings could not be loaded", error.TextContent);
            Assert.Empty(component.FindAll("form"));
        });
    }

    [Fact]
    public void SettingsPage_ShowsLocalStatusCenterAndChecksForUpdates()
    {
        Directory.CreateDirectory(Path.Combine(_root, "cache", "calendar"));
        File.WriteAllText(Path.Combine(_root, "cache", "calendar", "20260511-20260517-default.json"), "{}");
        var store = new FakeIntegrationSettingsStore();
        Services.AddSingleton<IIntegrationSettingsStore>(store);
        Services.AddSingleton<IArrIntegrationService>(new FakeArrIntegrationService());
        Services.AddSingleton<ISimklClient>(new FakeSimklClient());

        var component = Render<PremiereCalendar.Components.Pages.Settings>();

        component.WaitForAssertion(() =>
        {
            var status = component.Find("[data-testid='local-status-center']");
            Assert.Contains("Local status", status.TextContent);
            Assert.Contains("Calendar cache", status.TextContent);
            Assert.Contains("Backup", status.TextContent);
        });

        component.Find("button[title='Check for application updates']").Click();

        component.WaitForAssertion(() =>
            Assert.Contains("1.2.0", component.Find("[data-testid='release-update-result']").TextContent));
    }

    [Fact]
    public async Task SettingsPage_JobTimelineKeepsProviderDeltaFailureVisibleAboveRoutineJobs()
    {
        var store = new FakeIntegrationSettingsStore();
        Services.AddSingleton<IIntegrationSettingsStore>(store);
        Services.AddSingleton<IArrIntegrationService>(new FakeArrIntegrationService());
        Services.AddSingleton<ISimklClient>(new FakeSimklClient());
        var timeline = Services.GetRequiredService<BackgroundJobTimelineService>();
        var failedAt = DateTimeOffset.Parse("2026-05-16T09:00:00Z");

        await timeline.RecordAsync(
            "Provider delta sync",
            BackgroundJobStatus.Failed,
            "TMDb change tracking timed out after 20 seconds.",
            failedAt,
            TimeSpan.FromSeconds(20),
            CancellationToken.None);
        for (var index = 0; index < 8; index++)
        {
            await timeline.RecordAsync(
                "Adjacent week prefetch",
                BackgroundJobStatus.Succeeded,
                $"Prefetched adjacent week {index.ToString(System.Globalization.CultureInfo.InvariantCulture)}.",
                failedAt.AddMinutes(index + 1),
                TimeSpan.FromMilliseconds(100),
                CancellationToken.None);
        }

        var component = Render<PremiereCalendar.Components.Pages.Settings>();

        component.WaitForAssertion(() =>
        {
            var jobTimeline = component.Find(".job-timeline");
            var firstJob = Assert.Single(jobTimeline.QuerySelectorAll("li").Take(1));
            Assert.Contains("Provider delta sync", firstJob.TextContent);
            Assert.Contains("TMDb change tracking timed out", firstJob.TextContent);
        });
    }

    [Fact]
    public void SettingsPage_ShowsNoReleaseMessageWhenGitHubHasNoPublishedReleases()
    {
        Services.AddSingleton(new ReleaseUpdateService(
            new HttpClient(new StaticHttpMessageHandler("{}", HttpStatusCode.NotFound))
            {
                BaseAddress = new Uri("https://api.github.com/")
            },
            "1.0.0"));
        var store = new FakeIntegrationSettingsStore();
        Services.AddSingleton<IIntegrationSettingsStore>(store);
        Services.AddSingleton<IArrIntegrationService>(new FakeArrIntegrationService());
        Services.AddSingleton<ISimklClient>(new FakeSimklClient());

        var component = Render<PremiereCalendar.Components.Pages.Settings>();

        component.WaitForElement("button[title='Check for application updates']");
        component.Find("button[title='Check for application updates']").Click();

        component.WaitForAssertion(() =>
        {
            var result = component.Find("[data-testid='release-update-result']").TextContent;
            Assert.Contains("No published releases found.", result);
            Assert.Contains("Current 1.0.0", result);
        });
    }

    [Fact]
    public void SettingsPage_BackupExportRedactsSecretsByDefault()
    {
        var store = new FakeIntegrationSettingsStore
        {
            Settings = new IntegrationSettings
            {
                Sonarr = new SonarrIntegrationSettings { ApiKey = "sonarr-secret" },
                Sources = new SourceIntegrationSettings
                {
                    Tmdb = new TmdbSourceSettings { BearerToken = "tmdb-secret" },
                    Watchmode = new WatchmodeSourceSettings { ApiKey = "watchmode-secret" },
                    Simkl = new SimklSourceSettings { ClientSecret = "simkl-secret", AccessToken = "simkl-token" }
                }
            }
        };
        Services.AddSingleton<IIntegrationSettingsStore>(store);
        Services.AddSingleton<IArrIntegrationService>(new FakeArrIntegrationService());
        Services.AddSingleton<ISimklClient>(new FakeSimklClient());

        var component = Render<PremiereCalendar.Components.Pages.Settings>();

        component.WaitForElement("section[aria-label='Backup and restore settings']");
        component.Find("section[aria-label='Backup and restore settings'] button").Click();

        component.WaitForAssertion(() =>
        {
            var backupJson = component.Find("textarea[aria-label='Settings backup JSON']").TextContent;
            Assert.DoesNotContain("sonarr-secret", backupJson, StringComparison.Ordinal);
            Assert.DoesNotContain("tmdb-secret", backupJson, StringComparison.Ordinal);
            Assert.DoesNotContain("watchmode-secret", backupJson, StringComparison.Ordinal);
            Assert.DoesNotContain("simkl-secret", backupJson, StringComparison.Ordinal);
            Assert.DoesNotContain("simkl-token", backupJson, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task SettingsPage_ImportRehydratesDerivedCountryFieldsBeforeNextSave()
    {
        var sourceSettingsStore = new FakeIntegrationSettingsStore
        {
            Settings = new IntegrationSettings
            {
                Sources = new SourceIntegrationSettings
                {
                    Tvmaze = new TvmazeSourceSettings { ScheduleCountries = ["BE", "NL"] },
                    Watchmode = new WatchmodeSourceSettings { Regions = ["BE", "NL"] }
                }
            }
        };
        var sourceBackup = new SettingsBackupService(sourceSettingsStore, _appStateStore, TimeProvider.System);
        var backupJson = await sourceBackup.ExportAsync(includeSecrets: true, CancellationToken.None);
        var targetStore = new FakeIntegrationSettingsStore
        {
            Settings = new IntegrationSettings
            {
                Sources = new SourceIntegrationSettings
                {
                    Tvmaze = new TvmazeSourceSettings { ScheduleCountries = ["US"] },
                    Watchmode = new WatchmodeSourceSettings { Regions = ["US"] }
                }
            }
        };
        Services.AddSingleton<IIntegrationSettingsStore>(targetStore);
        Services.AddSingleton<IArrIntegrationService>(new FakeArrIntegrationService());
        Services.AddSingleton<ISimklClient>(new FakeSimklClient());
        var component = Render<PremiereCalendar.Components.Pages.Settings>();

        component.WaitForElement("textarea[aria-label='Settings backup JSON']");
        component.Find("textarea[aria-label='Settings backup JSON']").Change(backupJson);
        component.FindAll("section[aria-label='Backup and restore settings'] button")[1].Click();
        component.WaitForAssertion(() => Assert.Equal(["BE", "NL"], targetStore.Settings.Sources.Tvmaze.ScheduleCountries));

        component.Find("button[title='Save integration settings']").Click();

        component.WaitForAssertion(() =>
        {
            Assert.Equal(["BE", "NL"], targetStore.Settings.Sources.Tvmaze.ScheduleCountries);
            Assert.Equal(["BE", "NL"], targetStore.Settings.Sources.Watchmode.Regions);
        });
    }

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
            Assert.Equal("status", component.Find("[data-testid='simkl-auth-status']").GetAttribute("role"));
            Assert.Equal("polite", component.Find("[data-testid='simkl-auth-status']").GetAttribute("aria-live"));
            Assert.Null(component.Find("[data-testid='simkl-pin-panel']").GetAttribute("role"));
            Assert.Null(component.Find("[data-testid='simkl-pin-panel']").GetAttribute("aria-live"));
            Assert.Equal("status", component.Find("[data-testid='simkl-pin-status']").GetAttribute("role"));
            Assert.Equal("polite", component.Find("[data-testid='simkl-pin-status']").GetAttribute("aria-live"));
            Assert.Empty(component.Find("[data-testid='simkl-pin-status']").QuerySelectorAll("a,button"));
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

    [Fact]
    public void SettingsPage_AutoPollsSimklAuthorizationAfterTransientFailure()
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
        simklClient.PinStatusFailures.Enqueue(new TimeoutException("Temporary SIMKL timeout."));
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
            Assert.True(simklClient.CheckPinCodeCallCount >= 2);
        }, TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task SettingsPage_StopsSimklPollingAfterTerminalFailure()
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
                IntervalSeconds: 1),
            PinStatusResult = new SimklPinStatusResult(SimklPinStatus.Failed, Message: "PIN rejected.")
        };
        Services.AddSingleton<IIntegrationSettingsStore>(store);
        Services.AddSingleton<IArrIntegrationService>(new FakeArrIntegrationService());
        Services.AddSingleton<ISimklClient>(simklClient);

        var component = Render<PremiereCalendar.Components.Pages.Settings>();

        component.Find("button[aria-label='Connect SIMKL']").Click();

        component.WaitForAssertion(() =>
        {
            Assert.Equal(1, simklClient.CheckPinCodeCallCount);
            Assert.Contains("PIN rejected.", component.Markup);
            Assert.Empty(component.FindAll("[aria-label='SIMKL authorization code']"));
        }, TimeSpan.FromSeconds(3));

        await Task.Delay(TimeSpan.FromMilliseconds(1300));

        Assert.Equal(1, simklClient.CheckPinCodeCallCount);
    }

    [Fact]
    public void SettingsPage_ViewSyncOverviewFailureDoesNotBreakSettings()
    {
        var store = new FakeIntegrationSettingsStore();
        _viewSyncService.GetOverviewException = new ArgumentOutOfRangeException("value", "View-sync schema initialization failed.");
        Services.AddSingleton<IIntegrationSettingsStore>(store);
        Services.AddSingleton<IArrIntegrationService>(new FakeArrIntegrationService());
        Services.AddSingleton<ISimklClient>(new FakeSimklClient());

        var component = Render<PremiereCalendar.Components.Pages.Settings>();

        component.WaitForAssertion(() =>
        {
            Assert.Contains("Settings", component.Markup);
            Assert.Contains("View sync", component.Markup);
        });
    }

    [Fact]
    public void SettingsPage_RendersViewSyncControlsAndSavesDeviceGroup()
    {
        var store = new FakeIntegrationSettingsStore();
        _viewSyncService.Overview = new ViewSyncOverview(
            new ViewSyncDevice(
                "device-a",
                "Office PC",
                SyncEnabled: false,
                GroupId: null,
                DateTimeOffset.Parse("2026-05-10T10:00:00Z")),
            [new ViewSyncGroup("group-a", "Living room", DateTimeOffset.Parse("2026-05-10T09:00:00Z"))],
            [],
            null);
        Services.AddSingleton<IIntegrationSettingsStore>(store);
        Services.AddSingleton<IArrIntegrationService>(new FakeArrIntegrationService());
        Services.AddSingleton<ISimklClient>(new FakeSimklClient());

        var component = Render<PremiereCalendar.Components.Pages.Settings>();

        component.WaitForAssertion(() =>
        {
            Assert.NotEmpty(component.FindAll("[aria-label='View sync settings']"));
            Assert.Contains("View sync", component.Markup);
            Assert.Equal(
                "Office PC",
                component.Find("input[aria-label='This device name']").GetAttribute("value"));
            Assert.Contains("Living room", component.Find("select[aria-label='View sync group']").TextContent);
        });

        component.Find("input[aria-label='Sync viewing on this browser']").Change(true);
        component.Find("input[aria-label='This device name']").Change("Kitchen tablet");
        component.Find("select[aria-label='View sync group']").Change("group-a");
        component.Find("button[aria-label='Save view sync settings']").Click();

        component.WaitForAssertion(() =>
        {
            Assert.Equal("device-a", _viewSyncService.LastSavedDeviceId);
            Assert.Equal("Kitchen tablet", _viewSyncService.LastSavedName);
            Assert.True(_viewSyncService.LastSavedEnabled);
            Assert.Equal("group-a", _viewSyncService.LastSavedGroupId);
        });
    }

    [Fact]
    public void SettingsPage_AddsCurrentBrowserToSelectedGroupWithoutSeparateToggle()
    {
        var store = new FakeIntegrationSettingsStore();
        _viewSyncService.Overview = new ViewSyncOverview(
            new ViewSyncDevice(
                "device-a",
                "Office PC",
                SyncEnabled: false,
                GroupId: null,
                DateTimeOffset.Parse("2026-05-10T10:00:00Z")),
            [new ViewSyncGroup("group-a", "Living room", DateTimeOffset.Parse("2026-05-10T09:00:00Z"))],
            [],
            null);
        Services.AddSingleton<IIntegrationSettingsStore>(store);
        Services.AddSingleton<IArrIntegrationService>(new FakeArrIntegrationService());
        Services.AddSingleton<ISimklClient>(new FakeSimklClient());

        var component = Render<PremiereCalendar.Components.Pages.Settings>();

        component.WaitForElement("select[aria-label='View sync group']");
        component.Find("select[aria-label='View sync group']").Change("group-a");

        component.WaitForAssertion(() =>
            Assert.Contains("Add this browser", component.Find("button[aria-label='Save view sync settings']").TextContent));

        component.Find("button[aria-label='Save view sync settings']").Click();

        component.WaitForAssertion(() =>
        {
            Assert.True(_viewSyncService.LastSavedEnabled);
            Assert.Equal("group-a", _viewSyncService.LastSavedGroupId);
        });
    }

    [Fact]
    public void SettingsPage_CreatesViewSyncGroupAndUngroupsDevice()
    {
        var store = new FakeIntegrationSettingsStore();
        _viewSyncService.Overview = new ViewSyncOverview(
            new ViewSyncDevice(
                "device-a",
                "Office PC",
                SyncEnabled: true,
                "group-a",
                DateTimeOffset.Parse("2026-05-10T10:00:00Z")),
            [new ViewSyncGroup("group-a", "Living room", DateTimeOffset.Parse("2026-05-10T09:00:00Z"))],
            [new ViewSyncDevice("device-a", "Office PC", true, "group-a", DateTimeOffset.Parse("2026-05-10T10:00:00Z"))],
            new ViewSyncGroupState(
                "group-a",
                "series",
                "/series?week=2026-05-04&day=2026-05-05",
                2,
                DateTimeOffset.Parse("2026-05-10T10:05:00Z"),
                "device-a",
                "Office PC"));
        Services.AddSingleton<IIntegrationSettingsStore>(store);
        Services.AddSingleton<IArrIntegrationService>(new FakeArrIntegrationService());
        Services.AddSingleton<ISimklClient>(new FakeSimklClient());

        var component = Render<PremiereCalendar.Components.Pages.Settings>();

        component.WaitForElement("input[aria-label='New view sync group name']");
        component.WaitForAssertion(() =>
        {
            var devices = component.Find("[aria-label='Devices in Living room']");
            Assert.Contains("Office PC", devices.TextContent);
            Assert.Contains("me", devices.TextContent);
            Assert.NotEmpty(component.FindAll("[data-testid='view-sync-me-badge']"));
        });
        component.Find("input[aria-label='New view sync group name']").Change("Bedroom");
        component.Find("button[aria-label='Create view sync group']").Click();
        component.Find("button[aria-label='Ungroup this device']").Click();

        component.WaitForAssertion(() =>
        {
            Assert.Equal("Bedroom", _viewSyncService.CreatedGroupName);
            Assert.Equal("device-a", _viewSyncService.UngroupedDeviceId);
        });
    }

    [Fact]
    public void SettingsPage_ShowsLatestViewSyncUrlForEachCalendarRoute()
    {
        var store = new FakeIntegrationSettingsStore();
        _viewSyncService.Overview = new ViewSyncOverview(
            new ViewSyncDevice(
                "device-a",
                "Office PC",
                SyncEnabled: true,
                "group-a",
                DateTimeOffset.Parse("2026-05-10T10:00:00Z")),
            [new ViewSyncGroup("group-a", "Living room", DateTimeOffset.Parse("2026-05-10T09:00:00Z"))],
            [new ViewSyncDevice("device-a", "Office PC", true, "group-a", DateTimeOffset.Parse("2026-05-10T10:00:00Z"))],
            new ViewSyncGroupState(
                "group-a",
                "series",
                "/series?week=2026-05-04&day=2026-05-05",
                3,
                DateTimeOffset.Parse("2026-05-10T10:05:00Z"),
                "device-a",
                "Office PC"),
            [
                new ViewSyncGroupState(
                    "group-a",
                    "all",
                    "/?week=2026-05-04&day=2026-05-04",
                    1,
                    DateTimeOffset.Parse("2026-05-10T10:01:00Z"),
                    "device-b",
                    "Tablet"),
                new ViewSyncGroupState(
                    "group-a",
                    "movies",
                    "/movies?week=2026-05-11&day=2026-05-12",
                    2,
                    DateTimeOffset.Parse("2026-05-10T10:03:00Z"),
                    "device-c",
                    "TV"),
                new ViewSyncGroupState(
                    "group-a",
                    "series",
                    "/series?week=2026-05-04&day=2026-05-05",
                    3,
                    DateTimeOffset.Parse("2026-05-10T10:05:00Z"),
                    "device-a",
                    "Office PC")
            ]);
        Services.AddSingleton<IIntegrationSettingsStore>(store);
        Services.AddSingleton<IArrIntegrationService>(new FakeArrIntegrationService());
        Services.AddSingleton<ISimklClient>(new FakeSimklClient());

        var component = Render<PremiereCalendar.Components.Pages.Settings>();

        component.WaitForAssertion(() =>
        {
            var routes = component.Find("[aria-label='Latest synced calendar routes']");
            Assert.Contains("All", routes.TextContent);
            Assert.Contains("/?week=2026-05-04&day=2026-05-04", routes.TextContent);
            Assert.Contains("Movies", routes.TextContent);
            Assert.Contains("/movies?week=2026-05-11&day=2026-05-12", routes.TextContent);
            Assert.Contains("Series", routes.TextContent);
            Assert.Contains("/series?week=2026-05-04&day=2026-05-05", routes.TextContent);
        });
    }

    [Fact]
    public void SettingsPage_GroupsViewSyncDevicesAndUrlsByGroup()
    {
        var store = new FakeIntegrationSettingsStore();
        var livingRoom = new ViewSyncGroup("group-a", "Living room", DateTimeOffset.Parse("2026-05-10T09:00:00Z"));
        var bedroom = new ViewSyncGroup("group-b", "Bedroom", DateTimeOffset.Parse("2026-05-10T09:30:00Z"));
        var currentDevice = new ViewSyncDevice("device-a", "Office PC", true, "group-a", DateTimeOffset.Parse("2026-05-10T10:00:00Z"));
        _viewSyncService.Overview = new ViewSyncOverview(
            currentDevice,
            [livingRoom, bedroom],
            [currentDevice],
            null,
            [],
            [
                new ViewSyncGroupOverview(
                    livingRoom,
                    [
                        currentDevice,
                        new ViewSyncDevice("device-b", "Kitchen tablet", true, "group-a", DateTimeOffset.Parse("2026-05-10T10:02:00Z"))
                    ],
                    [
                        new ViewSyncGroupState(
                            "group-a",
                            "all",
                            "/?week=2026-05-04&day=2026-05-04",
                            1,
                            DateTimeOffset.Parse("2026-05-10T10:01:00Z"),
                            "device-b",
                            "Kitchen tablet"),
                        new ViewSyncGroupState(
                            "group-a",
                            "series",
                            "/series?week=2026-05-04&day=2026-05-05",
                            2,
                            DateTimeOffset.Parse("2026-05-10T10:03:00Z"),
                            "device-a",
                            "Office PC")
                    ]),
                new ViewSyncGroupOverview(
                    bedroom,
                    [new ViewSyncDevice("device-c", "Bedroom TV", true, "group-b", DateTimeOffset.Parse("2026-05-10T10:04:00Z"))],
                    [
                        new ViewSyncGroupState(
                            "group-b",
                            "movies",
                            "/movies?week=2026-05-11&day=2026-05-12",
                            1,
                            DateTimeOffset.Parse("2026-05-10T10:05:00Z"),
                            "device-c",
                            "Bedroom TV")
                    ])
            ]);
        Services.AddSingleton<IIntegrationSettingsStore>(store);
        Services.AddSingleton<IArrIntegrationService>(new FakeArrIntegrationService());
        Services.AddSingleton<ISimklClient>(new FakeSimklClient());

        var component = Render<PremiereCalendar.Components.Pages.Settings>();

        component.WaitForAssertion(() =>
        {
            var groups = component.Find("[aria-label='View sync groups']");
            var livingRoomCard = component.Find("[aria-label='View sync group Living room']");
            var bedroomCard = component.Find("[aria-label='View sync group Bedroom']");
            Assert.Contains("Living room", groups.TextContent);
            Assert.Contains("Office PC", livingRoomCard.TextContent);
            Assert.Contains("me", livingRoomCard.TextContent);
            Assert.Contains("Kitchen tablet", livingRoomCard.TextContent);
            Assert.Contains("/?week=2026-05-04&day=2026-05-04", livingRoomCard.TextContent);
            Assert.Contains("/series?week=2026-05-04&day=2026-05-05", livingRoomCard.TextContent);
            Assert.Contains("Bedroom", groups.TextContent);
            Assert.Contains("Bedroom TV", bedroomCard.TextContent);
            Assert.Contains("/movies?week=2026-05-11&day=2026-05-12", bedroomCard.TextContent);
            Assert.DoesNotContain("Kitchen tablet", bedroomCard.TextContent);
        });
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

    private sealed class ThrowingIntegrationSettingsStore : IIntegrationSettingsStore
    {
        public Task<IntegrationSettings> GetAsync(CancellationToken cancellationToken = default)
        {
            throw new IOException("Settings database is unavailable.");
        }

        public Task SaveAsync(IntegrationSettings settings, CancellationToken cancellationToken = default)
        {
            throw new IOException("Settings database is unavailable.");
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
        public Queue<Exception> PinStatusFailures { get; } = new();

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
            if (PinStatusFailures.Count > 0)
            {
                throw PinStatusFailures.Dequeue();
            }

            return Task.FromResult(PinStatusResults.Count > 0
                ? PinStatusResults.Dequeue()
                : PinStatusResult);
        }
    }

    private sealed class FakeViewSyncService : IViewSyncService
    {
        public event EventHandler<ViewSyncStateChangedEventArgs>? StateChanged
        {
            add { }
            remove { }
        }

        public ViewSyncOverview Overview { get; set; } = new(
            new ViewSyncDevice(
                "device-a",
                "This browser",
                SyncEnabled: false,
                GroupId: null,
                DateTimeOffset.Parse("2026-05-10T10:00:00Z")),
            [],
            [],
            null);

        public string? LastSavedDeviceId { get; private set; }
        public string? LastSavedName { get; private set; }
        public bool LastSavedEnabled { get; private set; }
        public string? LastSavedGroupId { get; private set; }
        public string? CreatedGroupName { get; private set; }
        public string? UngroupedDeviceId { get; private set; }
        public Exception? GetOverviewException { get; set; }

        public Task<ViewSyncOverview> GetOverviewAsync(string deviceId, CancellationToken cancellationToken)
        {
            if (GetOverviewException is not null)
            {
                throw GetOverviewException;
            }

            return Task.FromResult(Overview);
        }

        public Task<ViewSyncOverview> SaveDeviceAsync(
            string deviceId,
            string displayName,
            bool syncEnabled,
            string? groupId,
            CancellationToken cancellationToken)
        {
            LastSavedDeviceId = deviceId;
            LastSavedName = displayName;
            LastSavedEnabled = syncEnabled;
            LastSavedGroupId = groupId;
            Overview = Overview with
            {
                Device = new ViewSyncDevice(
                    deviceId,
                    displayName,
                    syncEnabled && !string.IsNullOrWhiteSpace(groupId),
                    groupId,
                    DateTimeOffset.Parse("2026-05-10T10:00:00Z"))
            };
            return Task.FromResult(Overview);
        }

        public Task<ViewSyncGroup> CreateGroupAsync(string name, CancellationToken cancellationToken)
        {
            CreatedGroupName = name;
            var group = new ViewSyncGroup("group-created", name, DateTimeOffset.Parse("2026-05-10T11:00:00Z"));
            Overview = Overview with { Groups = [.. Overview.Groups, group] };
            return Task.FromResult(group);
        }

        public Task<ViewSyncOverview> UngroupDeviceAsync(string deviceId, CancellationToken cancellationToken)
        {
            UngroupedDeviceId = deviceId;
            Overview = Overview with
            {
                Device = Overview.Device with { SyncEnabled = false, GroupId = null },
                GroupDevices = []
            };
            return Task.FromResult(Overview);
        }

        public Task<ViewSyncPublishResult> PublishUrlAsync(string deviceId, string relativeUrl, CancellationToken cancellationToken)
        {
            return Task.FromResult(new ViewSyncPublishResult(false, Overview.GroupState));
        }

        public Task<ViewSyncGroupState?> GetLatestStateForDeviceAsync(string deviceId, CancellationToken cancellationToken)
        {
            return Task.FromResult(Overview.GroupState);
        }

        public Task<ViewSyncGroupState?> GetLatestStateForDeviceAsync(
            string deviceId,
            string? routeKey,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(routeKey is null
                || string.Equals(Overview.GroupState?.RouteKey, routeKey, StringComparison.OrdinalIgnoreCase)
                    ? Overview.GroupState
                    : null);
        }
    }

    private sealed class InMemoryAppStateStore : IAppStateStore
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken)
        {
            _values.TryGetValue(key, out var value);
            return Task.FromResult(value);
        }

        public Task SetValueAsync(string key, string value, CancellationToken cancellationToken)
        {
            _values[key] = value;
            return Task.CompletedTask;
        }

        public Task DeleteValueAsync(string key, CancellationToken cancellationToken)
        {
            _values.Remove(key);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<string, string>> GetValuesByPrefixAsync(string prefix, CancellationToken cancellationToken)
        {
            IReadOnlyDictionary<string, string> values = _values
                .Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal))
                .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
            return Task.FromResult(values);
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

    private sealed class StaticHttpMessageHandler(string content, HttpStatusCode statusCode = HttpStatusCode.OK) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            });
        }
    }
}
