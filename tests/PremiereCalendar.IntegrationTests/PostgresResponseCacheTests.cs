using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed class PostgresResponseCacheTests
{
    [ConfiguredPostgresFact]
    public async Task ResponseCache_PersistsAcrossInstancesAndCleansOnlyOwnedExpiredRows()
    {
        var connection = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("PREMIERECALENDAR_TEST_POSTGRES"));
        var passwordFile = Environment.GetEnvironmentVariable("PREMIERECALENDAR_TEST_POSTGRES_PASSWORD_FILE");
        if (!string.IsNullOrWhiteSpace(passwordFile)) connection.Password = File.ReadAllText(passwordFile).TrimEnd('\r', '\n');
        connection.Database = "postgres";
        await using var admin = new NpgsqlConnection(connection.ConnectionString);
        await admin.OpenAsync();
        var database = $"premierecalendar_response_test_{Guid.NewGuid():N}";
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            connection.Database = database;
            var options = Microsoft.Extensions.Options.Options.Create(new AppDatabaseOptions { Provider = "PostgreSql", ConnectionString = connection.ConnectionString });
            var environment = new ResponseCacheEnvironment();
            var recovery = new DatabaseRecoveryState();
            await new PostgresDatabaseInitializer(options, environment, recovery, TimeProvider.System,
                NullLogger<PostgresDatabaseInitializer>.Instance, new PremiereTelemetry()).InitializeAsync();
            Assert.True(recovery.Snapshot.IsHealthy);
            var store = new SqliteProviderCacheStateStore(options, environment);
            var first = new ProviderResponseCache(store, NullLogger<ProviderResponseCache>.Instance);
            await first.SaveAsync("tmdb", "movie-detail-fixture", new TmdbDetailsWithExtras { Id = 42, Runtime = 99, OriginalLanguage = "en" }, CancellationToken.None);
            var restarted = new ProviderResponseCache(new SqliteProviderCacheStateStore(options, environment), NullLogger<ProviderResponseCache>.Instance);
            var hit = await restarted.GetAsync<TmdbDetailsWithExtras>("tmdb", "movie-detail-fixture", TimeSpan.FromHours(12), CancellationToken.None);
            Assert.NotNull(hit);
            Assert.Equal((42, 99, "en"), (hit.Value.Id, hit.Value.Runtime, hit.Value.OriginalLanguage));

            var lockedState = new ProviderCacheState("responsecache-tmdb", ProviderCacheScope.Item, "locked-response", DateTimeOffset.UtcNow,
                null, null, null, "{}");
            await store.SaveAsync(lockedState, CancellationToken.None);
            await using (var blocker = new NpgsqlConnection(connection.ConnectionString))
            {
                await blocker.OpenAsync();
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
                        await using var waiting = new NpgsqlCommand("SELECT count(*) FROM pg_stat_activity WHERE datname = @database AND wait_event_type = 'Lock'", admin);
                        waiting.Parameters.AddWithValue("database", database);
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

            var old = DateTimeOffset.UtcNow.AddDays(-10);
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
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private sealed class ConfiguredPostgresFactAttribute : FactAttribute
    {
        public ConfiguredPostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("PREMIERECALENDAR_TEST_POSTGRES")))
                Skip = "Configure PREMIERECALENDAR_TEST_POSTGRES for isolated PostgreSQL response-cache coverage.";
        }
    }

    private sealed class ResponseCacheEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PremiereCalendar";
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
