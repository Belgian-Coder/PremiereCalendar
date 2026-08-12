using System.Net;
using System.Text;
using PremiereCalendar.Updates;

namespace PremiereCalendar.UnitTests;

public sealed class GitHubReleaseClientTests
{
    [Fact]
    public async Task DownloadStableAsync_RejectsLookalikeAssetHost()
    {
        const string metadata = """
            {"assets":[{"name":"stable.manifest.json","browser_download_url":"https://evilgithub.com/stable.manifest.json"}]}
            """;
        using var httpClient = new HttpClient(new StubHandler(_ => Json(metadata)));
        var client = new GitHubReleaseClient(httpClient);
        var destination = Path.Combine(Path.GetTempPath(), "premiere-github-client-tests", Guid.NewGuid().ToString("N"));

        try
        {
            var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
                client.DownloadStableAsync("Belgian-Coder", "PremiereCalendar", destination));

            Assert.Contains("HTTPS on github.com", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
        }
    }

    [Fact]
    public async Task DownloadStableAsync_RejectsOversizedReleaseMetadataBeforeReadingBody()
    {
        using var httpClient = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent([0])
            {
                Headers = { ContentLength = 1024 * 1024 + 1 }
            }
        }));
        var client = new GitHubReleaseClient(httpClient);

        var error = await Assert.ThrowsAsync<InvalidDataException>(() =>
            client.DownloadStableAsync("Belgian-Coder", "PremiereCalendar", Path.GetTempPath()));

        Assert.Contains("metadata is too large", error.Message, StringComparison.Ordinal);
    }

    private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(value, Encoding.UTF8, "application/json")
    };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
