using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;
using Xunit.Abstractions;

namespace PremiereCalendar.UnitTests;

public sealed partial class PremiereServiceTests
{
    private readonly ITestOutputHelper _evidence;
    public PremiereServiceTests(ITestOutputHelper evidence) => _evidence = evidence;

    [Theory]
    [InlineData("fresh")]
    [InlineData("stale")]
    [InlineData("cold")]
    public async Task PerformanceEvidence_TwoThousandItemsWithOccupiedScheduler(string cacheState)
    {
        var root = Path.Combine(Path.GetTempPath(), "premiere-load-evidence", Guid.NewGuid().ToString("N"));
        var environment = new PerformanceEnvironment(root);
        await using var evidenceDatabase = await PostgresTestDatabase.CreateAsync(initializeSchema: false);
        var database = evidenceDatabase.Options;
        // The scheduler's hosted service waits for this state to become healthy before it claims work.
        var recovery = new DatabaseRecoveryState();
        await evidenceDatabase.InitializeSchemaAsync(recovery);
        await InstallWriteCountersAsync(evidenceDatabase);
        var options = Microsoft.Extensions.Options.Options.Create(new ProviderSchedulerOptions { LeaseSeconds = 30 });
        var store = new ProviderWorkStore(database, environment);
        var scheduler = new ProviderWorkScheduler(store, TimeProvider.System, options);
        var start = new DateOnly(2026, 10, 26);
        var cached = Enumerable.Range(1, 2_000).Select(id => new PremiereItem
        {
            CanonicalId = $"movie:{id}", MediaType = PremiereMediaType.Movie, Type = PremiereItemType.MovieFirstRelease, TmdbId = id,
            Title = $"Movie {id}", PremiereDate = start.AddDays(id % 7), TmdbScore = 7
        }).ToArray();
        var tmdb = new FakeTmdbClient
        {
            FilterMovieItemsByDate = true,
            MovieItems = cached.Select(item => new TmdbMovieDiscoverItem
                { Id = item.TmdbId, Title = item.Title, ReleaseDate = item.PremiereDate.ToString("yyyy-MM-dd"), VoteAverage = 7 }).ToArray()
        };
        var cache = new FakeCalendarCache
        {
            Items = cacheState == "fresh" ? cached : null,
            ExpiredItems = cacheState == "stale" ? cached : null
        };
        var service = CreateService(tmdb, calendarCache: cache, workScheduler: scheduler);
        var pipeline = new OccupiedCalendarPipeline(service, start);
        await using var services = new ServiceCollection().AddSingleton<IPremiereLoadPipeline>(pipeline).BuildServiceProvider();
        using var worker = new ProviderWorkSchedulerHostedService(scheduler, services.GetRequiredService<IServiceScopeFactory>(),
            recovery, options, NullLogger<ProviderWorkSchedulerHostedService>.Instance);
        Task release = Task.CompletedTask;
        try
        {
            for (var i = 1; i <= 2; i++)
            {
                var payload = new CalendarProviderWorkPayload(start.AddDays(-i * 7), start.AddDays(-i * 7 + 6), false, null);
                await scheduler.EnqueueAsync(new ProviderWorkRequest(ProviderWorkKind.CalendarForeground, $"occupied-{i}",
                    ProviderWorkPriority.Foreground, JsonSerializer.Serialize(payload)));
            }
            await worker.StartAsync(CancellationToken.None);
            await pipeline.Occupied.Task.WaitAsync(TimeSpan.FromSeconds(10));
            release = ReleaseWorkersAsync(pipeline);
            await ResetWriteCountersAsync(evidenceDatabase);
            var allocated = GC.GetTotalAllocatedBytes();
            var timer = Stopwatch.StartNew();
            // Leave enough room to measure the archived baseline as well as the candidate.
            using var loadBudget = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            double? firstMilliseconds = null;
            IReadOnlyList<PremiereItem> items = [];
            await foreach (var update in service.StreamPremieresAsync(start, start.AddDays(6),
                filters: new CalendarFilters { ShowSeries = false }).WithCancellation(loadBudget.Token))
            {
                firstMilliseconds ??= timer.Elapsed.TotalMilliseconds;
                items = update.Items;
            }
            var completionMilliseconds = timer.Elapsed.TotalMilliseconds;
            var allocatedBytes = GC.GetTotalAllocatedBytes() - allocated;
            var counts = await store.GetStateCountsAsync(CancellationToken.None);
            var jobs = counts.Values.Sum();
            var writeCounts = await ReadWriteCountersAsync(evidenceDatabase);
            _evidence.WriteLine($"PERFORMANCE state={cacheState} items={items.Count} first_ms={firstMilliseconds:F2} completion_ms={completionMilliseconds:F2} allocated_bytes={allocatedBytes} jobs={jobs} discover_calls={tmdb.MovieCalls.Count} job_row_writes={writeCounts.JobRowWrites} lease_row_writes={writeCounts.LeaseRowWrites} checkpoint_writes={writeCounts.CheckpointWrites}");
            _evidence.WriteLine("Write counts are SQL row mutations captured by PostgreSQL triggers, not physical disk I/O.");
            Assert.True(writeCounts.JobRowWrites >= 0);
            Assert.True(writeCounts.LeaseRowWrites >= 0);
            Assert.True(writeCounts.CheckpointWrites >= 0);
            Assert.Equal(2_000, items.Count);
            var expectedProjection = cached.OrderBy(item => item.CanonicalId, StringComparer.Ordinal)
                .Select(item => new { item.CanonicalId, item.PremiereDate, item.Title, item.TmdbScore }).ToArray();
            var actualProjection = items.OrderBy(item => item.CanonicalId, StringComparer.Ordinal)
                .Select(item => new { item.CanonicalId, item.PremiereDate, item.Title, item.TmdbScore }).ToArray();
            Assert.Equal(JsonSerializer.Serialize(expectedProjection), JsonSerializer.Serialize(actualProjection));
            var projectionHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(actualProjection))));
            _evidence.WriteLine($"PROJECTION sha256={projectionHash}");
            if (cacheState == "fresh")
            {
                Assert.Equal(2, jobs); // Only the two deliberately occupied jobs, no cache-hit job.
                Assert.Empty(tmdb.MovieCalls);
                Assert.True(firstMilliseconds < 1_000, $"Fresh cached first result took {firstMilliseconds:F2} ms.");
            }
        }
        finally
        {
            pipeline.Release.TrySetResult();
            await release;
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await worker.StopAsync(stop.Token);
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task ReleaseWorkersAsync(OccupiedCalendarPipeline pipeline)
    {
        await Task.Delay(300);
        pipeline.Release.TrySetResult();
    }

    // Counters are an append-only log (one row per mutated row) rather than a counter row that every trigger
    // updates: concurrent job transactions would otherwise serialize on, and could deadlock over, shared counter rows.
    private static async Task InstallWriteCountersAsync(PostgresTestDatabase database)
    {
        await using var connection = await database.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE UNLOGGED TABLE PerformanceSqlWriteLog (
                Name TEXT NOT NULL
            );

            CREATE FUNCTION PerformanceCountRowWrite() RETURNS trigger AS $$
            BEGIN
                INSERT INTO PerformanceSqlWriteLog (Name) VALUES (TG_ARGV[0]);
                RETURN NULL;
            END;
            $$ LANGUAGE plpgsql;

            CREATE TRIGGER PerformanceCountProviderWorkJobs
            AFTER INSERT OR UPDATE OR DELETE ON ProviderWorkJobs
            FOR EACH ROW EXECUTE FUNCTION PerformanceCountRowWrite('job_row_writes');

            CREATE TRIGGER PerformanceCountProviderWorkLeases
            AFTER INSERT OR UPDATE OR DELETE ON ProviderWorkLeases
            FOR EACH ROW EXECUTE FUNCTION PerformanceCountRowWrite('lease_row_writes');

            CREATE TRIGGER PerformanceCountProviderWorkCheckpoints
            AFTER UPDATE OF CheckpointJson ON ProviderWorkJobs
            FOR EACH ROW WHEN (OLD.CheckpointJson IS DISTINCT FROM NEW.CheckpointJson)
            EXECUTE FUNCTION PerformanceCountRowWrite('checkpoint_writes');
            """;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task ResetWriteCountersAsync(PostgresTestDatabase database)
    {
        await using var connection = await database.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM PerformanceSqlWriteLog";
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<(long JobRowWrites, long LeaseRowWrites, long CheckpointWrites)> ReadWriteCountersAsync(
        PostgresTestDatabase database)
    {
        await using var connection = await database.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT Name, COUNT(*) FROM PerformanceSqlWriteLog GROUP BY Name";
        await using var reader = await command.ExecuteReaderAsync();
        var counters = new Dictionary<string, long>(StringComparer.Ordinal);
        while (await reader.ReadAsync())
        {
            counters[reader.GetString(0)] = reader.GetInt64(1);
        }

        return (
            counters.GetValueOrDefault("job_row_writes"),
            counters.GetValueOrDefault("lease_row_writes"),
            counters.GetValueOrDefault("checkpoint_writes"));
    }

    private sealed class OccupiedCalendarPipeline(PremiereService service, DateOnly visibleStart) : IPremiereLoadPipeline
    {
        private int _active;
        public TaskCompletionSource Occupied { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<PremiereLoadProgress> StreamCoreAsync(DateOnly start, DateOnly end, bool forceRefresh,
            CalendarFilters? filters, ProviderWorkResumeState? resumeState,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (start < visibleStart)
            {
                if (Interlocked.Increment(ref _active) == 2) Occupied.TrySetResult();
                await Release.Task.WaitAsync(cancellationToken);
                yield return new PremiereLoadProgress("Occupied fixture", 0, 0, [], IsFinal: true);
                yield break;
            }
            await foreach (var update in service.StreamCoreAsync(start, end, forceRefresh, filters, resumeState, cancellationToken))
                yield return update;
        }
    }

    private sealed class PerformanceEnvironment(string root) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "PremiereCalendar.Tests";
        public string WebRootPath { get; set; } = root;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = root;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
