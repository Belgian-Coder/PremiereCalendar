using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class PremiereServiceTests
{
    [Theory]
    [InlineData("none")]
    [InlineData("historical")]
    [InlineData("pending")]
    public async Task FailedCanonicalRevalidationPreservesPriorCheckedDateOnlyWhenInvalidationWasAlreadyCovered(string invalidation)
    {
        var date = new DateOnly(2026, 11, 2);
        var canonicalDate = date.AddDays(1);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var identity = new ItemEnrichmentIdentity(PremiereMediaType.Series, 901);
        if (invalidation == "historical")
        {
            await store.ReadManyAsync(coordinator.Partition, [identity], true, CancellationToken.None);
            await store.InvalidateTmdbAsync(PremiereMediaType.Series, [901], clock.GetUtcNow().AddHours(-1), CancellationToken.None);
        }
        FakeTmdbClient Tmdb(int runtime)
        {
            var tmdb = new FakeTmdbClient { TvItems = [new TmdbTvDiscoverItem
                { Id = 901, Name = "Canonical fixture", FirstAirDate = date.ToString("yyyy-MM-dd") }] };
            tmdb.TvDetailsById[901] = new TmdbDetailsWithExtras { Id = 901, EpisodeRunTime = [runtime] };
            return tmdb;
        }
        var first = Tmdb(47);
        first.SeasonOneEpisodeOneDatesById[901] = canonicalDate;
        await CreateService(first, itemEnrichmentCache: coordinator).GetPremieresAsync(date, date.AddDays(6), CancellationToken.None,
            filters: new CalendarFilters { ShowMovies = false });
        var before = Assert.Single(store.Records.Values);
        Assert.Equal(canonicalDate, JsonSerializer.Deserialize<ItemEnrichmentMetadata>(before.MetadataJson!)?.FirstEpisodeDate);
        clock.Advance(TimeSpan.FromDays(8));
        if (invalidation == "pending")
            await store.InvalidateTmdbAsync(PremiereMediaType.Series, [901], clock.GetUtcNow(), CancellationToken.None);
        var failingSeason = Tmdb(58);

        await CreateService(failingSeason, itemEnrichmentCache: coordinator).GetPremieresAsync(date, date.AddDays(6), CancellationToken.None,
            filters: new CalendarFilters { ShowMovies = false });

        var after = Assert.Single(store.Records.Values);
        var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(after.MetadataJson!)!;
        Assert.Equal(58, metadata.RuntimeMinutes);
        Assert.Equal(clock.GetUtcNow(), after.MetadataFetchedUtc);
        Assert.Equal(invalidation != "pending", metadata.FirstEpisodeDateChecked);
        Assert.Equal(invalidation == "pending" ? (DateOnly?)null : canonicalDate, metadata.FirstEpisodeDate);
        Assert.Single(failingSeason.TvSeasonDetailCalls);
    }

    [Fact]
    public async Task CanonicalValidationSharesHttpRequestAfterFirstCallerCancelsAndAcrossVoteRevision()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var item = CanonicalSeriesItem(date, clock);
        await coordinator.ImportAsync([item], CancellationToken.None);
        var identity = new ItemEnrichmentIdentity(PremiereMediaType.Series, 901);
        var session = await coordinator.CreateSessionAsync(CancellationToken.None);
        await session.PrepareAsync([identity], null, CancellationToken.None);
        using var handler = new CanonicalValidationHttpHandler(date);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://tmdb.invalid/3/") };
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var service = CanonicalHttpService(http, memory, coordinator);
        using var scope = CanonicalSessionScope(service, session);
        using var firstCaller = new CancellationTokenSource();

        var first = InvokeCanonicalValidation(service, firstCaller.Token);
        await handler.SeasonStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = InvokeCanonicalValidation(service, CancellationToken.None);
        firstCaller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(handler.SeasonToken.IsCancellationRequested);
        handler.ReleaseSeason.TrySetResult();
        Assert.Equal(date, await second);

        // Persisting the canonical date and a newer vote observation changes the
        // metadata revision, but is still the same validation generation.
        await session.GetAsync(identity, false, (_, _, _, _) => Task.FromResult(new ItemEnrichmentRefresh(null, null)),
            CancellationToken.None, ratingsInputs: item with { TmdbScore = 8, TmdbVoteCount = 200 });
        await session.FlushAsync(CancellationToken.None);
        Assert.Equal(date, await InvokeCanonicalValidation(service, CancellationToken.None));
        Assert.Single(handler.SeasonPaths);
        Assert.True(JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!)?.FirstEpisodeDateChecked);
    }

    [Fact]
    public async Task FailedCanonicalValidationStaysUncheckedAfterVoteRevisionAndMetadataRefresh()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var item = CanonicalSeriesItem(date, clock);
        await coordinator.ImportAsync([item], CancellationToken.None);
        var identity = new ItemEnrichmentIdentity(PremiereMediaType.Series, 901);
        var session = await coordinator.CreateSessionAsync(CancellationToken.None);
        await session.PrepareAsync([identity], null, CancellationToken.None);
        var tmdb = new FakeTmdbClient();
        tmdb.TvDetailsById[901] = new TmdbDetailsWithExtras { Id = 901, Runtime = 47 };
        var service = CreateService(tmdb, itemEnrichmentCache: coordinator);
        using var scope = CanonicalSessionScope(service, session);
        Assert.Null(await InvokeCanonicalValidation(service, CancellationToken.None));

        await session.GetAsync(identity, false, (_, _, _, _) => Task.FromResult(new ItemEnrichmentRefresh(null, null)),
            CancellationToken.None, ratingsInputs: item with { TmdbScore = 8, TmdbVoteCount = 200 });
        await session.FlushAsync(CancellationToken.None);
        Assert.Single(session.FirstEpisodeDates);
        var mapped = await InvokeCanonicalSeriesMapping(service, new TmdbTvDiscoverItem
        {
            Id = 901, Name = item.Title, FirstAirDate = date.ToString("yyyy-MM-dd")
        }, date);
        Assert.NotNull(mapped);
        await session.FlushAsync(CancellationToken.None);

        var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Assert.Single(store.Records.Values).MetadataJson!);
        Assert.False(metadata?.FirstEpisodeDateChecked);
        Assert.Null(metadata?.FirstEpisodeDate);
        Assert.Single(tmdb.TvSeasonDetailCalls);
        // Calendar semantics can describe a fallback date; they are not evidence
        // that a provider validation actually succeeded.
        Assert.False(ItemEnrichmentMetadata.FromItem(item with
        {
            DateSemantics = new(date, PremiereDateSourceKind.TmdbSeasonOneEpisodeOne, PremiereDataConfidence.High, "Fixture")
        }).FirstEpisodeDateChecked);
    }

    [Fact]
    public async Task HeldCanonicalValidationCannotPublishCheckedDateIntoNewGeneration()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        await coordinator.ImportAsync([CanonicalSeriesItem(date, clock)], CancellationToken.None);
        var identity = new ItemEnrichmentIdentity(PremiereMediaType.Series, 901);
        var session = await coordinator.CreateSessionAsync(CancellationToken.None);
        await session.PrepareAsync([identity], null, CancellationToken.None);
        using var handler = new CanonicalValidationHttpHandler(date);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://tmdb.invalid/3/") };
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var service = CanonicalHttpService(http, memory, coordinator);
        using var scope = CanonicalSessionScope(service, session);
        var validation = InvokeCanonicalValidation(service, CancellationToken.None);
        await handler.SeasonStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromMinutes(1));
        await store.InvalidateTmdbAsync(PremiereMediaType.Series, [901], clock.GetUtcNow(), CancellationToken.None);
        await session.ReconcileAsync([], includeConsumed: true, CancellationToken.None);
        handler.ReleaseSeason.TrySetResult();
        Assert.Equal(date, await validation);

        Assert.Equal(1, session.Find(identity)?.Record.Generation);
        Assert.False(session.Find(identity)?.Metadata?.FirstEpisodeDateChecked);
        Assert.False(session.HasPendingWrites);
        Assert.Empty(session.FirstEpisodeDates);
    }

    [Fact]
    public async Task InvalidationWhileSeasonValidationIsHeldPreventsCompleteSeriesCalendarPublication()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var cache = new FakeCalendarCache();
        using var handler = new CanonicalValidationHttpHandler(date);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://tmdb.invalid/3/") };
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var service = CanonicalHttpService(http, memory, coordinator, cache);
        var loading = CollectAsync();
        await handler.SeasonStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, await store.InvalidateTmdbAsync(PremiereMediaType.Series, [901], clock.GetUtcNow(), CancellationToken.None));
        handler.ReleaseSeason.TrySetResult();
        var final = Assert.Single(await loading, update => update.IsFinal);

        Assert.True(final.HasSourceErrors);
        Assert.Empty(cache.SetCalls);

        async Task<List<PremiereLoadProgress>> CollectAsync()
        {
            var updates = new List<PremiereLoadProgress>();
            await foreach (var update in service.StreamCoreAsync(date, date, filters: new CalendarFilters { ShowMovies = false }))
                updates.Add(update);
            return updates;
        }
    }

    [Fact]
    public async Task FaultedCanonicalValidationFlightIsRemovedAndCanRetry()
    {
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var session = await coordinator.CreateSessionAsync(CancellationToken.None);
        await session.PrepareAsync([new ItemEnrichmentIdentity(PremiereMediaType.Series, 901)], null, CancellationToken.None);
        using var handler = new CanonicalValidationHttpHandler(date) { FailFirstSeason = true };
        handler.ReleaseSeason.TrySetResult();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://tmdb.invalid/3/") };
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var service = CanonicalHttpService(http, memory, coordinator);
        using var scope = CanonicalSessionScope(service, session);

        await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeCanonicalValidation(service, CancellationToken.None));
        Assert.Empty(session.FirstEpisodeDates);
        Assert.Equal(date, await InvokeCanonicalValidation(service, CancellationToken.None));
        Assert.Equal(2, handler.SeasonPaths.Count);
    }

    private static PremiereItem CanonicalSeriesItem(DateOnly date, ItemCacheClock clock) => new()
    {
        CanonicalId = "series:901", Type = PremiereItemType.SeriesPremiere, MediaType = PremiereMediaType.Series,
        TmdbId = 901, Title = "Canonical fixture", PremiereDate = date, RuntimeMinutes = 47,
        TmdbScore = 7, TmdbVoteCount = 100, LastUpdatedUtc = clock.GetUtcNow().AddHours(-2)
    };

    private static PremiereService CanonicalHttpService(HttpClient http, IMemoryCache memory,
        ItemEnrichmentCacheCoordinator coordinator, ICalendarCache? calendar = null)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new TmdbOptions { BearerToken = "fixture", SourceRegions = [] });
        var tmdb = new TmdbClient(http, memory, options, NullLogger<TmdbClient>.Instance);
        return new PremiereService(tmdb, new FakeOmdbClient(), new FakeTvmazeClient(), new FakeWatchmodeClient(),
            calendar ?? new NullCalendarCache(), new TrailerSelector(), new RatingMapper(), [], [], options,
            NullLogger<PremiereService>.Instance, itemEnrichmentCache: coordinator);
    }

    private static IDisposable CanonicalSessionScope(PremiereService service, ItemEnrichmentCacheCoordinator.ItemEnrichmentSession session)
        => (IDisposable)typeof(PremiereService).GetMethod("UseItemCacheSession", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [session])!;

    private static Task<DateOnly?> InvokeCanonicalValidation(PremiereService service, CancellationToken cancellationToken)
        => (Task<DateOnly?>)typeof(PremiereService).GetMethod("GetSeasonOneEpisodeOneDateAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [901, cancellationToken, true])!;

    private static Task<PremiereItem?> InvokeCanonicalSeriesMapping(PremiereService service, TmdbTvDiscoverItem item, DateOnly date)
    {
        var method = typeof(PremiereService).GetMethod("MapSeriesCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var arguments = method.GetParameters().Select(parameter => parameter.Name switch
        {
            "item" => (object)item,
            "cancellationToken" => CancellationToken.None,
            "forceRefresh" => true,
            "premiereDateOverride" => date,
            _ => parameter.DefaultValue
        }).ToArray();
        return (Task<PremiereItem?>)method.Invoke(service, arguments)!;
    }

    private sealed class CanonicalValidationHttpHandler(DateOnly date) : HttpMessageHandler
    {
        public TaskCompletionSource SeasonStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSeason { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentBag<string> SeasonPaths { get; } = [];
        public CancellationToken SeasonToken;
        public bool FailFirstSeason;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            string json;
            if (path.EndsWith("/tv/901/season/1", StringComparison.Ordinal))
            {
                SeasonPaths.Add(path);
                SeasonToken = cancellationToken;
                SeasonStarted.TrySetResult();
                await ReleaseSeason.Task.WaitAsync(cancellationToken);
                if (FailFirstSeason && SeasonPaths.Count == 1) throw new InvalidOperationException("Fixture failure");
                json = JsonSerializer.Serialize(new { episodes = new[] { new { season_number = 1, episode_number = 1, air_date = date.ToString("yyyy-MM-dd") } } });
            }
            else if (path.EndsWith("/discover/tv", StringComparison.Ordinal))
                json = JsonSerializer.Serialize(new { page = 1, total_pages = 1, total_results = 1,
                    results = new[] { new { id = 901, name = "Canonical fixture", first_air_date = date.ToString("yyyy-MM-dd") } } });
            else if (path.EndsWith("/tv/901", StringComparison.Ordinal))
                json = "{\"id\":901,\"name\":\"Canonical fixture\",\"episode_run_time\":[47]}";
            else throw new InvalidOperationException("Unexpected fixture request: " + path);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
