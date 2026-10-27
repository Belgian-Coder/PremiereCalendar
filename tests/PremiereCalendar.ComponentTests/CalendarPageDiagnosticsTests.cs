using System.Runtime.CompilerServices;
using System.Text.Json;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.ComponentTests;

public sealed class CalendarPageDiagnosticsTests
{
    [Fact]
    public async Task FirstNonemptyTimingDoesNotCountAnEmptyProgressNotification()
    {
        using var fixture = new DiagnosticFixture();
        fixture.Service.EmitEmptyFirst = true;
        fixture.Navigation.NavigateTo("/movies?week=2026-05-04");
        var component = fixture.Context.Render<PremiereCalendar.Components.Pages.Calendar>();
        component.WaitForAssertion(() => Assert.Single(fixture.Service.Contexts));
        fixture.Service.ReleaseFirst.TrySetResult();
        var events = await fixture.WaitForEventAsync("calendar.page.finished");
        var first = Assert.Single(events, entry => EventName(entry) == "calendar.page.first_result");
        var nonempty = Assert.Single(events, entry => EventName(entry) == "calendar.page.first_nonempty_result");
        Assert.Equal(0, first.GetProperty("details").GetProperty("count").GetInt32());
        Assert.Equal(1, nonempty.GetProperty("details").GetProperty("count").GetInt32());
        Assert.True(nonempty.GetProperty("eventSequence").GetInt64() > first.GetProperty("eventSequence").GetInt64());
    }

    [Fact]
    public async Task CachedCardsGetFreshnessBeforeBackgroundCompletionAndPersistCorrelatedRenderEvents()
    {
        using var fixture = new DiagnosticFixture();
        fixture.Navigation.NavigateTo("/movies?week=2026-05-04&sort=title");
        var component = fixture.Context.Render<PremiereCalendar.Components.Pages.Calendar>();
        component.WaitForAssertion(() =>
        {
            Assert.Single(component.FindAll("[data-testid='premiere-card']"));
            Assert.Contains("partial", component.Find(".cache-freshness-pill").ClassName);
        });
        Assert.False(fixture.Service.ReleaseFirst.Task.IsCompleted);
        Assert.Single(fixture.Service.Contexts);
        Assert.All(fixture.Metadata.AmbientContexts, context => Assert.Null(context));
        Assert.Null(fixture.Telemetry.CaptureDiagnosticContext());

        fixture.Service.ReleaseFirst.TrySetResult();
        var events = await fixture.WaitForEventAsync("calendar.page.finished");
        var started = Assert.Single(events, entry => EventName(entry) == "calendar.page.started");
        var loadId = LoadId(started);
        Assert.Equal(fixture.Service.Contexts[0]!.LoadId, loadId);
        Assert.All(events.Where(entry => EventName(entry).StartsWith("calendar.page.", StringComparison.Ordinal)
            || EventName(entry) == "test.service.produced"), entry => Assert.Equal(loadId, LoadId(entry)));
        Assert.Contains(events, entry => EventName(entry) == "calendar.page.settings_completed"
            && entry.GetProperty("details").GetProperty("durationMilliseconds").GetDouble() >= 0);
        Assert.Contains(events, entry => EventName(entry) == "calendar.page.cache_metadata_completed"
            && Detail(entry, "reason") == "first_cached_result" && Detail(entry, "outcome") == "applied");
        Assert.Contains(events, entry => EventName(entry) == "calendar.page.first_result"
            && entry.GetProperty("details").GetProperty("fromCache").GetBoolean());
        Assert.Contains(events, entry => EventName(entry) == "calendar.page.result_completed");
        var commits = events.Where(entry => EventName(entry) == "calendar.page.render_completed").ToArray();
        Assert.NotEmpty(commits);
        Assert.All(commits, entry =>
        {
            Assert.Equal("server_render_commit", Detail(entry, "outcome"));
            Assert.Contains(events, request => EventName(request) == "calendar.page.render_requested"
                && request.GetProperty("details").GetProperty("sequence").GetInt64()
                    == entry.GetProperty("details").GetProperty("sequence").GetInt64());
        });
        Assert.Equal("movies", Detail(started, "stage"));
        Assert.Equal("2026-05-04", Detail(started, "weekStart"));
        Assert.NotEqual("redacted", Detail(started, "cacheKey"));
        var raw = fixture.ReadRaw();
        Assert.DoesNotContain("PRIVATE_TITLE", raw);
        Assert.DoesNotContain("SECRET_TOKEN", raw);
        Assert.DoesNotContain("http://", raw);
        Assert.DoesNotContain("sort=title", raw);
        Assert.Single(fixture.Service.Contexts);
    }

    [Fact]
    public async Task LateCanceledRouteKeepsItsOwnDiagnosticIdAndCannotCommitOldCacheMetadata()
    {
        using var fixture = new DiagnosticFixture();
        fixture.Service.IgnoreFirstCancellation = true;
        fixture.Metadata.FirstReadRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Navigation.NavigateTo("/movies?week=2026-05-04");
        var component = fixture.Context.Render<PremiereCalendar.Components.Pages.Calendar>();
        await fixture.Metadata.FirstReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var oldId = fixture.Service.Contexts.Single()!.LoadId;

        fixture.Navigation.NavigateTo("/movies?week=2026-05-11");
        var events = await fixture.WaitForEventAsync("calendar.page.finished", entry => Detail(entry, "weekStart") == "2026-05-11");
        var newId = fixture.Service.Contexts[1]!.LoadId;
        Assert.NotEqual(oldId, newId);
        component.WaitForAssertion(() => Assert.Contains("Next week", Assert.Single(component.FindAll("[data-testid='premiere-card']")).TextContent));
        fixture.Service.ReleaseFirst.TrySetResult();
        fixture.Metadata.FirstReadRelease.TrySetResult();
        events = await fixture.WaitForEventAsync("calendar.page.finished", entry => LoadId(entry) == oldId);
        await fixture.WaitForEventAsync("calendar.page.cache_metadata_completed",
            entry => LoadId(entry) == oldId && Detail(entry, "outcome") == "canceled");
        var oldFinished = Assert.Single(events, entry => EventName(entry) == "calendar.page.finished" && LoadId(entry) == oldId);
        Assert.Equal("canceled", Detail(oldFinished, "outcome"));
        Assert.Equal("2026-05-04", Detail(oldFinished, "weekStart"));
        var newStartedSequence = events.Single(entry => EventName(entry) == "calendar.page.started" && LoadId(entry) == newId)
            .GetProperty("eventSequence").GetInt64();
        Assert.DoesNotContain(events, entry => EventName(entry) == "calendar.page.render_completed" && LoadId(entry) == oldId
            && entry.GetProperty("eventSequence").GetInt64() > newStartedSequence);
        component.WaitForAssertion(() =>
        {
            Assert.Contains("Next week", Assert.Single(component.FindAll("[data-testid='premiere-card']")).TextContent);
            Assert.Contains("complete", component.Find(".cache-freshness-pill").ClassName);
        });
        Assert.Equal(2, fixture.Service.Contexts.Count);
        Assert.All(fixture.Metadata.AmbientContexts, context => Assert.Null(context));
        Assert.Null(fixture.Telemetry.CaptureDiagnosticContext());
    }

    [Fact]
    public async Task BackgroundDeadlinePersistsItsReasonAndRetainedRowCount()
    {
        using var fixture = new DiagnosticFixture();
        fixture.Navigation.NavigateTo("/movies?week=2026-05-04");
        var component = fixture.Context.Render<PremiereCalendar.Components.Pages.Calendar>();
        var events = await fixture.WaitForEventAsync("calendar.page.finished");
        Assert.Contains(events, entry => EventName(entry) == "calendar.page.foreground_handoff"
            && Detail(entry, "reason") == "stale_cache_budget");
        Assert.Contains(events, entry => EventName(entry) == "calendar.page.deadline"
            && Detail(entry, "reason") == "background_budget");
        var finished = Assert.Single(events, entry => EventName(entry) == "calendar.page.finished");
        Assert.Equal("deadline", Detail(finished, "outcome"));
        Assert.Equal(1, finished.GetProperty("details").GetProperty("count").GetInt32());
        Assert.Single(component.FindAll("[data-testid='premiere-card']"));
        Assert.Single(fixture.Service.Contexts);
    }

    [Fact]
    public async Task SupersededSettingsPreconditionCannotLabelOrStartNewRouteProviderWork()
    {
        using var fixture = new DiagnosticFixture();
        fixture.Settings.FirstGetRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Service.ReleaseFirst.TrySetResult();
        fixture.Navigation.NavigateTo("/movies?week=2026-05-04");
        fixture.Context.Render<PremiereCalendar.Components.Pages.Calendar>();
        await fixture.Settings.FirstGetStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.Navigation.NavigateTo("/movies?week=2026-05-11");
        await fixture.WaitForEventAsync("calendar.page.finished", entry => Detail(entry, "weekStart") == "2026-05-11");
        fixture.Settings.FirstGetRelease.TrySetResult();
        var events = await fixture.WaitForEventAsync("calendar.page.finished", entry => Detail(entry, "weekStart") == "2026-05-04");
        var oldFinished = Assert.Single(events, entry => EventName(entry) == "calendar.page.finished" && Detail(entry, "weekStart") == "2026-05-04");
        Assert.Equal("canceled", Detail(oldFinished, "outcome"));
        Assert.Equal("superseded", Detail(oldFinished, "reason"));
        var currentStarted = Assert.Single(events, entry => EventName(entry) == "calendar.page.started" && Detail(entry, "weekStart") == "2026-05-11");
        Assert.Equal(LoadId(currentStarted), Assert.Single(fixture.Service.Contexts)!.LoadId);
    }

    [Fact]
    public async Task ServiceFailurePersistsFixedReasonWithoutProviderExceptionText()
    {
        using var fixture = new DiagnosticFixture();
        fixture.Service.FailFirst = true;
        fixture.Navigation.NavigateTo("/movies?week=2026-05-04");
        var component = fixture.Context.Render<PremiereCalendar.Components.Pages.Calendar>();
        component.WaitForAssertion(() => Assert.Single(component.FindAll("[data-testid='premiere-card']")));
        fixture.Service.ReleaseFirst.TrySetResult();
        var events = await fixture.WaitForEventAsync("calendar.page.finished");
        var finished = Assert.Single(events, entry => EventName(entry) == "calendar.page.finished");
        Assert.Equal("failed", Detail(finished, "outcome"));
        Assert.Equal("service_exception", Detail(finished, "reason"));
        Assert.Single(component.FindAll("[data-testid='premiere-card']"));
        Assert.DoesNotContain("PRIVATE_EXCEPTION", fixture.ReadRaw());
        Assert.Single(fixture.Service.Contexts);
    }

    [Fact]
    public async Task SettingsFailurePersistsPreconditionTimingWithoutStartingProviderLoad()
    {
        using var fixture = new DiagnosticFixture();
        fixture.Settings.GetException = new IOException("PRIVATE_EXCEPTION");
        fixture.Navigation.NavigateTo("/movies?week=2026-05-04");
        fixture.Context.Render<PremiereCalendar.Components.Pages.Calendar>();
        var events = await fixture.WaitForEventAsync("calendar.page.finished");
        Assert.Contains(events, entry => EventName(entry) == "calendar.page.settings_completed" && Detail(entry, "outcome") == "failed");
        var finished = Assert.Single(events, entry => EventName(entry) == "calendar.page.finished");
        Assert.Equal("precondition_failed", Detail(finished, "outcome"));
        Assert.Equal("settings", Detail(finished, "reason"));
        Assert.Empty(fixture.Service.Contexts);
        Assert.DoesNotContain("PRIVATE_EXCEPTION", fixture.ReadRaw());
    }

    private static string EventName(JsonElement entry) => entry.GetProperty("event").GetString()!;
    private static string? LoadId(JsonElement entry) => entry.GetProperty("loadId").GetString();
    private static string? Detail(JsonElement entry, string name)
        => entry.GetProperty("details").TryGetProperty(name, out var value) ? value.GetString() : null;

    private sealed class DiagnosticFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "premiere-page-diagnostics-" + Guid.NewGuid().ToString("N"));
        private readonly PerformanceDiagnosticStore _store;
        public BunitContext Context { get; } = new();
        public PremiereTelemetry Telemetry { get; }
        public RecordingPremiereService Service { get; }
        public RecordingMetadataStore Metadata { get; }
        public FakeIntegrationSettingsStore Settings { get; } = new()
        {
            Settings = new IntegrationSettings { Sources = new SourceIntegrationSettings
                { Tmdb = new TmdbSourceSettings { BearerToken = "SECRET_TOKEN" } } }
        };
        public NavigationManager Navigation => Context.Services.GetRequiredService<NavigationManager>();

        public DiagnosticFixture()
        {
            Directory.CreateDirectory(_directory);
            _store = new PerformanceDiagnosticStore(Microsoft.Extensions.Options.Options.Create(new TelemetryOptions
            {
                PerformanceDiagnosticsDirectory = _directory, Enabled = false, TraceSampleRatio = 0
            }), new FakeEnvironment { ContentRootPath = _directory });
            Telemetry = new PremiereTelemetry(_store);
            Service = new RecordingPremiereService(Telemetry);
            Metadata = new RecordingMetadataStore(Telemetry);
            Context.JSInterop.Mode = JSRuntimeMode.Loose;
            Context.Services.AddLogging();
            Context.Services.AddSingleton(Telemetry);
            Context.Services.AddSingleton<IPremiereService>(Service);
            Context.Services.AddSingleton<ICalendarCacheMaintenance>(Metadata);
            Context.Services.AddSingleton<IAdjacentWeekPrefetcher, FakeAdjacentWeekPrefetcher>();
            Context.Services.AddSingleton<CalendarPageCoordinator>();
            Context.Services.AddSingleton<ICalendarFilterUsageStore, FakeCalendarFilterUsageStore>();
            Context.Services.AddSingleton<IFilterCatalogService, FakeFilterCatalogService>();
            Context.Services.AddSingleton<IIntegrationSettingsStore>(Settings);
            Context.Services.AddSingleton<IArrIntegrationService, FakeArrIntegrationService>();
            Context.Services.AddSingleton<IViewSyncService, DisabledViewSyncService>();
            Context.Services.AddSingleton<IAppStateStore, InMemoryAppStateStore>();
            Context.Services.AddSingleton(TimeProvider.System);
            Context.Services.AddSingleton<CalendarPresetService>();
            Context.Services.AddSingleton<CalendarVisitChangeService>();
            Context.Services.Configure<CalendarLoadOptions>(options =>
            {
                options.ForegroundLoadBudgetSeconds = 4;
                options.StaleCacheForegroundBudgetSeconds = 1;
                options.BackgroundCompletionBudgetSeconds = 1;
            });
        }

        public async Task<JsonElement[]> WaitForEventAsync(string name, Func<JsonElement, bool>? predicate = null)
        {
            var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            do
            {
                var events = ReadEvents();
                if (events.Any(entry => EventName(entry) == name && (predicate is null || predicate(entry)))) return events;
                await Task.Delay(25);
            } while (DateTimeOffset.UtcNow < deadline);
            throw new TimeoutException("Missing persisted diagnostic event: " + name);
        }

        public JsonElement[] ReadEvents() => ReadRaw().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();
        public string ReadRaw()
        {
            Telemetry.FlushDiagnostics();
            return string.Join("\n", Directory.GetFiles(_directory, "*.ndjson").Select(path =>
            {
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(file);
                return reader.ReadToEnd();
            }));
        }
        public void Dispose()
        {
            Context.Dispose();
            _store.Dispose();
            Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class RecordingPremiereService(PremiereTelemetry telemetry) : IPremiereService
    {
        public List<PerformanceDiagnosticContext?> Contexts { get; } = [];
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool IgnoreFirstCancellation { get; set; }
        public bool FailFirst { get; set; }
        public bool EmitEmptyFirst { get; set; }
        public IAsyncEnumerable<PremiereLoadProgress> StreamPremieresAsync(DateOnly start, DateOnly end, bool forceRefresh = false,
            CalendarFilters? filters = null, CancellationToken cancellationToken = default)
        {
            var context = telemetry.CaptureDiagnosticContext();
            Contexts.Add(context);
            return telemetry.WithDiagnosticContext(Produce(start, Contexts.Count == 1, cancellationToken), context, cancellationToken);
        }
        private async IAsyncEnumerable<PremiereLoadProgress> Produce(DateOnly start, bool first,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            var item = new PremiereItem { CanonicalId = "movie:1", TmdbId = 1, Title = first ? "PRIVATE_TITLE" : "Next week",
                MediaType = PremiereMediaType.Movie, Type = PremiereItemType.MovieFirstRelease, PremiereDate = start, OriginalLanguage = "en" };
            if (first)
            {
                if (EmitEmptyFirst) yield return new PremiereLoadProgress("Started", 0, 0, []);
                telemetry.RecordDiagnostic("test.service.produced");
                yield return new PremiereLoadProgress("Expired week cache", 1, 1, [item], FromCache: true) { IsStaleCache = true };
                if (IgnoreFirstCancellation) await ReleaseFirst.Task;
                else await ReleaseFirst.Task.WaitAsync(cancellationToken);
                if (FailFirst) throw new IOException("PRIVATE_EXCEPTION");
            }
            telemetry.RecordDiagnostic("test.service.produced");
            yield return new PremiereLoadProgress("Complete", 1, 1, [item], IsFinal: true);
        }
        public Task<IReadOnlyList<PremiereItem>> GetPremieresAsync(DateOnly start, DateOnly end, CancellationToken cancellationToken,
            bool forceRefresh = false, IProgress<PremiereLoadProgress>? progress = null, CalendarFilters? filters = null)
            => throw new NotSupportedException();
    }

    private sealed class RecordingMetadataStore(PremiereTelemetry telemetry) : ICalendarCacheMaintenance
    {
        public TaskCompletionSource? FirstReadRelease { get; set; }
        public TaskCompletionSource FirstReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<PerformanceDiagnosticContext?> AmbientContexts { get; } = [];
        public async Task<CalendarCacheMetadata?> GetWeekMetadataAsync(DateOnly start, DateOnly end, string cacheKey, CancellationToken cancellationToken)
        {
            AmbientContexts.Add(telemetry.CaptureDiagnosticContext());
            if (AmbientContexts.Count == 1)
            {
                FirstReadStarted.TrySetResult();
                if (FirstReadRelease is { } release) await release.Task;
            }
            return new CalendarCacheMetadata(DateTimeOffset.UtcNow, 1, 1,
                start == new DateOnly(2026, 5, 4) ? CalendarCacheCompleteness.Partial : CalendarCacheCompleteness.Complete);
        }
        public Task<int> CleanupAsync(DateTimeOffset nowUtc, TimeSpan retention, CancellationToken cancellationToken) => Task.FromResult(0);
    }

    private sealed class DisabledViewSyncService : IViewSyncService
    {
        public event EventHandler<ViewSyncStateChangedEventArgs>? StateChanged { add { } remove { } }
        public Task<ViewSyncOverview> GetOverviewAsync(string deviceId, CancellationToken cancellationToken)
            => Task.FromResult(new ViewSyncOverview(new(deviceId, "Test", false, null, DateTimeOffset.UtcNow), [], [], null));
        public Task<ViewSyncOverview> SaveDeviceAsync(string deviceId, string displayName, bool syncEnabled, string? groupId, CancellationToken cancellationToken)
            => GetOverviewAsync(deviceId, cancellationToken);
        public Task<ViewSyncGroup> CreateGroupAsync(string name, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ViewSyncOverview> UngroupDeviceAsync(string deviceId, CancellationToken cancellationToken) => GetOverviewAsync(deviceId, cancellationToken);
        public Task<ViewSyncPublishResult> PublishUrlAsync(string deviceId, string relativeUrl, CancellationToken cancellationToken)
            => Task.FromResult(new ViewSyncPublishResult(false, null));
        public Task<ViewSyncGroupState?> GetLatestStateForDeviceAsync(string deviceId, string? routeKey, CancellationToken cancellationToken)
            => Task.FromResult<ViewSyncGroupState?>(null);
        public Task<ViewSyncGroupState?> GetLatestStateForDeviceAsync(string deviceId, CancellationToken cancellationToken)
            => Task.FromResult<ViewSyncGroupState?>(null);
    }

    private sealed class FakeEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PremiereCalendar.ComponentTests";
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = "";
        public string WebRootPath { get; set; } = "";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class FakeAdjacentWeekPrefetcher : IAdjacentWeekPrefetcher
    {
        public List<DateOnly> WeekStarts { get; } = [];
        public TaskCompletionSource Prefetched { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void PrefetchAdjacentWeeks(DateOnly weekStart, CalendarFilters? filters = null)
        {
            WeekStarts.Add(weekStart);
            Prefetched.TrySetResult();
        }
    }

    private sealed class FakeCalendarFilterUsageStore : ICalendarFilterUsageStore
    {
        public TaskCompletionSource? RecordRelease { get; init; }
        public TaskCompletionSource RecordStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken RecordCancellation { get; private set; }

        public async Task RecordUseAsync(
            CalendarPageMode pageMode,
            CalendarFilters filters,
            int itemCount,
            DateTimeOffset usedAtUtc,
            CancellationToken cancellationToken)
        {
            RecordCancellation = cancellationToken;
            RecordStarted.TrySetResult();
            if (RecordRelease is { } release)
            {
                await release.Task.WaitAsync(cancellationToken);
            }
        }

        public Task<CalendarFilterUsageProfile?> GetProfileAsync(string profileKey, CancellationToken cancellationToken)
        {
            return Task.FromResult<CalendarFilterUsageProfile?>(null);
        }

        public Task<IReadOnlyList<CalendarFilterUsageProfile>> GetTopProfilesAsync(
            int count,
            DateTimeOffset nowUtc,
            TimeSpan retention,
            CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<CalendarFilterUsageProfile>>([]);
        }

        public Task MarkWarmedAsync(
            string profileKey,
            CalendarPageMode pageMode,
            CalendarFilters filters,
            bool isDefault,
            int itemCount,
            DateTimeOffset warmedAtUtc,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task MarkWarmFailedAsync(
            string profileKey,
            CalendarPageMode pageMode,
            CalendarFilters filters,
            bool isDefault,
            string failure,
            DateTimeOffset failedAtUtc,
            CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public Task<int> CleanupAsync(
            DateTimeOffset cutoffUtc,
            IReadOnlySet<string> retainedProfileKeys,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(0);
        }
    }

    private sealed class FakeFilterCatalogService : IFilterCatalogService
    {
        public Task<FilterCatalog> GetCatalogAsync(CancellationToken cancellationToken, bool forceRefresh = false)
        {
            return Task.FromResult(new FilterCatalog
            {
                SeriesGenres = [new("18", "Drama"), new("9648", "Mystery")],
                MovieGenres = [new("28", "Action"), new("35", "Comedy")],
                SeriesProviders = [new("HBO Max", "HBO Max")],
                MovieProviders = [new("Apple TV", "Apple TV")],
                Languages = [new("en", "English (EN)"), new("nl", "Dutch (NL)"), new("fr", "French (FR)")],
                Countries = [new("BE", "Belgium (BE)"), new("US", "United States of America (US)"), new("AU", "Australia (AU)")],
                MovieCertifications = [new("US:PG-13", "PG-13")]
            });
        }
    }

    private sealed class FakeIntegrationSettingsStore : IIntegrationSettingsStore
    {
        public Exception? GetException { get; set; }
        public TaskCompletionSource? FirstGetRelease { get; set; }
        public TaskCompletionSource FirstGetStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _getCalls;
        public IntegrationSettings Settings { get; set; } = new();

        public async Task<IntegrationSettings> GetAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _getCalls) == 1)
            {
                FirstGetStarted.TrySetResult();
                if (FirstGetRelease is { } release) await release.Task;
            }
            if (GetException is { } exception) throw exception;
            return Settings;
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
            return Task.FromResult(new ArrConnectionOptions([], []));
        }

        public Task<ArrConnectionOptions> GetRadarrOptionsAsync(
            RadarrIntegrationSettings settings,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ArrConnectionOptions([], []));
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
}
