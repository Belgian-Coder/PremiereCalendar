using System.Net;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class ReleaseUpdateServiceTests
{
    [Fact]
    public async Task CheckLatestAsync_ReturnsNoReleaseResultWhenGitHubHasNoPublishedReleases()
    {
        using var httpClient = new HttpClient(new StaticStatusHandler(HttpStatusCode.NotFound, "{}"))
        {
            BaseAddress = new Uri("https://api.github.com/")
        };
        var service = new ReleaseUpdateService(httpClient, "1.0.0");

        var result = await service.CheckLatestAsync(CancellationToken.None);

        Assert.Equal("1.0.0", result.CurrentVersion);
        Assert.Equal("1.0.0", result.LatestVersion);
        Assert.False(result.IsUpdateAvailable);
        Assert.Equal("", result.ReleaseUrl);
        Assert.Equal("No published releases found.", result.ReleaseName);
        Assert.Null(result.PublishedUtc);
    }

    private sealed class StaticStatusHandler(HttpStatusCode statusCode, string content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content)
            });
        }
    }
}
