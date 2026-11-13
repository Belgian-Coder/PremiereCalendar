using System.IO.Compression;
using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed class ImdbDatasetImporterTests
{
    [Fact]
    public async Task ImportRatingsAsync_ReadsGzippedTitleRatingsDataset()
    {
        var root = CreateRoot();
        try
        {
            TestSqliteDatabase.Initialize(root, "data/app.db");
            var handler = new GzipHandler(
                "tconst\taverageRating\tnumVotes\n" +
                "tt0000001\t7.6\t12345\n" +
                "tt0000002\t8.1\t23456\n");
            var store = new SqliteImdbRatingsStore(
                Microsoft.Extensions.Options.Options.Create(new AppDatabaseOptions { Path = "data/app.db" }),
                new FakeWebHostEnvironment(root));
            var importer = new ImdbDatasetImporter(
                new HttpClient(handler) { BaseAddress = new Uri("https://datasets.imdbws.com/") },
                store,
                Microsoft.Extensions.Options.Options.Create(new ImdbDatasetOptions { RatingsUrl = "title.ratings.tsv.gz" }),
                TimeProvider.System,
                NullLogger<ImdbDatasetImporter>.Instance);

            var imported = await importer.ImportRatingsAsync(CancellationToken.None);

            Assert.Equal(2, imported);
            var rating = await store.GetByImdbIdAsync("tt0000002", CancellationToken.None);
            Assert.NotNull(rating);
            Assert.Equal(8.1, rating.AverageRating);
            Assert.Equal(23456, rating.VoteCount);
        }
        finally
        {
            TryDelete(root);
        }
    }

    [Fact]
    public async Task ImportRatingsAsync_SkipsHeaderMalformedAndMissingRowsAndTrimsIds()
    {
        var root = CreateRoot();
        try
        {
            TestSqliteDatabase.Initialize(root, "data/app.db");
            var handler = new GzipHandler(
                "tconst\taverageRating\tnumVotes\r\n" +
                "tt0000001\t7.6\t12345\r\n" +
                "\r\n" +
                "tt0000002\t\\N\t5\r\n" +
                "tt0000003\t8.1\r\n" +
                "   \t8.1\t5\r\n" +
                "  TT0000004 \t 6.5 \t 77 \textra\r\n" +
                "tt0000005\t9.0\tabc\r\n" +
                "tt0000006\t5.5\t99");
            var store = new SqliteImdbRatingsStore(
                Microsoft.Extensions.Options.Options.Create(new AppDatabaseOptions { Path = "data/app.db" }),
                new FakeWebHostEnvironment(root));
            var importer = new ImdbDatasetImporter(
                new HttpClient(handler) { BaseAddress = new Uri("https://datasets.imdbws.com/") },
                store,
                Microsoft.Extensions.Options.Options.Create(new ImdbDatasetOptions { RatingsUrl = "title.ratings.tsv.gz" }),
                TimeProvider.System,
                NullLogger<ImdbDatasetImporter>.Instance);

            Assert.Equal(3, await importer.ImportRatingsAsync(CancellationToken.None));

            Assert.Equal(12345, (await store.GetByImdbIdAsync("tt0000001", CancellationToken.None))!.VoteCount);
            var trimmed = await store.GetByImdbIdAsync("tt0000004", CancellationToken.None);
            Assert.Equal(6.5, trimmed!.AverageRating);
            Assert.Equal(77, trimmed.VoteCount);
            Assert.Equal(99, (await store.GetByImdbIdAsync("tt0000006", CancellationToken.None))!.VoteCount);
            foreach (var skipped in new[] { "tt0000002", "tt0000003", "tt0000005", "tconst" })
                Assert.Null(await store.GetByImdbIdAsync(skipped, CancellationToken.None));
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"premiere-calendar-imdb-import-{Guid.NewGuid():N}");
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

    private sealed class GzipHandler(string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
            using (var writer = new StreamWriter(gzip))
            {
                writer.Write(content);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(buffer.ToArray())
            });
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
