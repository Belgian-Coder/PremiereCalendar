using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class ArrIntegrationServiceTests
{
    [Fact]
    public async Task AddAsync_PostsRadarrMovieUsingTmdbIdAndStoredParameters()
    {
        JsonNode? postedPayload = null;
        JsonNode? postedTagPayload = null;
        var handler = new FakeHttpMessageHandler(async request =>
        {
            if (request.RequestUri?.AbsolutePath == "/api/v3/movie"
                && request.Method == HttpMethod.Post)
            {
                postedPayload = JsonNode.Parse(await request.Content!.ReadAsStringAsync());
                return Json("""{"title":"Fight Club"}""");
            }

            if (request.RequestUri?.AbsolutePath == "/api/v3/tag"
                && request.Method == HttpMethod.Post)
            {
                postedTagPayload = JsonNode.Parse(await request.Content!.ReadAsStringAsync());
                return Json("""{"id":9,"label":"import"}""");
            }

            return request.RequestUri?.PathAndQuery switch
            {
                "/api/v3/movie?tmdbId=550" => Json("[]"),
                "/api/v3/movie/lookup/tmdb?tmdbId=550" => Json("""{"title":"Fight Club","tmdbId":550,"year":1999,"images":[]}"""),
                "/api/v3/tag" => Json("[]"),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });

        var service = new ArrIntegrationService(
            new HttpClient(handler),
            new FakeIntegrationSettingsStore(new IntegrationSettings
            {
                Radarr = new RadarrIntegrationSettings
                {
                    Enabled = true,
                    BaseUrl = "http://radarr.test/",
                    ApiKey = "radarr-key",
                    RootFolderPath = "/movies",
                    QualityProfileId = 4,
                    MinimumAvailability = "released",
                    Monitored = true,
                    SearchOnAdd = true,
                    TagOnAdd = "import"
                }
            }),
            NullLogger<ArrIntegrationService>.Instance);

        var result = await service.AddAsync(new PremiereItem
        {
            CanonicalId = "movie:550",
            MediaType = PremiereMediaType.Movie,
            Type = PremiereItemType.MovieFirstRelease,
            TmdbId = 550,
            Title = "Fight Club",
            PremiereDate = new DateOnly(1999, 10, 15)
        });

        Assert.True(result.Succeeded);
        Assert.False(result.AlreadyExists);
        Assert.NotNull(postedPayload);
        Assert.Equal(550, postedPayload!["tmdbId"]!.GetValue<int>());
        Assert.Equal("/movies", postedPayload["rootFolderPath"]!.GetValue<string>());
        Assert.Equal(4, postedPayload["qualityProfileId"]!.GetValue<int>());
        Assert.True(postedPayload["addOptions"]!["searchForMovie"]!.GetValue<bool>());
        Assert.Equal("import", postedTagPayload!["label"]!.GetValue<string>());
        Assert.Contains(9, postedPayload["tags"]!.AsArray().Select(tag => tag!.GetValue<int>()));
        Assert.All(handler.Requests, request => Assert.True(request.Headers.Contains("X-Api-Key")));
    }

    [Fact]
    public async Task AddAsync_ReusesConfiguredSonarrTagWhenAddingSeries()
    {
        JsonNode? postedPayload = null;
        var handler = new FakeHttpMessageHandler(async request =>
        {
            if (request.RequestUri?.AbsolutePath == "/api/v3/series"
                && request.Method == HttpMethod.Post)
            {
                postedPayload = JsonNode.Parse(await request.Content!.ReadAsStringAsync());
                return Json("""{"title":"Breaking Bad"}""");
            }

            if (request.RequestUri?.AbsolutePath == "/api/v3/tag"
                && request.Method == HttpMethod.Post)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest);
            }

            return request.RequestUri?.PathAndQuery switch
            {
                "/api/v3/series?tvdbId=81189" => Json("[]"),
                "/api/v3/series/lookup?term=tvdb:81189" => Json("""[{"title":"Breaking Bad","tvdbId":81189,"year":2008,"images":[],"seasons":[]}]"""),
                "/api/v3/tag" => Json("""[{"id":12,"label":"premiere-import"}]"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });

        var service = new ArrIntegrationService(
            new HttpClient(handler),
            new FakeIntegrationSettingsStore(new IntegrationSettings
            {
                Sonarr = new SonarrIntegrationSettings
                {
                    Enabled = true,
                    BaseUrl = "http://sonarr.test/",
                    ApiKey = "sonarr-key",
                    RootFolderPath = "/series",
                    QualityProfileId = 4,
                    SeriesType = "standard",
                    Monitor = "all",
                    SeasonFolder = true,
                    SearchOnAdd = true,
                    TagOnAdd = "premiere-import"
                }
            }),
            NullLogger<ArrIntegrationService>.Instance);

        var result = await service.AddAsync(new PremiereItem
        {
            CanonicalId = "tv:1396",
            MediaType = PremiereMediaType.Series,
            Type = PremiereItemType.SeriesPremiere,
            TmdbId = 1396,
            TvdbId = 81189,
            Title = "Breaking Bad",
            PremiereDate = new DateOnly(2008, 1, 20)
        });

        Assert.True(result.Succeeded);
        Assert.NotNull(postedPayload);
        Assert.Equal(81189, postedPayload!["tvdbId"]!.GetValue<int>());
        Assert.Equal("/series", postedPayload["rootFolderPath"]!.GetValue<string>());
        Assert.Equal(4, postedPayload["qualityProfileId"]!.GetValue<int>());
        Assert.Contains(12, postedPayload["tags"]!.AsArray().Select(tag => tag!.GetValue<int>()));
    }

    [Fact]
    public async Task AddAsync_SkipsTagCallsWhenConfiguredTagIsBlank()
    {
        JsonNode? postedPayload = null;
        var handler = new FakeHttpMessageHandler(async request =>
        {
            if (request.RequestUri?.AbsolutePath == "/api/v3/tag")
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            if (request.RequestUri?.AbsolutePath == "/api/v3/movie"
                && request.Method == HttpMethod.Post)
            {
                postedPayload = JsonNode.Parse(await request.Content!.ReadAsStringAsync());
                return Json("""{"title":"Fight Club"}""");
            }

            return request.RequestUri?.PathAndQuery switch
            {
                "/api/v3/movie?tmdbId=550" => Json("[]"),
                "/api/v3/movie/lookup/tmdb?tmdbId=550" => Json("""{"title":"Fight Club","tmdbId":550,"year":1999,"images":[]}"""),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
        });

        var service = new ArrIntegrationService(
            new HttpClient(handler),
            new FakeIntegrationSettingsStore(new IntegrationSettings
            {
                Radarr = new RadarrIntegrationSettings
                {
                    Enabled = true,
                    BaseUrl = "http://radarr.test/",
                    ApiKey = "radarr-key",
                    RootFolderPath = "/movies",
                    QualityProfileId = 4,
                    MinimumAvailability = "released",
                    TagOnAdd = " "
                }
            }),
            NullLogger<ArrIntegrationService>.Instance);

        var result = await service.AddAsync(new PremiereItem
        {
            CanonicalId = "movie:550",
            MediaType = PremiereMediaType.Movie,
            Type = PremiereItemType.MovieFirstRelease,
            TmdbId = 550,
            Title = "Fight Club",
            PremiereDate = new DateOnly(1999, 10, 15)
        });

        Assert.True(result.Succeeded);
        Assert.NotNull(postedPayload);
        Assert.Null(postedPayload!["tags"]);
        Assert.DoesNotContain(handler.Requests, request => request.RequestUri?.AbsolutePath == "/api/v3/tag");
    }

    [Fact]
    public async Task AddAsync_ReturnsClearSonarrFailureWhenTvdbIdIsMissing()
    {
        var service = new ArrIntegrationService(
            new HttpClient(new FakeHttpMessageHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)))),
            new FakeIntegrationSettingsStore(new IntegrationSettings
            {
                Sonarr = new SonarrIntegrationSettings
                {
                    Enabled = true,
                    BaseUrl = "http://sonarr.test/",
                    ApiKey = "sonarr-key",
                    RootFolderPath = "/series",
                    QualityProfileId = 4
                }
            }),
            NullLogger<ArrIntegrationService>.Instance);

        var result = await service.AddAsync(new PremiereItem
        {
            CanonicalId = "tv:1",
            MediaType = PremiereMediaType.Series,
            Type = PremiereItemType.SeriesPremiere,
            TmdbId = 1,
            Title = "Series Without TVDB",
            PremiereDate = new DateOnly(2026, 5, 7)
        });

        Assert.False(result.Succeeded);
        Assert.Equal(ArrIntegrationTarget.Sonarr, result.Target);
        Assert.Contains("TVDB ID", result.Message);
    }

    private static HttpResponseMessage Json(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _handler;

        public FakeHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler)
        {
            _handler = handler;
        }

        public List<HttpRequestMessage> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return await _handler(request);
        }
    }

    private sealed class FakeIntegrationSettingsStore : IIntegrationSettingsStore
    {
        private IntegrationSettings _settings;

        public FakeIntegrationSettingsStore(IntegrationSettings settings)
        {
            _settings = settings;
        }

        public Task<IntegrationSettings> GetAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_settings);
        }

        public Task SaveAsync(IntegrationSettings settings, CancellationToken cancellationToken = default)
        {
            _settings = settings;
            return Task.CompletedTask;
        }
    }
}
