using System.Text.Json;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class PremiereServiceTests
{
    [Fact]
    public async Task CallerCancellationStillWinsWhileDrainingAfterProviderThrottle()
    {
        var service = CreateService(new FakeTmdbClient { MovieDetailDelay = TimeSpan.FromSeconds(5) },
            discoveryProviders: [new PendingThenThrottledProvider(DateTimeOffset.UtcNow.AddMinutes(2))],
            sourceTimeoutSeconds: 10, calendarCache: new PartialSnapshotCache());
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in service.StreamPremieresAsync(new(2026, 8, 31), new(2026, 9, 6),
                cancellationToken: cancellation.Token, filters: new() { ShowSeries = false })) { }
        });
    }

    [Fact]
    public async Task RetryDeadlineSurvivesSourceTimeoutWhileDrainingAcceptedCandidates()
    {
        var deadline = DateTimeOffset.UtcNow.AddMinutes(2);
        var service = CreateService(new FakeTmdbClient { MovieDetailDelay = TimeSpan.FromSeconds(5) },
            discoveryProviders: [new PendingThenThrottledProvider(deadline)], sourceTimeoutSeconds: 1,
            calendarCache: new PartialSnapshotCache());
        var updates = new List<PremiereLoadProgress>();
        await foreach (var update in service.StreamPremieresAsync(new(2026, 8, 31), new(2026, 9, 6),
            filters: new() { ShowSeries = false })) updates.Add(update);
        Assert.True(updates[^1].HasSourceErrors);
        Assert.Equal(deadline, updates[^1].RetryAfterUtc);
        Assert.Empty(updates[^1].Items);
    }

    [Fact]
    public async Task IndependentSuccessfulFeedSurvivesTheActualSourceDeadline()
    {
        var cache = new PartialSnapshotCache();
        var provider = new IndependentThenStalledProvider();
        var service = CreateService(new FakeTmdbClient(), discoveryProviders: [provider],
            calendarCache: cache, sourceTimeoutSeconds: 1);
        var updates = new List<PremiereLoadProgress>();
        await foreach (var update in service.StreamPremieresAsync(new(2026, 8, 31), new(2026, 9, 6),
            filters: new() { ShowSeries = false })) updates.Add(update);
        Assert.True(provider.CleanupObserved);
        Assert.True(updates[^1].HasSourceErrors);
        Assert.Contains("Simkl", updates[^1].FailedSourceNames);
        Assert.Equal(31, updates[^1].Items.Count);
        Assert.Equal(31, cache.Items!.Count);
    }

    [Fact]
    public async Task SuccessfulSubBatchFromFailedExternalStreamSurvivesIntoPartialCache()
    {
        var cache = new PartialSnapshotCache();
        var service = CreateService(new FakeTmdbClient(),
            discoveryProviders: [new PartialThenFailedProvider()], calendarCache: cache);
        var updates = new List<PremiereLoadProgress>();
        await foreach (var update in service.StreamPremieresAsync(new(2026, 8, 31), new(2026, 9, 6),
            filters: new() { ShowSeries = false })) updates.Add(update);
        Assert.True(updates[^1].HasSourceErrors);
        Assert.Contains("Simkl", updates[^1].FailedSourceNames);
        Assert.Equal(31, updates[^1].Items.Count);
        Assert.Equal(31, cache.Items!.Count);
        Assert.Equal(31, cache.Items.Select(item => item.CanonicalId).Distinct().Count());
        Assert.All(cache.Items, item => Assert.Contains(item.MergeContributions, contribution => contribution.Source == "Simkl"));
    }

    [Fact]
    public async Task FailedSourceRetainsPartialMovieWeekAndNextLoadShowsItBeforeProviderWork()
    {
        var start = new DateOnly(2026, 8, 31);
        var cache = new PartialSnapshotCache();
        var failed = new DiagnosticFailingProvider();
        var tmdb = new FakeTmdbClient
        {
            FilterMovieItemsByDate = true,
            MovieItems = [new() { Id = 31, Title = "Fixture movie", ReleaseDate = "2026-08-31", VoteAverage = 7 }]
        };
        var service = CreateService(tmdb, discoveryProviders: [failed], calendarCache: cache);
        var filters = new CalendarFilters { ShowSeries = false, ShowMovies = true };
        var updates = new List<PremiereLoadProgress>();
        await foreach (var update in service.StreamPremieresAsync(start, start.AddDays(6), filters: filters)) updates.Add(update);
        Assert.True(updates[^1].HasSourceErrors);
        Assert.True(cache.PartialWrites > 0);
        Assert.Equal("movie:31", Assert.Single(cache.Items!).CanonicalId);
        var requestsBefore = tmdb.MovieCalls.Count;
        await using var next = service.StreamPremieresAsync(start, start.AddDays(6), filters: filters).GetAsyncEnumerator();
        Assert.True(await next.MoveNextAsync());
        Assert.True(next.Current.FromCache);
        Assert.False(next.Current.IsFinal);
        Assert.Equal("movie:31", Assert.Single(next.Current.Items).CanonicalId);
        Assert.Equal(requestsBefore, tmdb.MovieCalls.Count);
    }

    [Fact]
    public async Task CalendarTracesPersistCorrelatedLoadSourceEnrichmentAndPartialCacheDecisions()
    {
        var directory = Path.Combine(Path.GetTempPath(), "premiere-calendar-trace-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var store = new PerformanceDiagnosticStore(new TelemetryOptions(), directory);
            var telemetry = new PremiereTelemetry(store);
            var pageContext = telemetry.CreateDiagnosticContext();
            var tmdb = new FakeTmdbClient
            {
                FilterMovieItemsByDate = true,
                MovieItems = [new() { Id = 31, Title = "PRIVATE-FIXTURE-TITLE", ReleaseDate = "2026-08-31", VoteAverage = 7 }]
            };
            var service = CreateService(tmdb, discoveryProviders: [new DiagnosticFailingProvider()],
                calendarCache: new PartialSnapshotCache(), telemetry: telemetry);
            IAsyncEnumerable<PremiereLoadProgress> stream;
            using (telemetry.UseDiagnosticContext(pageContext))
                stream = service.StreamPremieresAsync(new(2026, 8, 31), new(2026, 9, 6), filters: new() { ShowSeries = false });
            Assert.Null(telemetry.CaptureDiagnosticContext());
            await foreach (var _ in stream) Assert.Null(telemetry.CaptureDiagnosticContext());
            telemetry.FlushDiagnostics();
            store.Dispose();
            var lines = Directory.GetFiles(Path.Combine(directory, "App_Data", "logs", "performance"))
                .SelectMany(File.ReadAllLines).ToArray();
            Assert.DoesNotContain(lines, line => line.Contains("PRIVATE-FIXTURE-TITLE", StringComparison.Ordinal));
            var rows = lines.Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                Assert.All(rows, row => Assert.Equal(pageContext.LoadId, row.RootElement.GetProperty("loadId").GetString()));
                var events = rows.Select(row => row.RootElement.GetProperty("event").GetString()).ToArray();
                Assert.Contains("calendar.load.started", events);
                Assert.Contains("calendar.load.first_result", events);
                Assert.Contains("calendar.cache.read", events);
                Assert.Contains("calendar.cache.partial_publication", events);
                Assert.Contains("source.finished", events);
                Assert.Contains("enrichment.item.started", events);
                Assert.Contains(rows, row => row.RootElement.GetProperty("event").GetString() == "calendar.load.finished"
                    && row.RootElement.GetProperty("details").GetProperty("outcome").GetString() == "partial");
                Assert.Contains(rows, row => row.RootElement.TryGetProperty("operationId", out var id) && Guid.TryParse(id.GetString(), out _));
            }
            finally { foreach (var row in rows) row.Dispose(); }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class PendingThenThrottledProvider(DateTimeOffset deadline)
        : INamedPremiereDiscoveryProvider, IStreamingPremiereDiscoveryProvider
    {
        public string DisplayName => "ThrottledFixture";
        public Task<IReadOnlyList<ExternalPremiereCandidate>> GetCandidatesAsync(DateOnly start, DateOnly end,
            CancellationToken cancellationToken, bool forceRefresh = false) => throw new NotSupportedException();
        public async IAsyncEnumerable<IReadOnlyList<ExternalPremiereCandidate>> StreamCandidatesAsync(
            DateOnly start, DateOnly end, bool forceRefresh = false,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return [new(PremiereMediaType.Movie, start, "Fixture", 31, null, null, "Fixture")];
            await Task.Yield();
            throw new ProviderRetryAfterException("fixture", deadline);
        }
    }

    private sealed class DiagnosticFailingProvider : INamedPremiereDiscoveryProvider
    {
        public string DisplayName => "Simkl";
        public Task<IReadOnlyList<ExternalPremiereCandidate>> GetCandidatesAsync(DateOnly start, DateOnly end,
            CancellationToken cancellationToken, bool forceRefresh = false)
            => Task.FromException<IReadOnlyList<ExternalPremiereCandidate>>(new ExternalApiException("Fixture unavailable"));
    }

    private sealed class PartialThenFailedProvider : INamedPremiereDiscoveryProvider, IStreamingPremiereDiscoveryProvider
    {
        public string DisplayName => "Simkl";
        public Task<IReadOnlyList<ExternalPremiereCandidate>> GetCandidatesAsync(DateOnly start, DateOnly end,
            CancellationToken cancellationToken, bool forceRefresh = false) => throw new NotSupportedException();
        public async IAsyncEnumerable<IReadOnlyList<ExternalPremiereCandidate>> StreamCandidatesAsync(
            DateOnly start, DateOnly end, bool forceRefresh = false,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return Enumerable.Range(1, 31).Select(id => new ExternalPremiereCandidate(
                PremiereMediaType.Movie, start, $"Movie {id}", id, null, null, "Simkl")).ToArray();
            await Task.Yield();
            throw new ExternalApiException("Required feed fixture failure");
        }
    }

    private sealed class IndependentThenStalledProvider : INamedPremiereDiscoveryProvider, IStreamingPremiereDiscoveryProvider
    {
        public string DisplayName => "Simkl";
        public bool PreserveBatchBoundaries => true;
        public bool CleanupObserved { get; private set; }
        public Task<IReadOnlyList<ExternalPremiereCandidate>> GetCandidatesAsync(DateOnly start, DateOnly end,
            CancellationToken cancellationToken, bool forceRefresh = false) => throw new NotSupportedException();
        public async IAsyncEnumerable<IReadOnlyList<ExternalPremiereCandidate>> StreamCandidatesAsync(
            DateOnly start, DateOnly end, bool forceRefresh = false,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                yield return Enumerable.Range(1, 31).Select(id => new ExternalPremiereCandidate(
                    PremiereMediaType.Movie, start, $"Movie {id}", id, null, null, "Simkl")).ToArray();
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally { CleanupObserved = true; }
        }
    }

    private sealed class PartialSnapshotCache : ICalendarCache
    {
        public IReadOnlyList<PremiereItem>? Items { get; private set; }
        public int PartialWrites { get; private set; }
        public Task<IReadOnlyList<PremiereItem>?> GetWeekAsync(DateOnly start, DateOnly end, string cacheKey,
            CancellationToken cancellationToken, bool allowExpired = false)
            => Task.FromResult(allowExpired ? Items : null);
        public Task SetPartialWeekAsync(DateOnly start, DateOnly end, string cacheKey, IReadOnlyList<PremiereItem> items,
            CancellationToken cancellationToken)
        { Items = items; PartialWrites++; return Task.CompletedTask; }
        public Task SetWeekAsync(DateOnly start, DateOnly end, string cacheKey, IReadOnlyList<PremiereItem> items,
            CancellationToken cancellationToken) => throw new InvalidOperationException("Failed sources cannot produce a complete cache.");
    }
}
