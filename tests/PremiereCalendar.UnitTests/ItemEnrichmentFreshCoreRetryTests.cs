using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class PremiereServiceTests
{
    [Theory]
    [InlineData("matching")]
    [InlineData("generation")]
    [InlineData("revision")]
    [InlineData("invalidation")]
    [InlineData("newer-core-clock")]
    [InlineData("identity")]
    [InlineData("core-payload")]
    [InlineData("retained-optional-clock")]
    [InlineData("manual-ratings")]
    [InlineData("no-proof")]
    [InlineData("malformed-proof")]
    public async Task FreshMovieCoreRetryRequiresTheExactVerifiedCore(string change)
    {
        await using var fixture = await FreshCoreRetryFixture.CreateAsync();
        var eligible = fixture.StartEligible(change);
        await fixture.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var winner = await fixture.PublishCoreAsync(change);
        fixture.Release.TrySetResult();
        await eligible.WaitAsync(TimeSpan.FromSeconds(3));
        await fixture.Session.FlushAsync(default, rejectGenerationConflicts: false);

        Assert.Equal(1, fixture.ProviderCalls);
        if (change is "matching" or "manual-ratings")
        {
            if (change == "matching") fixture.AssertComplete();
            else
            {
                Assert.Equal(2, fixture.Current.MetadataRevision);
                Assert.Equal(JsonSerializer.Serialize(fixture.Complete), fixture.Current.MetadataJson);
                Assert.Equal(winner.RatingsJson, fixture.Current.RatingsJson);
                Assert.Equal(winner.RatingsFetchedUtc, fixture.Current.RatingsFetchedUtc);
                Assert.True(fixture.Session.Find(fixture.Identity)!.PreserveRatings);
            }
            Assert.Equal([0L, 1L], fixture.Store.Attempts.Select(batch => Assert.Single(batch).ExpectedMetadataRevision!.Value));
        }
        else
        {
            Assert.Equal(winner, fixture.Current);
            Assert.Single(fixture.Store.Attempts);
        }
    }

    [Fact]
    public async Task FreshMovieCoreRetryPreservesNewerAcceptedTmdbObservation()
    {
        await using var fixture = await FreshCoreRetryFixture.CreateAsync();
        var eligible = fixture.StartEligible();
        await fixture.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await fixture.PublishCoreAsync();
        var observed = fixture.Clock.GetUtcNow().AddMinutes(1);
        var winner = fixture.Current;
        fixture.Replace(winner with
        {
            TmdbObservedUtc = observed,
            MetadataJson = JsonSerializer.Serialize(fixture.Core with { TmdbVotes = new(9.1, 9100, observed) })
        });
        fixture.Release.TrySetResult();
        await eligible.WaitAsync(TimeSpan.FromSeconds(3));
        await fixture.Session.FlushAsync(default);

        fixture.AssertComplete(expectOriginalVotes: false);
        Assert.Equal(observed, fixture.Current.TmdbObservedUtc);
        Assert.Equal(new ItemEnrichmentTmdbObservation(9.1, 9100, observed),
            JsonSerializer.Deserialize<ItemEnrichmentMetadata>(fixture.Current.MetadataJson!)!.TmdbVotes);
    }

    [Fact]
    public async Task FreshMovieCoreRetryNormalizesOnlyTmdbOwnedSuccessClocks()
    {
        await using var fixture = await FreshCoreRetryFixture.CreateAsync(tmdbOwnsComponents: true);
        var eligible = fixture.StartEligible();
        await fixture.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await fixture.PublishCoreAsync();
        var winner = fixture.Current;
        fixture.Replace(winner with { MetadataJson = JsonSerializer.Serialize(fixture.Core with
        {
            ArtworkFetchedUtc = fixture.Clock.GetUtcNow().AddSeconds(1),
            AvailabilityFetchedUtc = fixture.Clock.GetUtcNow().AddSeconds(1)
        }) });
        fixture.Release.TrySetResult();
        await eligible.WaitAsync(TimeSpan.FromSeconds(3));
        await fixture.Session.FlushAsync(default);
        fixture.AssertComplete();
    }

    [Fact]
    public async Task FreshMovieCoreRetryCannotCrossAnInterveningForeignWrite()
    {
        await using var fixture = await FreshCoreRetryFixture.CreateAsync();
        var eligible = fixture.StartEligible();
        await fixture.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await fixture.PublishCoreAsync();
        fixture.Release.TrySetResult();
        await eligible.WaitAsync(TimeSpan.FromSeconds(3));
        ItemEnrichmentRecord? foreign = null;
        fixture.Store.BeforeWrite = (writes, _) =>
        {
            if (Assert.Single(writes).ExpectedMetadataRevision == 1)
            {
                foreign = fixture.Current with { MetadataRevision = 2,
                    MetadataJson = JsonSerializer.Serialize(fixture.Core with { Title = "Manual repair", RuntimeMinutes = 99 }) };
                fixture.Replace(foreign);
            }
            return Task.CompletedTask;
        };
        await fixture.Session.FlushAsync(default);

        Assert.NotNull(foreign);
        Assert.Equal(foreign, fixture.Current);
        Assert.Equal(2, fixture.Store.Attempts.Count);
        Assert.Equal(1, fixture.ProviderCalls);
    }

    private sealed class FreshCoreRetryFixture : IAsyncDisposable
    {
        public ItemCacheClock Clock { get; } = new();
        public ItemEnrichmentIdentity Identity { get; } = new(PremiereMediaType.Movie, 901);
        public MemoryItemEnrichmentStore Memory { get; private set; } = null!;
        public CoreContinuationStore Store { get; private set; } = null!;
        public ItemEnrichmentCacheCoordinator.ItemEnrichmentSession Session { get; private set; } = null!;
        public ItemEnrichmentMetadata Core { get; private set; } = null!;
        public ItemEnrichmentMetadata Complete { get; private set; } = null!;
        public ItemEnrichmentRatings Ratings { get; private set; } = null!;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ProviderCalls;
        public ItemEnrichmentRecord Current => Assert.Single(Memory.Records.Values);
        public void Replace(ItemEnrichmentRecord record) => Memory.Records[Assert.Single(Memory.Records.Keys)] = record;

        public static async Task<FreshCoreRetryFixture> CreateAsync(bool tmdbOwnsComponents = false)
        {
            var fixture = new FreshCoreRetryFixture();
            fixture.Memory = new(fixture.Clock);
            fixture.Store = new(fixture.Memory);
            var coordinator = new ItemEnrichmentCacheCoordinator(fixture.Store,
                Microsoft.Extensions.Options.Options.Create(new ItemEnrichmentCacheOptions()),
                Microsoft.Extensions.Options.Options.Create(new TmdbOptions()), NullLogger<ItemEnrichmentCacheCoordinator>.Instance,
                timeProvider: fixture.Clock);
            fixture.Session = await coordinator.CreateSessionAsync(default);
            await fixture.Session.PrepareAsync([fixture.Identity], null, default);
            var now = fixture.Clock.GetUtcNow();
            fixture.Core = new()
            {
                Title = "Fresh core", ImdbId = "tt0000901", WikidataId = "Q901", OriginalLanguage = "en", RuntimeMinutes = 110,
                Overview = "Original core overview", GenreIds = [18], Genres = ["Drama"], CoreMetadataOnly = true,
                AvailabilityComplete = false, ArtworkComplete = false, OptionalMetadataComplete = false,
                OptionalFreshnessTracked = true, TmdbVotes = new(7.1, 7100, now)
            };
            if (tmdbOwnsComponents) fixture.Core = fixture.Core with
            {
                PosterUrl = "https://fixture.invalid/tmdb.jpg", ImageSource = "TMDb poster", ArtworkComplete = true,
                ArtworkFetchedUtc = now, OptionalMetadataComplete = true, AvailabilityComplete = true,
                AvailabilityFetchedUtc = now, Sources = [new() { Id = 9, Name = "TMDb availability", Kind = "flatrate" }]
            };
            fixture.Complete = fixture.Core with
            {
                CoreMetadataOnly = false, AvailabilityComplete = true, ArtworkComplete = true, OptionalMetadataComplete = true,
                AvailabilityOwnedByWatchmode = !tmdbOwnsComponents, AvailabilityFetchedUtc = now, ArtworkFetchedUtc = now,
                PosterUrl = tmdbOwnsComponents ? fixture.Core.PosterUrl : "https://fixture.invalid/fanart.jpg",
                ImageSource = tmdbOwnsComponents ? fixture.Core.ImageSource : ArtworkSources.Fanart,
                Sources = tmdbOwnsComponents ? fixture.Core.Sources : [new() { Id = 31, Name = "Watchmode availability", Kind = "flatrate" }],
                OmdbPlot = "Successful optional plot", OmdbPosterUrl = "https://fixture.invalid/omdb.jpg"
            };
            fixture.Ratings = new(8.4, 8400, 94, 96, null, fixture.Core.ImdbId, fixture.Core.WikidataId, fixture.Core.Title, 2026)
            {
                ProviderFreshnessTracked = true, ExternalFetchedUtc = now, TomatoesFetchedUtc = now,
                ExternalValues = new(ImdbScore: 8.4, ImdbVoteCount: 8400),
                TomatoesValues = new(RottenTomatoesScore: 94, RottenTomatoesAudienceScore: 96)
            };
            return fixture;
        }

        public Task<ItemEnrichmentSnapshot> StartEligible(string change = "matching") => Session.GetAsync(Identity, true,
            async (_, metadataDue, ratingsDue, token) =>
            {
                Assert.True(metadataDue);
                Assert.True(ratingsDue);
                ProviderCalls++;
                Entered.TrySetResult();
                await Release.Task.WaitAsync(token);
                return new(Complete, Ratings, FreshMovieCoreJson: change switch
                { "no-proof" => null, "malformed-proof" => "{", _ => JsonSerializer.Serialize(Core) });
            }, default);

        public async Task<ItemEnrichmentRecord> PublishCoreAsync(string change = "matching")
        {
            var current = Current;
            await Memory.WriteManyAsync(Assert.Single(Memory.Records.Keys).Partition,
                [new(Identity, current.Generation, JsonSerializer.Serialize(Core), Clock.GetUtcNow(),
                    TmdbObservedUtc: Core.TmdbVotes?.TmdbObservedUtc, ExpectedMetadataRevision: current.MetadataRevision)], default);
            current = Current;
            var metadata = change switch
            {
                "identity" => Core with { ImdbId = "tt0000999" },
                "core-payload" => Core with { GenreIds = [99], Genres = ["Foreign genre"] },
                "retained-optional-clock" => Core with { ArtworkFetchedUtc = Clock.GetUtcNow().AddMinutes(1) },
                _ => Core
            };
            current = current with { MetadataJson = JsonSerializer.Serialize(metadata) };
            current = change switch
            {
                "generation" => current with { Generation = 1 },
                "revision" => current with { MetadataRevision = 2 },
                "invalidation" => current with { MetadataInvalidatedUtc = Clock.GetUtcNow().AddMinutes(1) },
                "newer-core-clock" => current with { MetadataFetchedUtc = Clock.GetUtcNow().AddMinutes(1) },
                "manual-ratings" => current with { RatingsJson = JsonSerializer.Serialize(Ratings with { FreshnessVerified = false }),
                    RatingsFetchedUtc = Clock.GetUtcNow() },
                _ => current
            };
            Replace(current);
            return current;
        }

        public void AssertComplete(bool expectOriginalVotes = true)
        {
            Assert.Equal(2, Current.MetadataRevision);
            var actual = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(Current.MetadataJson!)!;
            Assert.Equal(JsonSerializer.Serialize(Complete with { TmdbVotes = expectOriginalVotes ? Complete.TmdbVotes : actual.TmdbVotes }),
                Current.MetadataJson);
            Assert.Equal(JsonSerializer.Serialize(Ratings), Current.RatingsJson);
            Assert.Equal(Clock.GetUtcNow(), Current.MetadataFetchedUtc);
            Assert.Equal(Clock.GetUtcNow(), Current.RatingsFetchedUtc);
        }

        public async ValueTask DisposeAsync()
        {
            Release.TrySetResult();
            await Session.DisposeAsync();
        }
    }
}
