using System.Text.Json;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class PremiereServiceTests
{
    [Theory]
    [InlineData("refreshed", false)]
    [InlineData("cached", false)]
    [InlineData("same-flight-joiner", false)]
    [InlineData("unaccepted-proposal", false)]
    [InlineData("refreshed", true)]
    public async Task AcceptedTomatoesForEachYearSurviveForeignPublicationBeforeReconciliation(string mode, bool newerOmdbOwnsTomatoes)
    {
        var fixture = await AcceptedTomatoesFixture.CreateAsync();
        await using var firstSession = await fixture.NewSessionAsync();
        var projectionSession = firstSession;
        ItemEnrichmentCacheCoordinator.ItemEnrichmentSession? joinedSession = null;
        var firstItem = firstSession.Find(fixture.Identity)!.Apply(fixture.First);
        var providerCalls = 0;
        if (mode == "same-flight-joiner")
        {
            joinedSession = await fixture.NewSessionAsync();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var first = firstSession.GetAsync(fixture.Identity, true, async (previous, _, _, token) =>
            {
                providerCalls++;
                entered.SetResult();
                await release.Task.WaitAsync(token);
                return fixture.Refresh(previous, fixture.First, new( RottenTomatoesScore: 83, RottenTomatoesAudienceScore: 91));
            }, default, ratingsOnly: true, ratingsInputs: fixture.First);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var joined = joinedSession.GetAsync(fixture.Identity, true, (previous, _, _, _) =>
            {
                providerCalls++;
                return Task.FromResult(fixture.Refresh(previous, fixture.First, new(RottenTomatoesScore: 1)));
            }, default, ratingsOnly: true, ratingsInputs: fixture.First);
            release.SetResult();
            await first;
            firstItem = (await joined).Apply(fixture.First);
            await firstSession.FlushAsync(default);
            await joinedSession.FlushAsync(default);
            projectionSession = joinedSession;
        }
        else if (mode != "cached")
        {
            providerCalls++;
            var outcome = await firstSession.GetAsync(fixture.Identity, true,
                (previous, _, _, _) => Task.FromResult(fixture.Refresh(previous, fixture.First,
                    new(RottenTomatoesScore: 83, RottenTomatoesAudienceScore: 91))),
                default, ratingsOnly: true, ratingsInputs: fixture.First);
            firstItem = outcome.Apply(fixture.First);
            if (mode != "unaccepted-proposal") await firstSession.FlushAsync(default);
        }
        Assert.Equal(mode == "cached" ? 0 : 1, providerCalls);
        Assert.Equal(mode is "cached" or "unaccepted-proposal" ? 0 : mode == "same-flight-joiner" ? 2 : 1, fixture.Store.Writes);

        fixture.Store.PauseNextRead();
        var firstReconciliation = projectionSession.ReconcileAsync([firstItem], false, default);
        try
        {
            await fixture.Store.ReadPaused.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
            await using var secondSession = await fixture.NewSessionAsync();
            var external = new ItemEnrichmentRatingValues(ImdbScore: 8.8, ImdbVoteCount: 4000,
                RottenTomatoesScore: newerOmdbOwnsTomatoes ? 99 : null,
                RottenTomatoesAudienceScore: newerOmdbOwnsTomatoes ? 98 : null, MetacriticScore: 82);
            var second = await secondSession.GetAsync(fixture.Identity, true,
                (previous, _, _, _) => Task.FromResult(fixture.Refresh(previous, fixture.Second,
                    new(RottenTomatoesScore: 94, RottenTomatoesAudienceScore: 96), external)),
                default, ratingsOnly: true, ratingsInputs: fixture.Second);
            await secondSession.FlushAsync(default);
            var secondRow = Assert.Single((await secondSession.ReconcileAsync([second.Apply(fixture.Second)], false, default)).Items);
            var winner = Assert.Single(fixture.Memory.Records.Values);
            var writes = fixture.Store.Writes;
            fixture.Store.ResumeRead.TrySetResult();
            var reconciliation = await firstReconciliation.WaitAsync(TimeSpan.FromSeconds(5));
            var firstRow = Assert.Single(reconciliation.Items);

            Assert.Empty(reconciliation.DirtyMedia);
            Assert.Equal(newerOmdbOwnsTomatoes ? 99 : mode is "cached" or "unaccepted-proposal" ? 11 : 83, firstRow.RottenTomatoesScore);
            Assert.Equal(newerOmdbOwnsTomatoes ? 98 : mode is "cached" or "unaccepted-proposal" ? 12 : 91, firstRow.RottenTomatoesAudienceScore);
            Assert.Equal(newerOmdbOwnsTomatoes ? 99 : 94, secondRow.RottenTomatoesScore);
            Assert.Equal(newerOmdbOwnsTomatoes ? 98 : 96, secondRow.RottenTomatoesAudienceScore);
            Assert.Equal(8.8, firstRow.ImdbScore);
            Assert.Equal(4000, firstRow.ImdbVoteCount);
            Assert.Equal(82, firstRow.MetacriticScore);
            Assert.Equal(fixture.First.PremiereDate, firstRow.PremiereDate);
            Assert.Equal(fixture.Second.PremiereDate, secondRow.PremiereDate);
            Assert.Equal(fixture.First.Sources, firstRow.Sources);
            Assert.Equal(fixture.First.SourceNames, firstRow.SourceNames);
            Assert.Equal(fixture.MetadataClock, firstRow.LastUpdatedUtc);
            Assert.Equal(writes, fixture.Store.Writes);
            var stillWinner = Assert.Single(fixture.Memory.Records.Values);
            Assert.Equal(winner.MetadataJson, stillWinner.MetadataJson);
            Assert.Equal(winner.RatingsJson, stillWinner.RatingsJson);
            Assert.Equal(winner.RatingsFetchedUtc, stillWinner.RatingsFetchedUtc);
            var storedRatings = JsonSerializer.Deserialize<ItemEnrichmentRatings>(stillWinner.RatingsJson!)!;
            Assert.Equal(2026, storedRatings.Year);
            Assert.Equal(94, storedRatings.TomatoesValues?.RottenTomatoesScore);
            Assert.Equal(fixture.Clock.GetUtcNow(), storedRatings.ExternalFetchedUtc);
            Assert.Equal(fixture.Clock.GetUtcNow(), storedRatings.TomatoesFetchedUtc);
        }
        finally
        {
            fixture.Store.ResumeRead.TrySetResult();
            await firstReconciliation.WaitAsync(TimeSpan.FromSeconds(5));
            if (joinedSession is not null) await joinedSession.DisposeAsync();
        }
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("revision")]
    [InlineData("manual-foreign")]
    [InlineData("manual-same")]
    [InlineData("same-binding-empty")]
    [InlineData("already-invalid")]
    public async Task AcceptedTomatoesProofCannotCrossInvalidationRepairOrSameBindingAbsence(string change)
    {
        var fixture = await AcceptedTomatoesFixture.CreateAsync();
        if (change == "already-invalid")
        {
            var pair = Assert.Single(fixture.Memory.Records);
            fixture.Memory.Records[pair.Key] = pair.Value with
                { Generation = pair.Value.Generation + 1, MetadataInvalidatedUtc = fixture.Clock.GetUtcNow() };
        }
        await using var first = await fixture.NewSessionAsync();
        var item = first.Find(fixture.Identity)!.Apply(fixture.First);
        if (change != "already-invalid")
        {
            item = (await first.GetAsync(fixture.Identity, true,
                (previous, _, _, _) => Task.FromResult(fixture.Refresh(previous, fixture.First,
                    new(RottenTomatoesScore: 83, RottenTomatoesAudienceScore: 91))),
                default, ratingsOnly: true, ratingsInputs: fixture.First)).Apply(fixture.First);
            await first.FlushAsync(default);
        }
        fixture.Clock.Advance(TimeSpan.FromMinutes(1));
        await using var second = await fixture.NewSessionAsync();
        await second.GetAsync(fixture.Identity, true,
            (previous, _, _, _) => Task.FromResult(fixture.Refresh(previous, fixture.Second,
                new(RottenTomatoesScore: 94, RottenTomatoesAudienceScore: 96))),
            default, ratingsOnly: true, ratingsInputs: fixture.Second);
        await second.FlushAsync(default);
        var current = Assert.Single(fixture.Memory.Records);
        var ratings = JsonSerializer.Deserialize<ItemEnrichmentRatings>(current.Value.RatingsJson!)!;
        var changed = change switch
        {
            "generation" => current.Value with { Generation = current.Value.Generation + 1, MetadataInvalidatedUtc = fixture.Clock.GetUtcNow() },
            "revision" => current.Value with { MetadataRevision = current.Value.MetadataRevision + 1 },
            "manual-foreign" or "manual-same" => current.Value with { RatingsJson = JsonSerializer.Serialize(ratings with
                { FreshnessVerified = false, Year = change == "manual-same" ? 2025 : 2026, RottenTomatoesScore = 97, RottenTomatoesAudienceScore = 98 }) },
            "same-binding-empty" => current.Value with { RatingsJson = JsonSerializer.Serialize(ratings with
                { Year = 2025, RottenTomatoesScore = null, RottenTomatoesAudienceScore = null, TomatoesValues = new(), TomatoesFetchedUtc = fixture.Clock.GetUtcNow() }) },
            _ => current.Value
        };
        fixture.Memory.Records[current.Key] = changed;
        var writes = fixture.Store.Writes;

        var result = Assert.Single((await first.ReconcileAsync([item], false, default)).Items);

        Assert.Equal(change == "manual-same" ? 97 : (int?)null, result.RottenTomatoesScore);
        Assert.Equal(change == "manual-same" ? 98 : (int?)null, result.RottenTomatoesAudienceScore);
        Assert.Equal(writes, fixture.Store.Writes);
        Assert.Equal(changed.RatingsJson, Assert.Single(fixture.Memory.Records.Values).RatingsJson);
    }

    private sealed class AcceptedTomatoesFixture
    {
        public ItemCacheClock Clock { get; } = new();
        public MemoryItemEnrichmentStore Memory { get; private set; } = null!;
        public AcceptedProjectionStore Store { get; private set; } = null!;
        public ItemEnrichmentCacheCoordinator Coordinator { get; private set; } = null!;
        public ItemEnrichmentIdentity Identity { get; } = new(PremiereMediaType.Movie, 901);
        public PremiereItem First { get; private set; } = null!;
        public PremiereItem Second => First with { PremiereDate = First.PremiereDate.AddYears(1) };
        public DateTimeOffset MetadataClock { get; private set; }
        public static async Task<AcceptedTomatoesFixture> CreateAsync()
        {
            var fixture = new AcceptedTomatoesFixture();
            var memory = new MemoryItemEnrichmentStore(fixture.Clock);
            var first = ItemCacheLegacyItem(new(2025, 11, 2), fixture.Clock) with
            {
                Title = "Shared year movie", WikidataId = "Q901", RottenTomatoesScore = 11, RottenTomatoesAudienceScore = 12,
                PosterUrl = "https://saved.example/poster.jpg", ImageSource = ArtworkSources.Fanart,
                Sources = [new() { Id = 31, Name = "Saved provider", Kind = "flatrate" }], SourceNames = ["Saved provider"]
            };
            await ItemCacheCoordinator(memory, fixture.Clock).ImportAsync([first], default);
            var pair = Assert.Single(memory.Records);
            var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(pair.Value.MetadataJson!)! with { AvailabilityComplete = true };
            var tracked = ItemEnrichmentRatings.FromItem(first) with
            {
                ProviderFreshnessTracked = true, ExternalValues = new(ImdbScore: 7.1, ImdbVoteCount: 1000, MetacriticScore: 71),
                ExternalFetchedUtc = first.LastUpdatedUtc, TomatoesValues = new(RottenTomatoesScore: 11, RottenTomatoesAudienceScore: 12),
                TomatoesFetchedUtc = first.LastUpdatedUtc
            };
            memory.Records[pair.Key] = pair.Value with { MetadataJson = JsonSerializer.Serialize(metadata), RatingsJson = JsonSerializer.Serialize(tracked) };
            var store = new AcceptedProjectionStore(memory);
            fixture.Memory = memory;
            fixture.Store = store;
            fixture.First = first;
            fixture.MetadataClock = pair.Value.MetadataFetchedUtc!.Value;
            fixture.Coordinator = new ItemEnrichmentCacheCoordinator(store,
                Microsoft.Extensions.Options.Options.Create(new ItemEnrichmentCacheOptions()),
                Microsoft.Extensions.Options.Options.Create(new TmdbOptions()),
                Microsoft.Extensions.Logging.Abstractions.NullLogger<ItemEnrichmentCacheCoordinator>.Instance, timeProvider: fixture.Clock);
            fixture.Clock.Advance(TimeSpan.FromMinutes(1));
            return fixture;
        }
        public async Task<ItemEnrichmentCacheCoordinator.ItemEnrichmentSession> NewSessionAsync()
        {
            var session = await Coordinator.CreateSessionAsync(default);
            await session.PrepareAsync([Identity], null, default);
            return session;
        }
        public ItemEnrichmentRefresh Refresh(ItemEnrichmentSnapshot previous, PremiereItem target,
            ItemEnrichmentRatingValues tomatoes, ItemEnrichmentRatingValues? external = null)
        {
            var old = previous.Ratings!;
            var currentExternal = external ?? old.ExternalValues;
            var externalClock = external is null ? old.ExternalFetchedUtc : Clock.GetUtcNow();
            var scores = ItemEnrichmentRatingsMerger.Combine(currentExternal, externalClock, tomatoes, Clock.GetUtcNow());
            var ratings = old with { Title = target.Title, WikidataId = target.WikidataId, Year = target.PremiereDate.Year,
                TomatoesValues = tomatoes, TomatoesFetchedUtc = Clock.GetUtcNow(),
                ExternalValues = currentExternal, ExternalFetchedUtc = externalClock,
                ImdbScore = currentExternal?.ImdbScore, ImdbVoteCount = currentExternal?.ImdbVoteCount,
                MetacriticScore = currentExternal?.MetacriticScore, RottenTomatoesScore = scores.Critic, RottenTomatoesAudienceScore = scores.Audience };
            return new(null, ratings, RefreshMetadataClock: false);
        }
    }

    private sealed class AcceptedProjectionStore(IItemEnrichmentStore inner) : IItemEnrichmentStore
    {
        private int _pause;
        public int Writes;
        public TaskCompletionSource ReadPaused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ResumeRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void PauseNextRead() => Volatile.Write(ref _pause, 1);
        public Task<IReadOnlyDictionary<ItemEnrichmentIdentity, ItemEnrichmentRecord>> ReadManyAsync(string partition,
            IReadOnlyCollection<ItemEnrichmentIdentity> identities, bool reserveMissing, CancellationToken token)
            => inner.ReadManyAsync(partition, identities, reserveMissing, token);
        public async Task<IReadOnlyDictionary<ItemEnrichmentIdentity, ItemEnrichmentRecord>> ReadCurrentManyAsync(string partition,
            IReadOnlyCollection<ItemEnrichmentIdentity> identities, CancellationToken token)
        {
            if (Interlocked.Exchange(ref _pause, 0) == 1)
            { ReadPaused.TrySetResult(); await ResumeRead.Task.WaitAsync(token); }
            return await inner.ReadCurrentManyAsync(partition, identities, token);
        }
        public Task<IReadOnlyDictionary<ItemEnrichmentIdentity, ItemEnrichmentRecord>> WriteAcceptedAsync(string partition,
            IReadOnlyList<ItemEnrichmentWrite> writes, CancellationToken token)
        { Writes++; return inner.WriteAcceptedAsync(partition, writes, token); }
        public Task<int> WriteManyAsync(string partition, IReadOnlyList<ItemEnrichmentWrite> writes, CancellationToken token)
            => inner.WriteManyAsync(partition, writes, token);
        public Task<int> InvalidateTmdbAsync(PremiereMediaType type, IReadOnlyCollection<int> ids, DateTimeOffset changed, CancellationToken token)
            => inner.InvalidateTmdbAsync(type, ids, changed, token);
        public Task<int> InvalidateTvmazeAsync(IReadOnlyCollection<int> ids, DateTimeOffset changed, CancellationToken token)
            => inner.InvalidateTvmazeAsync(ids, changed, token);
        public Task<int> CleanupAsync(DateTimeOffset cutoff, CancellationToken token) => inner.CleanupAsync(cutoff, token);
    }
}
