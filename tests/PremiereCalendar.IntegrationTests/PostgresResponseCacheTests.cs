using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed class PostgresResponseCacheTests : IDisposable
{
    private readonly PostgresTestDatabase _database = PostgresTestDatabase.Create();

    public void Dispose() => _database.Dispose();

    [Fact]
    public async Task ResponseCache_PersistsAcrossInstancesAndCleansOnlyOwnedExpiredRows()
    {
        var store = new ProviderCacheStateStore(_database.Options);
        var first = new ProviderResponseCache(store, NullLogger<ProviderResponseCache>.Instance);
        await first.SaveAsync("tmdb", "movie-detail-fixture", new TmdbDetailsWithExtras { Id = 42, Runtime = 99, OriginalLanguage = "en" }, CancellationToken.None);
        var restarted = new ProviderResponseCache(new ProviderCacheStateStore(_database.Options), NullLogger<ProviderResponseCache>.Instance);
        var hit = await restarted.GetAsync<TmdbDetailsWithExtras>("tmdb", "movie-detail-fixture", TimeSpan.FromHours(12), CancellationToken.None);
        Assert.NotNull(hit);
        Assert.Equal((42, 99, "en"), (hit.Value.Id, hit.Value.Runtime, hit.Value.OriginalLanguage));

        var lockedState = new ProviderCacheState("responsecache-tmdb", ProviderCacheScope.Item, "locked-response", DateTimeOffset.UtcNow,
            null, null, null, "{}");
        await store.SaveAsync(lockedState, CancellationToken.None);
        await using var observer = await _database.OpenAsync();
        await using (var blocker = await _database.OpenAsync())
        {
            await using var transaction = await blocker.BeginTransactionAsync();
            await using (var hold = new NpgsqlCommand("SELECT 1 FROM ProviderCacheState WHERE Provider = 'responsecache-tmdb' AND CacheKey = 'locked-response' FOR UPDATE", blocker, transaction))
                await hold.ExecuteNonQueryAsync();
            using var waitBudget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var blockedWrite = store.SaveAsync(lockedState with { MetadataJson = "{\"new\":true}" }, waitBudget.Token);
            try
            {
                // Observe the database lock itself, rather than guessing whether
                // the writer has reached SQL after an arbitrary sleep.
                while (true)
                {
                    await using var waiting = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'", observer);
                    if (Convert.ToInt32(await waiting.ExecuteScalarAsync(waitBudget.Token)) > 0) break;
                    await Task.Delay(10, waitBudget.Token);
                }
                Assert.False(blockedWrite.IsCompleted);
                var independent = await store.GetAsync("tmdb", ProviderCacheScope.Item, "independent-metadata", CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(1));
                Assert.Null(independent);
                Assert.Null(await store.GetAsync("responsecache-tmdb", ProviderCacheScope.Item, "independent-response", CancellationToken.None)
                    .WaitAsync(TimeSpan.FromSeconds(1)));
                Assert.NotNull(await restarted.GetAsync<TmdbDetailsWithExtras>("tmdb", "movie-detail-fixture", TimeSpan.FromHours(12), CancellationToken.None));
            }
            finally
            {
                await transaction.RollbackAsync();
                await blockedWrite;
            }
        }

        var old = DateTimeOffset.UtcNow.AddMonths(-3).AddTicks(-1);
        foreach (var state in new[]
        {
            new ProviderCacheState("responsecache-tmdb", ProviderCacheScope.Item, "expired-response", old, null, null, null, "{}"),
            new ProviderCacheState("responsecache-rottentomatoes", ProviderCacheScope.Item, "expired-response", old, null, null, null, "{}"),
            new ProviderCacheState("responsecache-tmdb", ProviderCacheScope.Week, "preserved-week", old, null, null, null, "{}"),
            new ProviderCacheState("tmdb", ProviderCacheScope.Item, "preserved-legacy", old, null, null, null, "{}")
        }) await store.SaveAsync(state, CancellationToken.None);
        Assert.Equal(2, await restarted.CleanupAsync(CancellationToken.None));
        Assert.Null(await store.GetAsync("responsecache-tmdb", ProviderCacheScope.Item, "expired-response", CancellationToken.None));
        Assert.Null(await store.GetAsync("responsecache-rottentomatoes", ProviderCacheScope.Item, "expired-response", CancellationToken.None));
        Assert.NotNull(await store.GetAsync("responsecache-tmdb", ProviderCacheScope.Week, "preserved-week", CancellationToken.None));
        Assert.NotNull(await store.GetAsync("tmdb", ProviderCacheScope.Item, "preserved-legacy", CancellationToken.None));
        Assert.Single(await store.GetRecentAsync(20, CancellationToken.None));
        Assert.NotNull(await restarted.GetAsync<TmdbDetailsWithExtras>("tmdb", "movie-detail-fixture", TimeSpan.FromHours(12), CancellationToken.None));
    }
}
