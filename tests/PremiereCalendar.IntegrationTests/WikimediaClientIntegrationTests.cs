using Microsoft.Extensions.Caching.Memory;
using System.Net;
using PremiereCalendar.IntegrationTests.Support;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.IntegrationTests;

public sealed class WikimediaClientIntegrationTests
{
    [Fact]
    public async Task GetReusableImageUrlAsync_ReadsWikidataImageAndRequiresCommonsMetadata()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "www.wikidata.org")
            {
                return StubHttpMessageHandler.Json(
                    """
                    {
                      "entities": {
                        "Q123": {
                          "claims": {
                            "P18": [
                              {
                                "mainsnak": {
                                  "datavalue": { "value": "Reusable poster.jpg" }
                                }
                              }
                            ]
                          }
                        }
                      }
                    }
                    """);
            }

            return StubHttpMessageHandler.Json(
                """
                {
                  "query": {
                    "pages": {
                      "1": {
                        "imageinfo": [
                          {
                            "url": "https://upload.wikimedia.org/wikipedia/commons/reusable-poster.jpg",
                            "extmetadata": {
                              "LicenseShortName": { "value": "CC BY-SA 4.0" },
                              "UsageTerms": { "value": "Creative Commons Attribution-Share Alike 4.0" }
                            }
                          }
                        ]
                      }
                    }
                  }
                }
                """);
        });
        var client = new WikimediaClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://www.wikidata.org/") },
            new MemoryCache(new MemoryCacheOptions()),
            Microsoft.Extensions.Options.Options.Create(new WikimediaOptions
            {
                Enabled = true,
                CommonsApiUrl = "https://commons.wikimedia.org/w/api.php"
            }));

        var imageUrl = await client.GetReusableImageUrlAsync("Q123", CancellationToken.None);

        Assert.Equal("https://upload.wikimedia.org/wikipedia/commons/reusable-poster.jpg", imageUrl);
        Assert.Contains(handler.Requests, request => request.Uri.Host == "www.wikidata.org");
        Assert.Contains(handler.Requests, request => request.Uri.Host == "commons.wikimedia.org");
    }

    [Fact]
    public async Task GetReusableImageUrlAsync_ReturnsNullWhenHttpClientTimesOutWithoutCallerCancellation()
    {
        var handler = new StubHttpMessageHandler(_ => throw new TaskCanceledException("Simulated HTTP timeout."));
        var client = new WikimediaClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://www.wikidata.org/") },
            new MemoryCache(new MemoryCacheOptions()),
            Microsoft.Extensions.Options.Options.Create(new WikimediaOptions
            {
                Enabled = true,
                CommonsApiUrl = "https://commons.wikimedia.org/w/api.php"
            }));

        var imageUrl = await client.GetReusableImageUrlAsync("Q123", CancellationToken.None);

        Assert.Null(imageUrl);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GetRottenTomatoesIdAsync_ReadsP1258Identifier()
    {
        var handler = new StubHttpMessageHandler(_ => StubHttpMessageHandler.Json(
            """
            {
              "entities": {
                "Q123": {
                  "claims": {
                    "P1258": [
                      {
                        "mainsnak": {
                          "datavalue": { "value": "m/corporate_retreat" }
                        }
                      }
                    ]
                  }
                }
              }
            }
            """));
        var client = new WikimediaClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://www.wikidata.org/") },
            new MemoryCache(new MemoryCacheOptions()),
            Microsoft.Extensions.Options.Options.Create(new WikimediaOptions
            {
                Enabled = true,
                CommonsApiUrl = "https://commons.wikimedia.org/w/api.php"
            }));

        var rottenTomatoesId = await client.GetRottenTomatoesIdAsync("Q123", CancellationToken.None);

        Assert.Equal("m/corporate_retreat", rottenTomatoesId);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task GetReusableImageUrlAsync_DoesNotCacheTransientWikidataFailureAsMissingImage()
    {
        var wikidataAttempts = 0;
        var handler = new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri!.Host == "www.wikidata.org")
            {
                wikidataAttempts++;
                if (wikidataAttempts == 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError);
                }

                return StubHttpMessageHandler.Json(
                    """
                    {
                      "entities": {
                        "Q123": {
                          "claims": {
                            "P18": [
                              {
                                "mainsnak": {
                                  "datavalue": { "value": "Recovered poster.jpg" }
                                }
                              }
                            ]
                          }
                        }
                      }
                    }
                    """);
            }

            return StubHttpMessageHandler.Json(
                """
                {
                  "query": {
                    "pages": {
                      "1": {
                        "imageinfo": [
                          {
                            "url": "https://upload.wikimedia.org/wikipedia/commons/recovered-poster.jpg",
                            "extmetadata": {
                              "LicenseShortName": { "value": "CC BY-SA 4.0" }
                            }
                          }
                        ]
                      }
                    }
                  }
                }
                """);
        });
        var client = new WikimediaClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://www.wikidata.org/") },
            new MemoryCache(new MemoryCacheOptions()),
            Microsoft.Extensions.Options.Options.Create(new WikimediaOptions
            {
                Enabled = true,
                CommonsApiUrl = "https://commons.wikimedia.org/w/api.php"
            }));

        var failedImageUrl = await client.GetReusableImageUrlAsync("Q123", CancellationToken.None);
        var recoveredImageUrl = await client.GetReusableImageUrlAsync("Q123", CancellationToken.None);

        Assert.Null(failedImageUrl);
        Assert.Equal("https://upload.wikimedia.org/wikipedia/commons/recovered-poster.jpg", recoveredImageUrl);
        Assert.Equal(3, handler.Requests.Count);
    }
}
