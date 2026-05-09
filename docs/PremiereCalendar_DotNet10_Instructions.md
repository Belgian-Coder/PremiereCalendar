# Local Premiere Calendar — .NET 10 + Blazor + TMDb

> Historical implementation brief. The current app targets .NET 11 preview and the maintained docs are `README.md`, `docs/HowItWorks.md`, `docs/Architecture.md`, `docs/Configuration.md`, `docs/Troubleshooting.md`, and `docs/Testing.md`.

## 1. Goal

Build a locally hosted .NET 10 Web UI that replaces the useful part of the old Trakt/SIMKL calendar:

- weekly calendar view
- one block per release/premiere
- series descriptions
- movie descriptions
- trailer links that point to real existing trailer videos
- rating display and score-range filtering
- no personal watchlist requirement
- no need to mark items as watched

The app is intentionally **local-first**. It should run on your own machine, keep API keys server-side, and avoid account/social/tracking features.

## 2. Data-source decision

Use **TMDb as the primary source**.

Why:

- TMDb Discover TV supports `first_air_date.gte`, `first_air_date.lte`, `with_original_language`, `with_origin_country`, `with_networks`, `watch_region`, `with_watch_providers`, `vote_average`, and runtime filters.
- TMDb Discover Movie supports `primary_release_date.gte`, `primary_release_date.lte`, `with_original_language`, `with_origin_country`, `with_runtime.gte`, `vote_average`, `watch_region`, and `with_watch_providers`.
- TMDb movie/TV details support `append_to_response`, so you can enrich results with `videos` and `external_ids` in one detail call.
- TMDb has movie and TV video endpoints. These videos usually include YouTube trailers/teasers when available.
- TMDb image paths can be converted into real image URLs through the configuration/image URL rules.

Use **TMDb score** by default.

Optional: use **OMDb** as an enrichment source for IMDb and Rotten Tomatoes ratings, because TMDb does **not** directly return IMDb or Rotten Tomatoes scores. TMDb can provide external IMDb IDs for many movies/series. OMDb can then be queried by IMDb ID.

Do **not** scrape Rotten Tomatoes or IMDb pages. Use an API or skip those scores.


### 2.1 Should you integrate sources besides TMDb?

Short version: **TMDb should remain the primary source.** It has most of what this app needs for discovery: premiere dates, original language, origin country, descriptions, posters/backdrops, trailers, external IDs, watch-provider filters, and TMDb user scores.

Extra sources should be treated as **enrichment layers**, not replacements.

| Source | Add now? | What it adds | Why not make it primary? |
|---|---:|---|---|
| **TMDb** | Yes | Discovery filters, descriptions, posters, backdrops, videos/trailers, external IDs, TMDb scores, watch-provider filters | Best fit for the actual premiere-calendar requirement. |
| **OMDb** | Optional now | IMDb score and sometimes Rotten Tomatoes score via IMDb ID | Rating enrichment only. Not a discovery/calendar source. |
| **TVmaze** | Later | Episode-level TV schedules, network/web-channel schedule validation, exact `S01E01` checks | Better for airing schedules than broad premiere discovery. Country schedule excludes global web channels like Netflix; web schedule is separate. |
| **JustWatch Partner API / Widget** | Later, only if accessible | Strong legal VOD availability by country/provider, fresh streaming availability, provider offers | Partner/commercial integration. Not a simple public hobby API. Do not scrape it. |
| **Watchmode / Streaming Availability APIs** | Later | Streaming availability across services, regions and monetization types | Useful only if TMDb watch providers are not good enough; usually paid/commercial. |
| **TheTVDB** | Later | Extra TV metadata and alternative TV database coverage | Extra API/licensing complexity. Only useful if TMDb coverage is insufficient for Dutch/Flemish/UK/AU series. |
| **IMDb official API / datasets** | Later / overkill | Official IMDb ratings and richer licensed metadata | Official API is AWS Data Exchange based; non-commercial datasets are possible but add ingestion/licensing work. |
| **Fanart.tv or similar artwork APIs** | No for MVP | More logos, clear art, banners | TMDb images are enough for a local calendar. |

Recommended path:

1. **MVP:** TMDb only.
2. **Rating enrichment:** add OMDb.
3. **TV confidence enrichment:** add TVmaze only if TMDb misses too many series premieres or if you want exact `S01E01` schedule validation.
4. **Streaming availability enrichment:** first try TMDb `watch_region` and watch-provider filters; only add JustWatch/Watchmode-style sources if TMDb provider data is not good enough.
5. **Do not integrate every source immediately.** More APIs will make the app more fragile unless each integration has tests, caching and graceful degradation.

### 2.2 Practical source strategy

Use this internal model:

```text
TMDb = canonical discovery source
OMDb = optional rating enrichment
TVmaze = optional TV schedule verifier
JustWatch/Watchmode = optional streaming availability enrichment
IMDb/TheTVDB = optional licensed/deeper metadata enrichment
```

The UI should show source confidence clearly:

- `TMDb date` for premiere date.
- `TMDb score` for default score.
- `IMDb via OMDb` if OMDb returned an IMDb score.
- `RT via OMDb` if OMDb returned a Rotten Tomatoes score.
- `Trailer via TMDb Videos` for trailer links.
- Optional later: `Verified by TVmaze` if a matching `S01E01` entry was found.

## 3. Required filters

### 3.1 Series filters

User requirement:

> Language: English from US/UK/Australia + Dutch
> Only premieres: first episode of a new series

Map this to TMDb Discover TV:

| Requirement | TMDb filter |
|---|---|
| Only new series premieres | `first_air_date.gte` + `first_air_date.lte` |
| Avoid all normal weekly episodes | Do **not** use `air_date.gte` / `air_date.lte` |
| English | `with_original_language=en` |
| US / UK / Australia origin | `with_origin_country=US|GB|AU` |
| Dutch | `with_original_language=nl` |
| Optional Dutch country restriction | `with_origin_country=NL|BE` |

Use two separate queries and merge results:

#### Query A — English US/UK/Australia series premieres

```http
GET /3/discover/tv
  ?first_air_date.gte={startDate}
  &first_air_date.lte={endDate}
  &with_original_language=en
  &with_origin_country=US|GB|AU
  &sort_by=first_air_date.asc
  &include_adult=false
```

#### Query B — Dutch-language series premieres

```http
GET /3/discover/tv
  ?first_air_date.gte={startDate}
  &first_air_date.lte={endDate}
  &with_original_language=nl
  &with_origin_country=NL|BE
  &sort_by=first_air_date.asc
  &include_adult=false
```

Make `NL|BE` configurable. If you want all Dutch-language premieres regardless of origin country, omit `with_origin_country`.

### 3.2 Movie filters

User requirement:

> Language: English from US/UK/Australia + Dutch
> first-release date; type does not matter
> runtime > 40 min
> show user scores from IMDb or Rotten Tomatoes or TMDb, with score-range filter in UI

Map this to TMDb Discover Movie:

| Requirement | TMDb filter |
|---|---|
| First-release style date window | `primary_release_date.gte` + `primary_release_date.lte` |
| Type does not matter | Do **not** use `with_release_type` |
| Runtime above 40 min | `with_runtime.gte=41` |
| English | `with_original_language=en` |
| US / UK / Australia origin | `with_origin_country=US|GB|AU` |
| Dutch | `with_original_language=nl` |
| Optional Dutch country restriction | `with_origin_country=NL|BE` |
| TMDb user score | `vote_average`, `vote_count` |
| IMDb / Rotten Tomatoes | Optional OMDb enrichment via IMDb ID |

Use two separate queries and merge results:

#### Query C — English US/UK/Australia movie releases

```http
GET /3/discover/movie
  ?primary_release_date.gte={startDate}
  &primary_release_date.lte={endDate}
  &with_original_language=en
  &with_origin_country=US|GB|AU
  &with_runtime.gte=41
  &sort_by=primary_release_date.asc
  &include_adult=false
```

#### Query D — Dutch-language movie releases

```http
GET /3/discover/movie
  ?primary_release_date.gte={startDate}
  &primary_release_date.lte={endDate}
  &with_original_language=nl
  &with_origin_country=NL|BE
  &with_runtime.gte=41
  &sort_by=primary_release_date.asc
  &include_adult=false
```

Important: `primary_release_date` is a TMDb metadata field. It is not the same as “available on Netflix Belgium”. Later, you can add optional `watch_region=BE` and provider filters, but that changes the product from “premiere monitor” into “Belgian streaming availability monitor”.

## 4. Recommended architecture

Use a **Blazor Web App with interactive server rendering**.

Why this is the right fit:

- API keys stay on the server.
- The UI is simple enough for Blazor components.
- No separate API/backend project is required for the first version.
- Local hosting is trivial: `dotnet run`.
- Server-side caching is easy with `IMemoryCache`.

### 4.1 High-level structure

```text
PremiereCalendar/
  Components/
    Layout/
    Pages/
      Calendar.razor
      Settings.razor
    Shared/
      CalendarWeek.razor
      CalendarDay.razor
      PremiereCard.razor
      ScoreFilter.razor
  Models/
    CalendarFilters.cs
    PremiereItem.cs
    TmdbDtos.cs
    OmdbDtos.cs
  Options/
    TmdbOptions.cs
    OmdbOptions.cs
  Services/
    TmdbClient.cs
    OmdbClient.cs
    PremiereService.cs
    TrailerSelector.cs
    RatingMapper.cs
  wwwroot/
    app.css
  Program.cs
  appsettings.json

tests/
  PremiereCalendar.UnitTests/
  PremiereCalendar.IntegrationTests/
  PremiereCalendar.ComponentTests/      # optional, for bUnit Blazor component tests
```

### 4.2 Testability is part of the architecture

Do not treat tests as cleanup work after the app is finished. This app depends on external metadata APIs, date-window logic, filters, enrichment and UI rendering. Those are exactly the areas that tend to break silently during feature changes.

Use three test layers:

| Test layer | Project | Purpose |
|---|---|---|
| **Unit tests** | `PremiereCalendar.UnitTests` | Fast tests for query-building, score filtering, date grouping, trailer selection, rating parsing and de-duplication. |
| **Integration tests** | `PremiereCalendar.IntegrationTests` | Boot the app/service graph with fake HTTP responses and prove the TMDb/OMDb clients, caching and `PremiereService` work together. |
| **Component tests** | `PremiereCalendar.ComponentTests` | Optional bUnit tests for Blazor components such as weekly calendar rendering, score filter UI and premiere cards. |

Hard rule: **do not call live TMDb, OMDb, TVmaze, JustWatch or Watchmode endpoints in normal automated tests.** Use canned JSON fixtures and fake HTTP handlers. Live API smoke tests can exist, but they should be manual or opt-in because third-party outages, quota limits and changing metadata should not break normal local builds.


## 5. Prerequisites

Install:

- .NET 10 SDK
- Visual Studio 2026 / Rider / VS Code with C# tooling
- A TMDb account and API read access token
- Optional: an OMDb API key if you want IMDb/Rotten Tomatoes enrichment

Check your .NET SDK:

```bash
dotnet --list-sdks
dotnet --version
```

You want a `10.x` SDK.

## 6. Create the project

```bash
mkdir PremiereCalendar
cd PremiereCalendar

dotnet new blazor -o PremiereCalendar
cd PremiereCalendar
```

The default Blazor Web App template uses interactive server-side rendering by default. You can verify the created `.csproj` targets .NET 10:

```xml
<TargetFramework>net10.0</TargetFramework>
```

If the generated project targets another framework, edit the `.csproj` or regenerate it using the .NET 10 SDK.

Run the empty app:

```bash
dotnet run
```

Open the local URL shown by the console, usually one of:

```text
https://localhost:7xxx
http://localhost:5xxx
```


Create the test projects immediately, before adding API logic:

```bash
cd ..
dotnet new sln -n PremiereCalendar

dotnet sln add PremiereCalendar/PremiereCalendar.csproj

dotnet new xunit -o tests/PremiereCalendar.UnitTests
dotnet new xunit -o tests/PremiereCalendar.IntegrationTests

dotnet sln add tests/PremiereCalendar.UnitTests/PremiereCalendar.UnitTests.csproj
dotnet sln add tests/PremiereCalendar.IntegrationTests/PremiereCalendar.IntegrationTests.csproj

dotnet add tests/PremiereCalendar.UnitTests/PremiereCalendar.UnitTests.csproj reference PremiereCalendar/PremiereCalendar.csproj
dotnet add tests/PremiereCalendar.IntegrationTests/PremiereCalendar.IntegrationTests.csproj reference PremiereCalendar/PremiereCalendar.csproj
```

Optional component tests with bUnit:

```bash
dotnet new xunit -o tests/PremiereCalendar.ComponentTests
dotnet sln add tests/PremiereCalendar.ComponentTests/PremiereCalendar.ComponentTests.csproj
dotnet add tests/PremiereCalendar.ComponentTests/PremiereCalendar.ComponentTests.csproj reference PremiereCalendar/PremiereCalendar.csproj
dotnet add tests/PremiereCalendar.ComponentTests/PremiereCalendar.ComponentTests.csproj package bunit
```

Run everything regularly:

```bash
dotnet test
```

## 7. Store API keys with user-secrets

Do not put API keys in `appsettings.json`.

Initialize user-secrets:

```bash
dotnet user-secrets init
```

Set your TMDb bearer token:

```bash
dotnet user-secrets set "Tmdb:BearerToken" "YOUR_TMDB_V4_READ_ACCESS_TOKEN"
```

Optional OMDb key:

```bash
dotnet user-secrets set "Omdb:ApiKey" "YOUR_OMDB_API_KEY"
```

Your `appsettings.json` should contain non-secret defaults only:

```json
{
  "Tmdb": {
    "BaseUrl": "https://api.themoviedb.org/3/",
    "ImageBaseUrl": "https://image.tmdb.org/t/p/",
    "PosterSize": "w342",
    "BackdropSize": "w780",
    "EnglishOriginCountries": [ "US", "GB", "AU" ],
    "DutchOriginCountries": [ "NL", "BE" ],
    "IncludeDutchOriginRestriction": true,
    "DefaultLookAheadDays": 42,
    "MaxPagesPerQuery": 5
  },
  "Omdb": {
    "BaseUrl": "https://www.omdbapi.com/",
    "Enabled": false
  }
}
```

## 8. Add options classes

Create `Options/TmdbOptions.cs`:

```csharp
namespace PremiereCalendar.Options;

public sealed class TmdbOptions
{
    public string BaseUrl { get; init; } = "https://api.themoviedb.org/3/";
    public string ImageBaseUrl { get; init; } = "https://image.tmdb.org/t/p/";
    public string PosterSize { get; init; } = "w342";
    public string BackdropSize { get; init; } = "w780";
    public string? BearerToken { get; init; }

    public string[] EnglishOriginCountries { get; init; } = ["US", "GB", "AU"];
    public string[] DutchOriginCountries { get; init; } = ["NL", "BE"];

    public bool IncludeDutchOriginRestriction { get; init; } = true;
    public int DefaultLookAheadDays { get; init; } = 42;
    public int MaxPagesPerQuery { get; init; } = 5;
}
```

Create `Options/OmdbOptions.cs`:

```csharp
namespace PremiereCalendar.Options;

public sealed class OmdbOptions
{
    public string BaseUrl { get; init; } = "https://www.omdbapi.com/";
    public string? ApiKey { get; init; }
    public bool Enabled { get; init; }
}
```

## 9. Add domain model

Create `Models/PremiereItem.cs`:

```csharp
namespace PremiereCalendar.Models;

public enum PremiereMediaType
{
    Series,
    Movie
}

public sealed record PremiereItem
{
    public required PremiereMediaType MediaType { get; init; }
    public required int TmdbId { get; init; }
    public required string Title { get; init; }
    public required DateOnly PremiereDate { get; init; }

    public string? Overview { get; init; }
    public string? PosterUrl { get; init; }
    public string? BackdropUrl { get; init; }
    public string? TrailerUrl { get; init; }
    public string? TmdbUrl { get; init; }
    public string? ImdbUrl { get; init; }

    public string OriginalLanguage { get; init; } = "";
    public string[] OriginCountries { get; init; } = [];

    public int? RuntimeMinutes { get; init; }

    public double? TmdbScore { get; init; }
    public int? TmdbVoteCount { get; init; }

    public double? ImdbScore { get; init; }
    public int? RottenTomatoesScore { get; init; }
}
```

Create `Models/CalendarFilters.cs`:

```csharp
namespace PremiereCalendar.Models;

public enum ScoreSource
{
    Tmdb,
    Imdb,
    RottenTomatoes
}

public sealed class CalendarFilters
{
    public DateOnly WeekStart { get; set; } = StartOfWeek(DateOnly.FromDateTime(DateTime.Today));
    public bool ShowSeries { get; set; } = true;
    public bool ShowMovies { get; set; } = true;

    public ScoreSource ScoreSource { get; set; } = ScoreSource.Tmdb;
    public double MinScore { get; set; } = 0;
    public double MaxScore { get; set; } = 10;
    public bool IncludeUnknownScores { get; set; } = true;

    public string SearchText { get; set; } = "";

    public static DateOnly StartOfWeek(DateOnly date)
    {
        var diff = ((int)date.DayOfWeek + 6) % 7; // Monday start
        return date.AddDays(-diff);
    }
}
```

## 10. Add DTOs

Keep DTOs minimal. You only need the fields used by your app.

Create `Models/TmdbDtos.cs`:

```csharp
using System.Text.Json.Serialization;

namespace PremiereCalendar.Models;

public sealed record TmdbPagedResponse<T>
{
    [JsonPropertyName("page")]
    public int Page { get; init; }

    [JsonPropertyName("total_pages")]
    public int TotalPages { get; init; }

    [JsonPropertyName("results")]
    public List<T> Results { get; init; } = [];
}

public sealed record TmdbTvDiscoverItem
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("first_air_date")]
    public string? FirstAirDate { get; init; }

    [JsonPropertyName("overview")]
    public string? Overview { get; init; }

    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; init; }

    [JsonPropertyName("backdrop_path")]
    public string? BackdropPath { get; init; }

    [JsonPropertyName("original_language")]
    public string? OriginalLanguage { get; init; }

    [JsonPropertyName("origin_country")]
    public string[] OriginCountry { get; init; } = [];

    [JsonPropertyName("vote_average")]
    public double? VoteAverage { get; init; }

    [JsonPropertyName("vote_count")]
    public int? VoteCount { get; init; }
}

public sealed record TmdbMovieDiscoverItem
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("title")]
    public string? Title { get; init; }

    [JsonPropertyName("primary_release_date")]
    public string? PrimaryReleaseDate { get; init; }

    [JsonPropertyName("overview")]
    public string? Overview { get; init; }

    [JsonPropertyName("poster_path")]
    public string? PosterPath { get; init; }

    [JsonPropertyName("backdrop_path")]
    public string? BackdropPath { get; init; }

    [JsonPropertyName("original_language")]
    public string? OriginalLanguage { get; init; }

    [JsonPropertyName("origin_country")]
    public string[] OriginCountry { get; init; } = [];

    [JsonPropertyName("vote_average")]
    public double? VoteAverage { get; init; }

    [JsonPropertyName("vote_count")]
    public int? VoteCount { get; init; }
}

public sealed record TmdbDetailsWithExtras
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("runtime")]
    public int? Runtime { get; init; }

    [JsonPropertyName("videos")]
    public TmdbVideoResponse? Videos { get; init; }

    [JsonPropertyName("external_ids")]
    public TmdbExternalIds? ExternalIds { get; init; }
}

public sealed record TmdbVideoResponse
{
    [JsonPropertyName("results")]
    public List<TmdbVideo> Results { get; init; } = [];
}

public sealed record TmdbVideo
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("key")]
    public string? Key { get; init; }

    [JsonPropertyName("site")]
    public string? Site { get; init; }

    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("official")]
    public bool Official { get; init; }

    [JsonPropertyName("published_at")]
    public DateTimeOffset? PublishedAt { get; init; }
}

public sealed record TmdbExternalIds
{
    [JsonPropertyName("imdb_id")]
    public string? ImdbId { get; init; }
}
```

Create `Models/OmdbDtos.cs`:

```csharp
using System.Text.Json.Serialization;

namespace PremiereCalendar.Models;

public sealed record OmdbResponse
{
    [JsonPropertyName("imdbRating")]
    public string? ImdbRating { get; init; }

    [JsonPropertyName("Ratings")]
    public List<OmdbRating> Ratings { get; init; } = [];

    [JsonPropertyName("Response")]
    public string? Response { get; init; }

    [JsonPropertyName("Error")]
    public string? Error { get; init; }
}

public sealed record OmdbRating
{
    [JsonPropertyName("Source")]
    public string? Source { get; init; }

    [JsonPropertyName("Value")]
    public string? Value { get; init; }
}
```

## 11. Configure services in Program.cs

Edit `Program.cs`:

```csharp
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using PremiereCalendar.Components;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

builder.Services.Configure<TmdbOptions>(builder.Configuration.GetSection("Tmdb"));
builder.Services.Configure<OmdbOptions>(builder.Configuration.GetSection("Omdb"));

builder.Services.AddMemoryCache();

builder.Services.AddHttpClient<TmdbClient>((sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<TmdbOptions>>().Value;

    if (string.IsNullOrWhiteSpace(options.BearerToken))
    {
        throw new InvalidOperationException("Missing TMDb bearer token. Set user-secret Tmdb:BearerToken.");
    }

    client.BaseAddress = new Uri(options.BaseUrl);
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.BearerToken);
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
});

builder.Services.AddHttpClient<OmdbClient>((sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<OmdbOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl);
});

builder.Services.AddScoped<TrailerSelector>();
builder.Services.AddScoped<RatingMapper>();
builder.Services.AddScoped<PremiereService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
```

## 12. Implement the TMDb client

Create `Services/TmdbClient.cs`:

```csharp
using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class TmdbClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly TmdbOptions _options;

    public TmdbClient(HttpClient httpClient, IMemoryCache cache, IOptions<TmdbOptions> options)
    {
        _httpClient = httpClient;
        _cache = cache;
        _options = options.Value;
    }

    public async Task<IReadOnlyList<TmdbTvDiscoverItem>> DiscoverTvAsync(
        DateOnly start,
        DateOnly end,
        string originalLanguage,
        IReadOnlyList<string>? originCountries,
        CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["first_air_date.gte"] = FormatDate(start),
            ["first_air_date.lte"] = FormatDate(end),
            ["with_original_language"] = originalLanguage,
            ["sort_by"] = "first_air_date.asc",
            ["include_adult"] = "false"
        };

        if (originCountries is { Count: > 0 })
        {
            parameters["with_origin_country"] = string.Join('|', originCountries);
        }

        return await GetPagedAsync<TmdbTvDiscoverItem>("discover/tv", parameters, cancellationToken);
    }

    public async Task<IReadOnlyList<TmdbMovieDiscoverItem>> DiscoverMoviesAsync(
        DateOnly start,
        DateOnly end,
        string originalLanguage,
        IReadOnlyList<string>? originCountries,
        CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string?>
        {
            ["primary_release_date.gte"] = FormatDate(start),
            ["primary_release_date.lte"] = FormatDate(end),
            ["with_original_language"] = originalLanguage,
            ["sort_by"] = "primary_release_date.asc",
            ["with_runtime.gte"] = "41",
            ["include_adult"] = "false"
        };

        if (originCountries is { Count: > 0 })
        {
            parameters["with_origin_country"] = string.Join('|', originCountries);
        }

        return await GetPagedAsync<TmdbMovieDiscoverItem>("discover/movie", parameters, cancellationToken);
    }

    public Task<TmdbDetailsWithExtras?> GetTvDetailsWithExtrasAsync(int seriesId, CancellationToken cancellationToken)
    {
        return GetCachedAsync<TmdbDetailsWithExtras>(
            $"tmdb:tv-details:{seriesId}",
            $"tv/{seriesId}?append_to_response=videos,external_ids&language=en-US",
            TimeSpan.FromHours(12),
            cancellationToken);
    }

    public Task<TmdbDetailsWithExtras?> GetMovieDetailsWithExtrasAsync(int movieId, CancellationToken cancellationToken)
    {
        return GetCachedAsync<TmdbDetailsWithExtras>(
            $"tmdb:movie-details:{movieId}",
            $"movie/{movieId}?append_to_response=videos,external_ids&language=en-US",
            TimeSpan.FromHours(12),
            cancellationToken);
    }

    public string? BuildPosterUrl(string? posterPath)
    {
        if (string.IsNullOrWhiteSpace(posterPath))
        {
            return null;
        }

        return $"{_options.ImageBaseUrl.TrimEnd('/')}/{_options.PosterSize}{posterPath}";
    }

    public string? BuildBackdropUrl(string? backdropPath)
    {
        if (string.IsNullOrWhiteSpace(backdropPath))
        {
            return null;
        }

        return $"{_options.ImageBaseUrl.TrimEnd('/')}/{_options.BackdropSize}{backdropPath}";
    }

    private async Task<IReadOnlyList<T>> GetPagedAsync<T>(
        string path,
        Dictionary<string, string?> parameters,
        CancellationToken cancellationToken)
    {
        var firstPage = await GetPageAsync<T>(path, parameters, 1, cancellationToken);
        var results = firstPage.Results.ToList();

        var totalPagesToRead = Math.Min(firstPage.TotalPages, _options.MaxPagesPerQuery);

        for (var page = 2; page <= totalPagesToRead; page++)
        {
            var next = await GetPageAsync<T>(path, parameters, page, cancellationToken);
            results.AddRange(next.Results);
        }

        return results;
    }

    private async Task<TmdbPagedResponse<T>> GetPageAsync<T>(
        string path,
        Dictionary<string, string?> parameters,
        int page,
        CancellationToken cancellationToken)
    {
        parameters["page"] = page.ToString(CultureInfo.InvariantCulture);

        var query = BuildQuery(parameters);
        var relativeUrl = $"{path}?{query}";
        var cacheKey = $"tmdb:{relativeUrl}";

        return await GetCachedAsync<TmdbPagedResponse<T>>(
                   cacheKey,
                   relativeUrl,
                   TimeSpan.FromHours(6),
                   cancellationToken)
               ?? new TmdbPagedResponse<T>();
    }

    private async Task<T?> GetCachedAsync<T>(
        string cacheKey,
        string relativeUrl,
        TimeSpan duration,
        CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(cacheKey, out T? cached))
        {
            return cached;
        }

        using var response = await _httpClient.GetAsync(relativeUrl, cancellationToken);

        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new InvalidOperationException("TMDb returned 429 Too Many Requests. Increase cache duration or lower MaxPagesPerQuery.");
        }

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var value = await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);

        if (value is not null)
        {
            _cache.Set(cacheKey, value, duration);
        }

        return value;
    }

    private static string BuildQuery(Dictionary<string, string?> parameters)
    {
        return string.Join("&",
            parameters
                .Where(x => !string.IsNullOrWhiteSpace(x.Value))
                .Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value!)}"));
    }

    private static string FormatDate(DateOnly date)
    {
        return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
```

## 13. Implement trailer selection

Create `Services/TrailerSelector.cs`:

```csharp
using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public sealed class TrailerSelector
{
    public string? SelectTrailerUrl(TmdbVideoResponse? response)
    {
        if (response?.Results is not { Count: > 0 })
        {
            return null;
        }

        var video = response.Results
            .Where(v => string.Equals(v.Site, "YouTube", StringComparison.OrdinalIgnoreCase))
            .Where(v => !string.IsNullOrWhiteSpace(v.Key))
            .Where(v =>
                string.Equals(v.Type, "Trailer", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(v.Type, "Teaser", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(v => string.Equals(v.Type, "Trailer", StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(v => v.Official)
            .ThenByDescending(v => v.PublishedAt)
            .FirstOrDefault();

        return video?.Key is null
            ? null
            : $"https://www.youtube.com/watch?v={Uri.EscapeDataString(video.Key)}";
    }
}
```

This deliberately only creates a trailer link when TMDb has an actual YouTube video key. It does not invent links.

## 14. Optional OMDb enrichment

Create `Services/OmdbClient.cs`:

```csharp
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public sealed class OmdbClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;
    private readonly OmdbOptions _options;

    public OmdbClient(HttpClient httpClient, IMemoryCache cache, IOptions<OmdbOptions> options)
    {
        _httpClient = httpClient;
        _cache = cache;
        _options = options.Value;
    }

    public async Task<OmdbResponse?> GetByImdbIdAsync(string? imdbId, CancellationToken cancellationToken)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.ApiKey) || string.IsNullOrWhiteSpace(imdbId))
        {
            return null;
        }

        var cacheKey = $"omdb:{imdbId}";

        if (_cache.TryGetValue(cacheKey, out OmdbResponse? cached))
        {
            return cached;
        }

        var url = $"?apikey={Uri.EscapeDataString(_options.ApiKey)}&i={Uri.EscapeDataString(imdbId)}&plot=short&r=json";

        using var response = await _httpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var value = await JsonSerializer.DeserializeAsync<OmdbResponse>(stream, JsonOptions, cancellationToken);

        if (value is not null)
        {
            _cache.Set(cacheKey, value, TimeSpan.FromDays(7));
        }

        return value;
    }
}
```

Create `Services/RatingMapper.cs`:

```csharp
using System.Globalization;
using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public sealed class RatingMapper
{
    public double? ParseImdbScore(OmdbResponse? response)
    {
        if (response?.ImdbRating is null || response.ImdbRating.Equals("N/A", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return double.TryParse(response.ImdbRating, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    public int? ParseRottenTomatoesScore(OmdbResponse? response)
    {
        var rt = response?.Ratings.FirstOrDefault(x =>
            string.Equals(x.Source, "Rotten Tomatoes", StringComparison.OrdinalIgnoreCase));

        if (rt?.Value is null || !rt.Value.EndsWith('%'))
        {
            return null;
        }

        var numeric = rt.Value.TrimEnd('%');

        return int.TryParse(numeric, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }
}
```

Enable OMDb in `appsettings.Development.json` or user-secrets:

```bash
dotnet user-secrets set "Omdb:Enabled" "true"
dotnet user-secrets set "Omdb:ApiKey" "YOUR_OMDB_KEY"
```

If you do not configure OMDb, the app should still work with TMDb scores.

## 15. Implement PremiereService

Create `Services/PremiereService.cs`:

```csharp
using PremiereCalendar.Models;
using PremiereCalendar.Options;
using Microsoft.Extensions.Options;

namespace PremiereCalendar.Services;

public sealed class PremiereService
{
    private readonly TmdbClient _tmdbClient;
    private readonly OmdbClient _omdbClient;
    private readonly TrailerSelector _trailerSelector;
    private readonly RatingMapper _ratingMapper;
    private readonly TmdbOptions _options;

    public PremiereService(
        TmdbClient tmdbClient,
        OmdbClient omdbClient,
        TrailerSelector trailerSelector,
        RatingMapper ratingMapper,
        IOptions<TmdbOptions> options)
    {
        _tmdbClient = tmdbClient;
        _omdbClient = omdbClient;
        _trailerSelector = trailerSelector;
        _ratingMapper = ratingMapper;
        _options = options.Value;
    }

    public async Task<IReadOnlyList<PremiereItem>> GetPremieresAsync(
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken)
    {
        var seriesTasks = new[]
        {
            GetEnglishSeriesAsync(start, end, cancellationToken),
            GetDutchSeriesAsync(start, end, cancellationToken)
        };

        var movieTasks = new[]
        {
            GetEnglishMoviesAsync(start, end, cancellationToken),
            GetDutchMoviesAsync(start, end, cancellationToken)
        };

        var allSeries = (await Task.WhenAll(seriesTasks)).SelectMany(x => x);
        var allMovies = (await Task.WhenAll(movieTasks)).SelectMany(x => x);

        return allSeries
            .Concat(allMovies)
            .GroupBy(x => new { x.MediaType, x.TmdbId })
            .Select(g => g.First())
            .OrderBy(x => x.PremiereDate)
            .ThenBy(x => x.Title)
            .ToList();
    }

    private async Task<IReadOnlyList<PremiereItem>> GetEnglishSeriesAsync(
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken)
    {
        var raw = await _tmdbClient.DiscoverTvAsync(
            start,
            end,
            "en",
            _options.EnglishOriginCountries,
            cancellationToken);

        return await EnrichSeriesAsync(raw, cancellationToken);
    }

    private async Task<IReadOnlyList<PremiereItem>> GetDutchSeriesAsync(
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken)
    {
        var countries = _options.IncludeDutchOriginRestriction
            ? _options.DutchOriginCountries
            : [];

        var raw = await _tmdbClient.DiscoverTvAsync(
            start,
            end,
            "nl",
            countries,
            cancellationToken);

        return await EnrichSeriesAsync(raw, cancellationToken);
    }

    private async Task<IReadOnlyList<PremiereItem>> GetEnglishMoviesAsync(
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken)
    {
        var raw = await _tmdbClient.DiscoverMoviesAsync(
            start,
            end,
            "en",
            _options.EnglishOriginCountries,
            cancellationToken);

        return await EnrichMoviesAsync(raw, cancellationToken);
    }

    private async Task<IReadOnlyList<PremiereItem>> GetDutchMoviesAsync(
        DateOnly start,
        DateOnly end,
        CancellationToken cancellationToken)
    {
        var countries = _options.IncludeDutchOriginRestriction
            ? _options.DutchOriginCountries
            : [];

        var raw = await _tmdbClient.DiscoverMoviesAsync(
            start,
            end,
            "nl",
            countries,
            cancellationToken);

        return await EnrichMoviesAsync(raw, cancellationToken);
    }

    private async Task<IReadOnlyList<PremiereItem>> EnrichSeriesAsync(
        IReadOnlyList<TmdbTvDiscoverItem> raw,
        CancellationToken cancellationToken)
    {
        var items = new List<PremiereItem>();

        foreach (var item in raw)
        {
            if (!DateOnly.TryParse(item.FirstAirDate, out var date))
            {
                continue;
            }

            var details = await _tmdbClient.GetTvDetailsWithExtrasAsync(item.Id, cancellationToken);
            var trailerUrl = _trailerSelector.SelectTrailerUrl(details?.Videos);

            var imdbId = details?.ExternalIds?.ImdbId;
            var omdb = await _omdbClient.GetByImdbIdAsync(imdbId, cancellationToken);

            items.Add(new PremiereItem
            {
                MediaType = PremiereMediaType.Series,
                TmdbId = item.Id,
                Title = item.Name ?? $"TV #{item.Id}",
                PremiereDate = date,
                Overview = item.Overview,
                PosterUrl = _tmdbClient.BuildPosterUrl(item.PosterPath),
                BackdropUrl = _tmdbClient.BuildBackdropUrl(item.BackdropPath),
                TrailerUrl = trailerUrl,
                TmdbUrl = $"https://www.themoviedb.org/tv/{item.Id}",
                ImdbUrl = string.IsNullOrWhiteSpace(imdbId) ? null : $"https://www.imdb.com/title/{imdbId}/",
                OriginalLanguage = item.OriginalLanguage ?? "",
                OriginCountries = item.OriginCountry,
                TmdbScore = item.VoteAverage,
                TmdbVoteCount = item.VoteCount,
                ImdbScore = _ratingMapper.ParseImdbScore(omdb),
                RottenTomatoesScore = _ratingMapper.ParseRottenTomatoesScore(omdb)
            });
        }

        return items;
    }

    private async Task<IReadOnlyList<PremiereItem>> EnrichMoviesAsync(
        IReadOnlyList<TmdbMovieDiscoverItem> raw,
        CancellationToken cancellationToken)
    {
        var items = new List<PremiereItem>();

        foreach (var item in raw)
        {
            if (!DateOnly.TryParse(item.PrimaryReleaseDate, out var date))
            {
                continue;
            }

            var details = await _tmdbClient.GetMovieDetailsWithExtrasAsync(item.Id, cancellationToken);
            var trailerUrl = _trailerSelector.SelectTrailerUrl(details?.Videos);

            var imdbId = details?.ExternalIds?.ImdbId;
            var omdb = await _omdbClient.GetByImdbIdAsync(imdbId, cancellationToken);

            items.Add(new PremiereItem
            {
                MediaType = PremiereMediaType.Movie,
                TmdbId = item.Id,
                Title = item.Title ?? $"Movie #{item.Id}",
                PremiereDate = date,
                Overview = item.Overview,
                PosterUrl = _tmdbClient.BuildPosterUrl(item.PosterPath),
                BackdropUrl = _tmdbClient.BuildBackdropUrl(item.BackdropPath),
                TrailerUrl = trailerUrl,
                TmdbUrl = $"https://www.themoviedb.org/movie/{item.Id}",
                ImdbUrl = string.IsNullOrWhiteSpace(imdbId) ? null : $"https://www.imdb.com/title/{imdbId}/",
                OriginalLanguage = item.OriginalLanguage ?? "",
                OriginCountries = item.OriginCountry,
                RuntimeMinutes = details?.Runtime,
                TmdbScore = item.VoteAverage,
                TmdbVoteCount = item.VoteCount,
                ImdbScore = _ratingMapper.ParseImdbScore(omdb),
                RottenTomatoesScore = _ratingMapper.ParseRottenTomatoesScore(omdb)
            });
        }

        return items;
    }
}
```

## 16. Build the calendar page

Replace or add `Components/Pages/Calendar.razor`:

```razor
@page "/"
@rendermode InteractiveServer
@using PremiereCalendar.Models
@inject PremiereCalendar.Services.PremiereService PremiereService

<PageTitle>Premiere Calendar</PageTitle>

<h1>Premiere Calendar</h1>

<div class="toolbar">
    <button @onclick="PreviousWeek">Previous week</button>
    <button @onclick="CurrentWeek">This week</button>
    <button @onclick="NextWeek">Next week</button>

    <label>
        <input type="checkbox" @bind="_filters.ShowSeries" />
        Series
    </label>

    <label>
        <input type="checkbox" @bind="_filters.ShowMovies" />
        Movies
    </label>

    <label>
        Score:
        <select @bind="_filters.ScoreSource">
            <option value="@ScoreSource.Tmdb">TMDb</option>
            <option value="@ScoreSource.Imdb">IMDb</option>
            <option value="@ScoreSource.RottenTomatoes">Rotten Tomatoes</option>
        </select>
    </label>

    <label>
        Min:
        <input type="number" min="0" max="10" step="0.1" @bind="_filters.MinScore" />
    </label>

    <label>
        Max:
        <input type="number" min="0" max="10" step="0.1" @bind="_filters.MaxScore" />
    </label>

    <label>
        <input type="checkbox" @bind="_filters.IncludeUnknownScores" />
        Include unknown scores
    </label>

    <input placeholder="Search title..." @bind="_filters.SearchText" @bind:event="oninput" />

    <button @onclick="LoadAsync">Refresh</button>
</div>

@if (_isLoading)
{
    <p>Loading premieres...</p>
}
else if (!string.IsNullOrWhiteSpace(_error))
{
    <p class="error">@_error</p>
}
else
{
    <div class="week-header">
        <h2>@_filters.WeekStart.ToString("dd MMM yyyy") - @_filters.WeekStart.AddDays(6).ToString("dd MMM yyyy")</h2>
    </div>

    <div class="calendar-grid">
        @foreach (var day in DaysOfWeek())
        {
            var dayItems = FilteredItems()
                .Where(x => x.PremiereDate == day)
                .OrderBy(x => x.MediaType)
                .ThenByDescending(x => ScoreFor(x))
                .ThenBy(x => x.Title)
                .ToList();

            <section class="calendar-day">
                <h3>@day.ToString("ddd dd/MM")</h3>

                @if (dayItems.Count == 0)
                {
                    <div class="empty-day">No premieres</div>
                }
                else
                {
                    @foreach (var item in dayItems)
                    {
                        <article class="premiere-card">
                            <div class="card-main">
                                @if (!string.IsNullOrWhiteSpace(item.PosterUrl))
                                {
                                    <img src="@item.PosterUrl" alt="@item.Title poster" loading="lazy" />
                                }

                                <div class="card-content">
                                    <div class="card-type">@item.MediaType</div>
                                    <h4>@item.Title</h4>

                                    <div class="meta">
                                        <span>@item.OriginalLanguage.ToUpperInvariant()</span>
                                        @if (item.OriginCountries.Length > 0)
                                        {
                                            <span>@string.Join("/", item.OriginCountries)</span>
                                        }
                                        @if (item.RuntimeMinutes is not null)
                                        {
                                            <span>@item.RuntimeMinutes min</span>
                                        }
                                    </div>

                                    <div class="scores">
                                        <span>TMDb: @FormatScore(item.TmdbScore)</span>
                                        <span>IMDb: @FormatScore(item.ImdbScore)</span>
                                        <span>RT: @(item.RottenTomatoesScore is null ? "n/a" : $"{item.RottenTomatoesScore}%")</span>
                                    </div>

                                    @if (!string.IsNullOrWhiteSpace(item.Overview))
                                    {
                                        <p>@item.Overview</p>
                                    }

                                    <div class="links">
                                        @if (!string.IsNullOrWhiteSpace(item.TrailerUrl))
                                        {
                                            <a href="@item.TrailerUrl" target="_blank" rel="noopener noreferrer">Trailer</a>
                                        }

                                        @if (!string.IsNullOrWhiteSpace(item.TmdbUrl))
                                        {
                                            <a href="@item.TmdbUrl" target="_blank" rel="noopener noreferrer">TMDb</a>
                                        }

                                        @if (!string.IsNullOrWhiteSpace(item.ImdbUrl))
                                        {
                                            <a href="@item.ImdbUrl" target="_blank" rel="noopener noreferrer">IMDb</a>
                                        }
                                    </div>
                                </div>
                            </div>
                        </article>
                    }
                }
            </section>
        }
    </div>
}

@code {
    private CalendarFilters _filters = new();
    private IReadOnlyList<PremiereItem> _items = [];
    private bool _isLoading;
    private string? _error;

    protected override async Task OnInitializedAsync()
    {
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _isLoading = true;
        _error = null;

        try
        {
            var start = _filters.WeekStart;
            var end = start.AddDays(6);

            _items = await PremiereService.GetPremieresAsync(start, end, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _error = ex.Message;
        }
        finally
        {
            _isLoading = false;
        }
    }

    private IEnumerable<DateOnly> DaysOfWeek()
    {
        for (var i = 0; i < 7; i++)
        {
            yield return _filters.WeekStart.AddDays(i);
        }
    }

    private IEnumerable<PremiereItem> FilteredItems()
    {
        var query = _items.AsEnumerable();

        if (!_filters.ShowSeries)
        {
            query = query.Where(x => x.MediaType != PremiereMediaType.Series);
        }

        if (!_filters.ShowMovies)
        {
            query = query.Where(x => x.MediaType != PremiereMediaType.Movie);
        }

        if (!string.IsNullOrWhiteSpace(_filters.SearchText))
        {
            query = query.Where(x => x.Title.Contains(_filters.SearchText, StringComparison.OrdinalIgnoreCase));
        }

        query = query.Where(PassesScoreFilter);

        return query;
    }

    private bool PassesScoreFilter(PremiereItem item)
    {
        var score = ScoreFor(item);

        if (score is null)
        {
            return _filters.IncludeUnknownScores;
        }

        var min = _filters.MinScore;
        var max = _filters.ScoreSource == ScoreSource.RottenTomatoes
            ? _filters.MaxScore * 10
            : _filters.MaxScore;

        var normalizedMin = _filters.ScoreSource == ScoreSource.RottenTomatoes
            ? min * 10
            : min;

        return score >= normalizedMin && score <= max;
    }

    private double? ScoreFor(PremiereItem item)
    {
        return _filters.ScoreSource switch
        {
            ScoreSource.Tmdb => item.TmdbScore,
            ScoreSource.Imdb => item.ImdbScore,
            ScoreSource.RottenTomatoes => item.RottenTomatoesScore,
            _ => item.TmdbScore
        };
    }

    private static string FormatScore(double? score)
    {
        return score is null ? "n/a" : score.Value.ToString("0.0");
    }

    private async Task PreviousWeek()
    {
        _filters.WeekStart = _filters.WeekStart.AddDays(-7);
        await LoadAsync();
    }

    private async Task CurrentWeek()
    {
        _filters.WeekStart = CalendarFilters.StartOfWeek(DateOnly.FromDateTime(DateTime.Today));
        await LoadAsync();
    }

    private async Task NextWeek()
    {
        _filters.WeekStart = _filters.WeekStart.AddDays(7);
        await LoadAsync();
    }
}
```

## 17. Add basic CSS

Add this to `wwwroot/app.css` or the app stylesheet used by your template:

```css
.toolbar {
    display: flex;
    flex-wrap: wrap;
    gap: 0.75rem;
    align-items: center;
    margin-bottom: 1rem;
}

.toolbar label {
    display: inline-flex;
    align-items: center;
    gap: 0.35rem;
}

.toolbar input[type="text"],
.toolbar input:not([type]),
.toolbar input[type="number"],
.toolbar select {
    padding: 0.35rem;
}

.week-header {
    margin: 1rem 0;
}

.calendar-grid {
    display: grid;
    grid-template-columns: repeat(7, minmax(220px, 1fr));
    gap: 0.75rem;
    align-items: start;
}

.calendar-day {
    border: 1px solid #333;
    border-radius: 0.75rem;
    padding: 0.75rem;
    min-height: 20rem;
    background: rgba(255, 255, 255, 0.03);
}

.calendar-day h3 {
    margin-top: 0;
    font-size: 1rem;
}

.empty-day {
    opacity: 0.65;
    font-size: 0.9rem;
}

.premiere-card {
    border: 1px solid #444;
    border-radius: 0.75rem;
    margin-bottom: 0.75rem;
    overflow: hidden;
    background: rgba(255, 255, 255, 0.04);
}

.card-main {
    display: grid;
    grid-template-columns: 72px 1fr;
    gap: 0.75rem;
    padding: 0.75rem;
}

.card-main img {
    width: 72px;
    border-radius: 0.4rem;
}

.card-content h4 {
    margin: 0.15rem 0 0.35rem 0;
}

.card-type {
    font-size: 0.75rem;
    opacity: 0.7;
    text-transform: uppercase;
}

.meta,
.scores,
.links {
    display: flex;
    flex-wrap: wrap;
    gap: 0.45rem;
    font-size: 0.8rem;
    margin: 0.35rem 0;
}

.card-content p {
    font-size: 0.85rem;
    line-height: 1.35;
}

.error {
    color: #ff6b6b;
}

@media (max-width: 1600px) {
    .calendar-grid {
        grid-template-columns: repeat(4, minmax(220px, 1fr));
    }
}

@media (max-width: 1000px) {
    .calendar-grid {
        grid-template-columns: repeat(2, minmax(220px, 1fr));
    }
}

@media (max-width: 620px) {
    .calendar-grid {
        grid-template-columns: 1fr;
    }
}
```

## 18. Important scoring behavior

### TMDb score

TMDb score is available directly from Discover responses:

- `vote_average`
- `vote_count`

Use it as the default score because it is reliable and cheap.

Recommended UI behavior:

- show TMDb score as `x.x / 10`
- show vote count
- optionally suppress very low-confidence scores by hiding items with `vote_count < 5`

### IMDb score

Use optional OMDb enrichment:

1. Get `external_ids.imdb_id` from TMDb details.
2. Query OMDb with `i={imdbId}`.
3. Parse `imdbRating`.

### Rotten Tomatoes score

Use optional OMDb enrichment:

1. Query OMDb with IMDb ID.
2. Inspect `Ratings`.
3. Find `Source == "Rotten Tomatoes"`.
4. Parse the percentage.

Expect missing values. Do not treat missing IMDb/RT as zero.

Recommended UI behavior:

- checkbox: `Include unknown scores`
- if unchecked, hide items where the selected score source is missing
- if checked, show them with `n/a`

## 19. Date navigation behavior

Start with a weekly view:

- Monday to Sunday
- `Previous week`
- `This week`
- `Next week`
- refresh button

Later improvements:

- 2-week view
- 4-week list view
- month view
- export to `.ics`
- provider filters

## 20. API call strategy

For one week:

- 2 TV discover calls:
  - English US/GB/AU
  - Dutch NL/BE
- 2 movie discover calls:
  - English US/GB/AU
  - Dutch NL/BE
- detail calls for each returned item:
  - append `videos,external_ids`
- optional OMDb calls for each item with an IMDb ID

Use caching aggressively:

| Data | Cache duration |
|---|---:|
| Discover results | 6 hours |
| TMDb details/videos/external IDs | 12 hours |
| OMDb ratings | 7 days |
| TMDb configuration/images base URL | 30 days |

For a local app this is enough. Do not bulk-scrape months of data every page refresh.

## 21. Handling trailers correctly

Use these selection rules:

1. Only accept videos where `site == "YouTube"`.
2. Only accept videos with a non-empty `key`.
3. Prefer `type == "Trailer"`.
4. Fall back to `type == "Teaser"`.
5. Prefer `official == true`.
6. Build the URL as:

```csharp
$"https://www.youtube.com/watch?v={key}"
```

If no matching video exists, show:

```text
No trailer
```

Do not link to YouTube search results as if they were real trailers. That reintroduces noise.

## 22. Known limitations

### 22.1 Series premiere date quality

TMDb `first_air_date` is good enough for a premiere monitor, but not perfect. Some international/local shows can be missing or have incomplete dates.

### 22.2 Dutch/Flemish coverage

Large Dutch/Flemish titles are usually present. Smaller VRT/GoPlay/Videoland-style content can be incomplete.

### 22.3 Movie release date meaning

`primary_release_date` is the closest simple match for “first-release date”. It is not the same as Belgian theatrical release, streaming release, or VOD release.

If you later want Belgium-specific release availability, add:

```http
watch_region=BE
with_watch_providers={providerIds}
with_watch_monetization_types=flatrate|rent|buy|ads|free
```

But this changes the semantics.

### 22.4 IMDb and Rotten Tomatoes

TMDb does not provide IMDb/Rotten Tomatoes ratings directly.

OMDb enrichment is optional and not guaranteed to return every rating. Rotten Tomatoes values can be missing even when IMDb exists.

### 22.5 Rotten Tomatoes range normalization

TMDb and IMDb are `/10`.

Rotten Tomatoes is `%`.

In the UI, either:

- show RT filter as `0–100`, or
- keep a unified `0–10` slider and multiply by 10 internally for RT.

The provided sample uses a unified `0–10` input and converts RT internally.

## 23. Recommended MVP checklist

Implement in this order:

1. Create Blazor Web App.
2. Create unit and integration test projects.
3. Add TMDb options and user-secret.
4. Implement `TmdbClient` query-building tests before implementing live API calls.
5. Implement `TmdbClient`.
6. Implement TV Discover query only.
7. Add unit tests proving TV uses `first_air_date` and not `air_date`.
8. Render weekly calendar with TV premieres.
9. Add component tests for seven-day rendering and empty-day behavior.
10. Add movie Discover query.
11. Add unit tests proving movies use `primary_release_date`, `with_runtime.gte=41`, and no release-type filter.
12. Add detail enrichment with `videos,external_ids`.
13. Add trailer links and `TrailerSelector` tests.
14. Add score filter for TMDb score and score-filter tests.
15. Add optional OMDb enrichment.
16. Add IMDb and Rotten Tomatoes score filters with missing-rating tests.
17. Add caching and error handling.
18. Add integration tests with canned TMDb/OMDb JSON fixtures.
19. Add attribution/about page.

## 24. About / attribution page

Add a simple page:

```razor
@page "/about"

<h1>About</h1>

<p>This product uses the TMDB API but is not endorsed or certified by TMDB.</p>

<p>Movie and TV metadata are provided by TMDB. Optional IMDb and Rotten Tomatoes rating enrichment is provided through OMDb when configured.</p>
```

Do this even for a local-only app. It keeps the design clean if you later share it internally or deploy it.

## 25. Testing checklist


Testing is not optional for this app. The app should stay stable while you iterate on filters, API integrations, UI layout and score sources.

Minimum expectations:

- every new filtering rule gets unit tests;
- every new external source gets integration tests with canned JSON;
- every API client must handle missing/partial data without crashing the page;
- every score source must treat unknown values as `n/a`, never as `0`;
- every feature branch should pass `dotnet test` before merging or continuing to the next feature.

### 25.1 Unit tests to add first

Create tests for:

| Class / feature | Test cases |
|---|---|
| `CalendarFilters.StartOfWeek` | Monday start, Sunday handling, month/year boundary. |
| `TrailerSelector` | chooses official trailer first, falls back to teaser, ignores non-YouTube videos, returns `null` for empty data. |
| `RatingMapper` | parses IMDb `7.4`, handles `N/A`, parses Rotten Tomatoes `83%`, handles missing RT. |
| `PremiereService` de-duplication | same TMDb ID returned by multiple queries appears once. |
| Series query logic | uses `first_air_date.gte/lte`, never `air_date.gte/lte`. |
| Movie query logic | uses `primary_release_date.gte/lte`, `with_runtime.gte=41`, no `with_release_type`. |
| Score filtering | TMDb/IMDb use `0–10`, Rotten Tomatoes converts `0–10` UI range to `0–100`, unknown scores obey `IncludeUnknownScores`. |

### 25.2 Integration tests to protect external-source behavior

Use fake HTTP responses, not live APIs.

Integration tests should verify:

- `TmdbClient` sends the expected query parameters for series and movies.
- paged TMDb responses are merged correctly.
- details enrichment reads `videos` and `external_ids` correctly.
- OMDb enrichment is skipped when disabled.
- OMDb enrichment does not break the app when OMDb returns `Response=false` or missing ratings.
- the full `PremiereService.GetPremieresAsync` flow returns a stable list from fixture JSON.
- API failures produce controlled error messages instead of unhandled exceptions in the UI.

Use fixture files such as:

```text
tests/Fixtures/tmdb/discover-tv-en-premieres.json
tests/Fixtures/tmdb/discover-tv-nl-premieres.json
tests/Fixtures/tmdb/discover-movie-en.json
tests/Fixtures/tmdb/movie-details-with-videos.json
tests/Fixtures/omdb/by-imdb-id-success.json
tests/Fixtures/omdb/by-imdb-id-missing-rt.json
```

### 25.3 Component tests for the weekly calendar

With bUnit, verify that:

- the calendar renders seven day columns;
- empty days show `No premieres`;
- premiere cards show title, media type, language, country, score and links;
- the trailer link is absent when `TrailerUrl` is null;
- score source switching changes filtering behavior;
- search text filters visible cards;
- previous/current/next week buttons call the expected load behavior.

### 25.4 Manual smoke tests

Keep these manual because they depend on live third-party data:

- live TMDb token works;
- this week and next week load without exceptions;
- trailer links open real YouTube videos;
- OMDb enrichment works when enabled;
- the app behaves acceptably when OMDb is disabled or rate-limited.

### Series

Check that results are only new series premieres:

- Date is based on `first_air_date`.
- No normal weekly episodes appear.
- English results are only origin country `US`, `GB`, or `AU`.
- Dutch results are `nl`, optionally `NL` or `BE`.

### Movies

Check that results:

- use `primary_release_date`.
- include English US/GB/AU and Dutch items.
- exclude short films through `with_runtime.gte=41`.
- show runtime after detail enrichment.
- do not apply release type filtering.

### Trailers

Check that:

- trailer button only appears when TMDb returned a YouTube video.
- link opens YouTube directly.
- no fake YouTube search links are generated.

### Scores

Check that:

- TMDb score filter works without OMDb.
- IMDb and Rotten Tomatoes filters work only when OMDb is enabled.
- unknown scores are either included or excluded depending on checkbox.
- missing scores are shown as `n/a`, not `0`.

## 26. Future useful extensions

### Provider filters

Add TMDb watch provider filters:

```http
watch_region=BE
with_watch_providers={providerIds}
with_watch_monetization_types=flatrate
```

This can make a Netflix/Prime/Disney+/HBO/VRT MAX view.

### ICS export

Generate a simple `.ics` endpoint:

```text
GET /calendar.ics
```

Each item becomes an all-day event.

### Background refresh

For local use, a manual refresh is enough.

If you later want scheduled refresh:

- use a hosted service
- refresh the next 6–8 weeks once per day
- store normalized results in SQLite

### SQLite persistence

Only add SQLite if you want:

- faster startup
- historical archive
- manual hidden/ignored titles
- local notes
- stable results if TMDb changes metadata

### Ignore list

Add a local `IgnoredTitles` table or JSON file so you can hide content you never want to see again.


### TVmaze premiere verification

If TMDb misses or misdates too many TV premieres, add a TVmaze enrichment job:

- query TVmaze schedules for relevant countries and web channels;
- keep only episodes where `season == 1` and `number == 1`;
- match them back to TMDb items by title/date/external IDs where possible;
- mark the item as `Verified by TVmaze` instead of replacing the TMDb result blindly.

This is useful for confidence, but it adds matching complexity and must be covered by integration tests with fixtures.

### JustWatch / Watchmode availability enrichment

If you later want the calendar to answer “where can I legally watch this in Belgium?”, add a separate availability panel per card.

Do not make availability a hard filter in the first version. Availability data changes often and can hide relevant premieres. Prefer this UI model:

```text
Premiere date: TMDb
Availability: TMDb watch providers / JustWatch / Watchmode, if configured
```

### IMDb dataset ingestion

If OMDb ratings are too incomplete, consider official IMDb non-commercial datasets for a local-only personal app. This adds a daily TSV ingestion pipeline and license obligations, so keep it separate from the MVP.

## 27. Reference links

- [.NET install on Windows](https://learn.microsoft.com/en-us/dotnet/core/install/windows)
- [Blazor Web App overview](https://learn.microsoft.com/en-us/aspnet/core/blazor/?view=aspnetcore-10.0)
- [Blazor project structure](https://learn.microsoft.com/en-us/aspnet/core/blazor/project-structure?view=aspnetcore-10.0)
- [`dotnet new` templates](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-new-sdk-templates)
- [IHttpClientFactory](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/http-requests?view=aspnetcore-10.0)
- [ASP.NET Core memory caching](https://learn.microsoft.com/en-us/aspnet/core/performance/caching/memory?view=aspnetcore-10.0)
- [TMDb Discover TV](https://developer.themoviedb.org/reference/discover-tv)
- [TMDb Discover Movie](https://developer.themoviedb.org/reference/discover-movie)
- [TMDb TV videos](https://developer.themoviedb.org/reference/tv-series-videos)
- [TMDb Movie videos](https://developer.themoviedb.org/reference/movie-videos)
- [TMDb TV external IDs](https://developer.themoviedb.org/reference/tv-series-external-ids)
- [TMDb Movie external IDs](https://developer.themoviedb.org/reference/movie-external-ids)
- [TMDb append to response](https://developer.themoviedb.org/docs/append-to-response)
- [TMDb image basics](https://developer.themoviedb.org/docs/image-basics)
- [TMDb rate limiting](https://developer.themoviedb.org/docs/rate-limiting)
- [TMDb attribution FAQ](https://developer.themoviedb.org/docs/faq)
- [OMDb API](https://www.omdbapi.com/)
- [OMDb API key](https://www.omdbapi.com/apikey.aspx)
- [TVmaze API](https://www.tvmaze.com/api)
- [JustWatch Partner Integrations](https://apis.justwatch.com/docs/content_partner/)
- [JustWatch Partner Widget](https://apis.justwatch.com/docs/widget/)
- [Watchmode API docs](https://api.watchmode.com/docs)
- [TheTVDB API and Data Licensing](https://www.thetvdb.com/api-information)
- [TheTVDB API v4 documentation](https://thetvdb.github.io/v4-api/)
- [IMDb Developer](https://developer.imdb.com/)
- [IMDb Non-Commercial Datasets](https://developer.imdb.com/non-commercial-datasets/)
- [Testing in .NET](https://learn.microsoft.com/en-us/dotnet/core/testing/)
- [Unit testing C# with xUnit](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-csharp-with-xunit)
- [ASP.NET Core integration tests](https://learn.microsoft.com/en-us/aspnet/core/test/integration-tests?view=aspnetcore-10.0)
- [Blazor component testing](https://learn.microsoft.com/en-us/aspnet/core/blazor/test?view=aspnetcore-10.0)
- [bUnit](https://bunit.dev/)
