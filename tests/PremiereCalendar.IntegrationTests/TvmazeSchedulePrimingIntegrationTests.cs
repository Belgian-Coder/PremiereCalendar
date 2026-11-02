using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using PremiereCalendar.IntegrationTests.Support;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed class TvmazeSchedulePrimingIntegrationTests
{
    private static readonly DateOnly ScheduleDate = new(2026, 11, 2);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshBroadcastAndEmbeddedWebSchedulesSeedBothStrongAliasesWithFullMetadata(bool web)
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var expected = Show(82);
        var handler = new StubHttpMessageHandler(_ => Schedule(expected, web));
        var client = Client(handler, cache);

        var episodes = await client.GetScheduleAsync(ScheduleDate, "US", web, CancellationToken.None);
        var tvdb = await client.LookupShowAsync(81189, null, CancellationToken.None);
        var imdb = await Client(handler, cache).LookupShowAsync(null, "tt0944947", CancellationToken.None);

        var embedded = web ? episodes[0].Embedded!.Show : episodes[0].Show;
        Assert.Equal(expected, embedded);
        Assert.Same(embedded, tvdb);
        Assert.Same(embedded, imdb);
        Assert.Single(handler.Requests);
        Assert.Equal(web ? "/schedule/web" : "/schedule", handler.Requests[0].Uri.AbsolutePath);
    }

    [Fact]
    public async Task ExistingLookupFromAnotherClientIsRetainedAndForcedLookupStillFetches()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var fresh = Show(82) with { Summary = "Fresh lookup metadata" };
        var older = Show(82) with { Summary = "Schedule metadata" };
        var calls = 0;
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath.StartsWith("/schedule", StringComparison.Ordinal)
            ? Schedule(older, false) : StubHttpMessageHandler.Json(JsonSerializer.Serialize(fresh with { Runtime = 40 + ++calls })));
        var original = await Client(handler, cache).LookupShowAsync(81189, null, CancellationToken.None);
        await Client(handler, cache).GetScheduleAsync(ScheduleDate, "US", false, CancellationToken.None);

        Assert.Same(original, await Client(handler, cache).LookupShowAsync(81189, null, CancellationToken.None));
        var forced = await Client(handler, cache).LookupShowAsync(81189, null, CancellationToken.None, true);
        Assert.Equal(42, forced!.Runtime);
        Assert.Same(forced, await Client(handler, cache).LookupShowAsync(81189, null, CancellationToken.None));
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task PrimedLookupCanBeForcedAndTitleSearchIsNeverPrimed()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var scheduled = Show(82);
        var fresh = scheduled with { Summary = "Fetched directly", Runtime = 51 };
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/schedule" => Schedule(scheduled, false),
            "/search/shows" => StubHttpMessageHandler.Json(JsonSerializer.Serialize(new[] { new TvmazeSearchResult { Show = fresh } })),
            _ => StubHttpMessageHandler.Json(JsonSerializer.Serialize(fresh))
        });
        var client = Client(handler, cache);
        await client.GetScheduleAsync(ScheduleDate, "US", false, CancellationToken.None);
        Assert.Equal(scheduled, await client.LookupShowAsync(81189, null, CancellationToken.None));
        Assert.Equal(fresh, await client.LookupShowAsync(81189, null, CancellationToken.None, true));
        Assert.Equal(fresh, await client.SearchShowByNameAsync(scheduled.Name!, CancellationToken.None));
        Assert.Equal(["/schedule", "/lookup/shows", "/search/shows"], handler.Requests.Select(item => item.Uri.AbsolutePath));
    }

    [Fact]
    public async Task ConflictingShowIdsCannotSeedTheirSharedAliasButKeepUnambiguousAliases()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var first = Show(82);
        var second = Show(83) with { Externals = new() { TheTvdb = 81189, Imdb = "tt1234567" } };
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath == "/schedule"
            ? StubHttpMessageHandler.Json(JsonSerializer.Serialize(new[] { new TvmazeScheduleEpisode { Show = first }, new TvmazeScheduleEpisode { Show = second } }))
            : StubHttpMessageHandler.Json(JsonSerializer.Serialize(first)));
        var client = Client(handler, cache);
        await client.GetScheduleAsync(ScheduleDate, "US", false, CancellationToken.None);

        Assert.False(cache.TryGetValue("tvmaze:lookup:lookup/shows?thetvdb=81189", out _));
        Assert.Equal(first, await client.LookupShowAsync(null, "tt0944947", CancellationToken.None));
        Assert.Equal(second, await client.LookupShowAsync(null, "tt1234567", CancellationToken.None));
        Assert.Single(handler.Requests);
        await client.LookupShowAsync(81189, null, CancellationToken.None);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(0, "Valid", 81189, "tt0944947")]
    [InlineData(-1, "Valid", 81189, "tt0944947")]
    [InlineData(82, " ", 81189, "tt0944947")]
    [InlineData(82, "Valid", -1, "wrong")]
    [InlineData(82, "Valid", 0, "tt0000000")]
    public async Task InvalidShowsAndExternalIdsDoNotSeedLookups(int id, string name, int tvdb, string imdb)
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var invalid = Show(id) with { Name = name, Externals = new() { TheTvdb = tvdb, Imdb = imdb } };
        var handler = new StubHttpMessageHandler(_ => Schedule(invalid, false));
        await Client(handler, cache).GetScheduleAsync(ScheduleDate, "US", false, CancellationToken.None);
        Assert.False(cache.TryGetValue("tvmaze:lookup:lookup/shows?thetvdb=" + tvdb, out _));
        Assert.False(cache.TryGetValue("tvmaze:lookup:lookup/shows?imdb=" + imdb, out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedOrPartiallyMalformedScheduleDoesNotSeedAnyLookup(bool malformed)
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var handler = new StubHttpMessageHandler(_ => malformed
            ? StubHttpMessageHandler.Json("[{\"show\":" + JsonSerializer.Serialize(Show(82)) + "},invalid]")
            : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        Assert.Empty(await Client(handler, cache).GetScheduleAsync(ScheduleDate, "US", false, CancellationToken.None));
        Assert.False(cache.TryGetValue("tvmaze:lookup:lookup/shows?thetvdb=81189", out _));
        Assert.False(cache.TryGetValue("tvmaze:lookup:lookup/shows?imdb=tt0944947", out _));
    }

    [Fact]
    public async Task ScheduleCacheHitDoesNotRecreateOrExtendPrimedLookupAliases()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var handler = new StubHttpMessageHandler(_ => Schedule(Show(82), false));
        var client = Client(handler, cache);
        await client.GetScheduleAsync(ScheduleDate, "US", false, CancellationToken.None);
        cache.Remove("tvmaze:lookup:lookup/shows?thetvdb=81189");
        cache.Remove("tvmaze:lookup:lookup/shows?imdb=tt0944947");

        await client.GetScheduleAsync(ScheduleDate, "US", false, CancellationToken.None);
        Assert.Single(handler.Requests);
        Assert.False(cache.TryGetValue("tvmaze:lookup:lookup/shows?thetvdb=81189", out _));
        Assert.False(cache.TryGetValue("tvmaze:lookup:lookup/shows?imdb=tt0944947", out _));
    }

    [Fact]
    public async Task PrimedAliasesExpireWithTheOneHourScheduleFreshness()
    {
        var clock = new TestClock();
#pragma warning disable CS0618
        using var cache = new MemoryCache(new MemoryCacheOptions { Clock = clock });
#pragma warning restore CS0618
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath == "/schedule"
            ? Schedule(Show(82), false) : StubHttpMessageHandler.Json(JsonSerializer.Serialize(Show(82))));
        var client = Client(handler, cache);
        await client.GetScheduleAsync(ScheduleDate, "US", false, CancellationToken.None);
        Assert.NotNull(await client.LookupShowAsync(81189, null, CancellationToken.None));
        Assert.Single(handler.Requests);

        clock.UtcNow = clock.UtcNow.AddMinutes(61);
        Assert.NotNull(await client.LookupShowAsync(81189, null, CancellationToken.None));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentLookupPublicationCannotBeReplacedByAnOlderScheduleSeed(bool force)
    {
        using var cache = new PausingCache();
        var returnedLookupResponse = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fresh = Show(82) with { Summary = "Fresh lookup metadata" };
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/schedule") return Schedule(Show(82), false);
            returnedLookupResponse.TrySetResult();
            return StubHttpMessageHandler.Json(JsonSerializer.Serialize(fresh));
        });
        var schedule = Task.Run(() => Client(handler, cache).GetScheduleAsync(ScheduleDate, "US", false, CancellationToken.None));
        Assert.True(cache.SeedCheckEntered.Wait(TimeSpan.FromSeconds(5)));
        var lookup = Task.Run(() => Client(handler, cache).LookupShowAsync(81189, null, CancellationToken.None, force));
        try
        {
            await returnedLookupResponse.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Give the fresh response a chance to publish while the seed is paused
            // after observing the missing alias. Its write must share the seed gate.
            await Task.WhenAny(lookup, Task.Delay(100));
        }
        finally { cache.ReleaseSeedCheck.Set(); }
        await schedule.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(fresh, await lookup.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(fresh, await Client(handler, cache).LookupShowAsync(81189, null, CancellationToken.None));
    }

    [Fact]
    public async Task SchedulePrimingAndTotalTimingsPersistCountsContextAndCacheHitsWithoutMetadata()
    {
        var directory = Path.Combine(Path.GetTempPath(), "premiere-tvmaze-prime", Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new PerformanceDiagnosticStore(new(), directory);
            var telemetry = new PremiereTelemetry(store);
            var context = telemetry.CreateDiagnosticContext(jobId: Guid.NewGuid().ToString("N"));
            using var correlation = telemetry.UseDiagnosticContext(context);
            using var cache = new MemoryCache(new MemoryCacheOptions());
            cache.Set("tvmaze:lookup:lookup/shows?thetvdb=81189", Show(82));
            var handler = new StubHttpMessageHandler(_ => Schedule(Show(82), false));
            var client = Client(handler, cache, telemetry);
            await client.GetScheduleAsync(ScheduleDate, "US", false, CancellationToken.None);
            await client.GetScheduleAsync(ScheduleDate, "US", false, CancellationToken.None);
            telemetry.FlushDiagnostics();
            var events = Directory.GetFiles(Path.Combine(directory, "App_Data", "logs", "performance"), "performance-*.ndjson")
                .SelectMany(path => File.ReadAllLines(path)).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
            var prime = Assert.Single(events, item => item.GetProperty("event").GetString() == "provider.tvmaze.schedule_prime.completed");
            Assert.Equal(context.JobId, prime.GetProperty("jobId").GetString());
            Assert.Equal(1, prime.GetProperty("details").GetProperty("count").GetInt32());
            Assert.Equal(2, prime.GetProperty("details").GetProperty("total").GetInt32());
            Assert.True(prime.GetProperty("details").GetProperty("durationMilliseconds").GetDouble() >= 0);
            Assert.Contains(events, item => item.GetProperty("event").GetString() == "provider.tvmaze.schedule.completed"
                && item.GetProperty("details").GetProperty("cache").GetString() == "hit");
            Assert.All(events, item => Assert.DoesNotContain("Private Show", item.GetRawText()));
            Assert.All(events, item => Assert.DoesNotContain("81189", item.GetRawText()));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    private static TvmazeClient Client(HttpMessageHandler handler, IMemoryCache cache, PremiereTelemetry? telemetry = null)
        => new(new HttpClient(handler) { BaseAddress = new Uri("https://api.tvmaze.com/") }, cache,
            Microsoft.Extensions.Options.Options.Create(new TvmazeOptions { Enabled = true, EnableScheduleDiscovery = true }),
            telemetry: telemetry);

    private static HttpResponseMessage Schedule(TvmazeShow show, bool web)
        => StubHttpMessageHandler.Json(JsonSerializer.Serialize(new[] { web
            ? new TvmazeScheduleEpisode { Embedded = new() { Show = show } }
            : new TvmazeScheduleEpisode { Show = show } }));

    private static TvmazeShow Show(int id) => new()
    {
        Id = id, Name = "Private Show", Url = "https://www.tvmaze.com/shows/82/private",
        Language = "English", Premiered = "2026-01-02", Summary = "Complete schedule metadata", Runtime = 42,
        AverageRuntime = 43, OfficialSite = "https://example.test/official", Externals = new() { TheTvdb = 81189, Imdb = "tt0944947" },
        Image = new() { Medium = "https://example.test/m.jpg", Original = "https://example.test/o.jpg" },
        Network = new() { Name = "Network" }, WebChannel = new() { Name = "Web" }, Rating = new() { Average = 8.4 }
    };

    private sealed class TestClock : Microsoft.Extensions.Internal.ISystemClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
    }

    private sealed class PausingCache : IMemoryCache
    {
        private readonly MemoryCache _inner = new(new MemoryCacheOptions());
        private int _pause = 1;
        public ManualResetEventSlim SeedCheckEntered { get; } = new();
        public ManualResetEventSlim ReleaseSeedCheck { get; } = new();
        public ICacheEntry CreateEntry(object key) => _inner.CreateEntry(key);
        public void Remove(object key) => _inner.Remove(key);
        public bool TryGetValue(object key, out object? value)
        {
            var found = _inner.TryGetValue(key, out value);
            if (key is "tvmaze:lookup:lookup/shows?thetvdb=81189" && !found && Interlocked.Exchange(ref _pause, 0) == 1)
            {
                SeedCheckEntered.Set();
                if (!ReleaseSeedCheck.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("Seed fixture was not released.");
            }
            return found;
        }
        public void Dispose()
        {
            ReleaseSeedCheck.Set();
            _inner.Dispose();
            SeedCheckEntered.Dispose();
            ReleaseSeedCheck.Dispose();
        }
    }
}
