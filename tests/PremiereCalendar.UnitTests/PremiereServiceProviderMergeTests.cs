using System.Text.Json;
using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class PremiereServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverlappingProviderRefreshesPublishBothAcceptedSuccessesAndLogTheMerge(bool tomatoesFirst)
    {
        var directory = Path.Combine(Path.GetTempPath(), "PremiereCalendar-provider-merge-" + Guid.NewGuid().ToString("N"));
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
            var externalClock = clock.GetUtcNow();
            var external = await coordinator.CreateSessionAsync(CancellationToken.None);
            await external.PrepareAsync([identity], null, CancellationToken.None);
            await external.GetAsync(identity, true, (previous, _, _, _) => Task.FromResult(new ItemEnrichmentRefresh(null,
                previous.Ratings! with { ImdbScore = 8.4, ExternalFetchedUtc = externalClock,
                    ExternalValues = previous.Ratings!.ExternalValues! with { ImdbScore = 8.4 } })), CancellationToken.None, ratingsOnly: true);
            clock.Advance(TimeSpan.FromHours(1));
            var tomatoesClock = clock.GetUtcNow();
            var tomatoes = await coordinator.CreateSessionAsync(CancellationToken.None);
            await tomatoes.PrepareAsync([identity], null, CancellationToken.None);
            var provisional = await tomatoes.GetAsync(identity, true, (previous, _, _, _) => Task.FromResult(new ItemEnrichmentRefresh(
                previous.Metadata! with { RuntimeMinutes = 130 }, previous.Ratings! with
                {
                    RottenTomatoesScore = 90, RottenTomatoesAudienceScore = 90, TomatoesFetchedUtc = tomatoesClock,
                    TomatoesValues = new(RottenTomatoesScore: 90, RottenTomatoesAudienceScore: 90)
                })), CancellationToken.None);
            foreach (var session in tomatoesFirst ? new[] { tomatoes, external } : new[] { external, tomatoes })
                await session.FlushAsync(CancellationToken.None);
            var reconciled = await tomatoes.ReconcileAsync([provisional.Apply(item)], true, CancellationToken.None);
            Assert.Empty(reconciled.DirtyMedia);
            var final = Assert.Single(reconciled.Items);
            Assert.Equal(8.4, final.ImdbScore);
            Assert.Equal(90, final.RottenTomatoesScore);
            Assert.Equal(130, final.RuntimeMinutes);
            var ratings = JsonSerializer.Deserialize<ItemEnrichmentRatings>(Assert.Single(store.Records.Values).RatingsJson!);
            Assert.Equal(externalClock, ratings?.ExternalFetchedUtc);
            Assert.Equal(tomatoesClock, ratings?.TomatoesFetchedUtc);
            telemetry.FlushDiagnostics();
            var events = new List<JsonElement>();
            foreach (var file in Directory.GetFiles(Path.Combine(directory, "App_Data", "logs", "performance"), "*.ndjson"))
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                events.AddRange(reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => JsonSerializer.Deserialize<JsonElement>(line)));
            }
            var writes = events.Where(value => value.GetProperty("event").GetString() == "item_cache.write.component")
                .Select(value => value.GetProperty("details")).ToArray();
            Assert.Contains(writes, value => value.GetProperty("outcome").GetString() == "merged_projection");
            Assert.Contains(writes, value => value.GetProperty("outcome").GetString() == "retained_newer"
                && value.GetProperty("acceptedFetchedUtc").GetDateTimeOffset() > value.GetProperty("requestedFetchedUtc").GetDateTimeOffset());
            var mergedResult = Assert.Single(events, value => value.GetProperty("event").GetString() == "item_cache.result"
                && value.GetProperty("details").TryGetProperty("reason", out var reason) && reason.GetString() == "merged_projection")
                .GetProperty("details");
            Assert.Equal("refreshed", mergedResult.GetProperty("outcome").GetString());
            Assert.Equal(tomatoesFirst ? 1 : 2, mergedResult.GetProperty("count").GetInt32());
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
