using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.IntegrationTests.Support;
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed class RottenTomatoesPageReuseIntegrationTests
{
    [Fact]
    public async Task AutomaticStageTimingsPersistWithContextAndPageCacheOutcomes()
    {
        using var diagnostics = new DiagnosticFixture();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath == "/search"
            ? Search("PrivateExampleTitle", "/m/example", 83) : EmptyScorecard());
        var client = CreateClient(handler, cache, telemetry: diagnostics.Telemetry);
        var context = diagnostics.Telemetry.CreateDiagnosticContext(jobId: Guid.NewGuid().ToString("N"));
        using var correlation = diagnostics.Telemetry.UseDiagnosticContext(context);
        using var scope = ProviderRefreshScope.Use(ProviderRefreshScope.Create());

        Assert.Equal(83, (await Scores(client, "PrivateExampleTitle")).CriticScore);

        var events = diagnostics.ReadEvents();
        Assert.All(events, item =>
        {
            Assert.Equal("provider.rt.stage.completed", item.GetProperty("event").GetString());
            Assert.Equal(context.LoadId, item.GetProperty("loadId").GetString());
            Assert.Equal(context.JobId, item.GetProperty("jobId").GetString());
            Assert.True(item.GetProperty("details").GetProperty("durationMilliseconds").GetDouble() >= 0);
            Assert.DoesNotContain("PrivateExampleTitle", item.GetRawText());
            Assert.DoesNotContain("/m/example", item.GetRawText());
            Assert.DoesNotContain("https://", item.GetRawText());
        });
        var details = events.Select(item => item.GetProperty("details")).ToArray();
        Assert.Contains(details, item => item.GetProperty("stage").GetString() == "wikidata_identifier");
        Assert.Contains(details, item => item.GetProperty("stage").GetString() == "search_http");
        Assert.Contains(details, item => item.GetProperty("stage").GetString() == "search_match");
        Assert.Contains(details, item => item.GetProperty("stage").GetString() == "search_fallback");
        Assert.Single(details, item => item.GetProperty("stage").GetString() == "page_http");
        var parse = Assert.Single(details, item => item.GetProperty("stage").GetString() == "scorecard_parse");
        Assert.Equal("absent", parse.GetProperty("outcome").GetString());
        var pageLookups = details.Where(item => item.GetProperty("stage").GetString() == "page_lookup").ToArray();
        Assert.Equal(["miss", "hit"], pageLookups.Select(item => item.GetProperty("cache").GetString()));
        Assert.All(pageLookups, item => Assert.Equal("absent", item.GetProperty("outcome").GetString()));
    }

    [Theory]
    [InlineData("http", "page_http", "failed")]
    [InlineData("json", "scorecard_parse", "failed")]
    [InlineData("challenge", "scorecard_parse", "failed")]
    [InlineData("timeout", "page_http", "timeout")]
    public async Task AutomaticStageTimingsPersistFailuresAndTimeouts(string failure, string stage, string outcome)
    {
        using var diagnostics = new DiagnosticFixture();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/search") return Html("<html></html>");
            if (failure == "timeout") throw new OperationCanceledException();
            return failure == "http" ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : failure == "challenge" ? Html("<html><title>Just a moment...</title><body>Verify you are human</body></html>")
                : Html("<script id=\"media-scorecard-json\">{invalid}</script>");
        });
        var client = CreateClient(handler, cache, telemetry: diagnostics.Telemetry);
        if (failure == "timeout")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Scores(client, "Example"));
        else
            Assert.False((await Scores(client, "Example")).HasAnyScore);

        var details = diagnostics.ReadEvents().Select(item => item.GetProperty("details")).ToArray();
        Assert.Contains(details, item => item.GetProperty("stage").GetString() == stage
            && item.GetProperty("outcome").GetString() == outcome);
        Assert.Contains(details, item => item.GetProperty("stage").GetString() == "page_lookup"
            && item.GetProperty("outcome").GetString() == outcome);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyKnownPageIsReusedForCanonicalSearchMatchWhileSearchScoreIsRetained(bool scoped)
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath == "/search"
            ? Search("Example", "https://WWW.ROTTENTOMATOES.COM:443/m/example#ratings", 83)
            : EmptyScorecard());
        var client = CreateClient(handler, cache);
        using var scope = scoped ? ProviderRefreshScope.Use(ProviderRefreshScope.Create()) : null;

        var scores = await Scores(client, "Example");

        Assert.Equal(83, scores.CriticScore);
        Assert.Null(scores.AudienceScore);
        Assert.Equal(["/m/example", "/search"], handler.Requests.Select(request => request.Uri.AbsolutePath));
    }

    [Fact]
    public async Task SearchStillFetchesAlternatePageAndReturnsBothScores()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/m/example" => EmptyScorecard(),
            "/search" => Search("Example", "/m/example_2026", 83),
            _ => Scorecard(91, 94)
        });
        var client = CreateClient(handler, cache);
        using var scope = ProviderRefreshScope.Use(ProviderRefreshScope.Create());

        Assert.Equal(new RottenTomatoesScores(91, 94), await Scores(client, "Example"));
        Assert.Equal(["/m/example", "/search", "/m/example_2026"],
            handler.Requests.Select(request => request.Uri.AbsolutePath));
    }

    [Fact]
    public async Task SharedEmptyPageDoesNotReuseAnotherTitleSearchFallback()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var handler = new StubHttpMessageHandler(request => request.RequestUri!.AbsolutePath == "/search"
            ? request.RequestUri.Query.Contains("First", StringComparison.Ordinal)
                ? Search("First", "/m/example", 72)
                : Search("Second", "/m/example", 95)
            : EmptyScorecard());
        var client = CreateClient(handler, cache);
        using var scope = ProviderRefreshScope.Use(ProviderRefreshScope.Create());

        Assert.Equal(72, (await Scores(client, "First")).CriticScore);
        Assert.Equal(95, (await Scores(client, "Second")).CriticScore);
        Assert.Equal(1, handler.Requests.Count(request => request.Uri.AbsolutePath == "/m/example"));
        Assert.Equal(2, handler.Requests.Count(request => request.Uri.AbsolutePath == "/search"));
    }

    [Theory]
    [InlineData("http")]
    [InlineData("blank")]
    [InlineData("json")]
    [InlineData("array")]
    [InlineData("empty-scorecard")]
    [InlineData("challenge")]
    [InlineData("consent")]
    [InlineData("error")]
    [InlineData("garbage")]
    public async Task FailedOrMalformedKnownPageCanRetryAtSameSearchUrl(string failure)
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var pageCalls = 0;
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/search") return Search("Example", "/m/example", 83);
            if (++pageCalls > 1) return Scorecard(91, 94);
            return failure switch
            {
                "http" => new HttpResponseMessage(HttpStatusCode.NotFound),
                "blank" => Html(" "),
                "json" => Html("<script id=\"media-scorecard-json\">{invalid}</script>"),
                "array" => Html("<script id=\"media-scorecard-json\">[]</script>"),
                "challenge" => Html("<html><title>Just a moment...</title><body>Verify you are human</body></html>"),
                "consent" => Html("<html><title>Privacy consent</title><body>Please accept cookies</body></html>"),
                "error" => Html("<html><title>Rotten Tomatoes</title><body>Something went wrong</body></html>"),
                "garbage" => Html("unexpected response"),
                _ => Html("<script id=\"media-scorecard-json\"> </script>")
            };
        });
        var client = CreateClient(handler, cache);
        using var scope = ProviderRefreshScope.Use(ProviderRefreshScope.Create());

        Assert.Equal(new RottenTomatoesScores(91, 94), await Scores(client, "Example"));
        Assert.Equal(2, pageCalls);
        Assert.False(client.TryGetCachedScores(PremiereMediaType.Movie, "Example", 2026, "QExample", out _));
        Assert.Equal(new RottenTomatoesScores(91, 94), await Scores(client, "Example"));
        Assert.Equal(2, pageCalls);
        Assert.True(client.TryGetCachedScores(PremiereMediaType.Movie, "Example", 2026, "QExample", out _));
    }

    [Fact]
    public async Task ChallengeHtmlCannotVerifyAbsenceOrSuppressNextLookupInSameGeneration()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var pageCalls = 0;
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/search") return Html("<html>No matches</html>");
            return ++pageCalls == 1
                ? Html("<html><title>Just a moment...</title><body>Verify you are human</body></html>")
                : Scorecard(91, 94);
        });
        var client = CreateClient(handler, cache);
        using var scope = ProviderRefreshScope.Use(ProviderRefreshScope.Create());

        Assert.False((await Scores(client, "Example")).HasAnyScore);
        Assert.False(client.TryGetCachedScores(PremiereMediaType.Movie, "Example", 2026, "QExample", out _));
        Assert.Equal(new RottenTomatoesScores(91, 94), await Scores(client, "Example"));
        Assert.Equal(2, pageCalls);
        Assert.True(client.TryGetCachedScores(PremiereMediaType.Movie, "Example", 2026, "QExample", out _));
    }

    [Fact]
    public async Task ConcurrentIdentitiesSharePageAndOneCancelledWaiterDoesNotCancelSurvivor()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var started = Signal();
        var release = Signal();
        var pageCalls = 0;
        CancellationToken pageToken = default;
        var handler = new AsyncHandler(async (_, token) =>
        {
            var call = Interlocked.Increment(ref pageCalls);
            pageToken = token;
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return Scorecard(call == 1 ? 88 : 93, 94);
        });
        var client = CreateClient(handler, cache);
        using var cancelled = new CancellationTokenSource();
        using (ProviderRefreshScope.Use(ProviderRefreshScope.Create()))
        {
            var first = Scores(client, "First", cancelled.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = Scores(client, "Second");
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            Assert.False(pageToken.IsCancellationRequested);
            release.TrySetResult();
            Assert.Equal(88, (await second.WaitAsync(TimeSpan.FromSeconds(5))).CriticScore);
            Assert.Equal(1, pageCalls);
            Assert.Equal(88, (await Scores(client, "Third")).CriticScore);
            Assert.Equal(1, pageCalls);
        }

        using (ProviderRefreshScope.Use(ProviderRefreshScope.Create()))
            Assert.Equal(93, (await Scores(client, "Second")).CriticScore);
        Assert.Equal(2, pageCalls);
    }

    [Fact]
    public async Task PageCancelledByAllWaitersRemainsRetryable()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var started = Signal();
        var abandoned = Signal();
        var pageCalls = 0;
        var handler = new AsyncHandler(async (_, token) =>
        {
            if (Interlocked.Increment(ref pageCalls) == 1)
            {
                started.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { abandoned.TrySetResult(); }
            }
            return Scorecard(91, 94);
        });
        var client = CreateClient(handler, cache);
        using var scope = ProviderRefreshScope.Use(ProviderRefreshScope.Create());
        using var cancelled = new CancellationTokenSource();
        var first = Scores(client, "First", cancelled.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await abandoned.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(91, (await Scores(client, "Second")).CriticScore);
        Assert.Equal(2, pageCalls);
    }

    [Theory]
    [InlineData("https://mirror.example/")]
    [InlineData("https://www.rottentomatoes.com:8443/")]
    public async Task SamePagePathOnDifferentAuthorityCannotShareResult(string secondBaseUrl)
    {
        using var firstCache = new MemoryCache(new MemoryCacheOptions());
        using var secondCache = new MemoryCache(new MemoryCacheOptions());
        var firstHandler = new StubHttpMessageHandler(_ => Scorecard(72, 84));
        var secondHandler = new StubHttpMessageHandler(_ => Scorecard(95, 96));
        var first = CreateClient(firstHandler, firstCache);
        var second = CreateClient(secondHandler, secondCache, secondBaseUrl);
        using var scope = ProviderRefreshScope.Use(ProviderRefreshScope.Create());

        Assert.Equal(72, (await Scores(first, "First")).CriticScore);
        Assert.Equal(95, (await Scores(second, "Second")).CriticScore);
        Assert.Single(firstHandler.Requests);
        Assert.Single(secondHandler.Requests);
    }

    [Fact]
    public async Task OlderOrdinaryPageCannotReplaceConcurrentForcedPageMemo()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var started = Signal();
        var release = Signal();
        var pageCalls = 0;
        var handler = new AsyncHandler(async (_, token) =>
        {
            if (Interlocked.Increment(ref pageCalls) == 1)
            {
                started.TrySetResult();
                await release.Task.WaitAsync(token);
                return Scorecard(72, 84);
            }
            return Scorecard(95, 96);
        });
        var client = CreateClient(handler, cache);
        using var scope = ProviderRefreshScope.Use(ProviderRefreshScope.Create());
        var ordinary = Scores(client, "First", force: false);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(95, (await Scores(client, "Second")).CriticScore);
        release.TrySetResult();
        Assert.Equal(72, (await ordinary.WaitAsync(TimeSpan.FromSeconds(5))).CriticScore);
        Assert.Equal(95, (await Scores(client, "Third")).CriticScore);
        Assert.Equal(2, pageCalls);
        Assert.Equal(95, (await Scores(client, "Fourth", force: false)).CriticScore);
        Assert.Equal(3, pageCalls);
    }

    private static Task<RottenTomatoesScores> Scores(RottenTomatoesClient client, string title,
        CancellationToken token = default, bool force = true)
        => client.GetScoresAsync(PremiereMediaType.Movie, title, 2026, "Q" + title, token, force);

    private static RottenTomatoesClient CreateClient(HttpMessageHandler handler, IMemoryCache cache,
        string baseUrl = "https://www.rottentomatoes.com/", PremiereTelemetry? telemetry = null)
        => new(new HttpClient(handler) { BaseAddress = new Uri(baseUrl) }, cache,
            Microsoft.Extensions.Options.Options.Create(new RottenTomatoesOptions { BaseUrl = baseUrl }),
            NullLogger<RottenTomatoesClient>.Instance, new KnownPageWikimedia(), telemetry: telemetry);

    private static HttpResponseMessage Search(string title, string url, int score) => Html(
        $"""<search-page-media-row release-year="2026" tomatometer-score="{score}"><a href="{url}" data-qa="info-name">{title}</a></search-page-media-row>""");

    private static HttpResponseMessage Scorecard(int critics, int audience) => Html(
        "<script id=\"media-scorecard-json\">{\"criticsScore\":{\"score\":" + critics
        + "},\"audienceScore\":{\"score\":" + audience + "}}</script>");

    private static HttpResponseMessage EmptyScorecard() => Html("<script id=\"media-scorecard-json\">{}</script>");

    private static HttpResponseMessage Html(string html) => new(HttpStatusCode.OK)
        { Content = new StringContent(html, System.Text.Encoding.UTF8, "text/html") };

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class DiagnosticFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "premiere-rt-diagnostics", Guid.NewGuid().ToString("N"));
        private readonly PerformanceDiagnosticStore _store;
        public PremiereTelemetry Telemetry { get; }

        public DiagnosticFixture()
        {
            _store = new PerformanceDiagnosticStore(new(), _directory);
            Telemetry = new PremiereTelemetry(_store);
        }

        public JsonElement[] ReadEvents()
        {
            Telemetry.FlushDiagnostics();
            return Directory.GetFiles(Path.Combine(_directory, "App_Data", "logs", "performance"), "performance-*.ndjson")
                .SelectMany(path =>
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(stream);
                    return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
                })
                .Select(line => { using var document = JsonDocument.Parse(line); return document.RootElement.Clone(); })
                .ToArray();
        }

        public void Dispose()
        {
            _store.Dispose();
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }

    private sealed class AsyncHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => send(request, token);
    }

    private sealed class KnownPageWikimedia : IWikimediaClient
    {
        public Task<string?> GetReusableImageUrlAsync(string wikidataId, CancellationToken token, bool forceRefresh = false)
            => Task.FromResult<string?>(null);

        public Task<string?> GetRottenTomatoesIdAsync(string wikidataId, CancellationToken token, bool forceRefresh = false)
            => Task.FromResult<string?>("/m/example");
    }
}
