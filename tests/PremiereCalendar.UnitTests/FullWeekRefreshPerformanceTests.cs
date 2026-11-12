using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class PremiereServiceTests
{
    [Fact]
    public async Task FullWeekRefreshPerformance_WarmRefreshButtonApprovedSemantics()
    {
        var start = new DateOnly(2026, 11, 2);
        var rows = FullWeekRows(start, false);
        using var memory = new MemoryCache(new MemoryCacheOptions());
        using var handler = new FullWeekHttpHandler(rows);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://full-week.invalid/3/") };
        var options = Microsoft.Extensions.Options.Options.Create(new TmdbOptions
        { BearerToken = "local-fixture", SourceRegions = ["US"], SourceFetchConcurrency = 12 });
        using var limiter = new TmdbRequestLimiter(options);
        var tmdb = new TmdbClient(http, memory, options, NullLogger<TmdbClient>.Instance, requestLimiter: limiter);
        using var omdb = new FullWeekOmdb();
        using var tomatoes = new FullWeekTomatoes();
        using var availability = new FullWeekAvailability();
        using var artwork = new FullWeekArtwork();
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var service = new PremiereService(tmdb, omdb, new FakeTvmazeClient(), availability, new FakeCalendarCache(),
            new TrailerSelector(), new RatingMapper(), [artwork], [], options, NullLogger<PremiereService>.Instance,
            rottenTomatoesClient: tomatoes, itemEnrichmentCache: ItemCacheCoordinator(store, clock));
        var filters = new CalendarFilters { ShowSeries = false, PriorityDate = start,
            MovieFilters = { OriginalLanguages = ["en", "nl"], RuntimeMinMinutes = 45 } };
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var primed = await service.GetPremieresAsync(start, start.AddDays(6), budget.Token, forceRefresh: true, filters: filters);
        Assert.Equal(28, primed.Count);
        var clocks = store.Records.Values.ToDictionary(record => record.Identity, record =>
            (record.MetadataFetchedUtc, record.RatingsFetchedUtc, record.MetadataInvalidatedUtc));
        var before = Counts();
        // This explicitly measures the approved change in button behavior. The
        // frozen baseline lacks the new API and refreshes all enrichment again.
        var sourceRefresh = typeof(PremiereService).GetMethod("StreamSourceRefreshAsync",
            [typeof(DateOnly), typeof(DateOnly), typeof(CalendarFilters), typeof(CancellationToken)]);
        var stream = sourceRefresh is null
            ? service.StreamPremieresAsync(start, start.AddDays(6), forceRefresh: true, filters: filters, cancellationToken: budget.Token)
            : (IAsyncEnumerable<PremiereLoadProgress>)sourceRefresh.Invoke(service, [start, start.AddDays(6), filters, budget.Token])!;
        var timer = Stopwatch.StartNew();
        double? firstVisibleMs = null, firstFreshMs = null, allDaysMs = null, allRowsMs = null;
        var rediscovered = new HashSet<int>();
        var checkpoints = new HashSet<string>(StringComparer.Ordinal);
        PremiereLoadProgress? final = null;
        await foreach (var update in stream)
        {
            if (update.Items.Count > 0) firstVisibleMs ??= timer.Elapsed.TotalMilliseconds;
            if (!update.FromCache && update.CheckpointKey is not null)
            {
                foreach (var item in update.SourceItems) rediscovered.Add(item.TmdbId);
                if (rediscovered.Count > 0) firstFreshMs ??= timer.Elapsed.TotalMilliseconds;
                if (rows.Where(row => rediscovered.Contains(row.Id)).Select(row => row.Date).Distinct().Count() == 7)
                    allDaysMs ??= timer.Elapsed.TotalMilliseconds;
                if (rediscovered.Count == rows.Length) allRowsMs ??= timer.Elapsed.TotalMilliseconds;
                if (update.Phase == "complete") checkpoints.Add(update.CheckpointKey);
            }
            if (update.IsFinal) final = update;
        }
        var completionMs = timer.Elapsed.TotalMilliseconds;
        Assert.NotNull(final);
        Assert.False(final.HasSourceErrors);
        Assert.Equal(28, final.Items.Count);
        Assert.Equal(14, checkpoints.Count);
        Assert.NotNull(firstFreshMs);
        Assert.NotNull(allDaysMs);
        Assert.NotNull(allRowsMs);
        var after = Counts();
        var requests = after.ToDictionary(pair => pair.Key, pair => pair.Value - before[pair.Key]);
        Assert.Equal(14, requests["tmdb_discovery"]);
        foreach (var provider in new[] { "tmdb_details", "omdb", "rotten_tomatoes", "availability", "artwork" })
            Assert.Equal(sourceRefresh is null ? 28 : 0, requests[provider]);
        foreach (var record in store.Records.Values)
        {
            var prior = clocks[record.Identity];
            Assert.Equal(prior.MetadataFetchedUtc, record.MetadataFetchedUtc);
            Assert.Equal(prior.RatingsFetchedUtc, record.RatingsFetchedUtc);
            Assert.Equal(prior.MetadataInvalidatedUtc, record.MetadataInvalidatedUtc);
        }
        string BusinessProjection(IReadOnlyList<PremiereItem> items) => JsonSerializer.Serialize(items
            .OrderBy(item => item.CanonicalId, StringComparer.Ordinal).Select(item =>
            {
                var normalized = FullWeekNormalize(item);
                return normalized with { EnrichmentVersion = normalized.EnrichmentVersion is { } version
                    ? version with { MetadataRevision = 0 } : null };
            }));
        var projection = BusinessProjection(final.Items);
        Assert.Equal(BusinessProjection(primed), projection);
        var measurement = new
        {
            scenario = "warm_refresh_button",
            semantics = sourceRefresh is null ? "legacy_refresh_lists_and_all_enrichment" : "approved_refresh_lists_reuse_fresh_enrichment",
            priming_excluded = true, first_visible_row_ms = firstVisibleMs, first_row_ms = firstFreshMs,
            all_seven_days_ms = allDaysMs, all_rows_ms = allRowsMs, completion_ms = completionMs,
            rows = final.Items.Count, days = 7, source_checkpoints = checkpoints.Count, requests,
            source_timeout_seconds = options.Value.SourceTimeoutSeconds,
            tmdb_max_requests_per_second = options.Value.MaxRequestsPerSecond,
            tmdb_max_concurrency = options.Value.MaxConcurrentRequests,
            projection_sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(projection))),
            normalization = "wall_clock_observations_and_internal_metadata_revision_only"
        };
        _evidence.WriteLine("FULL_WEEK " + JsonSerializer.Serialize(measurement));
        var directory = Environment.GetEnvironmentVariable("PREMIERE_FULL_WEEK_EVIDENCE_DIR");
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, "warm_refresh_button.projection.json"), projection);
            await File.WriteAllTextAsync(Path.Combine(directory, "warm_refresh_button.measurement.json"),
                JsonSerializer.Serialize(measurement, new JsonSerializerOptions { WriteIndented = true }));
        }

        Dictionary<string, int> Counts() => new()
        {
            ["tmdb_discovery"] = handler.Paths.Count(path => path.Contains("/discover/", StringComparison.Ordinal)),
            ["tmdb_details"] = handler.Paths.Count(path => !path.Contains("/discover/", StringComparison.Ordinal)),
            ["omdb"] = omdb.Calls, ["rotten_tomatoes"] = tomatoes.Calls,
            ["availability"] = availability.Calls, ["artwork"] = artwork.Calls
        };
    }

    // The same file is copied into the frozen baseline by the local measurement
    // script. All HTTP terminates in FullWeekHttpHandler; no provider is contacted.
    [Theory]
    [InlineData("forced", false)]
    [InlineData("forced", true)]
    [InlineData("stale", false)]
    [InlineData("snapshot", false)]
    public async Task FullWeekRefreshPerformance_RecordsCompleteWeekAndProjection(string refreshMode, bool includeSeries)
    {
        var start = new DateOnly(2026, 11, 2);
        var expected = FullWeekRows(start, includeSeries);
        using var memory = new MemoryCache(new MemoryCacheOptions());
        var oldOnly = refreshMode == "forced" ? [] : Enumerable.Range(0, 56)
            .Select(index => new FullWeekRow(30_000 + index, false, start.AddDays(index % 7), "en", false)).ToArray();
        using var handler = new FullWeekHttpHandler(expected.Concat(oldOnly).ToArray());
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://full-week.invalid/3/") };
        var options = Microsoft.Extensions.Options.Options.Create(new TmdbOptions
        {
            BearerToken = "local-fixture", SourceRegions = ["US"], SourceFetchConcurrency = 12
        });
        using var limiter = new TmdbRequestLimiter(options);
        var tmdb = new TmdbClient(http, memory, options, NullLogger<TmdbClient>.Instance, requestLimiter: limiter);
        using var omdb = new FullWeekOmdb();
        using var tomatoes = new FullWeekTomatoes();
        using var availability = new FullWeekAvailability();
        using var artwork = new FullWeekArtwork();
        var tvmaze = new FakeTvmazeClient();
        var clock = new ItemCacheClock();
        var oldCached = oldOnly.Select(row => FullWeekCachedRow(row, clock)).ToArray();
        var cache = new FakeCalendarCache
        {
            Items = refreshMode == "snapshot" ? oldCached : null,
            ExpiredItems = refreshMode == "stale" ? oldCached : null
        };
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var service = new PremiereService(tmdb, omdb, tvmaze, availability, cache, new TrailerSelector(), new RatingMapper(),
            [artwork], [], options, NullLogger<PremiereService>.Instance, rottenTomatoesClient: tomatoes,
            itemEnrichmentCache: coordinator);
        var filters = new CalendarFilters
        {
            ShowMovies = true, ShowSeries = includeSeries, PriorityDate = start,
            MovieFilters = { OriginalLanguages = ["en", "nl"], RuntimeMinMinutes = 45 },
            SeriesFilters = { OriginalLanguages = ["en", "nl"], SeriesDateMode = SeriesDateMode.NewSeriesOnly }
        };
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var timer = Stopwatch.StartNew();
        double? firstRowMs = null, allDaysMs = null, allRowsMs = null;
        var checkpoints = new Dictionary<string, PremiereLoadProgress>(StringComparer.Ordinal);
        var updates = new List<PremiereLoadProgress>();
        var resume = refreshMode == "snapshot"
            ? new ProviderWorkResumeState(new Dictionary<string, IReadOnlyList<PremiereItem>>(), null) { RefreshCalendarSnapshot = true }
            : null;
        var currentIds = expected.Select(row => row.Id).ToHashSet();
        await foreach (var update in service.StreamCoreAsync(start, start.AddDays(6), forceRefresh: refreshMode == "forced",
            filters: filters, resumeState: resume, cancellationToken: budget.Token))
        {
            updates.Add(update);
            if (!update.FromCache && typeof(PremiereLoadProgress).GetProperty("IsCalendarReady")?.GetValue(update) is true)
                Assert.DoesNotContain(update.Items, item => !currentIds.Contains(item.TmdbId));
            var currentRows = update.Items.Where(item => currentIds.Contains(item.TmdbId)).ToArray();
            if (currentRows.Length > 0) firstRowMs ??= timer.Elapsed.TotalMilliseconds;
            if (currentRows.Select(item => item.PremiereDate).Distinct().Count() == 7)
                allDaysMs ??= timer.Elapsed.TotalMilliseconds;
            if (currentRows.Length == expected.Length) allRowsMs ??= timer.Elapsed.TotalMilliseconds;
            if (update is { IsFinal: false, Phase: "complete", CheckpointKey: not null })
                checkpoints[update.CheckpointKey] = update;
        }
        var completionMs = timer.Elapsed.TotalMilliseconds;
        var final = Assert.Single(updates, update => update.IsFinal);
        Assert.False(final.HasSourceErrors);
        Assert.Empty(final.FailedSourceNames);
        Assert.NotNull(firstRowMs);
        Assert.NotNull(allDaysMs);
        Assert.NotNull(allRowsMs);
        Assert.True(firstRowMs <= allDaysMs && allDaysMs <= allRowsMs && allRowsMs <= completionMs);
        Assert.Equal(expected.Length, final.Items.Count);
        Assert.Equal(7, final.Items.Select(item => item.PremiereDate).Distinct().Count());
        Assert.Equal(expected.Select(row => row.Id).Order(), final.Items.Select(item => item.TmdbId).Order());
        Assert.Equal(includeSeries ? 16 : 14, checkpoints.Count);
        Assert.All(checkpoints.Values, checkpoint => Assert.False(checkpoint.HasSourceErrors));
        // A compact ledger remains available when a preservation assertion fails.
        _evidence.WriteLine("FULL_WEEK_PRESERVATION " + JsonSerializer.Serialize(new
        {
            refreshMode, includeSeries,
            missing_availability = final.Items.Where(item => item.Sources.Length == 0).Select(item => item.TmdbId).ToArray(),
            availability_completed = availability.CompletedIds.Order().ToArray(),
            accepted_core_only = store.Records.Values.Where(record => currentIds.Contains(record.Identity.TmdbId)
                && JsonSerializer.Deserialize<ItemEnrichmentMetadata>(record.MetadataJson!)?.CoreMetadataOnly == true)
                .Select(record => new { id = record.Identity.TmdbId, record.MetadataRevision }).OrderBy(row => row.id).ToArray()
        }));
        foreach (var item in final.Items)
        {
            var row = Assert.Single(expected, row => row.Id == item.TmdbId);
            Assert.Equal(row.Date, item.PremiereDate);
            Assert.Equal(row.Title, item.Title);
            Assert.Equal(row.Title + " original", item.OriginalTitle);
            Assert.Equal(row.Language, item.OriginalLanguage);
            Assert.Equal(row.Series ? 43 : 110, item.RuntimeMinutes);
            Assert.Equal($"tt{row.Id:0000000}", item.ImdbId);
            if (row.Series) Assert.Equal(row.Id + 100_000, item.TvdbId);
            Assert.Equal(7.6, item.ImdbScore);
            Assert.Equal(12_345, item.ImdbVoteCount);
            Assert.Equal(81, item.RottenTomatoesScore);
            Assert.Equal(73, item.RottenTomatoesAudienceScore);
            Assert.Equal(71, item.MetacriticScore);
            Assert.Equal("https://fixture.invalid/artwork/" + row.Id + ".jpg", item.PosterUrl);
            Assert.Equal(["Fixture availability"], item.SourceNames);
            Assert.NotNull(item.TrailerUrl);
            Assert.NotNull(item.DateSemantics);
            Assert.Empty(item.MissingDataIssues);
            Assert.Equal(row.Series ? PremiereItemType.SeriesPremiere : PremiereItemType.MovieFirstRelease, item.Type);
        }
        Assert.Equal(14, handler.Paths.Count(path => path.StartsWith("/3/discover/movie?", StringComparison.Ordinal)));
        Assert.Equal(includeSeries ? 2 : 0, handler.Paths.Count(path => path.StartsWith("/3/discover/tv?", StringComparison.Ordinal)));
        var oldDetailCalls = handler.Paths.Count(path => oldOnly.Any(row => path.StartsWith($"/3/movie/{row.Id}?", StringComparison.Ordinal)));
        Assert.Equal(expected.Length, handler.Paths.Count(path => !path.Contains("/discover/", StringComparison.Ordinal)
            && !path.Contains("/season/", StringComparison.Ordinal)) - oldDetailCalls);
        Assert.Equal(includeSeries ? 14 : 0, handler.Paths.Count(path => path.Contains("/season/", StringComparison.Ordinal)));
        Assert.Equal(expected.Length + oldDetailCalls, omdb.Calls);
        Assert.Equal(expected.Length + oldDetailCalls, tomatoes.Calls);
        Assert.Equal(expected.Length + oldDetailCalls, availability.Calls);
        Assert.Equal(expected.Length + oldDetailCalls, artwork.Calls);
        Assert.InRange(handler.MaxActive, 1, 4);
        Assert.InRange(omdb.MaxActive, 1, 2);
        Assert.InRange(tomatoes.MaxActive, 1, 2);
        Assert.InRange(availability.MaxActive, 1, 2);
        Assert.InRange(artwork.MaxActive, 1, 2);

        // Every row field participates. Only wall-clock observations are removed;
        // array order is normalized because concurrent sources can merge in any order.
        var projection = JsonSerializer.Serialize(final.Items.OrderBy(item => item.CanonicalId, StringComparer.Ordinal)
            .Select(item => FullWeekNormalize(item)).ToArray());
        var projectionHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(projection)));
        var scenario = refreshMode + (includeSeries ? "_all_new_series" : "_movies");
        var measurement = new
        {
            scenario, first_row_ms = firstRowMs, all_seven_days_ms = allDaysMs, all_rows_ms = allRowsMs,
            completion_ms = completionMs, rows = final.Items.Count, days = 7, source_checkpoints = checkpoints.Count,
            old_only_cached_rows = oldOnly.Length, old_only_detail_calls = oldDetailCalls,
            source_timeout_seconds = options.Value.SourceTimeoutSeconds,
            tmdb_max_requests_per_second = options.Value.MaxRequestsPerSecond,
            tmdb_max_concurrency = options.Value.MaxConcurrentRequests,
            requests = new { tmdb = handler.Paths.Count, omdb = omdb.Calls, rotten_tomatoes = tomatoes.Calls,
                availability = availability.Calls, artwork = artwork.Calls, tvmaze_search = tvmaze.SearchCalls.Count },
            max_active = new { tmdb = handler.MaxActive, omdb = omdb.MaxActive, rotten_tomatoes = tomatoes.MaxActive,
                availability = availability.MaxActive, artwork = artwork.MaxActive },
            projection_sha256 = projectionHash,
            source_coverage = checkpoints.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new
                { checkpoint = pair.Key, rows = pair.Value.SourceItemCount, pair.Value.HasSourceErrors }).ToArray(),
            tmdb_requests = handler.Paths.Order(StringComparer.Ordinal).ToArray()
        };
        _evidence.WriteLine("FULL_WEEK " + JsonSerializer.Serialize(measurement));
        var directory = Environment.GetEnvironmentVariable("PREMIERE_FULL_WEEK_EVIDENCE_DIR");
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Combine(directory, scenario + ".projection.json"), projection);
            await File.WriteAllTextAsync(Path.Combine(directory, scenario + ".measurement.json"),
                JsonSerializer.Serialize(measurement, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    [Fact]
    public async Task FullWeekRefreshPerformance_OtherSourceFlushPreservesCompletedAvailability()
    {
        var start = new DateOnly(2026, 11, 2);
        var availability = new FullWeekPublicationAvailability();
        var tmdb = new FakeTmdbClient
        {
            FilterMovieItemsByDate = true,
            MovieItems = Enumerable.Range(1, 2).Select(id => new TmdbMovieDiscoverItem
                { Id = id, Title = $"Publication {id}", ReleaseDate = start.AddDays(id - 1).ToString("yyyy-MM-dd"), OriginalLanguage = "en" }).ToArray(),
            MovieDetailOverride = async (id, token) =>
            {
                if (id == 2) await availability.FirstStarted.Task.WaitAsync(token);
                return new TmdbDetailsWithExtras { Id = id, Runtime = 110, OriginalLanguage = "en" };
            }
        };
        var clock = TimeProvider.System;
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = new ItemEnrichmentCacheCoordinator(store,
            Microsoft.Extensions.Options.Options.Create(new ItemEnrichmentCacheOptions()),
            Microsoft.Extensions.Options.Options.Create(new TmdbOptions()), NullLogger<ItemEnrichmentCacheCoordinator>.Instance,
            timeProvider: clock);
        var service = CreateService(tmdb, watchmode: availability, itemEnrichmentCache: coordinator, sourceFetchConcurrency: 2);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        PremiereLoadProgress? final = null;
        try
        {
            await foreach (var update in service.StreamCoreAsync(start, start.AddDays(1),
                filters: new CalendarFilters { ShowSeries = false,
                    MovieFilters = { OriginalLanguages = ["en"], RuntimeMinMinutes = 45 } }, cancellationToken: budget.Token))
            {
                if (!update.IsFinal && update.Phase == "complete" && update.SourceItems.Any(item => item.TmdbId == 2))
                {
                    var core = Assert.Single(store.Records.Values, record => record.Identity.TmdbId == 1);
                    _evidence.WriteLine($"PUBLICATION_GATED id=1 revision={core.MetadataRevision} core_only={JsonSerializer.Deserialize<ItemEnrichmentMetadata>(core.MetadataJson!)?.CoreMetadataOnly}");
                    availability.ReleaseFirst.TrySetResult();
                }
                if (update.IsFinal) final = update;
            }
        }
        finally { availability.ReleaseFirst.TrySetResult(); }
        Assert.NotNull(final);
        Assert.False(final.HasSourceErrors);
        Assert.Equal([1, 2], availability.CompletedIds.Order());
        foreach (var item in final.Items)
        {
            var record = Assert.Single(store.Records.Values, record => record.Identity.TmdbId == item.TmdbId);
            var accepted = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(record.MetadataJson!)!;
            _evidence.WriteLine($"PUBLICATION_FINAL id={item.TmdbId} visible_sources={item.Sources.Length} accepted_sources={accepted.Sources.Length} revision={record.MetadataRevision} core_only={accepted.CoreMetadataOnly}");
            Assert.Equal(["Fixture availability"], item.SourceNames);
            Assert.Equal(31, Assert.Single(accepted.Sources).Id);
            Assert.False(accepted.CoreMetadataOnly);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullWeekRefreshPerformance_RefreshesMembershipBeforeOldOnlyMetadata(bool snapshotUpdate)
    {
        var start = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var oldOnly = Enumerable.Range(0, 56).Select(index => FullWeekCachedRow(
            new FullWeekRow(30_000 + index, false, start.AddDays(index % 7), "en"), clock)).ToArray();
        var cache = new FakeCalendarCache { Items = snapshotUpdate ? oldOnly : null, ExpiredItems = snapshotUpdate ? null : oldOnly };
        var tmdb = new FakeTmdbClient
        {
            FilterMovieItemsByDate = true,
            MovieItems = Enumerable.Range(0, 7).Select(day => new TmdbMovieDiscoverItem
                { Id = day + 1, Title = $"Current {day}", ReleaseDate = start.AddDays(day).ToString("yyyy-MM-dd"), OriginalLanguage = "en" }).ToArray()
        };
        var coordinator = ItemCacheCoordinator(new MemoryItemEnrichmentStore(clock), clock);
        var service = CreateService(tmdb, calendarCache: cache, itemEnrichmentCache: coordinator, sourceFetchConcurrency: 12);
        var resume = snapshotUpdate
            ? new ProviderWorkResumeState(new Dictionary<string, IReadOnlyList<PremiereItem>>(), null) { RefreshCalendarSnapshot = true }
            : null;
        var updates = new List<PremiereLoadProgress>();
        await foreach (var update in service.StreamCoreAsync(start, start.AddDays(6),
            filters: new CalendarFilters { ShowSeries = false, MovieFilters = { OriginalLanguages = ["en"] } }, resumeState: resume))
            updates.Add(update);
        var final = Assert.Single(updates, update => update.IsFinal);
        Assert.False(final.HasSourceErrors);
        Assert.Equal(Enumerable.Range(1, 7), final.Items.Select(item => item.TmdbId).Order());
        Assert.DoesNotContain(tmdb.MovieDetailCalls, call => call.Id >= 30_000);
        Assert.Equal(7, tmdb.MovieDetailCalls.Count);
        Assert.Contains(updates, update => update.IsStaleCache && update.Items.Any(item => item.TmdbId >= 30_000));
        Assert.All(cache.SetCalls, call => Assert.DoesNotContain(call.Items, item => item.TmdbId >= 30_000));
    }

    [Fact]
    public async Task FullWeekRefreshPerformance_CancellationStopsHeldRequestsAndPreservesCache()
    {
        var start = new DateOnly(2026, 11, 2);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var cancelled = 0;
        var tmdb = new FakeTmdbClient
        {
            FilterMovieItemsByDate = true,
            MovieItems = Enumerable.Range(0, 7).Select(day => new TmdbMovieDiscoverItem
                { Id = day + 1, Title = $"Held {day}", ReleaseDate = start.AddDays(day).ToString("yyyy-MM-dd"), OriginalLanguage = "en" }).ToArray(),
            MovieDetailOverride = async (_, token) =>
            {
                Interlocked.Increment(ref active);
                started.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); return null; }
                catch (OperationCanceledException) { Interlocked.Increment(ref cancelled); throw; }
                finally { Interlocked.Decrement(ref active); }
            }
        };
        var cache = new FakeCalendarCache();
        var service = CreateService(tmdb, calendarCache: cache, sourceFetchConcurrency: 12);
        using var cancellation = new CancellationTokenSource();
        var load = service.GetPremieresAsync(start, start.AddDays(6), cancellation.Token, forceRefresh: true,
            filters: new CalendarFilters { ShowSeries = false, MovieFilters = { OriginalLanguages = ["en"] } });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, Volatile.Read(ref active));
        Assert.True(Volatile.Read(ref cancelled) > 0);
        Assert.Empty(cache.SetCalls);
    }

    [Fact]
    public async Task FullWeekRefreshPerformance_ForwardProgressRenewsPerSourceDeadline()
    {
        var start = new DateOnly(2026, 11, 2);
        var tmdb = new FakeTmdbClient
        {
            FilterMovieItemsByDate = true, MovieDelay = TimeSpan.FromMilliseconds(600), MovieDetailDelay = TimeSpan.FromMilliseconds(600),
            MovieItems = Enumerable.Range(0, 7).Select(day => new TmdbMovieDiscoverItem
                { Id = day + 1, Title = $"Progress {day}", ReleaseDate = start.AddDays(day).ToString("yyyy-MM-dd"), OriginalLanguage = "en" }).ToArray()
        };
        var service = CreateService(tmdb, sourceTimeoutSeconds: 1, sourceFetchConcurrency: 12);
        var updates = new List<PremiereLoadProgress>();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await foreach (var update in service.StreamCoreAsync(start, start.AddDays(6), forceRefresh: true,
            filters: new CalendarFilters { ShowSeries = false, MovieFilters = { OriginalLanguages = ["en"] } }, cancellationToken: budget.Token))
            updates.Add(update);
        var final = Assert.Single(updates, update => update.IsFinal);
        Assert.Equal(7, final.Items.Count);
        Assert.False(final.HasSourceErrors);
        Assert.Equal(7, updates.Where(update => !update.IsFinal && update.Phase == "complete")
            .Select(update => update.CheckpointKey).Distinct().Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FullWeekRefreshPerformance_VerifiedDayIsVisibleBeforeOptionalCompletion(bool cancelAfterCore)
    {
        var start = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        // One requested day has two language sources, within the provider's
        // two-source cap even while every optional enrichment branch is held.
        var rows = FullWeekRows(start, false).Where(row => row.Date == start).ToArray();
        var filters = new CalendarFilters { ShowSeries = false,
            MovieFilters = { OriginalLanguages = ["en", "nl"], RuntimeMinMinutes = 45 } };
        var fixture = Create(hold: true);
        using var http = fixture.Http;
        using var memory = fixture.Memory;
        using var limiter = fixture.Limiter;
        using var artwork = fixture.Artwork;
        using var tomatoes = fixture.Tomatoes;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var updates = fixture.Service.StreamCoreAsync(start, start, forceRefresh: true,
            filters: filters, cancellationToken: budget.Token).GetAsyncEnumerator();
        var visible = false;
        while (await updates.MoveNextAsync())
        {
            var update = updates.Current;
            Assert.False(update.IsFinal);
            Assert.Empty(fixture.Cache.SetCalls);
            Assert.Equal(0, tomatoes.Completed);
            if (update.Items.Count != rows.Length) continue;
            Assert.Equal(rows.Select(row => row.Id).Order(), update.Items.Select(item => item.TmdbId).Order());
            Assert.Equal(start, Assert.Single(update.Items.Select(item => item.PremiereDate).Distinct()));
            foreach (var item in update.Items)
            {
                Assert.Equal(110, item.RuntimeMinutes);
                Assert.Equal(Assert.Single(rows, row => row.Id == item.TmdbId).Language, item.OriginalLanguage);
            }
            Assert.Equal(0, update.CompletedWork);
            Assert.All(fixture.Store.Records.Values, record => Assert.Null(record.MetadataJson));
            visible = true;
            break;
        }
        Assert.True(visible, "All eligible rows from both language sources for the requested day must be visible while optional work is held.");
        if (cancelAfterCore)
        {
            await budget.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            { while (await updates.MoveNextAsync()) { } });
            Assert.Equal(0, tomatoes.Active);
            Assert.Empty(fixture.Cache.SetCalls);
            return;
        }
        tomatoes.Release.TrySetResult();
        PremiereLoadProgress? final = null;
        while (await updates.MoveNextAsync()) if (updates.Current.IsFinal) final = updates.Current;
        Assert.NotNull(final);
        Assert.False(final.HasSourceErrors);
        Assert.Equal(rows.Length, final.Items.Count);
        Assert.All(final.Items, item => Assert.Equal(81, item.RottenTomatoesScore));

        var control = Create(hold: false);
        using var controlHttp = control.Http;
        using var controlMemory = control.Memory;
        using var controlLimiter = control.Limiter;
        using var controlArtwork = control.Artwork;
        using var controlTomatoes = control.Tomatoes;
        var expected = await control.Service.GetPremieresAsync(start, start, budget.Token,
            forceRefresh: true, filters: filters);
        string Projection(IReadOnlyList<PremiereItem> items) => JsonSerializer.Serialize(items.OrderBy(item => item.TmdbId).Select(FullWeekNormalize));
        Assert.Equal(Projection(expected), Projection(final.Items));
        Assert.Equal(control.Handler.Paths.Count, fixture.Handler.Paths.Count);
        Assert.Equal(2, fixture.Handler.Paths.Count(path => path.Contains("/discover/", StringComparison.Ordinal)));
        Assert.Equal(rows.Length, fixture.Handler.Paths.Count(path => !path.Contains("/discover/", StringComparison.Ordinal)));
        Assert.Equal(2, control.Handler.Paths.Count(path => path.Contains("/discover/", StringComparison.Ordinal)));
        Assert.Equal(rows.Length, control.Handler.Paths.Count(path => !path.Contains("/discover/", StringComparison.Ordinal)));
        Assert.Equal(control.Omdb.Calls.Count, fixture.Omdb.Calls.Count);
        Assert.Equal(control.Watchmode.SourceCalls.Count, fixture.Watchmode.SourceCalls.Count);
        Assert.Equal(control.Tomatoes.Calls, tomatoes.Calls);
        Assert.Equal(control.Artwork.Calls, artwork.Calls);
        Assert.Equal(rows.Length, fixture.Omdb.Calls.Count);
        Assert.Equal(rows.Length, fixture.Watchmode.SourceCalls.Count);
        Assert.Equal(rows.Length, tomatoes.Calls);
        Assert.Equal(rows.Length, artwork.Calls);

        (PremiereService Service, FullWeekHttpHandler Handler, HttpClient Http, MemoryCache Memory, TmdbRequestLimiter Limiter,
            FakeOmdbClient Omdb, FakeWatchmodeClient Watchmode, FullWeekHeldTomatoes Tomatoes, FullWeekArtwork Artwork,
            FakeCalendarCache Cache, MemoryItemEnrichmentStore Store) Create(bool hold)
        {
            var handler = new FullWeekHttpHandler(rows);
            var client = new HttpClient(handler) { BaseAddress = new Uri("https://full-week.invalid/3/") };
            var cacheMemory = new MemoryCache(new MemoryCacheOptions());
            var options = Microsoft.Extensions.Options.Options.Create(new TmdbOptions { BearerToken = "local-fixture", SourceFetchConcurrency = 12 });
            var requestLimiter = new TmdbRequestLimiter(options);
            var tmdb = new TmdbClient(client, cacheMemory, options, NullLogger<TmdbClient>.Instance, requestLimiter: requestLimiter);
            var omdb = new FakeOmdbClient();
            var watchmode = new FakeWatchmodeClient { Sources = [new PremiereSource { Id = 31, Name = "Fixture availability", Kind = "flatrate" }] };
            var provider = new FullWeekHeldTomatoes();
            if (!hold) provider.Release.TrySetResult();
            var artworkProvider = new FullWeekArtwork();
            var cache = new FakeCalendarCache();
            var store = new MemoryItemEnrichmentStore(clock);
            var service = new PremiereService(tmdb, omdb, new FakeTvmazeClient(), watchmode, cache, new TrailerSelector(),
                new RatingMapper(), [artworkProvider], [], options, NullLogger<PremiereService>.Instance,
                rottenTomatoesClient: provider, itemEnrichmentCache: ItemCacheCoordinator(store, clock));
            return (service, handler, client, cacheMemory, requestLimiter, omdb, watchmode, provider, artworkProvider, cache, store);
        }
    }

    private static PremiereItem FullWeekNormalize(PremiereItem item) => item with
    {
        LastUpdatedUtc = DateTimeOffset.UnixEpoch,
        EnrichmentVersion = item.EnrichmentVersion is { } version ? version with
        {
            MetadataFetchedUtc = version.MetadataFetchedUtc.HasValue ? DateTimeOffset.UnixEpoch : null,
            RatingsFetchedUtc = version.RatingsFetchedUtc.HasValue ? DateTimeOffset.UnixEpoch : null,
            MetadataInvalidatedUtc = version.MetadataInvalidatedUtc.HasValue ? DateTimeOffset.UnixEpoch : null
        } : null,
        OriginCountries = item.OriginCountries.Order(StringComparer.Ordinal).ToArray(),
        SourceNames = item.SourceNames.Order(StringComparer.Ordinal).ToArray(),
        Sources = item.Sources.OrderBy(source => source.Name, StringComparer.Ordinal).ThenBy(source => source.Kind, StringComparer.Ordinal).ToArray(),
        GenreIds = item.GenreIds.Order().ToArray(), Genres = item.Genres.Order(StringComparer.Ordinal).ToArray(),
        Keywords = item.Keywords.Order(StringComparer.Ordinal).ToArray(), MovieReleaseTypes = item.MovieReleaseTypes.Order().ToArray(),
        Certifications = item.Certifications.Order(StringComparer.Ordinal).ToArray(),
        MergeContributions = item.MergeContributions.OrderBy(contribution => JsonSerializer.Serialize(contribution), StringComparer.Ordinal).ToArray(),
        MissingDataIssues = item.MissingDataIssues.OrderBy(issue => JsonSerializer.Serialize(issue), StringComparer.Ordinal).ToArray()
    };

    private static PremiereItem FullWeekCachedRow(FullWeekRow row, ItemCacheClock clock) => new()
    {
        CanonicalId = $"movie:{row.Id}", TmdbId = row.Id, MediaType = PremiereMediaType.Movie,
        Type = PremiereItemType.MovieFirstRelease, Title = row.Title, PremiereDate = row.Date,
        OriginalLanguage = row.Language, RuntimeMinutes = 110, ImdbId = $"tt{row.Id:0000000}",
        LastUpdatedUtc = clock.GetUtcNow().AddDays(-8), TmdbScore = 7.2
    };

    private sealed record FullWeekRow(int Id, bool Series, DateOnly Date, string Language, bool Discoverable = true)
    {
        public string Title => $"Fixture {(Series ? "series" : "movie")} {Id}";
    }

    private static FullWeekRow[] FullWeekRows(DateOnly start, bool includeSeries)
        => Enumerable.Range(0, 7).SelectMany(day => new[] { "en", "nl" }.SelectMany((language, languageIndex) =>
            Enumerable.Range(0, 2).Select(index => new FullWeekRow(10_000 + day * 100 + languageIndex * 10 + index, false,
                start.AddDays(day), language)).Concat(includeSeries
                    ? [new FullWeekRow(20_000 + day * 100 + languageIndex * 10, true, start.AddDays(day), language)] : []))).ToArray();

    private sealed class FullWeekHttpHandler(FullWeekRow[] rows, bool paginate = false) : HttpMessageHandler
    {
        public ConcurrentBag<string> Paths { get; } = [];
        private int _active, _maxActive;
        public int MaxActive => Volatile.Read(ref _maxActive);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            Assert.Equal("full-week.invalid", uri.Host);
            Paths.Add(uri.PathAndQuery);
            var active = Interlocked.Increment(ref _active);
            FullWeekSetMaximum(ref _maxActive, active);
            try
            {
                await Task.Delay(60, token);
                object payload;
                if (uri.AbsolutePath.Contains("/discover/", StringComparison.Ordinal))
                {
                    var query = QueryHelpers.ParseQuery(uri.Query);
                    var series = uri.AbsolutePath.EndsWith("/tv", StringComparison.Ordinal);
                    var page = int.Parse(query["page"].ToString(), CultureInfo.InvariantCulture);
                    Assert.InRange(page, 1, paginate ? 2 : 1);
                    var dateKey = series ? "first_air_date" : "primary_release_date";
                    var from = DateOnly.ParseExact(query[dateKey + ".gte"].ToString(), "yyyy-MM-dd", CultureInfo.InvariantCulture);
                    var to = DateOnly.ParseExact(query[dateKey + ".lte"].ToString(), "yyyy-MM-dd", CultureInfo.InvariantCulture);
                    var selected = rows.Where(row => row.Discoverable && row.Series == series && row.Date >= from && row.Date <= to
                        && row.Language == query["with_original_language"].ToString()).ToArray();
                    var pageSize = Math.Max(1, (selected.Length + 1) / 2);
                    var pageRows = paginate ? selected.Skip((page - 1) * pageSize).Take(pageSize) : selected;
                    payload = new { page, total_pages = paginate ? 2 : 1, total_results = selected.Length,
                        results = pageRows.Select(row => new { id = row.Id, title = row.Title, name = row.Title,
                            original_title = row.Title + " original", original_name = row.Title + " original",
                            release_date = row.Date.ToString("yyyy-MM-dd"), first_air_date = row.Date.ToString("yyyy-MM-dd"),
                            original_language = row.Language, overview = "Fixture overview", origin_country = new[] { "US" },
                            genre_ids = new[] { 18 }, vote_average = 7.2, vote_count = 234 }).ToArray() };
                }
                else
                {
                    var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                    var row = Assert.Single(rows, row => row.Id == int.Parse(parts[2], CultureInfo.InvariantCulture));
                    payload = uri.AbsolutePath.Contains("/season/", StringComparison.Ordinal)
                        ? new { episodes = new[] { new { season_number = 1, episode_number = 1, air_date = row.Date.ToString("yyyy-MM-dd") } } }
                        : (object)new { id = row.Id, title = row.Title, name = row.Title,
                            runtime = row.Series ? 43 : 110, episode_run_time = new[] { 43 }, original_language = row.Language,
                            genres = new[] { new { id = 18, name = "Drama" } },
                            external_ids = new { imdb_id = $"tt{row.Id:0000000}", wikidata_id = $"Q{row.Id}",
                                tvdb_id = row.Series ? row.Id + 100_000 : (int?)null },
                            videos = new { results = new[] { new { site = "YouTube", type = "Trailer", key = $"trailer-{row.Id}", official = true } } } };
                }
                return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    private static void FullWeekSetMaximum(ref int maximum, int active)
    {
        var previous = Volatile.Read(ref maximum);
        while (active > previous)
        {
            var observed = Interlocked.CompareExchange(ref maximum, active, previous);
            if (observed == previous) break;
            previous = observed;
        }
    }

    // Pools match the healthy HTTP limits for these providers. Latencies are
    // synthetic; measured timings are comparison evidence, never test thresholds.
    private abstract class FullWeekOptionalPool(int milliseconds) : IDisposable
    {
        private readonly SemaphoreSlim _pool = new(2);
        private int _calls, _active, _maxActive;
        public int Calls => Volatile.Read(ref _calls);
        public int MaxActive => Volatile.Read(ref _maxActive);
        protected async Task PauseAsync(CancellationToken token)
        {
            Interlocked.Increment(ref _calls);
            await _pool.WaitAsync(token);
            FullWeekSetMaximum(ref _maxActive, Interlocked.Increment(ref _active));
            try { await Task.Delay(milliseconds, token); }
            finally { Interlocked.Decrement(ref _active); _pool.Release(); }
        }
        public void Dispose() => _pool.Dispose();
    }
    private sealed class FullWeekOmdb() : FullWeekOptionalPool(80), IOmdbClient
    {
        public async Task<OmdbItem?> GetByImdbIdAsync(string id, CancellationToken token, bool forceRefresh = false)
        {
            await PauseAsync(token);
            return new OmdbItem { Response = "True", ImdbRating = "7.6", ImdbVotes = "12,345", Metascore = "71" };
        }
    }
    private sealed class FullWeekTomatoes() : FullWeekOptionalPool(150), IRottenTomatoesClient
    {
        public async Task<RottenTomatoesScores> GetScoresAsync(PremiereMediaType type, string title, int? year,
            string? wikidataId, CancellationToken token, bool forceRefresh = false)
        { await PauseAsync(token); return new(81, 73); }
    }
    private sealed class FullWeekArtwork() : FullWeekOptionalPool(80), IArtworkProvider
    {
        public async Task<ArtworkCandidate?> GetArtworkAsync(ArtworkRequest request, CancellationToken token, bool forceRefresh = false)
        { await PauseAsync(token); return new($"https://fixture.invalid/artwork/{request.TmdbId}.jpg", "Fixture artwork"); }
    }
    private sealed class FullWeekAvailability() : FullWeekOptionalPool(80), IWatchmodeClient
    {
        public ConcurrentBag<int> CompletedIds { get; } = [];
        public async Task<IReadOnlyList<PremiereSource>> GetTitleSourcesAsync(PremiereMediaType type, int tmdbId, string? imdbId,
            IReadOnlyList<string> regions, CancellationToken token, bool forceRefresh = false)
        { await PauseAsync(token); CompletedIds.Add(tmdbId); return [new PremiereSource { Id = 31, Name = "Fixture availability", Kind = "flatrate" }]; }
        public Task<IReadOnlyList<ExternalPremiereCandidate>> GetReleaseCandidatesAsync(DateOnly start, DateOnly end,
            CancellationToken token, bool forceRefresh = false) => Task.FromResult<IReadOnlyList<ExternalPremiereCandidate>>([]);
    }
    private sealed class FullWeekPublicationAvailability : IWatchmodeClient
    {
        public TaskCompletionSource FirstStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentBag<int> CompletedIds { get; } = [];
        public async Task<IReadOnlyList<PremiereSource>> GetTitleSourcesAsync(PremiereMediaType type, int tmdbId, string? imdbId,
            IReadOnlyList<string> regions, CancellationToken token, bool forceRefresh = false)
        {
            if (tmdbId == 1) { FirstStarted.TrySetResult(); await ReleaseFirst.Task.WaitAsync(token); }
            CompletedIds.Add(tmdbId);
            return [new PremiereSource { Id = 31, Name = "Fixture availability", Kind = "flatrate" }];
        }
        public Task<IReadOnlyList<ExternalPremiereCandidate>> GetReleaseCandidatesAsync(DateOnly start, DateOnly end,
            CancellationToken token, bool forceRefresh = false) => Task.FromResult<IReadOnlyList<ExternalPremiereCandidate>>([]);
    }
    private sealed class FullWeekHeldTomatoes : IRottenTomatoesClient, IDisposable
    {
        private readonly SemaphoreSlim _pool = new(2);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls, Completed, Active;
        public async Task<RottenTomatoesScores> GetScoresAsync(PremiereMediaType type, string title, int? year,
            string? wikidataId, CancellationToken token, bool forceRefresh = false)
        {
            Interlocked.Increment(ref Calls);
            Started.TrySetResult();
            await _pool.WaitAsync(token);
            Interlocked.Increment(ref Active);
            try
            {
                await Release.Task.WaitAsync(token);
                Interlocked.Increment(ref Completed);
                return new(81, 73);
            }
            finally { Interlocked.Decrement(ref Active); _pool.Release(); }
        }
        public void Dispose() => _pool.Dispose();
    }
}
