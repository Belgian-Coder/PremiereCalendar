using Microsoft.AspNetCore.Hosting;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed class ItemEnrichmentStoreTests
{
    [Fact]
    public Task SQLite_SchemaThreeUpgradePreservesDataAndHistory() =>
        WithStoreAsync(false, fixture => SchemaThreeUpgradeAsync(fixture, false));

    [ConfiguredPostgresFact]
    public Task PostgreSQL_SchemaThreeUpgradePreservesDataAndHistory() =>
        WithStoreAsync(true, fixture => SchemaThreeUpgradeAsync(fixture, true));

    [ConfiguredPostgresFact]
    public Task PostgreSQL_UnrelatedRowsRemainAvailableDuringBlockedWrite() => WithStoreAsync(true, async fixture =>
    {
        var blockedKey = new ItemEnrichmentIdentity(PremiereMediaType.Movie, 1);
        var independentKey = new ItemEnrichmentIdentity(PremiereMediaType.Movie, 2);
        await fixture.Store.ReadManyAsync("a", [blockedKey, independentKey], true, CancellationToken.None);
        await using var blocker = (NpgsqlConnection)DatabaseConnectionFactory.Create(fixture.Options.Value, fixture.Environment.ContentRootPath);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand("SELECT 1 FROM ItemEnrichments WHERE PartitionKey = 'a' AND TmdbId = 1 FOR UPDATE", blocker, transaction))
            await hold.ExecuteNonQueryAsync();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var pendingWrite = fixture.Store.WriteManyAsync("a", [new(blockedKey, 0, "{}", fixture.Clock.GetUtcNow())], budget.Token);
        try
        {
            await using var observer = (NpgsqlConnection)DatabaseConnectionFactory.Create(fixture.Options.Value, fixture.Environment.ContentRootPath);
            await observer.OpenAsync(budget.Token);
            while (true)
            {
                await using var waiting = new NpgsqlCommand("SELECT COUNT(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'", observer);
                if (Convert.ToInt32(await waiting.ExecuteScalarAsync(budget.Token)) > 0) break;
                await Task.Delay(10, budget.Token);
            }
            Assert.False(pendingWrite.IsCompleted);
            Assert.Single(await fixture.Store.ReadManyAsync("a", [independentKey], false, budget.Token).WaitAsync(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
            Assert.Equal(1, await pendingWrite);
        }
    });

    [Theory]
    [InlineData("batch-restart-partitions")]
    [InlineData("independent-components")]
    [InlineData("metadata-revision-and-observation")]
    [InlineData("ratings-metadata-revision")]
    [InlineData("batched-provider-invalidation")]
    [InlineData("invalidation-race")]
    [InlineData("tvmaze-invalidation")]
    [InlineData("unmapped-tvmaze-reservation")]
    [InlineData("calendar-month-retention")]
    [InlineData("duplicate-writes")]
    public Task SQLite_StoreContract(string scenario) => WithStoreAsync(false, fixture => RunScenarioAsync(scenario, fixture));

    [ConfiguredPostgresTheory]
    [InlineData("batch-restart-partitions")]
    [InlineData("independent-components")]
    [InlineData("metadata-revision-and-observation")]
    [InlineData("ratings-metadata-revision")]
    [InlineData("batched-provider-invalidation")]
    [InlineData("invalidation-race")]
    [InlineData("tvmaze-invalidation")]
    [InlineData("unmapped-tvmaze-reservation")]
    [InlineData("calendar-month-retention")]
    [InlineData("duplicate-writes")]
    public Task PostgreSQL_StoreContract(string scenario) => WithStoreAsync(true, fixture => RunScenarioAsync(scenario, fixture));

    [Theory]
    [InlineData("external-first")]
    [InlineData("tomatoes-first")]
    [InlineData("equal-component-clocks")]
    [InlineData("verified-empty")]
    [InlineData("verified-empty-tomatoes")]
    [InlineData("dedicated-empty-over-external-fallback")]
    [InlineData("foreign-identities")]
    [InlineData("imdb-identity-only")]
    [InlineData("tomatoes-identity-only")]
    [InlineData("generation-invalidation")]
    [InlineData("dataset-with-omdb-failure")]
    [InlineData("dataset-before-providers")]
    [InlineData("dataset-after-providers")]
    [InlineData("dataset-null-fallback")]
    [InlineData("dataset-identity-change")]
    [InlineData("dataset-identity-removal")]
    [InlineData("exact-ratings-repair")]
    [InlineData("stale-exact-ratings-repair")]
    public Task SQLite_TrackedRatingsContract(string scenario) => WithStoreAsync(false, fixture => TrackedRatingsContractAsync(scenario, fixture));

    [ConfiguredPostgresTheory]
    [InlineData("external-first")]
    [InlineData("tomatoes-first")]
    [InlineData("equal-component-clocks")]
    [InlineData("verified-empty")]
    [InlineData("verified-empty-tomatoes")]
    [InlineData("dedicated-empty-over-external-fallback")]
    [InlineData("foreign-identities")]
    [InlineData("imdb-identity-only")]
    [InlineData("tomatoes-identity-only")]
    [InlineData("generation-invalidation")]
    [InlineData("dataset-with-omdb-failure")]
    [InlineData("dataset-before-providers")]
    [InlineData("dataset-after-providers")]
    [InlineData("dataset-null-fallback")]
    [InlineData("dataset-identity-change")]
    [InlineData("dataset-identity-removal")]
    [InlineData("exact-ratings-repair")]
    [InlineData("stale-exact-ratings-repair")]
    public Task PostgreSQL_TrackedRatingsContract(string scenario) => WithStoreAsync(true, fixture => TrackedRatingsContractAsync(scenario, fixture));

    [ConfiguredPostgresFact]
    public Task PostgreSQL_OverlappingTrackedWritesFromSeparateStoresPreserveIndependentSuccesses() => WithStoreAsync(true, async fixture =>
    {
        var seed = await SeedTrackedRatingsAsync(fixture);
        var (external, tomatoes) = IndependentTrackedWrites(fixture, seed);
        var otherStore = new SqliteItemEnrichmentStore(fixture.Options, fixture.Environment, fixture.Clock);
        await using var blocker = (NpgsqlConnection)DatabaseConnectionFactory.Create(fixture.Options.Value, fixture.Environment.ContentRootPath);
        await blocker.OpenAsync();
        await using var transaction = await blocker.BeginTransactionAsync();
        await using (var hold = new NpgsqlCommand("SELECT 1 FROM ItemEnrichments WHERE PartitionKey = 'tracked' AND TmdbId = 901 FOR UPDATE", blocker, transaction))
            await hold.ExecuteNonQueryAsync();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = fixture.Store.WriteManyAsync("tracked", [external], budget.Token);
        var second = otherStore.WriteManyAsync("tracked", [tomatoes], budget.Token);
        try
        {
            await using var observer = (NpgsqlConnection)DatabaseConnectionFactory.Create(fixture.Options.Value, fixture.Environment.ContentRootPath);
            await observer.OpenAsync(budget.Token);
            while (true)
            {
                await using var waiting = new NpgsqlCommand("SELECT COUNT(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'", observer);
                if (Convert.ToInt32(await waiting.ExecuteScalarAsync(budget.Token)) >= 2) break;
                await Task.Delay(10, budget.Token);
            }
            Assert.False(first.IsCompleted);
            Assert.False(second.IsCompleted);
        }
        finally
        {
            await transaction.RollbackAsync(CancellationToken.None);
            await Task.WhenAll(first, second);
        }
        Assert.Equal(1, await first);
        Assert.Equal(1, await second);
        await AssertIndependentTrackedWinnersAsync(fixture, seed);
    });

    private static async Task TrackedRatingsContractAsync(string scenario, Fixture fixture)
    {
        var seed = await SeedTrackedRatingsAsync(fixture, externalTomatoesFallback: scenario == "dedicated-empty-over-external-fallback",
            omitDatasetFields: scenario.StartsWith("dataset-", StringComparison.Ordinal));
        var now = fixture.Clock.GetUtcNow();
        var t1 = now.AddHours(-2);
        var t2 = now.AddHours(-1);
        if (scenario.StartsWith("dataset-", StringComparison.Ordinal))
        {
            await DatasetRatingsContractAsync(scenario, fixture, seed);
            return;
        }
        if (scenario is "exact-ratings-repair" or "stale-exact-ratings-repair")
        {
            var proposed = JsonSerializer.Deserialize<ItemEnrichmentRatings>(seed.Record.RatingsJson!)! with { ImdbScore = 8.8, ImdbVoteCount = 8800 };
            var repair = TrackedWrite(seed, proposed, seed.T0) with { ExpectedRatingsJson = seed.Record.RatingsJson };
            if (scenario == "stale-exact-ratings-repair")
            {
                await fixture.Store.WriteManyAsync("tracked", [TrackedWrite(seed, TrackedRatings(8.4, t1, 80, seed.T0), t1)], CancellationToken.None);
                var winner = (await ReadTrackedRatingsAsync(fixture, seed.Key)).Record;
                Assert.Equal(seed.Record.MetadataRevision, winner.MetadataRevision);
                Assert.Equal(0, await fixture.Store.WriteManyAsync("tracked", [repair], CancellationToken.None));
                var (record, ratings) = await ReadTrackedRatingsAsync(fixture, seed.Key);
                Assert.Equal(winner.RatingsJson, record.RatingsJson);
                Assert.Equal(8.4, ratings.ImdbScore);
                Assert.Equal(8.4, ratings.ExternalValues?.ImdbScore);
                Assert.Equal(t1, ratings.ExternalFetchedUtc);
                Assert.Equal(t1, record.RatingsFetchedUtc);
            }
            else
            {
                Assert.Equal(1, await fixture.Store.WriteManyAsync("tracked", [repair], CancellationToken.None));
                var (record, ratings) = await ReadTrackedRatingsAsync(fixture, seed.Key);
                Assert.Equal(8.8, ratings.ImdbScore);
                Assert.Equal(8800, ratings.ImdbVoteCount);
                Assert.Equal(7.1, ratings.ExternalValues?.ImdbScore);
                Assert.Equal(seed.T0, ratings.ExternalFetchedUtc);
                Assert.Equal(seed.T0, record.RatingsFetchedUtc);
                Assert.Equal(seed.Record.MetadataRevision, record.MetadataRevision);
            }
            return;
        }
        if (scenario is "external-first" or "tomatoes-first")
        {
            var (external, tomatoes) = IndependentTrackedWrites(fixture, seed);
            var writes = scenario == "external-first" ? new[] { external, tomatoes } : new[] { tomatoes, external };
            foreach (var write in writes)
                Assert.Equal(1, await fixture.Store.WriteManyAsync("tracked", [write], CancellationToken.None));
            await AssertIndependentTrackedWinnersAsync(fixture, seed);
            return;
        }
        if (scenario == "equal-component-clocks")
        {
            var accepted = TrackedRatings(8.4, t1, 90, t1);
            await fixture.Store.WriteManyAsync("tracked", [TrackedWrite(seed, accepted, t1)], CancellationToken.None);
            // A later aggregate clock cannot resolve a tie in either provider's
            // successful-result clock in favor of the losing payload.
            await fixture.Store.WriteManyAsync("tracked", [TrackedWrite(seed, TrackedRatings(5.5, t1, 40, t1), now)], CancellationToken.None);
            var (_, ratings) = await ReadTrackedRatingsAsync(fixture, seed.Key);
            Assert.Equal(8.4, ratings.ImdbScore);
            Assert.Equal(90, ratings.RottenTomatoesScore);
            Assert.Equal(8.4, ratings.ExternalValues?.ImdbScore);
            Assert.Equal(90, ratings.TomatoesValues?.RottenTomatoesScore);
            Assert.Equal(t1, ratings.ExternalFetchedUtc);
            Assert.Equal(t1, ratings.TomatoesFetchedUtc);
            return;
        }
        if (scenario == "verified-empty")
        {
            var empty = TrackedRatings(null, t2, 80, seed.T0);
            await fixture.Store.WriteManyAsync("tracked", [TrackedWrite(seed, empty, t2)], CancellationToken.None);
            await fixture.Store.WriteManyAsync("tracked", [TrackedWrite(seed, TrackedRatings(8.4, t1, 80, seed.T0), now)], CancellationToken.None);
            await fixture.Store.WriteManyAsync("tracked", [TrackedWrite(seed, TrackedRatings(9.9, t2, 80, seed.T0), now.AddMinutes(1))], CancellationToken.None);
            var (_, ratings) = await ReadTrackedRatingsAsync(fixture, seed.Key);
            Assert.Null(ratings.ImdbScore);
            Assert.Null(ratings.ImdbVoteCount);
            Assert.Null(ratings.MetacriticScore);
            Assert.NotNull(ratings.ExternalValues);
            Assert.Null(ratings.ExternalValues.ImdbScore);
            Assert.Equal(t2, ratings.ExternalFetchedUtc);
            Assert.Equal(80, ratings.RottenTomatoesScore);
            Assert.Equal(seed.T0, ratings.TomatoesFetchedUtc);
            return;
        }
        if (scenario == "verified-empty-tomatoes")
        {
            await fixture.Store.WriteManyAsync("tracked", [TrackedWrite(seed, TrackedRatings(7.1, seed.T0, null, t2), t2)], CancellationToken.None);
            await fixture.Store.WriteManyAsync("tracked", [TrackedWrite(seed, TrackedRatings(7.1, seed.T0, 90, t1), now)], CancellationToken.None);
            await fixture.Store.WriteManyAsync("tracked", [TrackedWrite(seed, TrackedRatings(7.1, seed.T0, 95, t2), now.AddMinutes(1))], CancellationToken.None);
            var (_, ratings) = await ReadTrackedRatingsAsync(fixture, seed.Key);
            Assert.Null(ratings.RottenTomatoesScore);
            Assert.Null(ratings.RottenTomatoesAudienceScore);
            Assert.NotNull(ratings.TomatoesValues);
            Assert.Null(ratings.TomatoesValues.RottenTomatoesScore);
            Assert.Equal(t2, ratings.TomatoesFetchedUtc);
            Assert.Equal(7.1, ratings.ImdbScore);
            Assert.Equal(seed.T0, ratings.ExternalFetchedUtc);
            return;
        }
        if (scenario == "dedicated-empty-over-external-fallback")
        {
            var old = JsonSerializer.Deserialize<ItemEnrichmentRatings>(seed.Record.RatingsJson!)!;
            // A failed dedicated lookup carries its old payload/clock and keeps
            // its previously accepted score rather than inventing a clear.
            await fixture.Store.WriteManyAsync("tracked", [TrackedWrite(seed, old, t1)], CancellationToken.None);
            Assert.Equal(90, (await ReadTrackedRatingsAsync(fixture, seed.Key)).Ratings.RottenTomatoesScore);
            var verifiedEmpty = old with
            {
                RottenTomatoesScore = null, RottenTomatoesAudienceScore = null,
                TomatoesFetchedUtc = t2, TomatoesValues = new()
            };
            await fixture.Store.WriteManyAsync("tracked", [TrackedWrite(seed, verifiedEmpty, t2)], CancellationToken.None);
            // A later failed/stale operation has a newer aggregate clock but
            // cannot bring back either its old dedicated score or OMDb fallback.
            await fixture.Store.WriteManyAsync("tracked", [TrackedWrite(seed, old, now)], CancellationToken.None);
            var (_, ratings) = await ReadTrackedRatingsAsync(fixture, seed.Key);
            Assert.Null(ratings.RottenTomatoesScore);
            Assert.Null(ratings.RottenTomatoesAudienceScore);
            Assert.Equal(80, ratings.ExternalValues?.RottenTomatoesScore);
            Assert.Equal(80, ratings.ExternalValues?.RottenTomatoesAudienceScore);
            Assert.Equal(seed.T0, ratings.ExternalFetchedUtc);
            Assert.NotNull(ratings.TomatoesValues);
            Assert.Null(ratings.TomatoesValues.RottenTomatoesScore);
            Assert.Null(ratings.TomatoesValues.RottenTomatoesAudienceScore);
            Assert.Equal(t2, ratings.TomatoesFetchedUtc);
            return;
        }
        if (scenario is "foreign-identities" or "imdb-identity-only" or "tomatoes-identity-only")
        {
            var imdb = scenario == "tomatoes-identity-only" ? "tt901" : "tt902";
            var wiki = scenario == "imdb-identity-only" ? "Q901" : "Q902";
            var winner = TrackedRatings(9.2, t1, 95, t1, imdb, wiki);
            var metadata = seed.Metadata with { ImdbId = imdb, WikidataId = wiki };
            Assert.Equal(1, await fixture.Store.WriteManyAsync("tracked", [new(seed.Key, 0, JsonSerializer.Serialize(metadata), t1,
                JsonSerializer.Serialize(winner), t1, ExpectedMetadataRevision: seed.Record.MetadataRevision)], CancellationToken.None));
            var accepted = (await ReadTrackedRatingsAsync(fixture, seed.Key)).Record;
            var stale = TrackedRatings(8.4, t2, 90, t2);
            // Also use the current revision in the wholly foreign case so the
            // test proves component identity checks rather than revision CAS alone.
            await fixture.Store.WriteManyAsync("tracked", [TrackedWrite(seed, stale, t2) with
            {
                ExpectedMetadataRevision = scenario == "foreign-identities" ? accepted.MetadataRevision : seed.Record.MetadataRevision
            }], CancellationToken.None);
            var (record, ratings) = await ReadTrackedRatingsAsync(fixture, seed.Key);
            Assert.Equal(imdb, ratings.ImdbId);
            Assert.Equal(wiki, ratings.WikidataId);
            Assert.Equal(scenario == "tomatoes-identity-only" ? 8.4 : 9.2, ratings.ImdbScore);
            Assert.Equal(scenario == "imdb-identity-only" ? 90 : 95, ratings.RottenTomatoesScore);
            Assert.Equal(scenario == "tomatoes-identity-only" ? t2 : t1, ratings.ExternalFetchedUtc);
            Assert.Equal(scenario == "imdb-identity-only" ? t2 : t1, ratings.TomatoesFetchedUtc);
            Assert.Equal(accepted.MetadataJson, record.MetadataJson);
            Assert.Equal(accepted.MetadataRevision, record.MetadataRevision);
            return;
        }
        if (scenario == "generation-invalidation")
        {
            Assert.Equal(1, await fixture.Store.InvalidateTmdbAsync(PremiereMediaType.Movie, [seed.Key.TmdbId], now, CancellationToken.None));
            var stale = new ItemEnrichmentWrite(seed.Key, seed.Record.Generation,
                JsonSerializer.Serialize(seed.Metadata with { RuntimeMinutes = 199 }), now.AddMinutes(1),
                JsonSerializer.Serialize(TrackedRatings(8.4, now.AddMinutes(1), 90, now.AddMinutes(1))), now.AddMinutes(1),
                ExpectedMetadataRevision: seed.Record.MetadataRevision);
            Assert.Equal(0, await fixture.Store.WriteManyAsync("tracked", [stale], CancellationToken.None));
            var (record, _) = await ReadTrackedRatingsAsync(fixture, seed.Key);
            Assert.Equal(1, record.Generation);
            Assert.Equal(now, record.MetadataInvalidatedUtc);
            Assert.Equal(seed.Record.MetadataJson, record.MetadataJson);
            Assert.Equal(seed.Record.RatingsJson, record.RatingsJson);
            Assert.Equal(seed.Record.MetadataFetchedUtc, record.MetadataFetchedUtc);
            Assert.Equal(seed.Record.RatingsFetchedUtc, record.RatingsFetchedUtc);
            return;
        }
        throw new ArgumentOutOfRangeException(nameof(scenario));
    }

    private static async Task DatasetRatingsContractAsync(string scenario, Fixture fixture, TrackedSeed seed)
    {
        Assert.DoesNotContain("\"Dataset\"", seed.Record.RatingsJson!);
        Assert.DoesNotContain("\"DatasetRequired\"", seed.Record.RatingsJson!);
        var now = fixture.Clock.GetUtcNow();
        var t1 = now.AddHours(-2);
        var t2 = now.AddHours(-1);
        var proposedDataset = TrackedRatings(7.1, seed.T0, 80, seed.T0) with
        {
            ImdbScore = 9.1, ImdbVoteCount = 2100,
            DatasetRequired = true, Dataset = new("tt901", 9.1, 2100, t1)
        };
        if (scenario == "dataset-with-omdb-failure")
        {
            Assert.Equal(1, await fixture.Store.WriteManyAsync("tracked", [TrackedWrite(seed, proposedDataset, t1)], CancellationToken.None));
            var (record, ratings) = await ReadTrackedRatingsAsync(fixture, seed.Key);
            Assert.Equal(9.1, ratings.ImdbScore);
            Assert.Equal(2100, ratings.ImdbVoteCount);
            Assert.True(ratings.ExternalRequired);
            Assert.True(ratings.DatasetRequired);
            Assert.Equal(new ItemEnrichmentDatasetRating("tt901", 9.1, 2100, t1), ratings.Dataset);
            Assert.Equal(7.1, ratings.ExternalValues?.ImdbScore);
            Assert.Equal(1000, ratings.ExternalValues?.ImdbVoteCount);
            Assert.Equal(seed.T0, ratings.ExternalFetchedUtc);
            Assert.Equal(80, ratings.RottenTomatoesScore);
            Assert.Equal(seed.T0, ratings.TomatoesFetchedUtc);
            Assert.Equal(t1, record.RatingsFetchedUtc);
            return;
        }
        if (scenario is "dataset-before-providers" or "dataset-after-providers")
        {
            var external = TrackedWrite(seed, TrackedRatings(8.4, t2, 80, seed.T0), t2);
            var tomatoes = TrackedWrite(seed, TrackedRatings(7.1, seed.T0, 90, now), now) with
            {
                MetadataJson = JsonSerializer.Serialize(seed.Metadata with { RuntimeMinutes = 130 }), MetadataFetchedUtc = now
            };
            var dataset = TrackedWrite(seed, proposedDataset, t1);
            var writes = scenario == "dataset-before-providers" ? new[] { dataset, external, tomatoes } : new[] { external, tomatoes, dataset };
            foreach (var write in writes)
                Assert.Equal(1, await fixture.Store.WriteManyAsync("tracked", [write], CancellationToken.None));
            var (record, ratings) = await ReadTrackedRatingsAsync(fixture, seed.Key);
            Assert.Equal(9.1, ratings.ImdbScore);
            Assert.Equal(2100, ratings.ImdbVoteCount);
            Assert.Equal(new ItemEnrichmentDatasetRating("tt901", 9.1, 2100, t1), ratings.Dataset);
            Assert.True(ratings.DatasetRequired);
            Assert.Equal(8.4, ratings.ExternalValues?.ImdbScore);
            Assert.Equal(t2, ratings.ExternalFetchedUtc);
            Assert.Equal(90, ratings.RottenTomatoesScore);
            Assert.Equal(now, ratings.TomatoesFetchedUtc);
            Assert.Equal(now, record.RatingsFetchedUtc);
            Assert.Equal(seed.Record.MetadataRevision + 1, record.MetadataRevision);
            return;
        }
        if (scenario == "dataset-null-fallback")
        {
            await fixture.Store.WriteManyAsync("tracked", [TrackedWrite(seed, proposedDataset, t1)], CancellationToken.None);
            var empty = proposedDataset with
            {
                ImdbScore = 7.1, ImdbVoteCount = 1000, Dataset = new("tt901", null, null, t2)
            };
            await fixture.Store.WriteManyAsync("tracked", [TrackedWrite(seed, empty, t2)], CancellationToken.None);
            await fixture.Store.WriteManyAsync("tracked", [TrackedWrite(seed, proposedDataset, now)], CancellationToken.None);
            await fixture.Store.WriteManyAsync("tracked", [TrackedWrite(seed, proposedDataset with
            {
                ImdbScore = 9.9, Dataset = new("tt901", 9.9, 9999, t2)
            }, now.AddMinutes(1))], CancellationToken.None);
            var (_, ratings) = await ReadTrackedRatingsAsync(fixture, seed.Key);
            Assert.Equal(new ItemEnrichmentDatasetRating("tt901", null, null, t2), ratings.Dataset);
            Assert.Equal(7.1, ratings.ImdbScore);
            Assert.Equal(1000, ratings.ImdbVoteCount);
            Assert.Equal(7.1, ratings.ExternalValues?.ImdbScore);
            Assert.Equal(seed.T0, ratings.ExternalFetchedUtc);
            return;
        }
        if (scenario == "dataset-identity-change")
        {
            await fixture.Store.WriteManyAsync("tracked", [TrackedWrite(seed, proposedDataset, t1)], CancellationToken.None);
            var winner = TrackedRatings(9.2, t2, 95, t2, imdb: "tt902") with
            {
                ImdbScore = 8.8, ImdbVoteCount = 2200,
                DatasetRequired = true, Dataset = new("tt902", 8.8, 2200, t2)
            };
            await fixture.Store.WriteManyAsync("tracked", [new(seed.Key, seed.Record.Generation,
                JsonSerializer.Serialize(seed.Metadata with { ImdbId = "tt902" }), t2,
                JsonSerializer.Serialize(winner), t2, ExpectedMetadataRevision: seed.Record.MetadataRevision)], CancellationToken.None);
            var accepted = (await ReadTrackedRatingsAsync(fixture, seed.Key)).Record;
            // The aggregate identity matches the new title, but the nested
            // dataset still belongs to the previous IMDb ID and must be rejected.
            var foreignDataset = proposedDataset with { ImdbId = "tt902", Dataset = new("tt901", 9.9, 9999, now), ImdbScore = 9.9 };
            await fixture.Store.WriteManyAsync("tracked", [TrackedWrite(seed, foreignDataset, now) with
            {
                ExpectedMetadataRevision = accepted.MetadataRevision
            }], CancellationToken.None);
            var (_, ratings) = await ReadTrackedRatingsAsync(fixture, seed.Key);
            Assert.Equal("tt902", ratings.ImdbId);
            Assert.Equal(new ItemEnrichmentDatasetRating("tt902", 8.8, 2200, t2), ratings.Dataset);
            Assert.Equal(8.8, ratings.ImdbScore);
            Assert.Equal(9.2, ratings.ExternalValues?.ImdbScore);
            Assert.Equal(t2, ratings.ExternalFetchedUtc);
            return;
        }
        if (scenario == "dataset-identity-removal")
        {
            await fixture.Store.WriteManyAsync("tracked", [TrackedWrite(seed, proposedDataset, t1)], CancellationToken.None);
            var noIdSkip = proposedDataset with
            {
                ImdbId = null, ImdbScore = null, ImdbVoteCount = null, MetacriticScore = null,
                ExternalValues = null, ExternalFetchedUtc = null, ExternalRequired = false,
                Dataset = null, DatasetRequired = false
            };
            Assert.Equal(1, await fixture.Store.WriteManyAsync("tracked", [new(seed.Key, seed.Record.Generation,
                JsonSerializer.Serialize(seed.Metadata with { ImdbId = null }), t2,
                JsonSerializer.Serialize(noIdSkip), t2, ExpectedMetadataRevision: seed.Record.MetadataRevision)], CancellationToken.None));
            var accepted = (await ReadTrackedRatingsAsync(fixture, seed.Key)).Record;
            // A later completion still carrying the removed ID cannot re-arm
            // dataset work or restore its now-unbound score.
            await fixture.Store.WriteManyAsync("tracked", [TrackedWrite(seed, proposedDataset with
            {
                Dataset = new("tt901", 9.9, 9999, now)
            }, now) with { ExpectedMetadataRevision = accepted.MetadataRevision }], CancellationToken.None);
            var (_, ratings) = await ReadTrackedRatingsAsync(fixture, seed.Key);
            Assert.Null(ratings.ImdbId);
            Assert.Null(ratings.Dataset);
            Assert.False(ratings.DatasetRequired);
            Assert.False(ratings.ExternalRequired);
            Assert.Null(ratings.ImdbScore);
            Assert.Null(ratings.ImdbVoteCount);
            return;
        }
        throw new ArgumentOutOfRangeException(nameof(scenario));
    }

    private static (ItemEnrichmentWrite External, ItemEnrichmentWrite Tomatoes) IndependentTrackedWrites(Fixture fixture, TrackedSeed seed)
    {
        var t1 = fixture.Clock.GetUtcNow().AddHours(-2);
        var t2 = fixture.Clock.GetUtcNow().AddHours(-1);
        var external = TrackedWrite(seed, TrackedRatings(8.4, t1, 80, seed.T0), t1);
        var tomatoes = TrackedWrite(seed, TrackedRatings(7.1, seed.T0, 90, t2), t2) with
        {
            MetadataJson = JsonSerializer.Serialize(seed.Metadata with { RuntimeMinutes = 130 }), MetadataFetchedUtc = t2
        };
        return (external, tomatoes);
    }

    private static async Task AssertIndependentTrackedWinnersAsync(Fixture fixture, TrackedSeed seed)
    {
        var (record, ratings) = await ReadTrackedRatingsAsync(fixture, seed.Key);
        Assert.True(ratings.ProviderFreshnessTracked);
        Assert.True(ratings.FreshnessVerified);
        Assert.Equal(8.4, ratings.ImdbScore);
        Assert.Equal(90, ratings.RottenTomatoesScore);
        Assert.Equal(8.4, ratings.ExternalValues?.ImdbScore);
        Assert.Equal(90, ratings.TomatoesValues?.RottenTomatoesScore);
        Assert.Equal(fixture.Clock.GetUtcNow().AddHours(-2), ratings.ExternalFetchedUtc);
        Assert.Equal(fixture.Clock.GetUtcNow().AddHours(-1), ratings.TomatoesFetchedUtc);
        Assert.Equal(fixture.Clock.GetUtcNow().AddHours(-1), record.RatingsFetchedUtc);
        Assert.Equal(130, JsonSerializer.Deserialize<ItemEnrichmentMetadata>(record.MetadataJson!)?.RuntimeMinutes);
        Assert.Equal(seed.Record.MetadataRevision + 1, record.MetadataRevision);
        Assert.Equal(0, record.Generation);
        var restarted = new SqliteItemEnrichmentStore(fixture.Options, fixture.Environment, fixture.Clock);
        var persisted = (await restarted.ReadManyAsync("tracked", [seed.Key], false, CancellationToken.None))[seed.Key];
        Assert.Equal(record.RatingsJson, persisted.RatingsJson);
        Assert.Equal(record.RatingsFetchedUtc, persisted.RatingsFetchedUtc);
    }

    private static ItemEnrichmentWrite TrackedWrite(TrackedSeed seed, ItemEnrichmentRatings ratings, DateTimeOffset aggregateClock)
        => new(seed.Key, seed.Record.Generation, RatingsJson: JsonSerializer.Serialize(ratings), RatingsFetchedUtc: aggregateClock,
            ExpectedMetadataRevision: seed.Record.MetadataRevision);

    private static ItemEnrichmentRatings TrackedRatings(double? external, DateTimeOffset externalClock,
        int? tomatoes, DateTimeOffset tomatoesClock, string imdb = "tt901", string wiki = "Q901")
    {
        var votes = external.HasValue ? 1000 : (int?)null;
        var metacritic = external.HasValue ? 71 : (int?)null;
        return new(external, votes, tomatoes, tomatoes, metacritic, imdb, wiki, "Title", 2026)
        {
            ProviderFreshnessTracked = true, ExternalFetchedUtc = externalClock, TomatoesFetchedUtc = tomatoesClock,
            ExternalValues = new(ImdbScore: external, ImdbVoteCount: votes, MetacriticScore: metacritic),
            TomatoesValues = new(RottenTomatoesScore: tomatoes, RottenTomatoesAudienceScore: tomatoes)
        };
    }

    private static async Task<TrackedSeed> SeedTrackedRatingsAsync(Fixture fixture, bool externalTomatoesFallback = false,
        bool omitDatasetFields = false)
    {
        var key = new ItemEnrichmentIdentity(PremiereMediaType.Movie, 901);
        var metadata = new ItemEnrichmentMetadata { ImdbId = "tt901", WikidataId = "Q901", Title = "Title", RuntimeMinutes = 112 };
        var t0 = fixture.Clock.GetUtcNow().AddHours(-3);
        var ratings = TrackedRatings(7.1, t0, externalTomatoesFallback ? 90 : 80, t0);
        if (externalTomatoesFallback) ratings = ratings with
        {
            ExternalValues = ratings.ExternalValues! with { RottenTomatoesScore = 80, RottenTomatoesAudienceScore = 80 }
        };
        var ratingsJson = JsonSerializer.Serialize(ratings);
        if (omitDatasetFields)
        {
            var legacy = JsonNode.Parse(ratingsJson)!.AsObject();
            legacy.Remove("Dataset");
            legacy.Remove("DatasetRequired");
            ratingsJson = legacy.ToJsonString();
        }
        await fixture.Store.ReadManyAsync("tracked", [key], true, CancellationToken.None);
        Assert.Equal(1, await fixture.Store.WriteManyAsync("tracked", [new(key, 0, JsonSerializer.Serialize(metadata), t0,
            ratingsJson, t0, ExpectedMetadataRevision: 0)], CancellationToken.None));
        var record = (await fixture.Store.ReadManyAsync("tracked", [key], false, CancellationToken.None))[key];
        return new(key, metadata, record, t0);
    }

    private static async Task<(ItemEnrichmentRecord Record, ItemEnrichmentRatings Ratings)> ReadTrackedRatingsAsync(Fixture fixture, ItemEnrichmentIdentity key)
    {
        var record = (await fixture.Store.ReadManyAsync("tracked", [key], false, CancellationToken.None))[key];
        return (record, Assert.IsType<ItemEnrichmentRatings>(JsonSerializer.Deserialize<ItemEnrichmentRatings>(record.RatingsJson!)));
    }

    private sealed record TrackedSeed(ItemEnrichmentIdentity Key, ItemEnrichmentMetadata Metadata, ItemEnrichmentRecord Record, DateTimeOffset T0);

    private static Task RunScenarioAsync(string scenario, Fixture fixture) => scenario switch
    {
        "batch-restart-partitions" => BatchRestartPartitionsAsync(fixture),
        "independent-components" => IndependentComponentsAsync(fixture),
        "metadata-revision-and-observation" => MetadataRevisionAndObservationAsync(fixture),
        "ratings-metadata-revision" => RatingsMetadataRevisionAsync(fixture),
        "batched-provider-invalidation" => BatchedProviderInvalidationAsync(fixture),
        "invalidation-race" => InvalidationRaceAsync(fixture),
        "tvmaze-invalidation" => TvmazeInvalidationAsync(fixture),
        "unmapped-tvmaze-reservation" => UnmappedTvmazeReservationAsync(fixture),
        "calendar-month-retention" => CalendarMonthRetentionAsync(fixture),
        "duplicate-writes" => DuplicateWritesAsync(fixture),
        _ => throw new ArgumentOutOfRangeException(nameof(scenario))
    };

    private static async Task BatchRestartPartitionsAsync(Fixture fixture)
    {
        var keys = Enumerable.Range(1, 501)
            .Select(id => new ItemEnrichmentIdentity(id % 2 == 0 ? PremiereMediaType.Series : PremiereMediaType.Movie, id))
            .Append(new ItemEnrichmentIdentity(PremiereMediaType.Series, 1)).ToArray();
        var now = fixture.Clock.GetUtcNow();
        var reserved = await fixture.Store.ReadManyAsync("config-a", keys.Concat(keys.Take(1)).ToArray(), true, CancellationToken.None);
        Assert.Equal(keys.Length, reserved.Count);
        Assert.All(reserved.Values, record =>
        {
            Assert.Equal(0, record.Generation);
            Assert.Equal(now, record.LastSeenUtc);
            Assert.Null(record.MetadataJson);
            Assert.Null(record.RatingsFetchedUtc);
        });
        var written = await fixture.Store.WriteManyAsync("config-a", keys.Select(key => new ItemEnrichmentWrite(
            key, 0, $"{{\"id\":{key.TmdbId},\"kind\":\"{key.MediaType}\"}}", now.AddHours(-2), "{\"score\":8}", now.AddHours(-1))).ToArray(), CancellationToken.None);
        Assert.Equal(keys.Length, written);
        fixture.Clock.UtcNow = now.AddDays(2);
        var restarted = new SqliteItemEnrichmentStore(fixture.Options, fixture.Environment, fixture.Clock);
        var restored = await restarted.ReadManyAsync("config-a", keys, false, CancellationToken.None);
        Assert.Equal(keys.Length, restored.Count);
        Assert.All(restored.Values, record =>
        {
            Assert.Equal(now.AddHours(-2), record.MetadataFetchedUtc);
            Assert.Equal(now.AddHours(-1), record.RatingsFetchedUtc);
            Assert.Equal(fixture.Clock.GetUtcNow(), record.LastSeenUtc);
            Assert.Equal(0, record.Generation);
            Assert.Contains(record.Identity.MediaType.ToString(), record.MetadataJson!);
        });
        Assert.Empty(await restarted.ReadManyAsync("config-b", keys, false, CancellationToken.None));
        var isolated = Assert.Single(await restarted.ReadManyAsync("config-b", [keys[0]], true, CancellationToken.None)).Value;
        Assert.Null(isolated.MetadataJson);
        Assert.Null(isolated.MetadataFetchedUtc);
        var known = await restarted.FindKnownTmdbIdsAsync(PremiereMediaType.Movie, Enumerable.Range(1, 800).ToArray(), CancellationToken.None);
        Assert.Equal(keys.Where(key => key.MediaType == PremiereMediaType.Movie).Select(key => key.TmdbId).Order(), known.Order());
    }

    private static async Task IndependentComponentsAsync(Fixture fixture)
    {
        var key = new ItemEnrichmentIdentity(PremiereMediaType.Series, 80);
        var now = fixture.Clock.GetUtcNow();
        await fixture.Store.ReadManyAsync("a", [key], true, CancellationToken.None);
        Assert.Equal(1, await fixture.Store.WriteManyAsync("a", [new(key, 0, "{\"title\":\"old\"}", now.AddDays(-1), "{\"score\":7}", now.AddDays(-2), 123)], CancellationToken.None));
        Assert.Equal(1, await fixture.Store.WriteManyAsync("a", [new(key, 0, RatingsJson: "{\"score\":8}", RatingsFetchedUtc: now, TvmazeShowId: 999)], CancellationToken.None));
        Assert.Equal(1, await fixture.Store.WriteManyAsync("a", [new(key, 0, "{\"title\":\"new\"}", now.AddHours(-12), "{\"score\":6}", now.AddDays(-3), 456)], CancellationToken.None));
        Assert.Equal(0, await fixture.Store.WriteManyAsync("a", [new(key, 0, "{\"title\":\"late\"}", now.AddDays(-4), "{\"score\":1}", now.AddDays(-4), 999)], CancellationToken.None));
        Assert.Equal(0, await fixture.Store.WriteManyAsync("a", [new(key, 0, " ", now.AddDays(1), "{\"score\":0}", null, 999)], CancellationToken.None));
        var result = Assert.Single(await fixture.Store.ReadManyAsync("a", [key], false, CancellationToken.None)).Value;
        Assert.Equal("{\"title\":\"new\"}", result.MetadataJson);
        Assert.Equal(now.AddHours(-12), result.MetadataFetchedUtc);
        Assert.Equal("{\"score\":8}", result.RatingsJson);
        Assert.Equal(now, result.RatingsFetchedUtc);
        Assert.Equal(456, result.TvmazeShowId);
        Assert.Equal(0, result.Generation);
    }

    private static async Task MetadataRevisionAndObservationAsync(Fixture fixture)
    {
        var key = new ItemEnrichmentIdentity(PremiereMediaType.Movie, 777);
        var now = fixture.Clock.GetUtcNow();
        var fetched = now.AddHours(-2);
        var observed = now.AddHours(-1);
        await fixture.Store.ReadManyAsync("a", [key], true, CancellationToken.None);
        Assert.Equal(1, await fixture.Store.WriteManyAsync("a", [new(key, 0, "{\"title\":\"original\"}", fetched,
            "{\"score\":7}", fetched, TmdbObservedUtc: now.AddHours(-3), ExpectedMetadataRevision: 0)], CancellationToken.None));
        var initial = (await fixture.Store.ReadManyAsync("a", [key], false, CancellationToken.None))[key];
        Assert.Equal(1, initial.MetadataRevision);
        Assert.Equal(1, await fixture.Store.WriteManyAsync("a", [new(key, 0, "{\"title\":\"current observation\"}", fetched,
            TmdbObservedUtc: observed, ExpectedMetadataRevision: initial.MetadataRevision)], CancellationToken.None));
        var current = (await fixture.Store.ReadManyAsync("a", [key], false, CancellationToken.None))[key];
        Assert.Equal(2, current.MetadataRevision);
        Assert.Equal(observed, current.TmdbObservedUtc);
        // The older observation is rejected even with the current revision. Its
        // ratings still belong to that retained metadata revision and can succeed.
        Assert.Equal(1, await fixture.Store.WriteManyAsync("a", [new(key, 0, "{\"title\":\"late old observation\"}", fetched,
            "{\"score\":9}", now, TmdbObservedUtc: now.AddHours(-2), ExpectedMetadataRevision: current.MetadataRevision)], CancellationToken.None));
        Assert.Equal(0, await fixture.Store.WriteManyAsync("a", [new(key, 0, "{\"title\":\"older observation with current revision\"}", now,
            TmdbObservedUtc: now.AddHours(-2), ExpectedMetadataRevision: current.MetadataRevision)], CancellationToken.None));
        Assert.Equal(0, await fixture.Store.WriteManyAsync("a", [new(key, 0, "{\"title\":\"missing observation\"}", now,
            ExpectedMetadataRevision: current.MetadataRevision)], CancellationToken.None));
        Assert.Equal(0, await fixture.Store.WriteManyAsync("a", [new(key, 0, "{\"title\":\"tied stale other fields\"}", fetched,
            TmdbObservedUtc: observed, ExpectedMetadataRevision: initial.MetadataRevision)], CancellationToken.None));
        // A repair based on the current snapshot can change other fields while
        // retaining both the successful-fetch and vote-observation clocks.
        Assert.Equal(1, await fixture.Store.WriteManyAsync("a", [new(key, 0, "{\"title\":\"same-clock repair\"}", fetched,
            TmdbObservedUtc: observed, ExpectedMetadataRevision: current.MetadataRevision)], CancellationToken.None));
        var repaired = (await fixture.Store.ReadManyAsync("a", [key], false, CancellationToken.None))[key];
        Assert.Equal(3, repaired.MetadataRevision);
        Assert.Equal("{\"title\":\"same-clock repair\"}", repaired.MetadataJson);
        Assert.Equal(fetched, repaired.MetadataFetchedUtc);
        Assert.Equal(observed, repaired.TmdbObservedUtc);
        Assert.Equal("{\"score\":9}", repaired.RatingsJson);
        Assert.Equal(now, repaired.RatingsFetchedUtc);
        Assert.Equal(0, repaired.Generation);
        Assert.Equal(0, await fixture.Store.WriteManyAsync("a", [new(key, 0, "{\"title\":\"late tied repair\"}", fetched,
            TmdbObservedUtc: observed, ExpectedMetadataRevision: current.MetadataRevision)], CancellationToken.None));
        Assert.Equal(1, await fixture.Store.WriteManyAsync("a", [new(key, 0, "{\"title\":\"new successful metadata\"}", now.AddMinutes(1),
            TmdbObservedUtc: observed, ExpectedMetadataRevision: repaired.MetadataRevision)], CancellationToken.None));
        var advanced = (await fixture.Store.ReadManyAsync("a", [key], false, CancellationToken.None))[key];
        Assert.Equal(4, advanced.MetadataRevision);
        Assert.Equal(0, await fixture.Store.WriteManyAsync("a", [new(key, 0, "{\"title\":\"older metadata with newer votes\"}", fetched,
            TmdbObservedUtc: now.AddMinutes(2), ExpectedMetadataRevision: advanced.MetadataRevision)], CancellationToken.None));
        Assert.Equal(0, await fixture.Store.WriteManyAsync("a", [new(key, 0, "{\"title\":\"unguarded tied metadata\"}", now.AddMinutes(1),
            TmdbObservedUtc: observed)], CancellationToken.None));
        Assert.Equal(1, await fixture.Store.WriteManyAsync("a", [new(key, 0, " ", now.AddMinutes(2), "{\"score\":10}", now.AddMinutes(3),
            TmdbObservedUtc: now.AddMinutes(2), ExpectedMetadataRevision: advanced.MetadataRevision)], CancellationToken.None));
        var final = (await fixture.Store.ReadManyAsync("a", [key], false, CancellationToken.None))[key];
        Assert.Equal(advanced.MetadataJson, final.MetadataJson);
        Assert.Equal(advanced.MetadataRevision, final.MetadataRevision);
        Assert.Equal(advanced.MetadataFetchedUtc, final.MetadataFetchedUtc);
        Assert.Equal(observed, final.TmdbObservedUtc);
        Assert.Equal("{\"score\":10}", final.RatingsJson);
        Assert.Equal(now.AddMinutes(3), final.RatingsFetchedUtc);
    }

    private static async Task RatingsMetadataRevisionAsync(Fixture fixture)
    {
        var key = new ItemEnrichmentIdentity(PremiereMediaType.Series, 800);
        var now = fixture.Clock.GetUtcNow();
        const string originalMetadata = "{\"imdbId\":\"ttoriginal\"}";
        const string winningMetadata = "{\"imdbId\":\"ttwinner\"}";
        const string winningRatings = "{\"imdbId\":\"ttwinner\",\"score\":9}";
        await fixture.Store.ReadManyAsync("a", [key], true, CancellationToken.None);
        Assert.Equal(1, await fixture.Store.WriteManyAsync("a", [new(key, 0, originalMetadata, now.AddHours(-3),
            "{\"imdbId\":\"ttoriginal\",\"score\":6}", now.AddHours(-3), ExpectedMetadataRevision: 0)], CancellationToken.None));
        var original = (await fixture.Store.ReadManyAsync("a", [key], false, CancellationToken.None))[key];
        Assert.Equal(1, await fixture.Store.WriteManyAsync("a", [new(key, 0, winningMetadata, now.AddHours(-2),
            winningRatings, now.AddHours(-2), ExpectedMetadataRevision: original.MetadataRevision)], CancellationToken.None));
        var winner = (await fixture.Store.ReadManyAsync("a", [key], false, CancellationToken.None))[key];
        Assert.Equal(2, winner.MetadataRevision);
        // Both components of the losing operation refer to the old identity.
        // Even a newer ratings timestamp must not replace the winner's good score.
        Assert.Equal(0, await fixture.Store.WriteManyAsync("a", [new(key, 0, originalMetadata, now.AddHours(-1),
            "{\"imdbId\":\"ttoriginal\",\"score\":2}", now.AddHours(-1), ExpectedMetadataRevision: original.MetadataRevision)], CancellationToken.None));
        var retained = (await fixture.Store.ReadManyAsync("a", [key], false, CancellationToken.None))[key];
        Assert.Equal(winningMetadata, retained.MetadataJson);
        Assert.Equal(winningRatings, retained.RatingsJson);
        Assert.Equal(now.AddHours(-2), retained.RatingsFetchedUtc);
        Assert.Equal(winner.MetadataRevision, retained.MetadataRevision);
        // A metadata clock rejection does not prevent ratings that explicitly
        // belong to the current metadata revision from being accepted.
        Assert.Equal(1, await fixture.Store.WriteManyAsync("a", [new(key, 0, "{\"staleMetadata\":true}", now.AddHours(-4),
            "{\"imdbId\":\"ttwinner\",\"score\":8}", now.AddHours(-1), ExpectedMetadataRevision: winner.MetadataRevision)], CancellationToken.None));
        Assert.Equal(0, await fixture.Store.WriteManyAsync("a", [new(key, 0,
            RatingsJson: "{\"imdbId\":\"ttoriginal\",\"score\":1}", RatingsFetchedUtc: now,
            ExpectedMetadataRevision: original.MetadataRevision)], CancellationToken.None));
        Assert.Equal(1, await fixture.Store.WriteManyAsync("a", [new(key, 0,
            RatingsJson: "{\"imdbId\":\"ttwinner\",\"score\":7}", RatingsFetchedUtc: now,
            ExpectedMetadataRevision: winner.MetadataRevision)], CancellationToken.None));
        var ratingsOnly = (await fixture.Store.ReadManyAsync("a", [key], false, CancellationToken.None))[key];
        Assert.Equal(winningMetadata, ratingsOnly.MetadataJson);
        Assert.Equal("{\"imdbId\":\"ttwinner\",\"score\":7}", ratingsOnly.RatingsJson);
        Assert.Equal(winner.MetadataRevision, ratingsOnly.MetadataRevision);
        // An accepted metadata mutation and its corresponding ratings share the
        // pre-update revision; the metadata increment does not reject its ratings.
        Assert.Equal(1, await fixture.Store.WriteManyAsync("a", [new(key, 0, "{\"imdbId\":\"ttnewidentity\"}", now.AddMinutes(1),
            "{\"imdbId\":\"ttnewidentity\",\"score\":10}", now.AddMinutes(1), ExpectedMetadataRevision: winner.MetadataRevision)], CancellationToken.None));
        var changedTogether = (await fixture.Store.ReadManyAsync("a", [key], false, CancellationToken.None))[key];
        Assert.Equal(3, changedTogether.MetadataRevision);
        Assert.Equal("{\"imdbId\":\"ttnewidentity\"}", changedTogether.MetadataJson);
        Assert.Equal("{\"imdbId\":\"ttnewidentity\",\"score\":10}", changedTogether.RatingsJson);
        Assert.Equal(now.AddMinutes(1), changedTogether.RatingsFetchedUtc);
    }

    private static async Task BatchedProviderInvalidationAsync(Fixture fixture)
    {
        var keys = Enumerable.Range(2000, 301).Select(id => new ItemEnrichmentIdentity(PremiereMediaType.Series, id)).ToArray();
        var movie = keys[0] with { MediaType = PremiereMediaType.Movie };
        var now = fixture.Clock.GetUtcNow();
        await fixture.Store.ReadManyAsync("a", keys.Append(movie).ToArray(), true, CancellationToken.None);
        await fixture.Store.ReadManyAsync("b", [keys[0]], true, CancellationToken.None);
        await fixture.Store.WriteManyAsync("a", keys.Select(key => new ItemEnrichmentWrite(key, 0,
            "{\"title\":\"kept\"}", now.AddDays(-2), "{\"score\":8}", now.AddDays(-1), key.TmdbId + 10000)).ToArray(), CancellationToken.None);
        await fixture.Store.WriteManyAsync("b", [new(keys[0], 0, "{\"title\":\"other config\"}", now.AddDays(-2), TvmazeShowId: keys[0].TmdbId + 10000)], CancellationToken.None);
        // More than one SQL chunk, with a different actual change time per ID.
        var original = keys.Select((key, index) => (key.TmdbId, Changed: now.AddSeconds(-index)))
            .ToDictionary(change => change.TmdbId, change => change.Changed);
        original[-1] = now;
        original[999999] = now;
        Assert.Equal(302, await fixture.Store.InvalidateTmdbManyAsync(PremiereMediaType.Series, original, CancellationToken.None));
        Assert.Equal(0, await fixture.Store.InvalidateTmdbManyAsync(PremiereMediaType.Series, original, CancellationToken.None));
        var mixedTmdb = keys.Select((key, index) => (key.TmdbId, Changed: original[key.TmdbId].AddMinutes(index % 2 == 0 ? 1 : -1)))
            .ToDictionary(change => change.TmdbId, change => change.Changed);
        Assert.Equal(152, await fixture.Store.InvalidateTmdbManyAsync(PremiereMediaType.Series, mixedTmdb, CancellationToken.None));
        var mixedTvmaze = keys.Select((key, index) =>
        {
            var tmdbLatest = index % 2 == 0 ? mixedTmdb[key.TmdbId] : original[key.TmdbId];
            return (ShowId: key.TmdbId + 10000, Changed: tmdbLatest.AddMinutes(index % 3 == 0 ? 2 : -2));
        }).ToDictionary(change => change.ShowId, change => change.Changed);
        Assert.Equal(102, await fixture.Store.InvalidateTvmazeManyAsync(mixedTvmaze, CancellationToken.None));
        Assert.Equal(0, await fixture.Store.InvalidateTvmazeManyAsync(mixedTvmaze, CancellationToken.None));
        Assert.Equal(0, await fixture.Store.InvalidateTmdbManyAsync(PremiereMediaType.Series, original, CancellationToken.None));
        var records = await fixture.Store.ReadManyAsync("a", keys.Append(movie).ToArray(), false, CancellationToken.None);
        for (var index = 0; index < keys.Length; index++)
        {
            var key = keys[index];
            var tmdbLatest = index % 2 == 0 ? mixedTmdb[key.TmdbId] : original[key.TmdbId];
            var expectedLatest = index % 3 == 0 ? mixedTvmaze[key.TmdbId + 10000] : tmdbLatest;
            Assert.Equal(expectedLatest, records[key].MetadataInvalidatedUtc);
            Assert.Equal(1 + (index % 2 == 0 ? 1 : 0) + (index % 3 == 0 ? 1 : 0), records[key].Generation);
            Assert.Equal("{\"title\":\"kept\"}", records[key].MetadataJson);
            Assert.Equal(now.AddDays(-2), records[key].MetadataFetchedUtc);
            Assert.Equal(now.AddDays(-1), records[key].RatingsFetchedUtc);
            Assert.Equal(1, records[key].MetadataRevision);
        }
        Assert.Equal(0, records[movie].Generation);
        Assert.Null(records[movie].MetadataInvalidatedUtc);
        var otherPartition = (await fixture.Store.ReadManyAsync("b", [keys[0]], false, CancellationToken.None))[keys[0]];
        Assert.Equal(3, otherPartition.Generation);
        Assert.Equal(mixedTvmaze[keys[0].TmdbId + 10000], otherPartition.MetadataInvalidatedUtc);
        Assert.Equal("{\"title\":\"other config\"}", otherPartition.MetadataJson);
    }

    private static async Task InvalidationRaceAsync(Fixture fixture)
    {
        var key = new ItemEnrichmentIdentity(PremiereMediaType.Movie, 91);
        var otherKind = key with { MediaType = PremiereMediaType.Series };
        var missing = new ItemEnrichmentIdentity(PremiereMediaType.Movie, 92);
        var now = fixture.Clock.GetUtcNow();
        await fixture.Store.ReadManyAsync("a", [key, missing, otherKind], true, CancellationToken.None);
        await fixture.Store.ReadManyAsync("b", [key], true, CancellationToken.None);
        Assert.Contains(missing.TmdbId, await fixture.Store.FindKnownTmdbIdsAsync(PremiereMediaType.Movie, [missing.TmdbId], CancellationToken.None));
        await fixture.Store.WriteManyAsync("a", [new(key, 0, "{\"title\":\"saved\"}", now.AddHours(-2), "{\"score\":8}", now.AddHours(-1))], CancellationToken.None);
        var releaseProvider = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateProvider = CompleteAfterProviderAsync();
        async Task<int> CompleteAfterProviderAsync()
        {
            await releaseProvider.Task;
            return await fixture.Store.WriteManyAsync("a", [new(key, 0, "{\"title\":\"late\"}", now), new(missing, 0, "{\"title\":\"late miss\"}", now)], CancellationToken.None);
        }
        var changed = now.AddMinutes(-1);
        Assert.Equal(3, await fixture.Store.InvalidateTmdbAsync(PremiereMediaType.Movie, [key.TmdbId, missing.TmdbId], changed, CancellationToken.None));
        releaseProvider.SetResult();
        Assert.Equal(0, await lateProvider.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, await fixture.Store.InvalidateTmdbAsync(PremiereMediaType.Movie, [key.TmdbId, missing.TmdbId], changed, CancellationToken.None));
        Assert.Equal(0, await fixture.Store.InvalidateTmdbAsync(PremiereMediaType.Movie, [key.TmdbId], changed.AddMinutes(-1), CancellationToken.None));
        var rows = await fixture.Store.ReadManyAsync("a", [key, missing, otherKind], false, CancellationToken.None);
        Assert.Equal(1, rows[key].Generation);
        Assert.Equal(changed, rows[key].MetadataInvalidatedUtc);
        Assert.Equal("{\"title\":\"saved\"}", rows[key].MetadataJson);
        Assert.Equal(now.AddHours(-2), rows[key].MetadataFetchedUtc);
        Assert.Equal(now.AddHours(-1), rows[key].RatingsFetchedUtc);
        Assert.Equal(1, rows[missing].Generation);
        Assert.Null(rows[missing].MetadataJson);
        Assert.Equal(0, rows[otherKind].Generation);
        Assert.Equal(1, (await fixture.Store.ReadManyAsync("b", [key], false, CancellationToken.None))[key].Generation);
        Assert.Equal(0, await fixture.Store.WriteManyAsync("a", [new(key, 1, "{\"title\":\"predates invalidation\"}", changed.AddSeconds(-1))], CancellationToken.None));
        Assert.Equal(1, await fixture.Store.WriteManyAsync("a", [new(key, 1, "{\"title\":\"current\"}", now)], CancellationToken.None));
        var refreshed = (await fixture.Store.ReadManyAsync("a", [key], false, CancellationToken.None))[key];
        Assert.Equal(now, refreshed.MetadataFetchedUtc);
        Assert.Equal(changed, refreshed.MetadataInvalidatedUtc);
        Assert.Equal(now.AddHours(-1), refreshed.RatingsFetchedUtc);
        Assert.Equal(1, refreshed.Generation);
    }

    private static async Task TvmazeInvalidationAsync(Fixture fixture)
    {
        var first = new ItemEnrichmentIdentity(PremiereMediaType.Series, 11);
        var second = new ItemEnrichmentIdentity(PremiereMediaType.Series, 12);
        var movie = first with { MediaType = PremiereMediaType.Movie };
        var now = fixture.Clock.GetUtcNow();
        await fixture.Store.ReadManyAsync("a", [first, second, movie], true, CancellationToken.None);
        await fixture.Store.ReadManyAsync("b", [first], true, CancellationToken.None);
        await fixture.Store.WriteManyAsync("a", [new(first, 0, "{}", now.AddHours(-1), "{}", now, 1001), new(second, 0, "{}", now.AddHours(-1), TvmazeShowId: 1002), new(movie, 0, "{}", now.AddHours(-1), TvmazeShowId: 1001)], CancellationToken.None);
        await fixture.Store.WriteManyAsync("b", [new(first, 0, "{}", now.AddHours(-1), TvmazeShowId: 1001)], CancellationToken.None);
        Assert.Equal(2, await fixture.Store.InvalidateTvmazeAsync([1001, 9999], now, CancellationToken.None));
        var rows = await fixture.Store.ReadManyAsync("a", [first, second, movie], false, CancellationToken.None);
        Assert.Equal(1, rows[first].Generation);
        Assert.Equal(now.AddHours(-1), rows[first].MetadataFetchedUtc);
        Assert.Equal(now, rows[first].RatingsFetchedUtc);
        Assert.Equal(0, rows[second].Generation);
        Assert.Equal(0, rows[movie].Generation);
        Assert.Equal(0, await fixture.Store.InvalidateTvmazeAsync([1001], now, CancellationToken.None));
    }

    private static async Task UnmappedTvmazeReservationAsync(Fixture fixture)
    {
        var unresolved = new ItemEnrichmentIdentity(PremiereMediaType.Series, 101);
        var cachedWithoutMap = new ItemEnrichmentIdentity(PremiereMediaType.Series, 102);
        var cachedWithInvalidation = new ItemEnrichmentIdentity(PremiereMediaType.Series, 105);
        var mapped = new ItemEnrichmentIdentity(PremiereMediaType.Series, 103);
        var movie = unresolved with { MediaType = PremiereMediaType.Movie };
        var now = fixture.Clock.GetUtcNow();
        await fixture.Store.ReadManyAsync("a", [unresolved, cachedWithoutMap, cachedWithInvalidation, mapped, movie], true, CancellationToken.None);
        await fixture.Store.ReadManyAsync("b", [unresolved], true, CancellationToken.None);
        await fixture.Store.WriteManyAsync("a", [
            new(unresolved, 0, RatingsJson: "{\"score\":7}", RatingsFetchedUtc: now.AddHours(-1)),
            new(cachedWithoutMap, 0, "{\"title\":\"cached\"}", now.AddHours(-2), "{\"score\":8}", now.AddHours(-1)),
            new(cachedWithInvalidation, 0, "{\"title\":\"cached after prior change\"}", now.AddHours(-2), "{\"score\":9}", now.AddHours(-1)),
            new(mapped, 0, "{\"title\":\"mapped\"}", now.AddHours(-2), TvmazeShowId: 1103)
        ], CancellationToken.None);
        var earlierChange = now.AddHours(-3);
        Assert.Equal(1, await fixture.Store.InvalidateTmdbAsync(PremiereMediaType.Series, [cachedWithInvalidation.TmdbId], earlierChange, CancellationToken.None));
        fixture.Clock.UtcNow = now.AddMinutes(1);
        var epoch = fixture.Clock.GetUtcNow();
        Assert.Equal(4, await fixture.Store.FenceUnmappedTvmazeReservationsAsync(epoch, CancellationToken.None));
        Assert.Equal(0, await fixture.Store.FenceUnmappedTvmazeReservationsAsync(epoch, CancellationToken.None));
        Assert.Equal(0, await fixture.Store.FenceUnmappedTvmazeReservationsAsync(epoch.AddSeconds(-1), CancellationToken.None));
        // The result resolves its first TVmaze ID only after the update feed ran.
        Assert.Equal(0, await fixture.Store.WriteManyAsync("a", [new(unresolved, 0,
            "{\"title\":\"late first lookup\"}", epoch, TvmazeShowId: 1101)], CancellationToken.None));
        Assert.Equal(0, await fixture.Store.WriteManyAsync("a", [
            new(cachedWithoutMap, 0, "{\"title\":\"late cached lookup\"}", epoch, TvmazeShowId: 1102),
            new(cachedWithInvalidation, 1, "{\"title\":\"late cached lookup after prior change\"}", epoch, TvmazeShowId: 1105)
        ], CancellationToken.None));
        await using (var connection = DatabaseConnectionFactory.Create(fixture.Options.Value, fixture.Environment.ContentRootPath))
        {
            await connection.OpenAsync();
            await using var retention = connection.CreateCommand();
            retention.CommandText = "SELECT LastSeenUtc FROM ItemEnrichments WHERE PartitionKey = 'a' AND MediaType = 'Series' AND TmdbId = 101";
            Assert.Equal(now, DateTimeOffset.Parse((string)(await retention.ExecuteScalarAsync())!));
        }
        var rows = await fixture.Store.ReadManyAsync("a", [unresolved, cachedWithoutMap, cachedWithInvalidation, mapped, movie], false, CancellationToken.None);
        Assert.Equal(1, rows[unresolved].Generation);
        Assert.Equal(epoch, rows[unresolved].MetadataInvalidatedUtc);
        Assert.Null(rows[unresolved].TvmazeShowId);
        Assert.Null(rows[unresolved].MetadataJson);
        Assert.Null(rows[unresolved].MetadataFetchedUtc);
        Assert.Equal("{\"score\":7}", rows[unresolved].RatingsJson);
        Assert.Equal(now.AddHours(-1), rows[unresolved].RatingsFetchedUtc);
        Assert.Equal(1, rows[cachedWithoutMap].Generation);
        Assert.Equal("{\"title\":\"cached\"}", rows[cachedWithoutMap].MetadataJson);
        Assert.Equal(now.AddHours(-2), rows[cachedWithoutMap].MetadataFetchedUtc);
        Assert.Equal(now.AddHours(-1), rows[cachedWithoutMap].RatingsFetchedUtc);
        Assert.Null(rows[cachedWithoutMap].MetadataInvalidatedUtc);
        Assert.Equal(2, rows[cachedWithInvalidation].Generation);
        Assert.Equal("{\"title\":\"cached after prior change\"}", rows[cachedWithInvalidation].MetadataJson);
        Assert.Equal(now.AddHours(-2), rows[cachedWithInvalidation].MetadataFetchedUtc);
        Assert.Equal(now.AddHours(-1), rows[cachedWithInvalidation].RatingsFetchedUtc);
        Assert.Equal("{\"score\":9}", rows[cachedWithInvalidation].RatingsJson);
        Assert.Equal(earlierChange, rows[cachedWithInvalidation].MetadataInvalidatedUtc);
        Assert.True(rows[cachedWithInvalidation].MetadataFetchedUtc!.Value >= rows[cachedWithInvalidation].MetadataInvalidatedUtc!.Value);
        Assert.Equal(0, rows[mapped].Generation);
        Assert.Null(rows[mapped].MetadataInvalidatedUtc);
        Assert.Equal(0, rows[movie].Generation);
        Assert.Null(rows[movie].MetadataInvalidatedUtc);
        Assert.Equal(1, (await fixture.Store.ReadManyAsync("b", [unresolved], false, CancellationToken.None))[unresolved].Generation);
        var afterFeed = new ItemEnrichmentIdentity(PremiereMediaType.Series, 104);
        var newReservation = Assert.Single(await fixture.Store.ReadManyAsync("a", [afterFeed], true, CancellationToken.None)).Value;
        Assert.Equal(0, newReservation.Generation);
        Assert.Equal(1, await fixture.Store.WriteManyAsync("a", [new(afterFeed, newReservation.Generation,
            "{\"title\":\"new reservation\"}", epoch, TvmazeShowId: 1104)], CancellationToken.None));
        Assert.Equal(4, await fixture.Store.FenceUnmappedTvmazeReservationsAsync(epoch.AddMinutes(1), CancellationToken.None));
        var afterAnotherFeed = (await fixture.Store.ReadManyAsync("a", [cachedWithoutMap, cachedWithInvalidation], false, CancellationToken.None));
        Assert.Equal(now.AddHours(-2), afterAnotherFeed[cachedWithoutMap].MetadataFetchedUtc);
        Assert.Null(afterAnotherFeed[cachedWithoutMap].MetadataInvalidatedUtc);
        Assert.Equal(now.AddHours(-2), afterAnotherFeed[cachedWithInvalidation].MetadataFetchedUtc);
        Assert.Equal(earlierChange, afterAnotherFeed[cachedWithInvalidation].MetadataInvalidatedUtc);
    }

    private static async Task CalendarMonthRetentionAsync(Fixture fixture)
    {
        var keys = Enumerable.Range(1, 3).Select(id => new ItemEnrichmentIdentity(PremiereMediaType.Movie, id)).ToArray();
        var monthEnd = DateTimeOffset.Parse("2026-11-30T12:00:00Z");
        var cutoff = monthEnd.AddMonths(-3);
        fixture.Clock.UtcNow = cutoff.AddTicks(-1);
        await fixture.Store.ReadManyAsync("a", [keys[0]], true, CancellationToken.None);
        fixture.Clock.UtcNow = cutoff;
        await fixture.Store.ReadManyAsync("a", [keys[1]], true, CancellationToken.None);
        fixture.Clock.UtcNow = cutoff.AddDays(1);
        await fixture.Store.ReadManyAsync("other-config", [keys[2]], true, CancellationToken.None);
        fixture.Clock.UtcNow = monthEnd;
        // Looking up delta candidates must not keep unused items alive.
        Assert.Contains(keys[0].TmdbId, await fixture.Store.FindKnownTmdbIdsAsync(PremiereMediaType.Movie, [keys[0].TmdbId], CancellationToken.None));
        Assert.Equal(1, await fixture.Store.CleanupAsync(fixture.Clock.GetUtcNow().AddMonths(-3), CancellationToken.None));
        Assert.Equal(keys[1], Assert.Single(await fixture.Store.ReadManyAsync("a", keys, false, CancellationToken.None)).Key);
        Assert.Equal(keys[2], Assert.Single(await fixture.Store.ReadManyAsync("other-config", keys, false, CancellationToken.None)).Key);
        Assert.True(cutoff.AddDays(1) < monthEnd.AddDays(-90));
    }

    private static async Task DuplicateWritesAsync(Fixture fixture)
    {
        var key = new ItemEnrichmentIdentity(PremiereMediaType.Movie, 3);
        var now = fixture.Clock.GetUtcNow();
        Assert.Equal(0, await fixture.Store.WriteManyAsync("a", [new(key, 0, "{}", now)], CancellationToken.None));
        await fixture.Store.ReadManyAsync("a", [key], true, CancellationToken.None);
        Assert.Equal(3, await fixture.Store.WriteManyAsync("a", [new(key, 0, "{\"title\":1}", now.AddHours(-1)), new(key, 0, RatingsJson: "{\"score\":9}", RatingsFetchedUtc: now), new(key, 0, "{\"title\":2}", now)], CancellationToken.None));
        var row = (await fixture.Store.ReadManyAsync("a", [key], false, CancellationToken.None))[key];
        Assert.Equal("{\"title\":2}", row.MetadataJson);
        Assert.Equal("{\"score\":9}", row.RatingsJson);
    }

    private static async Task SchemaThreeUpgradeAsync(Fixture fixture, bool postgres)
    {
        await using var connection = DatabaseConnectionFactory.Create(fixture.Options.Value, fixture.Environment.ContentRootPath);
        await connection.OpenAsync();
        await using (var prepare = connection.CreateCommand())
        {
            prepare.CommandText = """
                INSERT INTO AppParameters (Key, Value, UpdatedUtc) VALUES ('preserved-setting', 'manual-value', '2026-01-01');
                INSERT INTO OmdbCache (ImdbId, Json, CachedAtUtc) VALUES ('tt123', '{"saved":true}', '2026-01-01');
                INSERT INTO ProviderCacheState (Provider, Scope, CacheKey, LastCheckedUtc, LastChangedUtc, Watermark, MetadataJson)
                    VALUES ('tmdb', 'Global', 'preserved-delta', '2026-01-01', '2026-01-01', 'saved-watermark', '{}');
                DROP TABLE ItemEnrichments;
                DELETE FROM SchemaMigrations WHERE Version = 4;
                """ + (postgres ? "" : "PRAGMA user_version = 3;");
            await prepare.ExecuteNonQueryAsync();
        }
        var previousHistory = await ReadHistoryAsync();
        var recovery = new DatabaseRecoveryState();
        if (postgres)
            await new PostgresDatabaseInitializer(fixture.Options, fixture.Environment, recovery, TimeProvider.System,
                NullLogger<PostgresDatabaseInitializer>.Instance, new PremiereTelemetry()).InitializeAsync();
        else
            await new SqliteDatabaseInitializer(fixture.Options, fixture.Environment, recovery, TimeProvider.System,
                NullLogger<SqliteDatabaseInitializer>.Instance).InitializeAsync();
        Assert.True(recovery.Snapshot.IsHealthy, recovery.Snapshot.Message);
        Assert.Equal(4, recovery.Snapshot.CurrentVersion);
        Assert.Equal(previousHistory, await ReadHistoryAsync());
        await using var verify = connection.CreateCommand();
        verify.CommandText = "SELECT Value FROM AppParameters WHERE Key = 'preserved-setting'";
        Assert.Equal("manual-value", await verify.ExecuteScalarAsync());
        verify.CommandText = "SELECT Json FROM OmdbCache WHERE ImdbId = 'tt123'";
        Assert.Equal("{\"saved\":true}", await verify.ExecuteScalarAsync());
        verify.CommandText = "SELECT Watermark FROM ProviderCacheState WHERE CacheKey = 'preserved-delta'";
        Assert.Equal("saved-watermark", await verify.ExecuteScalarAsync());
        var key = new ItemEnrichmentIdentity(PremiereMediaType.Movie, 9);
        Assert.Single(await new SqliteItemEnrichmentStore(fixture.Options, fixture.Environment)
            .ReadManyAsync("upgraded", [key], true, CancellationToken.None));

        async Task<string[]> ReadHistoryAsync()
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT Version, Name, Checksum, AppliedUtc FROM SchemaMigrations WHERE Version <= 3 ORDER BY Version";
            var history = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) history.Add($"{reader.GetInt32(0)}|{reader.GetString(1)}|{reader.GetString(2)}|{reader.GetString(3)}");
            return history.ToArray();
        }
    }

    private static async Task WithStoreAsync(bool postgres, Func<Fixture, Task> action)
    {
        var root = Path.Combine(Path.GetTempPath(), "premierecalendar-item-store-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        NpgsqlConnection? admin = null;
        var database = $"premierecalendar_items_test_{Guid.NewGuid():N}";
        try
        {
            var environment = new StoreEnvironment(root);
            IOptions<AppDatabaseOptions> options;
            if (postgres)
            {
                var builder = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("PREMIERECALENDAR_TEST_POSTGRES")) { Database = "postgres" };
                var passwordFile = Environment.GetEnvironmentVariable("PREMIERECALENDAR_TEST_POSTGRES_PASSWORD_FILE");
                if (!string.IsNullOrWhiteSpace(passwordFile)) builder.Password = File.ReadAllText(passwordFile).TrimEnd('\r', '\n');
                admin = new NpgsqlConnection(builder.ConnectionString);
                await admin.OpenAsync();
                await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin)) await create.ExecuteNonQueryAsync();
                builder.Database = database;
                options = Microsoft.Extensions.Options.Options.Create(new AppDatabaseOptions { Provider = "PostgreSql", ConnectionString = builder.ConnectionString });
                var recovery = new DatabaseRecoveryState();
                await new PostgresDatabaseInitializer(options, environment, recovery, TimeProvider.System, NullLogger<PostgresDatabaseInitializer>.Instance, new PremiereTelemetry()).InitializeAsync();
                Assert.True(recovery.Snapshot.IsHealthy, recovery.Snapshot.Message);
            }
            else
            {
                options = Microsoft.Extensions.Options.Options.Create(new AppDatabaseOptions { Path = "items.db" });
                TestSqliteDatabase.Initialize(root, "items.db");
            }
            var clock = new StoreClock { UtcNow = DateTimeOffset.Parse("2026-11-03T12:00:00Z") };
            await action(new Fixture(options, environment, clock, new SqliteItemEnrichmentStore(options, environment, clock)));
        }
        finally
        {
            if (admin is not null)
            {
                // Close only this fixture's pooled connections; other integration
                // tests can be using independent databases concurrently.
                await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)", admin);
                await drop.ExecuteNonQueryAsync();
                await admin.DisposeAsync();
            }
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed record Fixture(IOptions<AppDatabaseOptions> Options, StoreEnvironment Environment, StoreClock Clock, SqliteItemEnrichmentStore Store);
    private sealed class StoreClock : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; }
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
    private sealed class StoreEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PremiereCalendar.IntegrationTests";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
    private sealed class ConfiguredPostgresTheoryAttribute : TheoryAttribute
    {
        public ConfiguredPostgresTheoryAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PREMIERECALENDAR_TEST_POSTGRES")))
                Skip = "Configure PREMIERECALENDAR_TEST_POSTGRES for isolated PostgreSQL item-store coverage.";
        }
    }
    private sealed class ConfiguredPostgresFactAttribute : FactAttribute
    {
        public ConfiguredPostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PREMIERECALENDAR_TEST_POSTGRES")))
                Skip = "Configure PREMIERECALENDAR_TEST_POSTGRES for isolated PostgreSQL item-store coverage.";
        }
    }
}
