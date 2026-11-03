using System.Text.Json;

namespace PremiereCalendar.Services;

// Called only while the item row is locked by the store transaction. Provider
// clocks authorize provider payloads; the aggregate clock cannot authorize them.
internal static class ItemEnrichmentRatingsMerger
{
    internal static ItemEnrichmentRatings? ReadTracked(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty(nameof(ItemEnrichmentRatings.ProviderFreshnessTracked), out var tracked)
                || tracked.ValueKind != JsonValueKind.True
                || !root.TryGetProperty(nameof(ItemEnrichmentRatings.ExternalValues), out _)
                || !root.TryGetProperty(nameof(ItemEnrichmentRatings.TomatoesValues), out _)
                || !root.TryGetProperty(nameof(ItemEnrichmentRatings.ExternalFetchedUtc), out _)
                || !root.TryGetProperty(nameof(ItemEnrichmentRatings.TomatoesFetchedUtc), out _)) return null;
            return JsonSerializer.Deserialize<ItemEnrichmentRatings>(json);
        }
        catch (JsonException) { return null; }
    }

    internal static ItemEnrichmentWrite Merge(ItemEnrichmentRecord current, ItemEnrichmentWrite incoming,
        out bool ratingsRevisionAuthorized)
    {
        ratingsRevisionAuthorized = false;
        if (current.Generation != incoming.ExpectedGeneration || incoming.RatingsFetchedUtc is null)
            return incoming;
        if (incoming.ExpectedRatingsJson is not null)
            return incoming.ExpectedRatingsJson == current.RatingsJson && incoming.ExpectedMetadataRevision == current.MetadataRevision
                ? incoming : incoming with { RatingsJson = null, RatingsFetchedUtc = null };
        if (ReadTracked(current.RatingsJson) is not { } accepted || ReadTracked(incoming.RatingsJson) is not { } proposed)
            return incoming;

        var revisionMatches = incoming.ExpectedMetadataRevision is null || incoming.ExpectedMetadataRevision == current.MetadataRevision;
        // Maintenance writes deliberately carry unverified aggregate edits. They
        // retain the existing exact-revision/aggregate-clock write contract.
        if (!proposed.FreshnessVerified) return incoming;
        // A stale provider operation cannot cross a manual/unverified repair.
        if (!revisionMatches && !accepted.FreshnessVerified)
            return incoming with { RatingsJson = null, RatingsFetchedUtc = null };
        var metadata = ReadMetadata(MetadataAccepted(current, incoming) ? incoming.MetadataJson : current.MetadataJson);
        var acceptedExternalMatches = metadata is null ? true : Same(accepted.ImdbId, metadata.ImdbId);
        var proposedExternalMatches = metadata is null ? revisionMatches && Same(accepted.ImdbId, proposed.ImdbId)
            : Same(proposed.ImdbId, metadata.ImdbId);
        var acceptedTomatoesMatches = metadata is null ? true : Same(accepted.WikidataId, metadata.WikidataId);
        var proposedTomatoesMatches = (metadata is null ? revisionMatches : Same(proposed.WikidataId, metadata.WikidataId))
            && (revisionMatches || acceptedTomatoesMatches && accepted.MatchesTomatoes(proposed.WikidataId, proposed.Title, proposed.Year));
        // At the current metadata revision a new discovery binding may replace an
        // old title/year. A stale revision must use the accepted binding instead.
        if (revisionMatches && proposedTomatoesMatches)
            acceptedTomatoesMatches &= accepted.MatchesTomatoes(proposed.WikidataId, proposed.Title, proposed.Year);

        var externalIncoming = ChooseIncoming(acceptedExternalMatches, proposedExternalMatches,
            accepted.ExternalFetchedUtc, proposed.ExternalFetchedUtc);
        var tomatoesIncoming = ChooseIncoming(acceptedTomatoesMatches, proposedTomatoesMatches,
            accepted.TomatoesFetchedUtc, proposed.TomatoesFetchedUtc);
        var boundImdbId = metadata is not null ? metadata.ImdbId : accepted.ImdbId;
        var acceptedDatasetMatches = accepted.Dataset is { } currentDataset
            && !string.IsNullOrWhiteSpace(currentDataset.ImdbId)
            && (metadata is null ? Same(currentDataset.ImdbId, accepted.ImdbId) : Same(currentDataset.ImdbId, metadata.ImdbId));
        var proposedDatasetMatches = proposed.Dataset is { } incomingDataset
            && !string.IsNullOrWhiteSpace(incomingDataset.ImdbId)
            && (metadata is not null || revisionMatches) && Same(incomingDataset.ImdbId, boundImdbId);
        var datasetIncoming = ChooseIncoming(acceptedDatasetMatches, proposedDatasetMatches,
            accepted.Dataset?.FetchedUtc, proposed.Dataset?.FetchedUtc);
        var dataset = datasetIncoming ? proposed.Dataset : acceptedDatasetMatches ? accepted.Dataset : null;
        var datasetRequired = !string.IsNullOrWhiteSpace(boundImdbId)
            && (accepted.DatasetRequired || proposed.DatasetRequired || dataset is not null);
        var external = externalIncoming ? proposed.ExternalValues : acceptedExternalMatches ? accepted.ExternalValues : null;
        var tomatoes = tomatoesIncoming ? proposed.TomatoesValues : acceptedTomatoesMatches ? accepted.TomatoesValues : null;
        var externalClock = externalIncoming ? proposed.ExternalFetchedUtc : acceptedExternalMatches ? accepted.ExternalFetchedUtc : null;
        var tomatoesClock = tomatoesIncoming ? proposed.TomatoesFetchedUtc : acceptedTomatoesMatches ? accepted.TomatoesFetchedUtc : null;
        var externalRequired = externalIncoming ? proposed.ExternalRequired : accepted.ExternalRequired;
        var tomatoesRequired = tomatoesIncoming ? proposed.TomatoesRequired : accepted.TomatoesRequired;
        // Admission skips do not claim a new successful fetch. They may stop a
        // current binding being due while retaining its accepted payload/clock.
        if (revisionMatches && proposedExternalMatches && proposed.ExternalFetchedUtc == externalClock && !proposed.ExternalRequired)
            externalRequired = false;
        if (revisionMatches && proposedTomatoesMatches && proposed.TomatoesFetchedUtc == tomatoesClock && !proposed.TomatoesRequired)
            tomatoesRequired = false;
        if (!externalIncoming && !tomatoesIncoming && !datasetIncoming && acceptedExternalMatches && acceptedTomatoesMatches
            && (accepted.Dataset is null || acceptedDatasetMatches)
            && externalRequired == accepted.ExternalRequired && tomatoesRequired == accepted.TomatoesRequired
            && datasetRequired == accepted.DatasetRequired)
            return incoming with { RatingsJson = null, RatingsFetchedUtc = null };
        var tomatoesOwner = tomatoesIncoming ? proposed : acceptedTomatoesMatches ? accepted : proposed;
        var (critic, audience) = Combine(external, externalClock, tomatoes, tomatoesClock);
        // Keep an accepted manual aggregate until its owning component is
        // actually refreshed; a different provider cannot erase that repair.
        if ((!externalIncoming && !tomatoesIncoming && acceptedExternalMatches && acceptedTomatoesMatches)
            || !accepted.FreshnessVerified && !tomatoesIncoming && acceptedTomatoesMatches)
        {
            critic = accepted.RottenTomatoesScore;
            audience = accepted.RottenTomatoesAudienceScore;
        }
        var imdbScore = dataset?.ImdbScore ?? external?.ImdbScore;
        var imdbVotes = dataset?.ImdbScore is not null ? dataset.ImdbVoteCount : external?.ImdbVoteCount;
        if ((!externalIncoming && !datasetIncoming && acceptedExternalMatches && (accepted.Dataset is null || acceptedDatasetMatches))
            || !accepted.FreshnessVerified && !externalIncoming && dataset?.ImdbScore is null && acceptedExternalMatches)
        {
            imdbScore = accepted.ImdbScore;
            imdbVotes = accepted.ImdbVoteCount;
        }
        var result = new ItemEnrichmentRatings(
            imdbScore, imdbVotes,
            critic, audience,
            externalIncoming ? external?.MetacriticScore : acceptedExternalMatches ? accepted.MetacriticScore : null,
            boundImdbId,
            metadata is not null ? metadata.WikidataId : tomatoesOwner.WikidataId, tomatoesOwner.Title, tomatoesOwner.Year,
            FreshnessVerified: accepted.FreshnessVerified || (externalIncoming || !externalRequired) && (tomatoesIncoming || !tomatoesRequired)
                && (datasetIncoming || !datasetRequired))
        {
            ProviderFreshnessTracked = true, ExternalValues = external, TomatoesValues = tomatoes,
            ExternalFetchedUtc = externalClock, TomatoesFetchedUtc = tomatoesClock,
            ExternalRequired = externalRequired, TomatoesRequired = tomatoesRequired,
            DatasetRequired = datasetRequired, Dataset = dataset
        };
        var json = JsonSerializer.Serialize(result);
        if (json == current.RatingsJson)
            return incoming with { RatingsJson = null, RatingsFetchedUtc = null };
        ratingsRevisionAuthorized = true;
        return incoming with
        {
            RatingsJson = json,
            RatingsFetchedUtc = new[] { current.RatingsFetchedUtc, externalClock, tomatoesClock, dataset?.FetchedUtc }
                .Where(clock => clock.HasValue).Select(clock => clock!.Value).DefaultIfEmpty(DateTimeOffset.MinValue).Max()
        };
    }

    private static bool ChooseIncoming(bool currentMatches, bool incomingMatches, DateTimeOffset? current, DateTimeOffset? incoming)
        => incomingMatches && (!currentMatches || incoming is { } clock && (current is null || clock > current));

    internal static (int? Critic, int? Audience) Combine(ItemEnrichmentRatingValues? external, DateTimeOffset? externalClock,
        ItemEnrichmentRatingValues? tomatoes, DateTimeOffset? tomatoesClock)
    {
        // A present, newer dedicated payload can explicitly withdraw either
        // score. Missing OMDb fields cannot withdraw an independent RT result.
        if (tomatoes is not null && tomatoesClock is not null && (externalClock is null || tomatoesClock > externalClock))
            return (tomatoes.RottenTomatoesScore, tomatoes.RottenTomatoesAudienceScore);
        return (external?.RottenTomatoesScore ?? tomatoes?.RottenTomatoesScore,
            external?.RottenTomatoesAudienceScore ?? tomatoes?.RottenTomatoesAudienceScore);
    }

    private static ItemEnrichmentMetadata? ReadMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<ItemEnrichmentMetadata>(json); }
        catch (JsonException) { return null; }
    }

    private static bool MetadataAccepted(ItemEnrichmentRecord current, ItemEnrichmentWrite incoming)
        => !string.IsNullOrWhiteSpace(incoming.MetadataJson) && incoming.MetadataFetchedUtc is { } clock
            && (incoming.ExpectedMetadataRevision is null || incoming.ExpectedMetadataRevision == current.MetadataRevision)
            && (current.MetadataFetchedUtc is null || current.MetadataFetchedUtc < clock
                || current.MetadataFetchedUtc == clock && incoming.ExpectedMetadataRevision is not null)
            && (current.TmdbObservedUtc is null || incoming.TmdbObservedUtc >= current.TmdbObservedUtc)
            && (current.MetadataInvalidatedUtc is null || current.MetadataInvalidatedUtc <= clock);

    private static bool Same(string? left, string? right)
        => string.Equals(left?.Trim(), right?.Trim(), StringComparison.OrdinalIgnoreCase);
}
