using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class PremiereServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResumedSourceCheckpointIsReusedOnlyWhileItemGenerationRemainsValid(bool invalidate)
    {
        using var files = new DegradedCoverageFiles();
        var date = new DateOnly(2026, 11, 2);
        var clock = new ItemCacheClock();
        var store = new MemoryItemEnrichmentStore(clock);
        var coordinator = ItemCacheCoordinator(store, clock);
        var filters = new CalendarFilters { ShowSeries = false };
        var initial = CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"),
            calendarCache: files.CreateCache(), itemEnrichmentCache: coordinator);
        var checkpoints = new Dictionary<string, IReadOnlyList<PremiereItem>>(StringComparer.Ordinal);
        await foreach (var update in initial.StreamCoreAsync(date, date, filters: filters))
        {
            var key = update.CheckpointKey;
            if (update.Phase != "complete" || update.IsFinal || update.HasSourceErrors || key is null
                || !update.SourceName.StartsWith("TMDb movies", StringComparison.Ordinal)) continue;
            checkpoints[key] = update.SourceItems;
            break; // Preemption after the source checkpoint, before final week publication.
        }
        Assert.Single(checkpoints);
        Assert.Equal(112, Assert.Single(checkpoints.Values.Single()).RuntimeMinutes);
        clock.Advance(TimeSpan.FromMinutes(1));
        if (invalidate)
            await store.InvalidateTmdbAsync(PremiereMediaType.Movie, [901], clock.GetUtcNow(), CancellationToken.None);
        var fresh = ItemCacheMovie(date);
        fresh.MovieDetailsById[901] = fresh.MovieDetailsById[901] with { Runtime = 144 };
        var trakt = new DegradedTraktClient();
        var resumed = CreateService(fresh, omdb: ItemCacheOmdb("7.1"), calendarCache: files.CreateCache(),
            discoveryProviders: [new TraktDiscoveryProvider(trakt)], itemEnrichmentCache: coordinator);
        PremiereLoadProgress? final = null;
        await foreach (var update in resumed.StreamCoreAsync(date, date, filters: filters,
                           resumeState: new ProviderWorkResumeState(checkpoints, null)))
            if (update.IsFinal) final = update;

        Assert.NotNull(final);
        Assert.True(final.HasSourceErrors);
        Assert.True(final.IsDegradedResult);
        Assert.True(final.IsReusableDegraded);
        Assert.DoesNotContain("Item cache validation", final.FailedSourceNames);
        Assert.Equal("trakt", Assert.Single(final.UnavailableSources).Provider);
        var item = Assert.Single(final.Items);
        Assert.Equal(invalidate ? 144 : 112, item.RuntimeMinutes);
        Assert.Equal(invalidate ? 1 : 0, item.EnrichmentVersion?.Generation);
        if (invalidate)
        {
            Assert.NotEmpty(fresh.MovieCalls);
            Assert.All(fresh.MovieCalls, call => Assert.True(call.ForceRefresh));
            Assert.True(Assert.Single(fresh.MovieDetailCalls).ForceRefresh);
        }
        else
        {
            Assert.Empty(fresh.MovieCalls);
            Assert.Empty(fresh.MovieDetailCalls);
        }
        var snapshot = await files.ReadAsync(date);
        Assert.False(snapshot.IsComplete);
        Assert.True(snapshot.CanReuseDegraded(DateTimeOffset.UtcNow));
        Assert.Equal(item.EnrichmentVersion, Assert.Single(snapshot.Items).EnrichmentVersion);
    }
}
