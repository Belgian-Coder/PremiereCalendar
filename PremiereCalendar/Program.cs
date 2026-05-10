using System.Globalization;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.Extensions.Options;
using PremiereCalendar.Components;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "PremiereCalendar";
});
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "http://0.0.0.0:5298");

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options =>
    {
        options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(10);
        options.DisconnectedCircuitMaxRetained = 200;
        options.JSInteropDefaultCallTimeout = TimeSpan.FromSeconds(60);
        options.DetailedErrors = builder.Environment.IsDevelopment();
    })
    .AddHubOptions(options =>
    {
        options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
        options.HandshakeTimeout = TimeSpan.FromSeconds(30);
        options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        options.MaximumReceiveMessageSize = 256 * 1024;
    });
builder.Services.Configure<CircuitOptions>(options =>
{
    options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(10);
    options.DisconnectedCircuitMaxRetained = 200;
    options.JSInteropDefaultCallTimeout = TimeSpan.FromSeconds(60);
    options.DetailedErrors = builder.Environment.IsDevelopment();
});

builder.Services.Configure<TmdbOptions>(builder.Configuration.GetSection("Tmdb"));
builder.Services.Configure<OmdbOptions>(builder.Configuration.GetSection("Omdb"));
builder.Services.Configure<TvmazeOptions>(builder.Configuration.GetSection("Tvmaze"));
builder.Services.Configure<FanartOptions>(builder.Configuration.GetSection("Fanart"));
builder.Services.Configure<TraktOptions>(builder.Configuration.GetSection("Trakt"));
builder.Services.Configure<TheTvdbOptions>(builder.Configuration.GetSection("TheTvdb"));
builder.Services.Configure<WikimediaOptions>(builder.Configuration.GetSection("Wikimedia"));
builder.Services.Configure<WatchmodeOptions>(builder.Configuration.GetSection("Watchmode"));
builder.Services.Configure<SimklOptions>(builder.Configuration.GetSection("Simkl"));
builder.Services.Configure<CalendarCacheOptions>(builder.Configuration.GetSection("CalendarCache"));
builder.Services.Configure<ImageCacheOptions>(builder.Configuration.GetSection("ImageCache"));
builder.Services.Configure<AppDatabaseOptions>(builder.Configuration.GetSection("AppDatabase"));
builder.Services.Configure<CalendarWarmupOptions>(builder.Configuration.GetSection("CalendarWarmup"));
builder.Services.Configure<CalendarLoadOptions>(builder.Configuration.GetSection("CalendarLoad"));
builder.Services.Configure<CacheMaintenanceOptions>(builder.Configuration.GetSection("CacheMaintenance"));
builder.Services.Configure<ImdbDatasetOptions>(builder.Configuration.GetSection("ImdbDataset"));
builder.Services.Configure<ProviderDeltaSyncOptions>(builder.Configuration.GetSection("ProviderDeltaSync"));

builder.Services.AddMemoryCache();
builder.Services.AddResponseCompression(options =>
{
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(
    [
        "application/manifest+json",
        "image/svg+xml"
    ]);
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddHealthChecks();
builder.Services.AddSingleton<CircuitHandler, CalendarCircuitDiagnostics>();

builder.Services.AddHttpClient<ITmdbClient, TmdbClient>((sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<TmdbOptions>>().Value;

    client.BaseAddress = new Uri(options.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.RequestTimeoutSeconds, 5, 120));
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

    if (!string.IsNullOrWhiteSpace(options.BearerToken))
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.BearerToken);
    }
});

builder.Services.AddHttpClient<IOmdbClient, OmdbClient>((sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<OmdbOptions>>().Value;

    client.BaseAddress = new Uri(options.BaseUrl);
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
});

builder.Services.AddHttpClient<IImdbDatasetImporter, ImdbDatasetImporter>((sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<ImdbDatasetOptions>>().Value;

    client.BaseAddress = new Uri("https://datasets.imdbws.com/");
    client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.RequestTimeoutSeconds, 30, 600));
    client.DefaultRequestHeaders.UserAgent.ParseAdd("PremiereCalendar/1.0 (+https://github.com/Belgian-Coder/PremiereCalendar)");
});

builder.Services.AddHttpClient<ITvmazeClient, TvmazeClient>((sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<TvmazeOptions>>().Value;

    client.BaseAddress = new Uri(options.BaseUrl);
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    client.DefaultRequestHeaders.UserAgent.ParseAdd("PremiereCalendar/1.0 (+https://github.com/local/premiere-calendar)");
});

builder.Services.AddHttpClient<IFanartClient, FanartClient>((sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<FanartOptions>>().Value;

    client.BaseAddress = new Uri(options.BaseUrl);
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
});

builder.Services.AddHttpClient<ITraktClient, TraktClient>((sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<TraktOptions>>().Value;

    client.BaseAddress = new Uri(options.BaseUrl);
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    client.DefaultRequestHeaders.UserAgent.ParseAdd("PremiereCalendar/1.0 (+https://github.com/local/premiere-calendar)");
});

builder.Services.AddHttpClient<ITheTvdbClient, TheTvdbClient>((sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<TheTvdbOptions>>().Value;

    client.BaseAddress = new Uri(options.BaseUrl);
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
});

builder.Services.AddHttpClient<IWikimediaClient, WikimediaClient>((sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<WikimediaOptions>>().Value;

    client.BaseAddress = new Uri(options.WikidataBaseUrl);
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    client.DefaultRequestHeaders.UserAgent.ParseAdd("PremiereCalendar/1.0 (+https://github.com/local/premiere-calendar)");
});

builder.Services.AddHttpClient<IWatchmodeClient, WatchmodeClient>((sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<WatchmodeOptions>>().Value;

    client.BaseAddress = new Uri(options.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.RequestTimeoutSeconds, 5, 120));
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    client.DefaultRequestHeaders.UserAgent.ParseAdd("PremiereCalendar/1.0 (+https://github.com/local/premiere-calendar)");
});

builder.Services.AddHttpClient<ISimklClient, SimklClient>((sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<SimklOptions>>().Value;

    client.BaseAddress = new Uri(options.BaseUrl);
    client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.RequestTimeoutSeconds, 5, 120));
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    client.DefaultRequestHeaders.UserAgent.ParseAdd("PremiereCalendar/1.0 (+https://github.com/local/premiere-calendar)");
});

builder.Services.AddHttpClient<FileImageCache>(client =>
{
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
    client.Timeout = TimeSpan.FromSeconds(20);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("PremiereCalendar/1.0 (+https://github.com/local/premiere-calendar)");
});
builder.Services.AddHttpClient<IArrIntegrationService, ArrIntegrationService>(client =>
{
    client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    client.Timeout = TimeSpan.FromSeconds(20);
});

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<FileCalendarCache>();
builder.Services.AddSingleton<ICalendarCache>(sp => sp.GetRequiredService<FileCalendarCache>());
builder.Services.AddSingleton<ICalendarCacheMaintenance>(sp => sp.GetRequiredService<FileCalendarCache>());
builder.Services.AddTransient<IImageCache>(sp => sp.GetRequiredService<FileImageCache>());
builder.Services.AddTransient<IImageCacheMaintenance>(sp => sp.GetRequiredService<FileImageCache>());
builder.Services.AddSingleton<IIntegrationSettingsStore, SqliteIntegrationSettingsStore>();
builder.Services.AddSingleton<ICalendarFilterUsageStore, SqliteCalendarFilterUsageStore>();
builder.Services.AddSingleton<ISimklSyncStateStore, SqliteSimklSyncStateStore>();
builder.Services.AddSingleton<IImdbRatingsStore, SqliteImdbRatingsStore>();
builder.Services.AddSingleton<IOmdbCacheStore, SqliteOmdbCacheStore>();
builder.Services.AddSingleton<IProviderCacheStateStore, SqliteProviderCacheStateStore>();
builder.Services.AddSingleton<IViewSyncStore, SqliteViewSyncStore>();
builder.Services.AddSingleton<IViewSyncService, ViewSyncService>();
builder.Services.AddSingleton<ISingleFlightCoordinator, SingleFlightCoordinator>();
builder.Services.AddSingleton<ProviderRequestThrottler>();
builder.Services.AddSingleton<CalendarLoadCoordinator>();
builder.Services.AddSingleton<AdjacentWeekPrefetcher>();
builder.Services.AddSingleton<IAdjacentWeekPrefetcher>(sp => sp.GetRequiredService<AdjacentWeekPrefetcher>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<AdjacentWeekPrefetcher>());
builder.Services.AddScoped<CurrentWeekCalendarWarmupRunner>();
builder.Services.AddScoped<CacheMaintenanceRunner>();
builder.Services.AddHostedService<CurrentWeekCalendarWarmupService>();
builder.Services.AddHostedService<ImdbDatasetRefreshService>();
builder.Services.AddHostedService<ProviderDeltaSyncService>();
builder.Services.AddSingleton<IFilterCatalogService, TmdbFilterCatalogService>();
builder.Services.AddSingleton<TmdbRequestLimiter>();
builder.Services.AddSingleton<TrailerSelector>();
builder.Services.AddSingleton<RatingMapper>();
builder.Services.AddSingleton<IArtworkProvider, FanartArtworkProvider>();
builder.Services.AddSingleton<IArtworkProvider, TvmazeArtworkProvider>();
builder.Services.AddSingleton<IArtworkProvider, TheTvdbArtworkProvider>();
builder.Services.AddSingleton<IArtworkProvider, WikimediaArtworkProvider>();
builder.Services.AddSingleton<IPremiereDiscoveryProvider, TraktDiscoveryProvider>();
builder.Services.AddSingleton<IPremiereDiscoveryProvider, TvmazeScheduleDiscoveryProvider>();
builder.Services.AddScoped<IPremiereService, PremiereService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}

app.UseResponseCompression();
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets()
    .ShortCircuit();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();
app.MapHealthChecks("/health");
app.MapGet(
    "/cached-image",
    async Task<IResult> (
        string url,
        bool? refresh,
        int? w,
        IImageCache imageCache,
        IOptions<ImageCacheOptions> options,
        HttpContext httpContext,
        CancellationToken cancellationToken) =>
    {
        if (!options.Value.Enabled)
        {
            return ImageSourceUrlPolicy.TryCreateAllowedUri(url, options.Value.AllowedHosts, out var sourceUri)
                ? Results.Redirect(sourceUri.AbsoluteUri)
                : Results.BadRequest();
        }

        try
        {
            var image = await imageCache.GetOrAddAsync(url, refresh == true, cancellationToken, w);
            var maxAgeSeconds = Math.Max(60, Convert.ToInt32(image.BrowserMaxAge.TotalSeconds));
            var entityTag = $"\"{image.CacheKey}\"";

            httpContext.Response.Headers.CacheControl = $"public, max-age={maxAgeSeconds}";
            httpContext.Response.Headers.ETag = entityTag;
            httpContext.Response.Headers.LastModified = image.LastModifiedUtc.UtcDateTime.ToString("R", CultureInfo.InvariantCulture);

            if (RequestHasMatchingEntityTag(httpContext, entityTag))
            {
                return Results.StatusCode(StatusCodes.Status304NotModified);
            }

            return Results.File(
                File.OpenRead(image.FilePath),
                image.ContentType,
                lastModified: image.LastModifiedUtc,
                entityTag: new Microsoft.Net.Http.Headers.EntityTagHeaderValue(entityTag),
                enableRangeProcessing: true);
        }
        catch (ArgumentException)
        {
            return Results.BadRequest();
        }
        catch (ExternalApiException)
        {
            return Results.NotFound();
        }
    });

app.Run();

static bool RequestHasMatchingEntityTag(HttpContext httpContext, string entityTag)
{
    if (!httpContext.Request.Headers.TryGetValue("If-None-Match", out var values))
    {
        return false;
    }

    return values
        .SelectMany(value => (value ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .Any(value => value == "*" || string.Equals(value, entityTag, StringComparison.Ordinal));
}

public partial class Program;
