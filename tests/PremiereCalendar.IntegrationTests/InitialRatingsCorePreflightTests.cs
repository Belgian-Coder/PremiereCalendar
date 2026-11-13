using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed partial class PostgresPersistenceTests
{
    [Fact]
    public Task PostgreSQL_InitialRatingsCorePreflightRetriesTheFullSuccessfulProposal()
        => VerifyInitialRatingsCorePreflightAsync(new ItemEnrichmentStore(_database.Options), foreignWrite: false);

    [Fact]
    public Task PostgreSQL_InitialRatingsCorePreflightCannotCrossAnInterveningForeignWrite()
        => VerifyInitialRatingsCorePreflightAsync(new ItemEnrichmentStore(_database.Options), foreignWrite: true);

    private static async Task VerifyInitialRatingsCorePreflightAsync(IItemEnrichmentStore store, bool foreignWrite)
    {
        var observedStore = new InitialCoreRetryStore(store);
        var coordinator = new ItemEnrichmentCacheCoordinator(observedStore,
            Microsoft.Extensions.Options.Options.Create(new ItemEnrichmentCacheOptions()),
            Microsoft.Extensions.Options.Options.Create(new TmdbOptions()), NullLogger<ItemEnrichmentCacheCoordinator>.Instance);
        var partition = coordinator.Partition;
        var identity = new ItemEnrichmentIdentity(PremiereMediaType.Movie, 901);
        await using var session = await coordinator.CreateSessionAsync(default);
        await session.PrepareAsync([identity], null, default);
        var reserved = session.Find(identity)!.Record;
        var clock = reserved.LastSeenUtc;
        var core = new ItemEnrichmentMetadata
        {
            Title = "Initial ratings fixture", ImdbId = "tt0000901", WikidataId = "Q901",
            RuntimeMinutes = 110, OriginalLanguage = "en", CoreMetadataOnly = true,
            AvailabilityComplete = false, OptionalMetadataComplete = false, ArtworkComplete = false,
            OptionalFreshnessTracked = true
        };
        var coreJson = JsonSerializer.Serialize(core);
        var preflight = Assert.Single((await store.WriteAcceptedAsync(partition,
            [new(identity, reserved.Generation, coreJson, clock, ExpectedMetadataRevision: reserved.MetadataRevision)], default)).Values);
        Assert.Equal(1, preflight.MetadataRevision);
        var complete = core with
        {
            CoreMetadataOnly = false, AvailabilityComplete = true, OptionalMetadataComplete = true, ArtworkComplete = true,
            AvailabilityOwnedByWatchmode = true, AvailabilityFetchedUtc = clock, ArtworkFetchedUtc = clock,
            PosterUrl = "https://fixture.invalid/poster.jpg", ImageSource = ArtworkSources.Fanart,
            Sources = [new() { Id = 31, Name = "Fixture availability", Kind = "flatrate" }],
            OmdbPlot = "Successful OMDb plot", OmdbPosterUrl = "https://fixture.invalid/omdb.jpg"
        };
        var ratings = new ItemEnrichmentRatings(8.2, 12_345, 91, 86, 72,
            core.ImdbId, core.WikidataId, core.Title, 2026)
        {
            ProviderFreshnessTracked = true, ExternalFetchedUtc = clock, TomatoesFetchedUtc = clock,
            ExternalValues = new(ImdbScore: 8.2, ImdbVoteCount: 12_345, MetacriticScore: 72),
            TomatoesValues = new(RottenTomatoesScore: 91, RottenTomatoesAudienceScore: 86)
        };
        var providerCalls = 0;
        await session.GetAsync(identity, true, (_, _, _, _) =>
        {
            providerCalls++;
            return Task.FromResult(new ItemEnrichmentRefresh(complete, ratings, FreshMovieCoreJson: coreJson));
        }, default);
        ItemEnrichmentRecord? foreign = null;
        observedStore.BeforeWrite = async (writes, token) =>
        {
            if (foreignWrite && Assert.Single(writes).ExpectedMetadataRevision == preflight.MetadataRevision)
            {
                foreign = Assert.Single((await store.WriteAcceptedAsync(partition,
                    [new(identity, preflight.Generation, JsonSerializer.Serialize(core with { Title = "Manual repair", RuntimeMinutes = 99 }),
                        clock, ExpectedMetadataRevision: preflight.MetadataRevision)], token)).Values);
            }
        };
        await session.FlushAsync(default);
        var persisted = Assert.Single((await store.ReadCurrentManyAsync(partition, [identity], default)).Values);
        Assert.Equal(1, providerCalls);
        Assert.Equal([0L, 1L], observedStore.Attempts.Select(batch => Assert.Single(batch).ExpectedMetadataRevision!.Value));
        Assert.Equal(reserved.Generation, persisted.Generation);
        Assert.Equal(2, persisted.MetadataRevision);
        Assert.Equal(clock, persisted.MetadataFetchedUtc);
        if (foreignWrite)
        {
            Assert.NotNull(foreign);
            Assert.Equal(foreign, persisted);
            Assert.Null(persisted.RatingsJson);
            Assert.Null(persisted.RatingsFetchedUtc);
        }
        else
        {
            Assert.Equal(JsonSerializer.Serialize(complete), persisted.MetadataJson);
            Assert.Equal(JsonSerializer.Serialize(ratings), persisted.RatingsJson);
            Assert.Equal(clock, persisted.RatingsFetchedUtc);
        }
    }

    [Fact]
    public Task PostgreSQL_FreshContinuationRatingsFencePreservesRepairsInsideTheRetryTransaction()
        => VerifyContinuationRatingsFenceAsync(new ItemEnrichmentStore(_database.Options), fresh: true);

    [Fact]
    public Task PostgreSQL_NormalContinuationRatingsFencePreservesRepairsInsideTheRetryTransaction()
        => VerifyContinuationRatingsFenceAsync(new ItemEnrichmentStore(_database.Options), fresh: false);

    private static async Task VerifyContinuationRatingsFenceAsync(IItemEnrichmentStore store, bool fresh)
    {
        foreach (var baselineKind in new[] { "null", "payload", "clock" })
        {
            var observedStore = new InitialCoreRetryStore(store);
            var coordinator = new ItemEnrichmentCacheCoordinator(observedStore,
                Microsoft.Extensions.Options.Options.Create(new ItemEnrichmentCacheOptions()),
                Microsoft.Extensions.Options.Options.Create(new TmdbOptions()), NullLogger<ItemEnrichmentCacheCoordinator>.Instance);
            var partition = coordinator.Partition;
            var identity = new ItemEnrichmentIdentity(PremiereMediaType.Movie, baselineKind == "null" ? 910 : baselineKind == "payload" ? 911 : 912);
            var reservation = Assert.Single((await store.ReadManyAsync(partition, [identity], true, default)).Values);
            var seedClock = reservation.LastSeenUtc.AddHours(-2);
            var core = new ItemEnrichmentMetadata { Title = "Atomic persisted ratings", ImdbId = "tt901", OriginalLanguage = "en",
                RuntimeMinutes = 90, CoreMetadataOnly = true, ArtworkComplete = false, OptionalMetadataComplete = false, AvailabilityComplete = false };
            var seed = new ItemEnrichmentRatings(6.5, 6500, 70, 71, 72, "tt901", null, core.Title, 2026, FreshnessVerified: false);
            if (baselineKind != "null") await store.WriteAcceptedAsync(partition,
                [new(identity, reservation.Generation, RatingsJson: JsonSerializer.Serialize(seed), RatingsFetchedUtc: seedClock,
                    ExpectedMetadataRevision: reservation.MetadataRevision)], default);
            await using var session = await coordinator.CreateSessionAsync(default);
            await session.PrepareAsync([identity], null, default);
            var clock = session.Find(identity)!.Record.LastSeenUtc;
            var complete = core with { CoreMetadataOnly = false, ArtworkComplete = true, OptionalMetadataComplete = true,
                AvailabilityComplete = true, PosterUrl = "https://fixture.invalid/complete.jpg" };
            var proposed = new ItemEnrichmentRatings(8.4, 8400, 94, 96, null, "tt901", null, core.Title, 2026)
            {
                ProviderFreshnessTracked = true, ExternalFetchedUtc = clock, TomatoesFetchedUtc = clock,
                ExternalValues = new(ImdbScore: 8.4, ImdbVoteCount: 8400),
                TomatoesValues = new(RottenTomatoesScore: 94, RottenTomatoesAudienceScore: 96)
            };
            if (!fresh) await session.GetAsync(identity, false, (_, _, _, _) =>
                Task.FromResult(new ItemEnrichmentRefresh(core, null, RefreshRatingsClock: false)), default, metadataOnly: true);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var optional = session.GetAsync(identity, fresh, async (_, _, _, token) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return new(complete, proposed, RefreshMetadataClock: fresh,
                    FreshMovieCoreJson: fresh ? JsonSerializer.Serialize(core) : null);
            }, default);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (fresh) await store.WriteAcceptedAsync(partition,
                    [new(identity, reservation.Generation, JsonSerializer.Serialize(core), clock, ExpectedMetadataRevision: 0)], default);
                else await session.FlushAsync(default);
            }
            finally { release.TrySetResult(); }
            await optional.WaitAsync(TimeSpan.FromSeconds(5));
            ItemEnrichmentRecord? repaired = null;
            observedStore.BeforeWrite = async (writes, token) =>
            {
                if (Assert.Single(writes).ExpectedMetadataRevision != 1) return;
                var before = Assert.Single((await store.ReadCurrentManyAsync(partition, [identity], token)).Values);
                var json = baselineKind == "clock" ? before.RatingsJson! : JsonSerializer.Serialize(seed with { ImdbScore = 9.9 });
                var fetched = baselineKind == "clock" ? before.RatingsFetchedUtc!.Value.AddMinutes(1)
                    : before.RatingsFetchedUtc ?? clock.AddHours(-1);
                repaired = Assert.Single((await store.WriteAcceptedAsync(partition,
                    [new(identity, before.Generation, RatingsJson: json, RatingsFetchedUtc: fetched,
                        ExpectedMetadataRevision: before.MetadataRevision)], token)).Values);
                Assert.Equal(1, repaired.MetadataRevision); // Ratings-only repair never advances metadata CAS.
            };
            await session.FlushAsync(default);
            var accepted = Assert.Single((await store.ReadCurrentManyAsync(partition, [identity], default)).Values);
            Assert.NotNull(repaired);
            Assert.Equal(2, accepted.MetadataRevision);
            Assert.Equal(JsonSerializer.Serialize(complete), accepted.MetadataJson);
            Assert.Equal(repaired.RatingsJson, accepted.RatingsJson);
            Assert.Equal(repaired.RatingsFetchedUtc, accepted.RatingsFetchedUtc);
            Assert.True(session.Find(identity)!.PreserveRatings);
        }
    }

    private sealed class InitialCoreRetryStore(IItemEnrichmentStore inner) : IItemEnrichmentStore
    {
        public List<ItemEnrichmentWrite[]> Attempts { get; } = [];
        public Func<IReadOnlyList<ItemEnrichmentWrite>, CancellationToken, Task>? BeforeWrite;
        public Task<IReadOnlyDictionary<ItemEnrichmentIdentity, ItemEnrichmentRecord>> ReadManyAsync(string partition,
            IReadOnlyCollection<ItemEnrichmentIdentity> identities, bool reserveMissing, CancellationToken token)
            => inner.ReadManyAsync(partition, identities, reserveMissing, token);
        public Task<IReadOnlyDictionary<ItemEnrichmentIdentity, ItemEnrichmentRecord>> ReadCurrentManyAsync(string partition,
            IReadOnlyCollection<ItemEnrichmentIdentity> identities, CancellationToken token)
            => inner.ReadCurrentManyAsync(partition, identities, token);
        public Task<int> WriteManyAsync(string partition, IReadOnlyList<ItemEnrichmentWrite> writes, CancellationToken token)
            => inner.WriteManyAsync(partition, writes, token);
        public async Task<IReadOnlyDictionary<ItemEnrichmentIdentity, ItemEnrichmentRecord>> WriteAcceptedAsync(string partition,
            IReadOnlyList<ItemEnrichmentWrite> writes, CancellationToken token)
        {
            Attempts.Add(writes.ToArray());
            if (BeforeWrite is { } before) await before(writes, token);
            return await inner.WriteAcceptedAsync(partition, writes, token);
        }
        public Task<int> InvalidateTmdbAsync(PremiereMediaType mediaType, IReadOnlyCollection<int> ids, DateTimeOffset changed, CancellationToken token)
            => inner.InvalidateTmdbAsync(mediaType, ids, changed, token);
        public Task<int> InvalidateTvmazeAsync(IReadOnlyCollection<int> ids, DateTimeOffset changed, CancellationToken token)
            => inner.InvalidateTvmazeAsync(ids, changed, token);
        public Task<int> CleanupAsync(DateTimeOffset cutoff, CancellationToken token) => inner.CleanupAsync(cutoff, token);
    }
}
