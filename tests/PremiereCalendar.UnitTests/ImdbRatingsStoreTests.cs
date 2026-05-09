using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class ImdbRatingsStoreTests
{
    [Fact]
    public async Task SqliteImdbRatingsStore_RoundTripsRatingsByImdbId()
    {
        var root = CreateRoot();
        try
        {
            var store = CreateStore(root);
            var importedAt = DateTimeOffset.Parse("2026-05-09T10:00:00Z");

            await store.ReplaceAllAsync(
                [new ImdbRatingRecord("tt0000001", 7.6, 12345, importedAt)],
                importedAt,
                CancellationToken.None);

            var rating = await store.GetByImdbIdAsync("tt0000001", CancellationToken.None);
            var missing = await store.GetByImdbIdAsync("tt-missing", CancellationToken.None);
            var state = await store.GetStateAsync(CancellationToken.None);

            Assert.NotNull(rating);
            Assert.Equal(7.6, rating.AverageRating);
            Assert.Equal(12345, rating.VoteCount);
            Assert.Null(missing);
            Assert.Equal(importedAt, state.LastImportedUtc);
            Assert.Equal(1, state.RatingCount);
            Assert.Null(state.LastError);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task SqliteImdbRatingsStore_ReplaceAllRemovesRatingsMissingFromNewDataset()
    {
        var root = CreateRoot();
        try
        {
            var store = CreateStore(root);
            var firstImport = DateTimeOffset.Parse("2026-05-08T10:00:00Z");
            var secondImport = DateTimeOffset.Parse("2026-05-09T10:00:00Z");

            await store.ReplaceAllAsync(
                [
                    new ImdbRatingRecord("tt0000001", 7.6, 12345, firstImport),
                    new ImdbRatingRecord("tt0000002", 8.1, 10, firstImport)
                ],
                firstImport,
                CancellationToken.None);
            await store.ReplaceAllAsync(
                [new ImdbRatingRecord("tt0000002", 8.2, 11, secondImport)],
                secondImport,
                CancellationToken.None);

            Assert.Null(await store.GetByImdbIdAsync("tt0000001", CancellationToken.None));
            var rating = await store.GetByImdbIdAsync("tt0000002", CancellationToken.None);

            Assert.NotNull(rating);
            Assert.Equal(8.2, rating.AverageRating);
            Assert.Equal(11, rating.VoteCount);
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static SqliteImdbRatingsStore CreateStore(string root)
    {
        return new SqliteImdbRatingsStore(
            Microsoft.Extensions.Options.Options.Create(new AppDatabaseOptions { Path = "data/app.db" }),
            new FakeWebHostEnvironment(root));
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"premiere-calendar-imdb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDelete(string root)
    {
        try
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private sealed class FakeWebHostEnvironment(string contentRootPath) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "PremiereCalendar.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = contentRootPath;
        public string EnvironmentName { get; set; } = "Development";
        public string WebRootPath { get; set; } = contentRootPath;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
    }
}
