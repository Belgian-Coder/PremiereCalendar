using System.Text.Json;
using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class ItemEnrichmentRatingsMergerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnrelatedProviderUpdatePreservesTheAcceptedAggregate(bool datasetOnly)
    {
        var t0 = DateTimeOffset.Parse("2026-11-03T12:00:00Z");
        var t1 = t0.AddHours(1);
        var identity = new ItemEnrichmentIdentity(PremiereMediaType.Movie, 901);
        var metadata = new ItemEnrichmentMetadata { ImdbId = "tt901", WikidataId = "Q901", Title = "Title" };
        var accepted = new ItemEnrichmentRatings(8.8, 8800, 90, 91, 70, "tt901", "Q901", "Title", 2026)
        {
            ProviderFreshnessTracked = true, ExternalFetchedUtc = t0, TomatoesFetchedUtc = t0,
            ExternalValues = new(ImdbScore: 7.1, ImdbVoteCount: 7100, RottenTomatoesScore: 80, MetacriticScore: 70),
            TomatoesValues = new(RottenTomatoesScore: 90, RottenTomatoesAudienceScore: 91)
        };
        var proposed = datasetOnly
            ? accepted with { DatasetRequired = true, Dataset = new("tt901", 9.2, 9200, t1), ImdbScore = 9.2, ImdbVoteCount = 9200 }
            : accepted with { TomatoesFetchedUtc = t1, TomatoesValues = new(RottenTomatoesScore: 95, RottenTomatoesAudienceScore: 96),
                RottenTomatoesScore = 95, RottenTomatoesAudienceScore = 96 };
        var current = new ItemEnrichmentRecord(identity, JsonSerializer.Serialize(metadata), JsonSerializer.Serialize(accepted), t0, t0, t0,
            0, null, null, MetadataRevision: 1);
        var result = ItemEnrichmentRatingsMerger.Merge(current, new(identity, 0, RatingsJson: JsonSerializer.Serialize(proposed),
            RatingsFetchedUtc: t1, ExpectedMetadataRevision: 1), out var authorized);
        Assert.True(authorized);
        var merged = JsonSerializer.Deserialize<ItemEnrichmentRatings>(result.RatingsJson!)!;
        Assert.Equal(datasetOnly ? 9.2 : 8.8, merged.ImdbScore);
        Assert.Equal(datasetOnly ? 9200 : 8800, merged.ImdbVoteCount);
        Assert.Equal(datasetOnly ? 90 : 95, merged.RottenTomatoesScore);
        Assert.Equal(datasetOnly ? 91 : 96, merged.RottenTomatoesAudienceScore);
        Assert.Equal(t0, merged.ExternalFetchedUtc);
    }
}
