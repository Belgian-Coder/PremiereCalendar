using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class PremiereServiceTests
{
    // The coordinator serializes each snapshot once and reuses that string for the comparison, the pending
    // write and any merge. The persisted bytes must still equal an independent serialization of the
    // accepted snapshot (the pending-write merge itself is covered by the flush retry tests).
    [Fact]
    public async Task PersistedItemJsonIsByteIdenticalToIndependentSerializationAcrossRefreshes()
    {
        var clock = new ItemCacheClock();
        var memory = new MemoryItemEnrichmentStore(clock);
        var coordinator = new ItemEnrichmentCacheCoordinator(memory,
            Microsoft.Extensions.Options.Options.Create(new ItemEnrichmentCacheOptions()),
            Microsoft.Extensions.Options.Options.Create(new TmdbOptions()),
            NullLogger<ItemEnrichmentCacheCoordinator>.Instance, timeProvider: clock);
        var identity = new ItemEnrichmentIdentity(PremiereMediaType.Movie, 4242);
        await using var session = await coordinator.CreateSessionAsync(default);
        await session.PrepareAsync([identity], null, default);
        var metadata = new ItemEnrichmentMetadata
        {
            Title = "Café <Golden> + more", ImdbId = "tt0004242", RuntimeMinutes = 47, AvailabilityComplete = true,
            Genres = ["Drama", "Comedy"], GenreIds = [18, 35], OriginCountries = ["BE"], PosterUrl = "https://example.test/p.jpg?a=1&b=2"
        };
        var ratings = new ItemEnrichmentRatings(7.4, 740, 91, 88, 70, ImdbId: metadata.ImdbId, Title: metadata.Title, Year: 2026);

        // 1. Fresh refresh: metadata and ratings are both new.
        var first = await session.GetAsync(identity, true, (_, _, _, _) => Task.FromResult(new ItemEnrichmentRefresh(metadata, ratings)), default);
        await session.FlushAsync(default);
        var record = Assert.Single(memory.Records.Values);
        Assert.Equal(JsonSerializer.Serialize(first.Metadata), record.MetadataJson);
        Assert.Equal(JsonSerializer.Serialize(first.Ratings), record.RatingsJson);

        // 2. Later refresh in a new session with equal content in new instances, and with changed content.
        clock.Advance(TimeSpan.FromDays(8));
        await using (var second = await coordinator.CreateSessionAsync(default))
        {
            await second.PrepareAsync([identity], null, default);
            var equal = await second.GetAsync(identity, true, (_, _, _, _) => Task.FromResult(
                new ItemEnrichmentRefresh(metadata with { }, ratings with { })), default);
            await second.FlushAsync(default);
            record = Assert.Single(memory.Records.Values);
            Assert.Equal(JsonSerializer.Serialize(equal.Metadata), record.MetadataJson);
            Assert.Equal(JsonSerializer.Serialize(equal.Ratings), record.RatingsJson);
            Assert.Equal(JsonSerializer.Serialize(metadata), record.MetadataJson);
            Assert.Equal(JsonSerializer.Serialize(ratings), record.RatingsJson);
        }
        clock.Advance(TimeSpan.FromDays(8));
        await using (var third = await coordinator.CreateSessionAsync(default))
        {
            await third.PrepareAsync([identity], null, default);
            var changed = await third.GetAsync(identity, true, (_, _, _, _) => Task.FromResult(
                new ItemEnrichmentRefresh(metadata with { RuntimeMinutes = 50 }, ratings with { ImdbScore = 7.5, MetacriticScore = 72 })), default);
            await third.FlushAsync(default);
            record = Assert.Single(memory.Records.Values);
            Assert.Equal(JsonSerializer.Serialize(changed.Metadata), record.MetadataJson);
            Assert.Equal(JsonSerializer.Serialize(changed.Ratings), record.RatingsJson);
            Assert.Equal(50, JsonSerializer.Deserialize<ItemEnrichmentMetadata>(record.MetadataJson!)!.RuntimeMinutes);
            Assert.Equal(7.5, JsonSerializer.Deserialize<ItemEnrichmentRatings>(record.RatingsJson!)!.ImdbScore);
        }
    }
}
