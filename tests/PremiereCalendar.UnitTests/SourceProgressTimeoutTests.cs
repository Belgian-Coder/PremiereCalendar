using System.Diagnostics;
using System.Runtime.CompilerServices;
using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class PremiereServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SlowEnrichmentProgressRenewsBudgetEvenAfterMetadataWasAlreadyEmitted(bool external)
    {
        var start = new DateOnly(2026, 8, 31);
        var tmdb = new FakeTmdbClient
        {
            MovieDetailDelay = TimeSpan.FromMilliseconds(200),
            FilterMovieItemsByDate = true,
            MovieItems = external ? [] : Enumerable.Range(1, 6).Select(id => new TmdbMovieDiscoverItem
            { Id = id, Title = "Fixture", ReleaseDate = "2026-08-31" }).ToList()
        };
        var service = CreateService(tmdb, discoveryProviders: external ? [new ProgressDeadlineProvider("mapping")] : [],
            sourceTimeoutSeconds: 1, maxEnrichmentConcurrency: 1, enrichmentProgressBatchSize: 25);
        PremiereLoadProgress? final = null;
        var timer = Stopwatch.StartNew();
        await foreach (var update in service.StreamPremieresAsync(start, start,
            filters: new() { ShowSeries = false })) if (update.IsFinal) final = update;
        Assert.True(timer.Elapsed > TimeSpan.FromSeconds(1));
        Assert.NotNull(final);
        Assert.False(final.HasSourceErrors);
        Assert.Equal(6, final.Items.Count);
    }

    [Fact]
    public async Task SourceThatReturnsAfterItsCanceledDeadlineRetainsEarlierRowsAsPartial()
    {
        var provider = new ProgressDeadlineProvider("late");
        var cache = new FakeCalendarCache();
        var service = CreateService(new FakeTmdbClient(), discoveryProviders: [provider],
            calendarCache: cache, sourceTimeoutSeconds: 1);
        PremiereLoadProgress? final = null;
        await foreach (var update in service.StreamPremieresAsync(new(2026, 8, 31), new(2026, 9, 6),
            filters: new() { ShowSeries = false })) if (update.IsFinal) final = update;
        Assert.NotNull(final);
        Assert.True(final.HasSourceErrors);
        Assert.Single(final.Items);
        Assert.Empty(cache.SetCalls);
        Assert.True(provider.CleanupObserved);
    }

    [Fact]
    public async Task SlowConsumerDoesNotConsumeTheNextSourceProgressBudget()
    {
        var provider = new ProgressDeadlineProvider();
        var service = CreateService(new FakeTmdbClient(), discoveryProviders: [provider], sourceTimeoutSeconds: 1);
        var delayed = false;
        PremiereLoadProgress? final = null;
        await foreach (var update in service.StreamPremieresAsync(new(2026, 8, 31), new(2026, 9, 6),
            filters: new() { ShowSeries = false }))
        {
            if (!delayed && update.Items.Count > 0 && !update.IsFinal)
            {
                delayed = true;
                await Task.Delay(1250);
            }
            if (update.IsFinal) final = update;
        }
        Assert.True(delayed);
        Assert.NotNull(final);
        Assert.False(final.HasSourceErrors);
        Assert.Equal(6, final.Items.Count);
    }

    [Fact]
    public async Task ProgressingExternalSourceCanFinishBeyondOneBatchTimeoutAndPublishCompleteCache()
    {
        var cache = new FakeCalendarCache();
        var provider = new ProgressDeadlineProvider();
        var service = CreateService(new FakeTmdbClient(), discoveryProviders: [provider],
            calendarCache: cache, sourceTimeoutSeconds: 1);
        var updates = new List<PremiereLoadProgress>();
        var timer = Stopwatch.StartNew();
        await foreach (var update in service.StreamPremieresAsync(new(2026, 8, 31), new(2026, 9, 6),
            filters: new() { ShowSeries = false })) updates.Add(update);
        Assert.True(timer.Elapsed > TimeSpan.FromSeconds(1));
        Assert.True(updates[^1].IsFinal);
        Assert.False(updates[^1].HasSourceErrors);
        Assert.Equal(6, updates[^1].Items.Count);
        Assert.NotEmpty(cache.SetCalls);
        Assert.True(provider.CleanupObserved);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RepeatedOrEmptyBatchesDoNotKeepAnIdleExternalSourceAlive(bool duplicate)
    {
        var provider = new ProgressDeadlineProvider(duplicate ? "duplicate" : "empty");
        var service = CreateService(new FakeTmdbClient(), discoveryProviders: [provider], sourceTimeoutSeconds: 1);
        var updates = new List<PremiereLoadProgress>();
        var timer = Stopwatch.StartNew();
        await foreach (var update in service.StreamPremieresAsync(new(2026, 8, 31), new(2026, 9, 6),
            filters: new() { ShowSeries = false })) updates.Add(update);
        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(4));
        Assert.True(updates[^1].HasSourceErrors);
        Assert.Single(updates[^1].Items);
        Assert.True(provider.CleanupObserved);
    }

    [Fact]
    public async Task CallerCanCancelAnExternalSourceThatKeepsMakingProgress()
    {
        var provider = new ProgressDeadlineProvider();
        var service = CreateService(new FakeTmdbClient(), discoveryProviders: [provider], sourceTimeoutSeconds: 1);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(850));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in service.StreamPremieresAsync(new(2026, 8, 31), new(2026, 9, 6),
                filters: new() { ShowSeries = false }, cancellationToken: cancellation.Token)) { }
        });
        Assert.True(provider.CleanupObserved);
    }

    private sealed class ProgressDeadlineProvider(string mode = "progress")
        : INamedPremiereDiscoveryProvider, IStreamingPremiereDiscoveryProvider
    {
        public string DisplayName => "ProgressFixture";
        public bool PreserveBatchBoundaries => true;
        public bool CleanupObserved { get; private set; }
        public Task<IReadOnlyList<ExternalPremiereCandidate>> GetCandidatesAsync(DateOnly start, DateOnly end,
            CancellationToken cancellationToken, bool forceRefresh = false) => throw new NotSupportedException();
        public async IAsyncEnumerable<IReadOnlyList<ExternalPremiereCandidate>> StreamCandidatesAsync(
            DateOnly start, DateOnly end, bool forceRefresh = false,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            try
            {
                if (mode == "mapping")
                {
                    yield return Enumerable.Range(1, 6).Select(id => new ExternalPremiereCandidate(
                        PremiereMediaType.Movie, start, "Fixture", id, null, null, DisplayName)).ToArray();
                    yield break;
                }
                for (var index = 1; index <= (mode == "progress" ? 6 : 100); index++)
                {
                    if (index > 1)
                    {
                        if (mode == "late") await Task.Delay(1200); // Model a dependency returning after cancellation.
                        else await Task.Delay(350, cancellationToken);
                    }
                    yield return mode == "empty" && index > 1 ? []
                        : [new(PremiereMediaType.Movie, start, "Fixture", mode == "duplicate" ? 1 : index,
                            null, null, DisplayName)];
                }
            }
            finally { CleanupObserved = true; }
        }
    }
}
