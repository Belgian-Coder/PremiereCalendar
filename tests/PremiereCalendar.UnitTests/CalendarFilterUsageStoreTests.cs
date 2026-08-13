using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class CalendarFilterUsageStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"premiere-usage-store-{Guid.NewGuid():N}");

    [Fact]
    public async Task GetTopProfilesAsync_OrdersByUseCountAndDoesNotPinStoredWeek()
    {
        var now = new DateTimeOffset(2026, 5, 8, 10, 0, 0, TimeSpan.Zero);
        var store = CreateStore();
        var movieFilters = new CalendarFilters
        {
            WeekStart = new DateOnly(2026, 3, 2),
            ShowSeries = false,
            ShowMovies = true,
            MovieFilters =
            {
                SelectedSources = ["provider:8"],
                WatchRegion = "be"
            }
        };
        var seriesFilters = new CalendarFilters
        {
            WeekStart = new DateOnly(2026, 4, 6),
            ShowSeries = true,
            ShowMovies = false,
            SeriesFilters =
            {
                OriginalLanguages = ["en"]
            }
        };

        await store.RecordUseAsync(CalendarPageMode.Series, seriesFilters, itemCount: 4, now.AddMinutes(-1), CancellationToken.None);
        await store.RecordUseAsync(CalendarPageMode.Movies, movieFilters, itemCount: 7, now.AddMinutes(-3), CancellationToken.None);
        await store.RecordUseAsync(CalendarPageMode.Movies, movieFilters, itemCount: 8, now.AddMinutes(-2), CancellationToken.None);

        var profiles = await store.GetTopProfilesAsync(2, now, TimeSpan.FromDays(60), CancellationToken.None);

        Assert.Equal(2, profiles.Count);
        Assert.Equal(CalendarPageMode.Movies, profiles[0].PageMode);
        Assert.Equal(2, profiles[0].UseCount);
        Assert.Equal(8, profiles[0].LastItemCount);
        Assert.Equal(DateOnly.MinValue, profiles[0].Filters.WeekStart);
        Assert.Null(profiles[0].Filters.PriorityDate);
    }

    [Fact]
    public async Task CleanupAsync_RemovesOldProfilesExceptRetainedKeys()
    {
        var now = new DateTimeOffset(2026, 5, 8, 10, 0, 0, TimeSpan.Zero);
        var store = CreateStore();
        var oldFilters = new CalendarFilters { ShowSeries = true, ShowMovies = false };
        var retainedFilters = new CalendarFilters
        {
            ShowSeries = false,
            ShowMovies = true,
            MovieFilters = { SelectedSources = ["provider:9"] }
        };
        var recentFilters = new CalendarFilters
        {
            ShowSeries = true,
            ShowMovies = true,
            SeriesFilters = { OriginalLanguages = ["nl"] }
        };

        await store.RecordUseAsync(CalendarPageMode.Series, oldFilters, itemCount: 1, now.AddDays(-80), CancellationToken.None);
        await store.RecordUseAsync(CalendarPageMode.Movies, retainedFilters, itemCount: 2, now.AddDays(-80), CancellationToken.None);
        await store.RecordUseAsync(CalendarPageMode.All, recentFilters, itemCount: 3, now.AddDays(-1), CancellationToken.None);

        var beforeCleanup = await store.GetTopProfilesAsync(3, now, TimeSpan.FromDays(120), CancellationToken.None);
        var retainedKey = Assert.Single(beforeCleanup, profile => profile.PageMode == CalendarPageMode.Movies).ProfileKey;

        var removed = await store.CleanupAsync(now.AddDays(-60), new HashSet<string>(StringComparer.Ordinal) { retainedKey }, CancellationToken.None);
        var afterCleanup = await store.GetTopProfilesAsync(3, now, TimeSpan.FromDays(120), CancellationToken.None);

        Assert.Equal(1, removed);
        Assert.DoesNotContain(afterCleanup, profile => profile.PageMode == CalendarPageMode.Series);
        Assert.Contains(afterCleanup, profile => profile.ProfileKey == retainedKey);
        Assert.Contains(afterCleanup, profile => profile.PageMode == CalendarPageMode.All);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private SqliteCalendarFilterUsageStore CreateStore()
    {
        TestSqliteDatabase.Initialize(_root, "data/usage.db");
        return new SqliteCalendarFilterUsageStore(
            Microsoft.Extensions.Options.Options.Create(new AppDatabaseOptions { Path = "data/usage.db" }),
            new FakeWebHostEnvironment(_root));
    }

    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public FakeWebHostEnvironment(string root)
        {
            ContentRootPath = root;
            WebRootPath = root;
            ContentRootFileProvider = new PhysicalFileProvider(root);
            WebRootFileProvider = new PhysicalFileProvider(root);
        }

        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "PremiereCalendar.Tests";
        public string WebRootPath { get; set; }
        public IFileProvider WebRootFileProvider { get; set; }
        public string ContentRootPath { get; set; }
        public IFileProvider ContentRootFileProvider { get; set; }
    }
}
