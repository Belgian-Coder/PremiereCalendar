using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class PremiereServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CoreContinuationPublishesAfterAnotherSourceAcceptsItsCore(bool optionalCompletesDuringFlush)
    {
        await using var fixture = await CoreContinuationFixture.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (optionalCompletesDuringFlush) fixture.Store.BeforeWrite = async (_, token) =>
        { entered.TrySetResult(); await release.Task.WaitAsync(token); };
        var optional = fixture.StartOptional();
        await fixture.OptionalStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var coreFlush = fixture.Session.FlushAsync(default);
        try
        {
            if (optionalCompletesDuringFlush)
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                fixture.ReleaseOptional.TrySetResult();
                await optional.WaitAsync(TimeSpan.FromSeconds(3));
                Assert.True(fixture.Session.HasPendingWrites);
            }
        }
        finally { release.TrySetResult(); }
        await coreFlush.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.Store.BeforeWrite = null;
        fixture.ReleaseOptional.TrySetResult();
        await optional.WaitAsync(TimeSpan.FromSeconds(3));
        await fixture.Session.FlushAsync(default);

        Assert.Equal(2, fixture.Current.MetadataRevision);
        fixture.AssertOptionalAccepted();
        var continuation = Assert.Single(fixture.Store.Attempts.Last());
        Assert.Equal(1, continuation.ExpectedMetadataRevision);
        Assert.Equal(0, continuation.CoreContinuation?.MetadataRevision);
        Assert.Equal(fixture.CoreClock, continuation.MetadataFetchedUtc);
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("generation")]
    [InlineData("invalidation")]
    [InlineData("clock")]
    [InlineData("payload")]
    public async Task CoreContinuationCannotOverwriteForeignAcceptedMetadata(string change)
    {
        await using var fixture = await CoreContinuationFixture.CreateAsync();
        var optional = fixture.StartOptional();
        await fixture.OptionalStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await fixture.Session.FlushAsync(default);
        var current = fixture.Current;
        var foreign = change switch
        {
            "generation" => current with { Generation = current.Generation + 1 },
            "invalidation" => current with { Generation = current.Generation + 1, MetadataInvalidatedUtc = fixture.Clock.GetUtcNow().AddMinutes(1) },
            "clock" => current with { MetadataRevision = current.MetadataRevision + 1, MetadataFetchedUtc = fixture.CoreClock.AddMinutes(1) },
            "payload" => current with { MetadataRevision = current.MetadataRevision + 1,
                MetadataJson = JsonSerializer.Serialize(fixture.Core with { RuntimeMinutes = 99, Title = "Foreign repair" }) },
            _ => current with { MetadataRevision = current.MetadataRevision + 1 }
        };
        fixture.Replace(foreign);
        fixture.ReleaseOptional.TrySetResult();
        await optional.WaitAsync(TimeSpan.FromSeconds(3));
        await fixture.Session.FlushAsync(default, rejectGenerationConflicts: false);

        Assert.Equal(foreign.MetadataJson, fixture.Current.MetadataJson);
        Assert.Equal(foreign.Generation, fixture.Current.Generation);
        Assert.Equal(foreign.MetadataRevision, fixture.Current.MetadataRevision);
        Assert.Equal(foreign.MetadataFetchedUtc, fixture.Current.MetadataFetchedUtc);
        Assert.Equal(foreign.MetadataInvalidatedUtc, fixture.Current.MetadataInvalidatedUtc);
        Assert.Empty(JsonSerializer.Deserialize<ItemEnrichmentMetadata>(fixture.Current.MetadataJson!)!.Sources);
    }

    [Theory]
    [InlineData("equivalent")]
    [InlineData("payload")]
    [InlineData("clock")]
    [InlineData("invalidation")]
    public async Task CoreContinuationRequiresDataEquivalentCoreWinner(string change)
    {
        await using var fixture = await CoreContinuationFixture.CreateAsync();
        var optional = fixture.StartOptional();
        await fixture.OptionalStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        // A winner read is not an exclusive-writer receipt. An identical core
        // winner is safe to continue; a changed core must retain its own values.
        var equivalent = change == "equivalent";
        var metadata = change == "payload" ? fixture.Core with { Title = "Foreign core", RuntimeMinutes = 99 } : fixture.Core;
        fixture.Replace(fixture.Current with { MetadataJson = JsonSerializer.Serialize(metadata),
            MetadataFetchedUtc = change == "clock" ? fixture.CoreClock.AddMinutes(1) : fixture.CoreClock, MetadataRevision = 1,
            MetadataInvalidatedUtc = change == "invalidation" ? fixture.CoreClock.AddMinutes(1) : null });
        await fixture.Session.FlushAsync(default);
        fixture.ReleaseOptional.TrySetResult();
        await optional.WaitAsync(TimeSpan.FromSeconds(3));
        await fixture.Session.FlushAsync(default);

        var accepted = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(fixture.Current.MetadataJson!)!;
        Assert.Equal(equivalent ? 2 : 1, fixture.Current.MetadataRevision);
        if (equivalent) fixture.AssertOptionalAccepted();
        else
        {
            Assert.Equal(metadata.Title, accepted.Title);
            Assert.Equal(metadata.RuntimeMinutes, accepted.RuntimeMinutes);
            Assert.Empty(accepted.Sources);
            Assert.True(accepted.CoreMetadataOnly);
        }
    }

    [Fact]
    public async Task CoreContinuationDoesNotAdvanceForcedMetadataRefresh()
    {
        await using var fixture = await CoreContinuationFixture.CreateAsync();
        var forced = fixture.StartOptional(forceRefresh: true);
        await fixture.OptionalStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await fixture.Session.FlushAsync(default);
        fixture.ReleaseOptional.TrySetResult();
        await forced.WaitAsync(TimeSpan.FromSeconds(3));
        await fixture.Session.FlushAsync(default);

        Assert.Equal(1, fixture.Current.MetadataRevision);
        var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(fixture.Current.MetadataJson!)!;
        Assert.True(metadata.CoreMetadataOnly);
        Assert.Empty(metadata.Sources);
        var write = Assert.Single(fixture.Store.Attempts.Last());
        Assert.Null(write.CoreContinuation);
        Assert.Equal(0, write.ExpectedMetadataRevision);
    }

    [Fact]
    public async Task CoreContinuationCompletesWhileFailedCoreFlushIsHeldAndRetriesWithoutProviderWork()
    {
        await using var fixture = await CoreContinuationFixture.CreateAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Store.BeforeWrite = async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            throw new IOException("Held first core flush failed.");
        };
        var failedFlush = fixture.Session.FlushAsync(default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var optional = fixture.StartOptional();
            await fixture.OptionalStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
            fixture.ReleaseOptional.TrySetResult();
            await optional.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(fixture.Session.HasPendingWrites);
        }
        finally { release.TrySetResult(); }
        await failedFlush.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.Store.BeforeWrite = null;
        await fixture.Session.FlushAsync(default);

        fixture.AssertOptionalAccepted();
        Assert.Equal(1, fixture.OptionalCalls);
        Assert.Equal(1, fixture.Current.MetadataRevision);
        Assert.NotNull(Assert.Single(fixture.Store.Attempts.Last()).CoreContinuation);
        Assert.False(fixture.Session.HasPendingWrites);
    }

    [Fact]
    public async Task CoreContinuationRetryRetainsTheOriginalCoreProof()
    {
        await using var fixture = await CoreContinuationFixture.CreateAsync();
        var optional = fixture.StartOptional();
        await fixture.OptionalStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await fixture.Session.FlushAsync(default);
        fixture.ReleaseOptional.TrySetResult();
        await optional.WaitAsync(TimeSpan.FromSeconds(3));
        fixture.Store.BeforeWrite = (_, _) => throw new IOException("Optional flush failed.");
        await fixture.Session.FlushAsync(default);
        Assert.True(fixture.Session.HasPendingWrites);
        fixture.Store.BeforeWrite = null;
        await fixture.Session.FlushAsync(default);

        fixture.AssertOptionalAccepted();
        Assert.Equal(1, fixture.OptionalCalls);
        var attempts = fixture.Store.Attempts.ToArray();
        Assert.Equal(3, attempts.Length);
        Assert.Equal(attempts[1][0], attempts[2][0]);
        Assert.Equal(0, attempts[2][0].CoreContinuation?.MetadataRevision);
        Assert.Equal(1, attempts[2][0].ExpectedMetadataRevision);
    }

    private sealed class CoreContinuationFixture : IAsyncDisposable
    {
        public ItemCacheClock Clock { get; } = new();
        public ItemEnrichmentIdentity Identity { get; } = new(PremiereMediaType.Movie, 901);
        public MemoryItemEnrichmentStore Memory { get; private set; } = null!;
        public CoreContinuationStore Store { get; private set; } = null!;
        public ItemEnrichmentCacheCoordinator.ItemEnrichmentSession Session { get; private set; } = null!;
        public ItemEnrichmentMetadata Core { get; } = new()
        {
            Title = "Core continuation", ImdbId = "tt0000901", OriginalLanguage = "en", RuntimeMinutes = 110,
            CoreMetadataOnly = true, AvailabilityComplete = false, ArtworkComplete = false, OptionalMetadataComplete = false
        };
        public DateTimeOffset CoreClock { get; private set; }
        public TaskCompletionSource OptionalStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseOptional { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int OptionalCalls;
        public ItemEnrichmentRecord Current => Assert.Single(Memory.Records.Values);
        public void Replace(ItemEnrichmentRecord record) => Memory.Records[Assert.Single(Memory.Records.Keys)] = record;
        public static async Task<CoreContinuationFixture> CreateAsync()
        {
            var fixture = new CoreContinuationFixture();
            fixture.Memory = new(fixture.Clock);
            fixture.Store = new(fixture.Memory);
            var coordinator = new ItemEnrichmentCacheCoordinator(fixture.Store,
                Microsoft.Extensions.Options.Options.Create(new ItemEnrichmentCacheOptions()),
                Microsoft.Extensions.Options.Options.Create(new TmdbOptions()), NullLogger<ItemEnrichmentCacheCoordinator>.Instance,
                timeProvider: fixture.Clock);
            fixture.Session = await coordinator.CreateSessionAsync(default);
            await fixture.Session.PrepareAsync([fixture.Identity], null, default);
            var core = await fixture.Session.GetAsync(fixture.Identity, false, (_, metadataDue, _, _) =>
            {
                Assert.True(metadataDue);
                return Task.FromResult(new ItemEnrichmentRefresh(fixture.Core, null, RefreshRatingsClock: false));
            }, default, metadataOnly: true);
            fixture.CoreClock = core.Record.MetadataFetchedUtc!.Value;
            return fixture;
        }
        public Task<ItemEnrichmentSnapshot> StartOptional(bool forceRefresh = false) => Session.GetAsync(Identity, forceRefresh,
            async (previous, metadataDue, _, token) =>
            {
                Assert.Equal(forceRefresh, metadataDue);
                Interlocked.Increment(ref OptionalCalls);
                OptionalStarted.TrySetResult();
                await ReleaseOptional.Task.WaitAsync(token);
                return new(previous.Metadata! with
                {
                    CoreMetadataOnly = false, AvailabilityComplete = true, AvailabilityOwnedByWatchmode = true,
                    AvailabilityFetchedUtc = Clock.GetUtcNow(), Sources = [new() { Id = 31, Name = "Completed availability", Kind = "flatrate" }],
                    ArtworkComplete = true, OptionalMetadataComplete = true, PosterUrl = "https://fixture.invalid/901.jpg", ImageSource = "Fixture artwork"
                }, null, RefreshMetadataClock: forceRefresh, RefreshRatingsClock: false);
            }, default);
        public void AssertOptionalAccepted()
        {
            var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Current.MetadataJson!)!;
            Assert.False(metadata.CoreMetadataOnly);
            Assert.Equal(31, Assert.Single(metadata.Sources).Id);
            Assert.Equal("https://fixture.invalid/901.jpg", metadata.PosterUrl);
            Assert.Equal(CoreClock, Current.MetadataFetchedUtc);
        }
        public async ValueTask DisposeAsync()
        {
            ReleaseOptional.TrySetResult();
            await Session.DisposeAsync();
        }
    }

    private sealed class CoreContinuationStore(IItemEnrichmentStore inner) : IItemEnrichmentStore
    {
        public ConcurrentQueue<ItemEnrichmentWrite[]> Attempts { get; } = new();
        public Func<IReadOnlyList<ItemEnrichmentWrite>, CancellationToken, Task>? BeforeWrite;
        public Task<IReadOnlyDictionary<ItemEnrichmentIdentity, ItemEnrichmentRecord>> ReadManyAsync(string partition,
            IReadOnlyCollection<ItemEnrichmentIdentity> identities, bool reserveMissing, CancellationToken token)
            => inner.ReadManyAsync(partition, identities, reserveMissing, token);
        public async Task<int> WriteManyAsync(string partition, IReadOnlyList<ItemEnrichmentWrite> writes, CancellationToken token)
        {
            Attempts.Enqueue(writes.ToArray());
            if (BeforeWrite is { } before) await before(writes, token);
            return await inner.WriteManyAsync(partition, writes, token);
        }
        public Task<int> InvalidateTmdbAsync(PremiereMediaType mediaType, IReadOnlyCollection<int> ids, DateTimeOffset changed, CancellationToken token)
            => inner.InvalidateTmdbAsync(mediaType, ids, changed, token);
        public Task<int> InvalidateTvmazeAsync(IReadOnlyCollection<int> ids, DateTimeOffset changed, CancellationToken token)
            => inner.InvalidateTvmazeAsync(ids, changed, token);
        public Task<int> CleanupAsync(DateTimeOffset cutoff, CancellationToken token) => inner.CleanupAsync(cutoff, token);
    }
}
