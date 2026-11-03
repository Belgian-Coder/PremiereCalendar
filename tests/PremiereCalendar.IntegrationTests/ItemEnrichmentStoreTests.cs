using Microsoft.AspNetCore.Hosting;
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
