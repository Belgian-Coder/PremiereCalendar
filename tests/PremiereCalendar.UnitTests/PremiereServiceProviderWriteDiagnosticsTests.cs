using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class PremiereServiceTests
{
    [Fact]
    public async Task UncontestedProviderWriteIsAcceptedWhenRealProviderClockDiffersFromReservation()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PremiereCalendar-provider-write-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var diagnostics = new PerformanceDiagnosticStore(new(), directory);
            var telemetry = new PremiereTelemetry(diagnostics);
            var store = new MemoryItemEnrichmentStore(TimeProvider.System);
            var coordinator = new ItemEnrichmentCacheCoordinator(store,
                Microsoft.Extensions.Options.Options.Create(new ItemEnrichmentCacheOptions()),
                Microsoft.Extensions.Options.Options.Create(new TmdbOptions()),
                NullLogger<ItemEnrichmentCacheCoordinator>.Instance, timeProvider: TimeProvider.System, telemetry: telemetry);
            var date = new DateOnly(2026, 11, 2);
            var item = Assert.Single(await CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"),
                rottenTomatoes: new ComponentTomatoesClient { Result = new(80, 80) }, itemEnrichmentCache: coordinator)
                .GetPremieresAsync(date, date, CancellationToken.None, filters: new CalendarFilters { ShowSeries = false }));
            var identity = new ItemEnrichmentIdentity(item.MediaType, item.TmdbId);
            var operation = Guid.NewGuid().ToString("N");
            using (telemetry.UseDiagnosticContext(new() { OperationId = operation }))
            {
                var session = await coordinator.CreateSessionAsync(CancellationToken.None);
                await session.PrepareAsync([identity], null, CancellationToken.None);
                await Task.Delay(10);
                var fetched = TimeProvider.System.GetUtcNow();
                var proposed = await session.GetAsync(identity, true, (previous, _, _, _) => Task.FromResult(new ItemEnrichmentRefresh(null,
                    previous.Ratings! with
                    {
                        ImdbScore = 8.4, ExternalFetchedUtc = fetched,
                        ExternalValues = previous.Ratings!.ExternalValues! with { ImdbScore = 8.4 },
                        RottenTomatoesScore = 90, RottenTomatoesAudienceScore = 90, TomatoesFetchedUtc = fetched,
                        TomatoesValues = new(RottenTomatoesScore: 90, RottenTomatoesAudienceScore: 90)
                    })), CancellationToken.None, ratingsOnly: true);
                Assert.True(fetched > proposed.Record.RatingsFetchedUtc);
                await session.FlushAsync(CancellationToken.None);
                Assert.Equal(fetched, Assert.Single(store.Records.Values).RatingsFetchedUtc);
            }
            var events = ReadProviderWriteAuditEvents(telemetry, directory, operation);
            var writes = events.Where(value => value.GetProperty("event").GetString() == "item_cache.write.component")
                .Select(value => value.GetProperty("details")).ToArray();
            Assert.Equal("accepted_projection", Assert.Single(writes, value => value.GetProperty("stage").GetString() == "ratings")
                .GetProperty("outcome").GetString());
            Assert.DoesNotContain(writes, value => value.GetProperty("outcome").GetString() == "merged_projection");
            Assert.All(writes.Where(value => value.TryGetProperty("provider", out var provider)
                && provider.GetString() is "omdb" or "rottentomatoes"), value =>
                    Assert.Equal("accepted_projection", value.GetProperty("outcome").GetString()));
            Assert.Equal("reused", Assert.Single(writes, value => value.TryGetProperty("provider", out var provider)
                && provider.GetString() == "imdb_dataset").GetProperty("outcome").GetString());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task NoProviderAdvanceIsReusedEvenWhenAnotherWriterChangedTheAcceptedProjection()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PremiereCalendar-noop-provider-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var diagnostics = new PerformanceDiagnosticStore(new(), directory);
            var telemetry = new PremiereTelemetry(diagnostics);
            var clock = new ItemCacheClock();
            var store = new MemoryItemEnrichmentStore(clock);
            var coordinator = ItemCacheCoordinator(store, clock, telemetry: telemetry);
            var date = new DateOnly(2026, 11, 2);
            var item = Assert.Single(await CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"),
                rottenTomatoes: new ComponentTomatoesClient { Result = new(80, 80) }, itemEnrichmentCache: coordinator)
                .GetPremieresAsync(date, date, CancellationToken.None, filters: new CalendarFilters { ShowSeries = false }));
            var identity = new ItemEnrichmentIdentity(item.MediaType, item.TmdbId);
            clock.Advance(TimeSpan.FromHours(1));
            var noop = await coordinator.CreateSessionAsync(CancellationToken.None);
            await noop.PrepareAsync([identity], null, CancellationToken.None);
            await noop.GetAsync(identity, true, (previous, _, _, _) => Task.FromResult(new ItemEnrichmentRefresh(null, previous.Ratings)),
                CancellationToken.None, ratingsOnly: true);
            Assert.True(noop.HasPendingWrites);
            clock.Advance(TimeSpan.FromHours(1));
            var winner = await coordinator.CreateSessionAsync(CancellationToken.None);
            await winner.PrepareAsync([identity], null, CancellationToken.None);
            await winner.GetAsync(identity, true, (previous, _, _, _) => Task.FromResult(new ItemEnrichmentRefresh(null,
                previous.Ratings! with
                {
                    ImdbScore = 8.4, ExternalFetchedUtc = clock.GetUtcNow(),
                    ExternalValues = previous.Ratings!.ExternalValues! with { ImdbScore = 8.4 }
                })), CancellationToken.None, ratingsOnly: true);
            await winner.FlushAsync(CancellationToken.None);
            var acceptedJson = Assert.Single(store.Records.Values).RatingsJson;
            var operation = Guid.NewGuid().ToString("N");
            using (telemetry.UseDiagnosticContext(new() { OperationId = operation }))
                await noop.FlushAsync(CancellationToken.None);
            Assert.Equal(acceptedJson, Assert.Single(store.Records.Values).RatingsJson);
            var events = ReadProviderWriteAuditEvents(telemetry, directory, operation);
            var ratingsWrite = Assert.Single(events, value => value.GetProperty("event").GetString() == "item_cache.write.component"
                && value.GetProperty("details").GetProperty("stage").GetString() == "ratings").GetProperty("details");
            Assert.Equal("reused", ratingsWrite.GetProperty("outcome").GetString());
            Assert.Equal("no_provider_success_submitted", ratingsWrite.GetProperty("reason").GetString());
            Assert.Equal("reused", Assert.Single(events, value => value.GetProperty("event").GetString() == "item_cache.write.item")
                .GetProperty("details").GetProperty("outcome").GetString());
            Assert.DoesNotContain(events, value => value.GetProperty("event").GetString() == "item_cache.result");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RejectedProviderWritesCannotBorrowAuthorityFromCarriedComponents(bool carriedDataset, bool carriedExternal)
    {
        var directory = Path.Combine(Path.GetTempPath(), "PremiereCalendar-rejected-provider-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var diagnostics = new PerformanceDiagnosticStore(new(), directory);
            var telemetry = new PremiereTelemetry(diagnostics);
            var clock = new ItemCacheClock();
            var store = new MemoryItemEnrichmentStore(clock);
            var coordinator = ItemCacheCoordinator(store, clock, telemetry: telemetry);
            var date = new DateOnly(2026, 11, 2);
            var item = Assert.Single(await CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"),
                rottenTomatoes: new ComponentTomatoesClient { Result = new(80, 80) },
                imdbRatingsStore: carriedDataset ? new FakeImdbRatingsStore() : null, itemEnrichmentCache: coordinator)
                .GetPremieresAsync(date, date, CancellationToken.None, filters: new CalendarFilters { ShowSeries = false }));
            var identity = new ItemEnrichmentIdentity(item.MediaType, item.TmdbId);
            var rejected = await coordinator.CreateSessionAsync(CancellationToken.None);
            await rejected.PrepareAsync([identity], null, CancellationToken.None);
            clock.Advance(TimeSpan.FromHours(1));
            var losingClock = clock.GetUtcNow();
            var operation = Guid.NewGuid().ToString("N");
            using (telemetry.UseDiagnosticContext(new() { OperationId = operation }))
                await rejected.GetAsync(identity, true, (previous, _, _, _) => Task.FromResult(new ItemEnrichmentRefresh(null,
                    previous.Ratings! with
                    {
                        ImdbScore = carriedExternal ? previous.Ratings.ImdbScore : 8.4,
                        ExternalFetchedUtc = carriedExternal ? previous.Ratings.ExternalFetchedUtc : losingClock,
                        ExternalValues = carriedExternal ? previous.Ratings.ExternalValues : previous.Ratings.ExternalValues! with { ImdbScore = 8.4 },
                        RottenTomatoesScore = 90, RottenTomatoesAudienceScore = 90, TomatoesFetchedUtc = losingClock,
                        TomatoesValues = new(RottenTomatoesScore: 90, RottenTomatoesAudienceScore: 90)
                    })), CancellationToken.None, ratingsOnly: true);
            clock.Advance(TimeSpan.FromHours(1));
            var winner = await coordinator.CreateSessionAsync(CancellationToken.None);
            await winner.PrepareAsync([identity], null, CancellationToken.None);
            await winner.GetAsync(identity, true, (previous, _, _, _) => Task.FromResult(new ItemEnrichmentRefresh(null,
                previous.Ratings! with
                {
                    ImdbScore = carriedExternal ? previous.Ratings.ImdbScore : 9.6,
                    ExternalFetchedUtc = carriedExternal ? previous.Ratings.ExternalFetchedUtc : clock.GetUtcNow(),
                    ExternalValues = carriedExternal ? previous.Ratings.ExternalValues : previous.Ratings.ExternalValues! with { ImdbScore = 9.6 },
                    RottenTomatoesScore = 98, RottenTomatoesAudienceScore = 98, TomatoesFetchedUtc = clock.GetUtcNow(),
                    TomatoesValues = new(RottenTomatoesScore: 98, RottenTomatoesAudienceScore: 98)
                })), CancellationToken.None, ratingsOnly: true);
            await winner.FlushAsync(CancellationToken.None);
            var acceptedJson = Assert.Single(store.Records.Values).RatingsJson;
            using (telemetry.UseDiagnosticContext(new() { OperationId = operation }))
                await rejected.FlushAsync(CancellationToken.None);
            Assert.Equal(acceptedJson, Assert.Single(store.Records.Values).RatingsJson);

            var events = ReadProviderWriteAuditEvents(telemetry, directory, operation);
            var writes = events.Where(value => value.GetProperty("event").GetString() == "item_cache.write.component")
                .Select(value => value.GetProperty("details")).ToArray();
            Assert.Equal("concurrency_rejected", Assert.Single(writes, value => value.GetProperty("stage").GetString() == "ratings")
                .GetProperty("outcome").GetString());
            Assert.DoesNotContain(writes, value => value.GetProperty("outcome").GetString() is "accepted_projection" or "merged_projection");
            Assert.Equal("reused", Assert.Single(writes, value => value.TryGetProperty("provider", out var provider)
                && provider.GetString() == "imdb_dataset").GetProperty("outcome").GetString());
            Assert.Equal(carriedExternal ? "reused" : "retained_newer", Assert.Single(writes, value => value.TryGetProperty("provider", out var provider)
                && provider.GetString() == "omdb").GetProperty("outcome").GetString());
            Assert.Equal("retained_newer", Assert.Single(writes, value => value.TryGetProperty("provider", out var provider)
                && provider.GetString() == "rottentomatoes").GetProperty("outcome").GetString());
            Assert.DoesNotContain(events, value => value.GetProperty("event").GetString() == "item_cache.result"
                && value.GetProperty("details").GetProperty("outcome").GetString() is "refreshed" or "cleared" or "reused");
            Assert.Equal("concurrency_rejected", Assert.Single(events, value => value.GetProperty("event").GetString() == "item_cache.write.item")
                .GetProperty("details").GetProperty("outcome").GetString());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task AcceptedIndependentRatingsAreLoggedAsPartialWhenMetadataRevisionLoses()
    {
        var directory = Path.Combine(Path.GetTempPath(), "PremiereCalendar-partial-provider-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var diagnostics = new PerformanceDiagnosticStore(new(), directory);
            var telemetry = new PremiereTelemetry(diagnostics);
            var clock = new ItemCacheClock();
            var store = new MemoryItemEnrichmentStore(clock);
            var coordinator = ItemCacheCoordinator(store, clock, telemetry: telemetry);
            var date = new DateOnly(2026, 11, 2);
            var item = Assert.Single(await CreateService(ItemCacheMovie(date), omdb: ItemCacheOmdb("7.1"),
                rottenTomatoes: new ComponentTomatoesClient { Result = new(80, 80) }, itemEnrichmentCache: coordinator)
                .GetPremieresAsync(date, date, CancellationToken.None, filters: new CalendarFilters { ShowSeries = false }));
            clock.Advance(TimeSpan.FromHours(1));
            var identity = new ItemEnrichmentIdentity(item.MediaType, item.TmdbId);
            var operation = Guid.NewGuid().ToString("N");
            using (telemetry.UseDiagnosticContext(new() { OperationId = operation }))
            {
                var session = await coordinator.CreateSessionAsync(CancellationToken.None);
                await session.PrepareAsync([identity], null, CancellationToken.None);
                await session.GetAsync(identity, true, (previous, _, _, _) => Task.FromResult(new ItemEnrichmentRefresh(
                    previous.Metadata! with { RuntimeMinutes = 130 }, previous.Ratings! with
                    {
                        ImdbScore = 8.4, ExternalFetchedUtc = clock.GetUtcNow(),
                        ExternalValues = previous.Ratings!.ExternalValues! with { ImdbScore = 8.4 }
                    })), CancellationToken.None);
                store.BeforeWrite = () =>
                {
                    var pair = Assert.Single(store.Records);
                    var metadata = JsonSerializer.Deserialize<ItemEnrichmentMetadata>(pair.Value.MetadataJson!)!;
                    store.Records[pair.Key] = pair.Value with
                    {
                        MetadataJson = JsonSerializer.Serialize(metadata with { RuntimeMinutes = 144 }),
                        MetadataRevision = pair.Value.MetadataRevision + 1
                    };
                };
                await session.FlushAsync(CancellationToken.None);
            }
            var accepted = Assert.Single(store.Records.Values);
            Assert.Equal(144, JsonSerializer.Deserialize<ItemEnrichmentMetadata>(accepted.MetadataJson!)?.RuntimeMinutes);
            Assert.Equal(8.4, JsonSerializer.Deserialize<ItemEnrichmentRatings>(accepted.RatingsJson!)?.ImdbScore);
            var events = ReadProviderWriteAuditEvents(telemetry, directory, operation);
            Assert.Equal("partial_projection", Assert.Single(events, value => value.GetProperty("event").GetString() == "item_cache.write.item")
                .GetProperty("details").GetProperty("outcome").GetString());
            var ratingsWrite = Assert.Single(events, value => value.GetProperty("event").GetString() == "item_cache.write.component"
                && value.GetProperty("details").GetProperty("stage").GetString() == "ratings").GetProperty("details");
            Assert.Equal("accepted_projection", ratingsWrite.GetProperty("outcome").GetString());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private static JsonElement[] ReadProviderWriteAuditEvents(PremiereTelemetry telemetry, string directory, string operation)
    {
        telemetry.FlushDiagnostics();
        return Directory.GetFiles(Path.Combine(directory, "App_Data", "logs", "performance"), "*.ndjson")
            .SelectMany(file =>
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonSerializer.Deserialize<JsonElement>(line)).ToArray();
            })
            .Where(value => value.TryGetProperty("operationId", out var operationId) && operationId.GetString() == operation).ToArray();
    }
}
