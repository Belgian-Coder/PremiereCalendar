using System.Net;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class LocalObservabilityServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"premiere-local-observability-{Guid.NewGuid():N}");

    [Fact]
    public async Task AppStateStore_RoundTripsNamespacedValues()
    {
        var firstStore = CreateAppStateStore();

        await firstStore.SetValueAsync("Calendar.Presets", """{"count":1}""", CancellationToken.None);
        await firstStore.SetValueAsync("Diagnostics.BackgroundJobs", """{"jobs":[]}""", CancellationToken.None);

        var secondStore = CreateAppStateStore();
        var calendarValue = await secondStore.GetValueAsync("Calendar.Presets", CancellationToken.None);
        var diagnostics = await secondStore.GetValuesByPrefixAsync("Diagnostics.", CancellationToken.None);

        Assert.Equal("""{"count":1}""", calendarValue);
        Assert.Single(diagnostics);
        Assert.Equal("""{"jobs":[]}""", diagnostics["Diagnostics.BackgroundJobs"]);
    }

    [Fact]
    public async Task BackgroundJobTimeline_TrimsOldEventsAndKeepsNewestFirst()
    {
        var store = CreateAppStateStore();
        var timeline = new BackgroundJobTimelineService(store, TimeProvider.System);

        for (var index = 0; index < 105; index++)
        {
            await timeline.RecordAsync(
                "Warmup",
                BackgroundJobStatus.Succeeded,
                $"run {index}",
                new DateTimeOffset(2026, 5, 16, 8, 0, 0, TimeSpan.Zero).AddMinutes(index),
                TimeSpan.FromSeconds(index),
                CancellationToken.None);
        }

        var events = await timeline.GetRecentAsync(CancellationToken.None);

        Assert.Equal(100, events.Count);
        Assert.Equal("run 104", events[0].Message);
        Assert.Equal("run 5", events[^1].Message);
    }

    [Fact]
    public async Task BackgroundJobTimeline_PreservesConcurrentEvents()
    {
        var store = new YieldingAppStateStore();
        var timeline = new BackgroundJobTimelineService(store, TimeProvider.System);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(index =>
            timeline.RecordAsync(
                "Prefetch",
                BackgroundJobStatus.Succeeded,
                $"run {index}",
                new DateTimeOffset(2026, 5, 16, 8, index, 0, TimeSpan.Zero),
                cancellationToken: CancellationToken.None)));

        var events = await timeline.GetRecentAsync(CancellationToken.None);

        Assert.Equal(20, events.Count);
        Assert.Equal(
            Enumerable.Range(0, 20).Select(index => $"run {index}").Order(StringComparer.Ordinal),
            events.Select(entry => entry.Message).Order(StringComparer.Ordinal));
    }

    [Fact]
    public Task CacheInspector_SummarizesCalendarAndImageCacheDirectories()
    {
        var calendarDirectory = Path.Combine(_root, "cache", "calendar");
        var imageDirectory = Path.Combine(_root, "cache", "images");
        Directory.CreateDirectory(calendarDirectory);
        Directory.CreateDirectory(imageDirectory);
        File.WriteAllText(Path.Combine(calendarDirectory, "20260511-20260517-default.json"), "{}");
        File.WriteAllBytes(Path.Combine(imageDirectory, "poster.jpg"), new byte[128]);

        var inspector = new CacheInspectorService(
            Microsoft.Extensions.Options.Options.Create(new CalendarCacheOptions { Directory = "cache/calendar" }),
            Microsoft.Extensions.Options.Options.Create(new ImageCacheOptions { Directory = "cache/images" }),
            new FakeWebHostEnvironment(_root));

        var summary = inspector.GetSummary();

        Assert.Equal(1, summary.Calendar.FileCount);
        Assert.Equal(1, summary.Image.FileCount);
        Assert.True(summary.Calendar.TotalBytes > 0);
        Assert.Equal(128, summary.Image.TotalBytes);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task CalendarPresetService_SavesRouteScopedPresetWithoutPinnedWeek()
    {
        var service = new CalendarPresetService(CreateAppStateStore(), TimeProvider.System);
        var filters = new CalendarFilters
        {
            WeekStart = new DateOnly(2026, 5, 11),
            ShowSeries = false,
            ShowMovies = true,
            MovieFilters =
            {
                WatchRegion = "be",
                SelectedSources = ["provider:8"]
            }
        };

        var preset = await service.SaveAsync("Belgian movies", CalendarPageMode.Movies, filters, CancellationToken.None);
        var presets = await service.GetPresetsAsync(CalendarPageMode.Movies, CancellationToken.None);

        var loaded = Assert.Single(presets);
        Assert.Equal(preset.Id, loaded.Id);
        Assert.Equal("Belgian movies", loaded.Name);
        Assert.Equal(DateOnly.MinValue, loaded.Filters.WeekStart);
        Assert.False(loaded.Filters.ShowSeries);
        Assert.True(loaded.Filters.ShowMovies);
        Assert.Equal("BE", loaded.Filters.MovieFilters.WatchRegion);
    }

    [Fact]
    public async Task VisitChangeService_ReturnsSubtleDeltaAgainstPreviousVisit()
    {
        var service = new CalendarVisitChangeService(CreateAppStateStore(), TimeProvider.System);
        var scope = new CalendarVisitScope(
            CalendarPageMode.Series,
            new DateOnly(2026, 5, 11),
            "series-cache-key");

        var first = await service.RecordVisitAsync(scope, ["tv:1", "tv:2"], CancellationToken.None);
        var second = await service.RecordVisitAsync(scope, ["tv:2", "tv:3"], CancellationToken.None);

        Assert.False(first.HasPreviousVisit);
        Assert.True(second.HasPreviousVisit);
        Assert.Equal(1, second.NewCount);
        Assert.Equal(1, second.RemovedCount);
    }

    [Fact]
    public async Task ReleaseUpdateService_DetectsNewerGitHubRelease()
    {
        var handler = new StaticHttpMessageHandler(
            """
            {
              "tag_name": "v1.4.0",
              "html_url": "https://github.com/Belgian-Coder/PremiereCalendar/releases/tag/v1.4.0",
              "published_at": "2026-05-16T10:00:00Z",
              "name": "Premiere Calendar 1.4.0"
            }
            """);
        var service = new ReleaseUpdateService(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") },
            currentVersion: "1.3.0");

        var result = await service.CheckLatestAsync(CancellationToken.None);

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("1.3.0", result.CurrentVersion);
        Assert.Equal("1.4.0", result.LatestVersion);
        Assert.Equal("https://github.com/Belgian-Coder/PremiereCalendar/releases/tag/v1.4.0", result.ReleaseUrl);
    }

    [Fact]
    public void ReleaseUpdateService_ActivatesAsTypedHttpClient()
    {
        var services = new ServiceCollection();
        services.AddHttpClient<ReleaseUpdateService>(client =>
        {
            client.BaseAddress = new Uri("https://api.github.com/");
        });

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<ReleaseUpdateService>());
    }

    [Fact]
    public async Task SettingsBackupService_ExportsAndImportsSettingsAndLocalState()
    {
        var sourceSettingsStore = new InMemoryIntegrationSettingsStore();
        var sourceStateStore = new InMemoryAppStateStore();
        sourceSettingsStore.Settings.Sources.Tmdb.BearerToken = "tmdb-token";
        await sourceStateStore.SetValueAsync("Calendar.Presets", """[{"name":"Preset"}]""", CancellationToken.None);
        await sourceStateStore.SetValueAsync("Diagnostics.BackgroundJobs", """[{"job":"fresh"}]""", CancellationToken.None);
        var sourceBackup = new SettingsBackupService(sourceSettingsStore, sourceStateStore, TimeProvider.System);
        var backupJson = await sourceBackup.ExportAsync(includeSecrets: true, CancellationToken.None);

        var targetSettingsStore = new InMemoryIntegrationSettingsStore();
        var targetStateStore = new InMemoryAppStateStore();
        await targetStateStore.SetValueAsync("Calendar.Obsolete", """{"old":true}""", CancellationToken.None);
        await targetStateStore.SetValueAsync("Diagnostics.Obsolete", """{"old":true}""", CancellationToken.None);
        await targetStateStore.SetValueAsync("Other.State", """{"keep":true}""", CancellationToken.None);
        var targetBackup = new SettingsBackupService(targetSettingsStore, targetStateStore, TimeProvider.System);
        await targetBackup.ImportAsync(backupJson, CancellationToken.None);

        Assert.Equal("tmdb-token", targetSettingsStore.Settings.Sources.Tmdb.BearerToken);
        Assert.Equal(
            """[{"name":"Preset"}]""",
            await targetStateStore.GetValueAsync("Calendar.Presets", CancellationToken.None));
        Assert.Equal(
            """{"old":true}""",
            await targetStateStore.GetValueAsync("Diagnostics.Obsolete", CancellationToken.None));
        Assert.Null(await targetStateStore.GetValueAsync("Calendar.Obsolete", CancellationToken.None));
        Assert.Null(await targetStateStore.GetValueAsync("Diagnostics.BackgroundJobs", CancellationToken.None));
        Assert.Equal("""{"keep":true}""", await targetStateStore.GetValueAsync("Other.State", CancellationToken.None));
    }

    [Fact]
    public async Task SettingsBackupService_RedactsSecretsWhenRequested()
    {
        var settingsStore = new InMemoryIntegrationSettingsStore();
        settingsStore.Settings.Sonarr.ApiKey = "sonarr-secret";
        settingsStore.Settings.Sources.Tmdb.BearerToken = "tmdb-secret";
        settingsStore.Settings.Sources.Simkl.ClientSecret = "simkl-secret";
        var backup = new SettingsBackupService(settingsStore, new InMemoryAppStateStore(), TimeProvider.System);

        var backupJson = await backup.ExportAsync(includeSecrets: false, CancellationToken.None);

        Assert.DoesNotContain("sonarr-secret", backupJson, StringComparison.Ordinal);
        Assert.DoesNotContain("tmdb-secret", backupJson, StringComparison.Ordinal);
        Assert.DoesNotContain("simkl-secret", backupJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AppStateStore_PrefixReadsAreCaseSensitive()
    {
        var store = CreateAppStateStore();

        await store.SetValueAsync("Calendar.Valid", "yes", CancellationToken.None);
        await store.SetValueAsync("calendar.Hidden", "no", CancellationToken.None);

        var values = await store.GetValuesByPrefixAsync("Calendar.", CancellationToken.None);

        Assert.Single(values);
        Assert.Equal("yes", values["Calendar.Valid"]);
    }

    [Fact]
    public async Task CalendarPresetService_PreservesConcurrentSaves()
    {
        var store = new YieldingAppStateStore();
        var service = new CalendarPresetService(store, TimeProvider.System);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(index =>
            service.SaveAsync(
                $"Preset {index:00}",
                CalendarPageMode.Series,
                new CalendarFilters { WeekStart = new DateOnly(2026, 5, 11) },
                CancellationToken.None)));

        var presets = await service.GetPresetsAsync(CalendarPageMode.Series, CancellationToken.None);

        Assert.Equal(20, presets.Count);
        Assert.Equal(
            Enumerable.Range(0, 20).Select(index => $"Preset {index:00}"),
            presets.Select(preset => preset.Name));
    }

    [Theory]
    [InlineData("1.4.0-preview.1", "v1.4.0", true)]
    [InlineData("1.2", "v1.2.0", false)]
    [InlineData("1.4.0+local", "v1.4.0", false)]
    public async Task ReleaseUpdateService_UsesSemanticVersionComparison(
        string currentVersion,
        string latestTag,
        bool expectedUpdate)
    {
        var handler = new StaticHttpMessageHandler(
            $$"""
            {
              "tag_name": "{{latestTag}}",
              "html_url": "https://github.com/Belgian-Coder/PremiereCalendar/releases/tag/{{latestTag}}",
              "published_at": "2026-05-16T10:00:00Z",
              "name": "Premiere Calendar {{latestTag}}"
            }
            """);
        var service = new ReleaseUpdateService(
            new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") },
            currentVersion);

        var result = await service.CheckLatestAsync(CancellationToken.None);

        Assert.Equal(expectedUpdate, result.IsUpdateAvailable);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private SqliteAppStateStore CreateAppStateStore()
    {
        Directory.CreateDirectory(_root);
        return new SqliteAppStateStore(
            Microsoft.Extensions.Options.Options.Create(new AppDatabaseOptions { Path = "data/app-state.db" }),
            new FakeWebHostEnvironment(_root));
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

    private sealed class StaticHttpMessageHandler(string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    private sealed class InMemoryIntegrationSettingsStore : IIntegrationSettingsStore
    {
        public IntegrationSettings Settings { get; private set; } = new();

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

    private sealed class YieldingAppStateStore : IAppStateStore
    {
        private readonly object _sync = new();
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);

        public async Task<string?> GetValueAsync(string key, CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                _values.TryGetValue(key, out var value);
                return value;
            }
        }

        public async Task SetValueAsync(string key, string value, CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                _values[key] = value;
            }
        }

        public async Task DeleteValueAsync(string key, CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                _values.Remove(key);
            }
        }

        public async Task<IReadOnlyDictionary<string, string>> GetValuesByPrefixAsync(string prefix, CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                return _values
                    .Where(entry => entry.Key.StartsWith(prefix, StringComparison.Ordinal))
                    .ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal);
            }
        }
    }
}
