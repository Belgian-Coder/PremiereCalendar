using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Data.Sqlite;
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
        var database = Microsoft.Extensions.Options.Options.Create(new AppDatabaseOptions { Path = "evidence.db" });
        var recovery = new DatabaseRecoveryState();
        await new SqliteDatabaseInitializer(database, environment, recovery, TimeProvider.System,
            NullLogger<SqliteDatabaseInitializer>.Instance).InitializeAsync();
        InstallWriteCounters(root);
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
            ResetWriteCounters(root);
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
            var writeCounts = ReadWriteCounters(root);
            _evidence.WriteLine($"PERFORMANCE state={cacheState} items={items.Count} first_ms={firstMilliseconds:F2} completion_ms={completionMilliseconds:F2} allocated_bytes={allocatedBytes} jobs={jobs} discover_calls={tmdb.MovieCalls.Count} job_row_writes={writeCounts.JobRowWrites} lease_row_writes={writeCounts.LeaseRowWrites} checkpoint_writes={writeCounts.CheckpointWrites}");
            _evidence.WriteLine("Write counts are SQL row mutations captured by SQLite triggers, not physical disk I/O.");
            Assert.True(writeCounts.JobRowWrites >= 0);
            Assert.True(writeCounts.LeaseRowWrites >= 0);
            Assert.True(writeCounts.CheckpointWrites >= 0);
            Assert.Equal(2_000, items.Count);
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
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task ReleaseWorkersAsync(OccupiedCalendarPipeline pipeline)
    {
        await Task.Delay(300);
        pipeline.Release.TrySetResult();
    }

    private static void InstallWriteCounters(string root)
    {
        using var connection = OpenEvidenceDatabase(root);
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE PerformanceSqlWriteCounters (
                Name TEXT PRIMARY KEY,
                Count INTEGER NOT NULL
            );
            INSERT INTO PerformanceSqlWriteCounters (Name, Count) VALUES
                ('job_row_writes', 0),
                ('lease_row_writes', 0),
                ('checkpoint_writes', 0);

            CREATE TRIGGER PerformanceCountProviderWorkJobsInsert
            AFTER INSERT ON ProviderWorkJobs
            BEGIN
                UPDATE PerformanceSqlWriteCounters SET Count = Count + 1 WHERE Name = 'job_row_writes';
            END;
            CREATE TRIGGER PerformanceCountProviderWorkJobsUpdate
            AFTER UPDATE ON ProviderWorkJobs
            BEGIN
                UPDATE PerformanceSqlWriteCounters SET Count = Count + 1 WHERE Name = 'job_row_writes';
            END;
            CREATE TRIGGER PerformanceCountProviderWorkJobsDelete
            AFTER DELETE ON ProviderWorkJobs
            BEGIN
                UPDATE PerformanceSqlWriteCounters SET Count = Count + 1 WHERE Name = 'job_row_writes';
            END;

            CREATE TRIGGER PerformanceCountProviderWorkLeasesInsert
            AFTER INSERT ON ProviderWorkLeases
            BEGIN
                UPDATE PerformanceSqlWriteCounters SET Count = Count + 1 WHERE Name = 'lease_row_writes';
            END;
            CREATE TRIGGER PerformanceCountProviderWorkLeasesUpdate
            AFTER UPDATE ON ProviderWorkLeases
            BEGIN
                UPDATE PerformanceSqlWriteCounters SET Count = Count + 1 WHERE Name = 'lease_row_writes';
            END;
            CREATE TRIGGER PerformanceCountProviderWorkLeasesDelete
            AFTER DELETE ON ProviderWorkLeases
            BEGIN
                UPDATE PerformanceSqlWriteCounters SET Count = Count + 1 WHERE Name = 'lease_row_writes';
            END;

            CREATE TRIGGER PerformanceCountProviderWorkCheckpoints
            AFTER UPDATE OF CheckpointJson ON ProviderWorkJobs
            WHEN OLD.CheckpointJson IS NOT NEW.CheckpointJson
            BEGIN
                UPDATE PerformanceSqlWriteCounters SET Count = Count + 1 WHERE Name = 'checkpoint_writes';
            END;
            """;
        command.ExecuteNonQuery();
    }

    private static void ResetWriteCounters(string root)
    {
        using var connection = OpenEvidenceDatabase(root);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE PerformanceSqlWriteCounters SET Count = 0";
        command.ExecuteNonQuery();
    }

    private static (long JobRowWrites, long LeaseRowWrites, long CheckpointWrites) ReadWriteCounters(string root)
    {
        using var connection = OpenEvidenceDatabase(root);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Name, Count FROM PerformanceSqlWriteCounters";
        using var reader = command.ExecuteReader();
        var counters = new Dictionary<string, long>(StringComparer.Ordinal);
        while (reader.Read())
        {
            counters[reader.GetString(0)] = reader.GetInt64(1);
        }

        return (
            counters.GetValueOrDefault("job_row_writes"),
            counters.GetValueOrDefault("lease_row_writes"),
            counters.GetValueOrDefault("checkpoint_writes"));
    }

    private static SqliteConnection OpenEvidenceDatabase(string root)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(root, "evidence.db"),
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
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
