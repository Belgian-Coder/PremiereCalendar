using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed partial class PostgresPersistenceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "premierecalendar-postgres-tests", Guid.NewGuid().ToString("N"));
    private readonly TestEnvironment _environment;
    private PostgresTestDatabase? _databaseInstance;

    public PostgresPersistenceTests()
    {
        Directory.CreateDirectory(_root);
        _environment = new TestEnvironment(_root);
    }

    /// <summary>One migrated database per test; tests that exercise initialization itself create their own.</summary>
    private PostgresTestDatabase _database => _databaseInstance ??= PostgresTestDatabase.Create();

    public void Dispose()
    {
        _databaseInstance?.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task PostgreSql_InitializesSchemaAndStores()
    {
        using var database = await PostgresTestDatabase.CreateAsync(initializeSchema: false);
        var recovery = new DatabaseRecoveryState();
        var initializer = new PostgresDatabaseInitializer(
            database.Options,
            recovery,
            TimeProvider.System,
            NullLogger<PostgresDatabaseInitializer>.Instance,
            new PremiereTelemetry());

        await initializer.InitializeAsync();
        Assert.True(recovery.Snapshot.IsHealthy, recovery.Snapshot.Message);
        Assert.Equal(DatabaseSchema.CurrentVersion, recovery.Snapshot.CurrentVersion);

        var store = new AppStateStore(database.Options);
        await store.SetValueAsync("integration.roundtrip", "ok", CancellationToken.None);
        Assert.Equal("ok", await store.GetValueAsync("integration.roundtrip", CancellationToken.None));
        await store.DeleteValueAsync("integration.roundtrip", CancellationToken.None);
        Assert.Null(await store.GetValueAsync("integration.roundtrip", CancellationToken.None));

        var importedAt = DateTimeOffset.Parse("2026-08-24T10:00:00Z");
        var ratings = new ImdbRatingsStore(database.Options);
        await ratings.ReplaceAllAsync(
            [new ImdbRatingRecord("tt0000001", 7.25, 1234, importedAt)],
            importedAt,
            CancellationToken.None);
        var storedRating = await ratings.GetByImdbIdAsync("tt0000001", CancellationToken.None);
        Assert.NotNull(storedRating);
        Assert.Equal(7.25, storedRating.AverageRating, precision: 2);

        var enrichmentKey = new ItemEnrichmentIdentity(PremiereMediaType.Series, 123);
        var enrichments = new ItemEnrichmentStore(database.Options);
        await enrichments.ReadManyAsync("import-config", [enrichmentKey], true, CancellationToken.None);
        await enrichments.WriteManyAsync("import-config", [new(enrichmentKey, 0, "{\"title\":\"preserved\"}", importedAt,
            "{\"score\":8}", importedAt)], CancellationToken.None);
        await using (var connection = await database.OpenAsync())
        {
            await using var generation = connection.CreateCommand();
            generation.CommandText = """
                UPDATE ItemEnrichments SET Generation = 4294967296, MetadataRevision = 8589934592,
                    TvmazeFenceUtc = '2026-08-24T10:00:00.0000000+00:00', TmdbObservedUtc = '2026-08-24T10:00:00.0000000+00:00'
                """;
            await generation.ExecuteNonQueryAsync();
        }

        var enrichment = Assert.Single(await new ItemEnrichmentStore(database.Options)
            .ReadManyAsync("import-config", [enrichmentKey], false, CancellationToken.None)).Value;
        Assert.Equal(4294967296L, enrichment.Generation);
        Assert.Equal(8589934592L, enrichment.MetadataRevision);
        Assert.Equal(importedAt, enrichment.TmdbObservedUtc);
        Assert.Equal("{\"title\":\"preserved\"}", enrichment.MetadataJson);
        Assert.Equal(importedAt, enrichment.MetadataFetchedUtc);
        Assert.Equal(importedAt, enrichment.RatingsFetchedUtc);

        var verification = await initializer.VerifyAsync();
        Assert.True(verification.IsHealthy, verification.Message);
    }

    [Fact]
    public async Task PostgreSql_ConcurrentEnqueueAndLeaseMutationsKeepOneActiveJobPerKey()
    {
        var store = new ProviderWorkStore(_database.Options, _environment);
        var now = DateTimeOffset.UtcNow;
        var regular = new ProviderWorkRequest(ProviderWorkKind.CalendarForeground, "week:regular", ProviderWorkPriority.Foreground, "{\"ForceRefresh\":false}");
        var force = regular with { DedupeKey = "week:force", PayloadJson = "{\"ForceRefresh\":true}" };
        var requests = Enumerable.Range(0, 32).Select(index => index % 2 == 0 ? regular : force).ToArray();
        var handles = await Task.WhenAll(requests.Select(request => store.EnqueueAsync(request, now, CancellationToken.None)));
        Assert.Equal(2, handles.Count(handle => handle.Created));
        Assert.Single(handles.Where((_, index) => index % 2 == 0).Select(handle => handle.JobId).Distinct());
        Assert.Single(handles.Where((_, index) => index % 2 != 0).Select(handle => handle.JobId).Distinct());
        Assert.NotEqual(handles[0].JobId, handles[1].JobId);

        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(index =>
            store.ClaimNextAsync(false, $"worker-{index}", now, TimeSpan.FromSeconds(30), CancellationToken.None)));
        var claimed = claims.Where(job => job is not null).Select(job => job!).ToArray();
        Assert.Equal(2, claimed.Length);
        Assert.Equal(2, claimed.Select(job => job.JobId).Distinct().Count());
        Assert.Equal(2, claimed.Select(job => job.LeaseOwner).Distinct().Count());

        // Startup recovery leaves a still-live old claim alone, then the next
        // periodic recovery makes it reclaimable after its persisted expiry.
        await store.RecoverExpiredLeasesAsync(now.AddSeconds(10), CancellationToken.None);
        Assert.Null(await store.ClaimNextAsync(false, "restart", now.AddSeconds(10), TimeSpan.FromSeconds(30), CancellationToken.None));
        await store.RecoverExpiredLeasesAsync(now.AddSeconds(31), CancellationToken.None);
        var replacement = (await store.ClaimNextAsync(false, "restart", now.AddSeconds(31), TimeSpan.FromSeconds(30), CancellationToken.None))!;
        var stale = claimed.Single(job => job.JobId == replacement.JobId);
        var updatedAt = now.AddSeconds(32);
        Assert.False(await store.UpdateProgressAsync(stale.JobId, stale.LeaseOwner!, "stale", updatedAt, CancellationToken.None));
        Assert.False(await store.RenewLeaseAsync(stale.JobId, stale.LeaseOwner!, updatedAt, now.AddHours(1), CancellationToken.None));
        Assert.False(await store.RequeueAsync(stale.JobId, stale.LeaseOwner!, updatedAt, updatedAt, true, "stale", CancellationToken.None));
        Assert.False(await store.FailAsync(stale.JobId, stale.LeaseOwner!, "stale", updatedAt, CancellationToken.None));
        Assert.False(await store.CompleteAsync(stale.JobId, stale.LeaseOwner!, updatedAt, CancellationToken.None));
        Assert.True(await store.RenewLeaseAsync(replacement.JobId, replacement.LeaseOwner!, updatedAt, now.AddSeconds(62), CancellationToken.None));
        Assert.True(await store.UpdateProgressAsync(replacement.JobId, replacement.LeaseOwner!, "current", updatedAt, CancellationToken.None));

        await using (var connection = await _database.OpenAsync())
        {
            await using var invariant = connection.CreateCommand();
            invariant.CommandText = """
                SELECT COUNT(*) FROM ProviderWorkJobs job
                LEFT JOIN ProviderWorkLeases lease ON lease.JobId = job.JobId
                WHERE (job.State = 'Running' AND (lease.JobId IS NULL OR job.LeaseOwner <> lease.LeaseOwner OR job.LeaseExpiresUtc <> lease.LeaseExpiresUtc))
                    OR (job.State <> 'Running' AND lease.JobId IS NOT NULL)
                """;
            Assert.Equal(0L, await invariant.ExecuteScalarAsync());
        }
        Assert.True(await store.CompleteAsync(replacement.JobId, replacement.LeaseOwner!, updatedAt, CancellationToken.None));
        Assert.Equal("current", (await store.GetAsync(replacement.JobId, CancellationToken.None))!.CheckpointJson);
    }

    [Fact]
    public async Task PostgreSql_CachePublicationLocksOwnershipUntilFileWritesFinish()
    {
        var store = new ProviderWorkStore(_database.Options, _environment);
        var now = DateTimeOffset.UtcNow;
        var clock = new PublicationTimeProvider(now.AddSeconds(5));
        var handle = await store.EnqueueAsync(new(ProviderWorkKind.CalendarForeground, "cache-publication",
            ProviderWorkPriority.Foreground, "{}"), now, CancellationToken.None);
        var owner = (await store.ClaimNextAsync(false, "original", now, TimeSpan.FromSeconds(30), CancellationToken.None))!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cachePath = Path.Combine(_environment.ContentRootPath, "cache.json");
        var publication = store.WithOwnedLeaseAsync(handle.JobId, owner.LeaseOwner!, clock, TimeSpan.FromSeconds(30), async token =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            await File.WriteAllTextAsync(cachePath, "original", token);
        }, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        clock.UtcNow = now.AddSeconds(36);
        var recovery = store.RecoverExpiredLeasesAsync(clock.GetUtcNow(), CancellationToken.None);
        try
        {
            await using var observer = await _database.OpenAsync();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                await using var blocked = observer.CreateCommand();
                blocked.CommandText = "SELECT COUNT(*) FROM pg_stat_activity WHERE datname = current_database() AND cardinality(pg_blocking_pids(pid)) > 0";
                if (Convert.ToInt64(await blocked.ExecuteScalarAsync(deadline.Token)) > 0) break;
                await Task.Delay(10, deadline.Token);
            }
            Assert.False(recovery.IsCompleted);
            Assert.False(File.Exists(cachePath));
        }
        finally { release.TrySetResult(); }
        Assert.True(await publication.WaitAsync(TimeSpan.FromSeconds(5)));
        await recovery.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("original", await File.ReadAllTextAsync(cachePath));
        var replacement = (await store.ClaimNextAsync(false, "replacement", clock.GetUtcNow(), TimeSpan.FromSeconds(30), CancellationToken.None))!;
        Assert.NotEqual(owner.LeaseOwner, replacement.LeaseOwner);
        var staleCalled = false;
        Assert.False(await store.WithOwnedLeaseAsync(handle.JobId, owner.LeaseOwner!, clock, TimeSpan.FromSeconds(30), _ =>
        {
            staleCalled = true;
            return Task.CompletedTask;
        }, CancellationToken.None));
        Assert.False(staleCalled);

        clock.UtcNow = now.AddSeconds(40);
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.WithOwnedLeaseAsync(handle.JobId,
            replacement.LeaseOwner!, clock, TimeSpan.FromSeconds(30), async token =>
            {
                await cancellation.CancelAsync();
                token.ThrowIfCancellationRequested();
            }, cancellation.Token));
        await using (var connection = await _database.OpenAsync())
        {
            await using var lease = connection.CreateCommand();
            lease.CommandText = "SELECT job.LeaseExpiresUtc = lease.LeaseExpiresUtc AND job.LeaseExpiresUtc = @expires FROM ProviderWorkJobs job JOIN ProviderWorkLeases lease ON lease.JobId = job.JobId WHERE job.JobId = @id";
            lease.Parameters.AddWithValue("expires", now.AddSeconds(66).ToString("O", System.Globalization.CultureInfo.InvariantCulture));
            lease.Parameters.AddWithValue("id", handle.JobId);
            Assert.Equal(true, await lease.ExecuteScalarAsync());
        }
    }

    private sealed class PublicationTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => UtcNow;
    }

    [Fact]
    public async Task PostgreSql_DeadPublisherSessionKeepsFileGateUntilItsCallbackReturns()
    {
        var store = new ProviderWorkStore(_database.Options, _environment);
        var now = DateTimeOffset.UtcNow;
        var clock = new PublicationTimeProvider(now.AddSeconds(5));
        var handle = await store.EnqueueAsync(new(ProviderWorkKind.CalendarForeground, "dead-publisher",
            ProviderWorkPriority.Foreground, "{}"), now, CancellationToken.None);
        var original = (await store.ClaimNextAsync(false, "original", now, TimeSpan.FromSeconds(30), CancellationToken.None))!;
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cachePath = Path.Combine(_environment.ContentRootPath, "session-loss-cache.json");
        var first = store.WithOwnedLeaseAsync(handle.JobId, original.LeaseOwner!, clock, TimeSpan.FromSeconds(30), async token =>
        {
            firstEntered.TrySetResult();
            await releaseFirst.Task.WaitAsync(token);
            await File.WriteAllTextAsync(cachePath, "original", token);
        }, CancellationToken.None);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<bool>? second = null;
        Exception? firstFailure = null;
        var secondPublished = false;
        try
        {
            await using var observer = await _database.OpenAsync();
            await using (var terminate = observer.CreateCommand())
            {
                // The randomized test database contains exactly one idle
                // transaction here. Never target sessions outside this DB.
                terminate.CommandText = """
                    SELECT pg_terminate_backend(pid) FROM pg_stat_activity
                    WHERE datname = current_database() AND state = 'idle in transaction'
                        AND query LIKE '%UPDATE ProviderWorkLeases%'
                    """;
                Assert.Equal(true, await terminate.ExecuteScalarAsync());
            }
            clock.UtcNow = now.AddSeconds(31);
            await store.RecoverExpiredLeasesAsync(clock.GetUtcNow(), CancellationToken.None);
            var replacement = (await store.ClaimNextAsync(false, "replacement", clock.GetUtcNow(), TimeSpan.FromSeconds(30), CancellationToken.None))!;
            Assert.NotEqual(original.LeaseOwner, replacement.LeaseOwner);
            second = store.WithOwnedLeaseAsync(handle.JobId, replacement.LeaseOwner!, clock, TimeSpan.FromSeconds(30), async token =>
            {
                secondEntered.TrySetResult();
                await File.WriteAllTextAsync(cachePath, "replacement", token);
            }, CancellationToken.None);
            Assert.NotSame(secondEntered.Task, await Task.WhenAny(secondEntered.Task, Task.Delay(150)));
            Assert.False(File.Exists(cachePath));
        }
        finally
        {
            releaseFirst.TrySetResult();
            firstFailure = await Record.ExceptionAsync(async () => await first);
            if (second is not null) secondPublished = await second.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.NotNull(firstFailure);
        Assert.NotNull(second);
        Assert.True(secondPublished);
        Assert.Equal("replacement", await File.ReadAllTextAsync(cachePath));
    }

    private sealed class TestEnvironment(string root) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PremiereCalendar.IntegrationTests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = root;
        public string EnvironmentName { get; set; } = "Testing";
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new PhysicalFileProvider(root);
    }
}
