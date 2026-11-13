using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.Hosting;

public static class ProviderClientServiceExtensions
{
    public static IServiceCollection AddPremiereProviderClients(this IServiceCollection services)
    {
        services.AddHttpClient<ITmdbClient, TmdbClient>((sp, client) =>
            ConfigureAdaptiveJson(client, sp.GetRequiredService<IOptions<TmdbOptions>>().Value.BaseUrl))
            .ConfigureProviderTransport()
            .AddHttpMessageHandler(sp =>
            {
                var options = sp.GetRequiredService<IOptions<TmdbOptions>>().Value;
                return sp.GetRequiredService<AdaptiveProviderHandlerFactory>()
                    .Create("tmdb", options.MaxConcurrentRequests, TimeSpan.FromSeconds(30));
            });
        services.AddHttpClient<IOmdbClient, OmdbClient>((sp, client) =>
            ConfigureAdaptiveJson(client, sp.GetRequiredService<IOptions<OmdbOptions>>().Value.BaseUrl))
            .ConfigureProviderTransport()
            .AddHttpMessageHandler(sp => sp.GetRequiredService<AdaptiveProviderHandlerFactory>()
                .Create("omdb", 2, TimeSpan.FromSeconds(30)));
        services.AddHttpClient<IImdbDatasetImporter, ImdbDatasetImporter>((sp, client) =>
        {
            client.BaseAddress = new Uri("https://datasets.imdbws.com/");
            client.Timeout = Timeout.InfiniteTimeSpan;
            AddUserAgent(client);
        }).AddHttpMessageHandler(sp =>
        {
            var timeout = TimeSpan.FromSeconds(Math.Clamp(
                sp.GetRequiredService<IOptions<ImdbDatasetOptions>>().Value.RequestTimeoutSeconds, 30, 600));
            return sp.GetRequiredService<AdaptiveProviderHandlerFactory>().Create("imdb-dataset", 1, timeout);
        });
        services.AddHttpClient<ITvmazeClient, TvmazeClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<TvmazeOptions>>().Value;
            ConfigureAdaptiveJson(client, options.BaseUrl);
            AddUserAgent(client);
        }).ConfigureProviderTransport()
            .AddHttpMessageHandler(sp =>
        {
            var options = sp.GetRequiredService<IOptions<TvmazeOptions>>().Value;
            return sp.GetRequiredService<AdaptiveProviderHandlerFactory>()
                .Create("tvmaze", options.MaxConcurrentRequests, TimeSpan.FromSeconds(30));
        });
        services.AddHttpClient<IFanartClient, FanartClient>((sp, client) =>
            ConfigureAdaptiveJson(client, sp.GetRequiredService<IOptions<FanartOptions>>().Value.BaseUrl))
            .ConfigureProviderTransport()
            .AddHttpMessageHandler(sp => sp.GetRequiredService<AdaptiveProviderHandlerFactory>()
                .Create("fanart", 2, TimeSpan.FromSeconds(30)));
        services.AddHttpClient<ITraktClient, TraktClient>((sp, client) =>
        {
            ConfigureAdaptiveJson(client, sp.GetRequiredService<IOptions<TraktOptions>>().Value.BaseUrl);
            AddUserAgent(client);
        }).ConfigureProviderTransport()
            .AddHttpMessageHandler(sp => sp.GetRequiredService<AdaptiveProviderHandlerFactory>()
            .Create("trakt", 2, TimeSpan.FromSeconds(30)));
        services.AddHttpClient<ITheTvdbClient, TheTvdbClient>((sp, client) =>
            ConfigureAdaptiveJson(client, sp.GetRequiredService<IOptions<TheTvdbOptions>>().Value.BaseUrl))
            .ConfigureProviderTransport()
            .AddHttpMessageHandler(sp => sp.GetRequiredService<AdaptiveProviderHandlerFactory>()
                .Create("thetvdb", 2, TimeSpan.FromSeconds(30)));
        services.AddHttpClient<WikimediaClient>((sp, client) =>
        {
            ConfigureAdaptiveJson(client, sp.GetRequiredService<IOptions<WikimediaOptions>>().Value.WikidataBaseUrl);
            AddUserAgent(client);
        }).ConfigureProviderTransport()
            .AddHttpMessageHandler(sp => sp.GetRequiredService<AdaptiveProviderHandlerFactory>()
            .Create("wikimedia", 2, TimeSpan.FromSeconds(100)));
        // Artwork and identifier callers share one short-lived batching queue.
        services.AddSingleton<IWikimediaClient>(sp => sp.GetRequiredService<WikimediaClient>());
        services.AddHttpClient<IRottenTomatoesClient, RottenTomatoesClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<RottenTomatoesOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl);
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
            AddUserAgent(client);
        }).ConfigureProviderTransport()
            .AddHttpMessageHandler(sp =>
        {
            var timeout = TimeSpan.FromSeconds(Math.Clamp(
                sp.GetRequiredService<IOptions<RottenTomatoesOptions>>().Value.RequestTimeoutSeconds, 5, 120));
            return sp.GetRequiredService<AdaptiveProviderHandlerFactory>().Create("rottentomatoes", 2, timeout);
        });
        services.AddHttpClient<IWatchmodeClient, WatchmodeClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<WatchmodeOptions>>().Value;
            ConfigureAdaptiveJson(client, options.BaseUrl);
            AddUserAgent(client);
        }).ConfigureProviderTransport()
            .AddHttpMessageHandler(sp =>
        {
            var timeout = TimeSpan.FromSeconds(Math.Clamp(
                sp.GetRequiredService<IOptions<WatchmodeOptions>>().Value.RequestTimeoutSeconds, 5, 120));
            return sp.GetRequiredService<AdaptiveProviderHandlerFactory>().Create("watchmode", 2, timeout);
        });
        services.AddHttpClient<ISimklClient, SimklClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<SimklOptions>>().Value;
            ConfigureAdaptiveJson(client, options.BaseUrl);
            AddUserAgent(client);
        }).ConfigureProviderTransport()
            .AddHttpMessageHandler(sp =>
        {
            var timeout = TimeSpan.FromSeconds(Math.Clamp(
                sp.GetRequiredService<IOptions<SimklOptions>>().Value.RequestTimeoutSeconds, 5, 120));
            return sp.GetRequiredService<AdaptiveProviderHandlerFactory>().Create("simkl", 2, timeout, deferFinalThrottle: true);
        });
        services.AddHttpClient<FileImageCache>(client =>
        {
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
            client.Timeout = Timeout.InfiniteTimeSpan;
            AddUserAgent(client);
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        // Redirects are followed by FileImageCache itself, validating every hop against the image host allow-list.
        .AddHttpMessageHandler(sp => sp.GetRequiredService<AdaptiveProviderHandlerFactory>()
            .Create("images", 4, TimeSpan.FromSeconds(20)));
        services.AddHttpClient<IArrIntegrationService, ArrIntegrationService>(client =>
        {
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            client.Timeout = TimeSpan.FromSeconds(20);
        });
        return services;
    }

    /// <summary>Lifetime of a pooled provider connection; bounds how long a stale DNS answer can be reused.</summary>
    internal static readonly TimeSpan ProviderPooledConnectionLifetime = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Shared transport for the JSON/HTML provider clients: negotiates gzip, deflate or Brotli (never zstd, whose
    /// multi-frame decoding is not trusted yet) and keeps connections pooled across factory handler rotation.
    /// Decompression strips Content-Length/Content-Encoding from the response seen by the adaptive handler, so body
    /// completion for those responses relies on end-of-stream rather than a declared length.
    /// </summary>
    internal static IHttpClientBuilder ConfigureProviderTransport(this IHttpClientBuilder builder) => builder
        .ConfigurePrimaryHttpMessageHandler(CreateProviderPrimaryHandler)
        // Connection rotation is owned by PooledConnectionLifetime; recycling the whole handler chain every two
        // minutes would only discard warm connections and TLS sessions.
        .SetHandlerLifetime(Timeout.InfiniteTimeSpan);

    internal static SocketsHttpHandler CreateProviderPrimaryHandler() => new()
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        PooledConnectionLifetime = ProviderPooledConnectionLifetime,
        // The factory default for primary handlers is cookie-less; keep it so providers cannot track sessions.
        UseCookies = false
    };

    private static void ConfigureAdaptiveJson(HttpClient client, string baseUrl)
    {
        client.BaseAddress = new Uri(baseUrl);
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static void AddUserAgent(HttpClient client) =>
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PremiereCalendar/1.0 (+https://github.com/Belgian-Coder/PremiereCalendar)");
}
