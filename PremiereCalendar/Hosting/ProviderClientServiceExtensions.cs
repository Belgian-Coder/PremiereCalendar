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
            .AddHttpMessageHandler(sp =>
            {
                var options = sp.GetRequiredService<IOptions<TmdbOptions>>().Value;
                return sp.GetRequiredService<AdaptiveProviderHandlerFactory>()
                    .Create("tmdb", options.MaxConcurrentRequests, TimeSpan.FromSeconds(30));
            });
        services.AddHttpClient<IOmdbClient, OmdbClient>((sp, client) =>
            ConfigureAdaptiveJson(client, sp.GetRequiredService<IOptions<OmdbOptions>>().Value.BaseUrl))
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
        }).AddHttpMessageHandler(sp =>
        {
            var options = sp.GetRequiredService<IOptions<TvmazeOptions>>().Value;
            return sp.GetRequiredService<AdaptiveProviderHandlerFactory>()
                .Create("tvmaze", options.MaxConcurrentRequests, TimeSpan.FromSeconds(30));
        });
        services.AddHttpClient<IFanartClient, FanartClient>((sp, client) =>
            ConfigureAdaptiveJson(client, sp.GetRequiredService<IOptions<FanartOptions>>().Value.BaseUrl))
            .AddHttpMessageHandler(sp => sp.GetRequiredService<AdaptiveProviderHandlerFactory>()
                .Create("fanart", 2, TimeSpan.FromSeconds(30)));
        services.AddHttpClient<ITraktClient, TraktClient>((sp, client) =>
        {
            ConfigureAdaptiveJson(client, sp.GetRequiredService<IOptions<TraktOptions>>().Value.BaseUrl);
            AddUserAgent(client);
        }).AddHttpMessageHandler(sp => sp.GetRequiredService<AdaptiveProviderHandlerFactory>()
            .Create("trakt", 2, TimeSpan.FromSeconds(30)));
        services.AddHttpClient<ITheTvdbClient, TheTvdbClient>((sp, client) =>
            ConfigureAdaptiveJson(client, sp.GetRequiredService<IOptions<TheTvdbOptions>>().Value.BaseUrl))
            .AddHttpMessageHandler(sp => sp.GetRequiredService<AdaptiveProviderHandlerFactory>()
                .Create("thetvdb", 2, TimeSpan.FromSeconds(30)));
        services.AddHttpClient<IWikimediaClient, WikimediaClient>((sp, client) =>
        {
            ConfigureAdaptiveJson(client, sp.GetRequiredService<IOptions<WikimediaOptions>>().Value.WikidataBaseUrl);
            AddUserAgent(client);
        }).AddHttpMessageHandler(sp => sp.GetRequiredService<AdaptiveProviderHandlerFactory>()
            .Create("wikimedia", 2, TimeSpan.FromSeconds(100)));
        services.AddHttpClient<IRottenTomatoesClient, RottenTomatoesClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<RottenTomatoesOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl);
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
            AddUserAgent(client);
        }).AddHttpMessageHandler(sp =>
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
        }).AddHttpMessageHandler(sp =>
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
        }).AddHttpMessageHandler(sp =>
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
        }).AddHttpMessageHandler(sp => sp.GetRequiredService<AdaptiveProviderHandlerFactory>()
            .Create("images", 4, TimeSpan.FromSeconds(20)));
        services.AddHttpClient<IArrIntegrationService, ArrIntegrationService>(client =>
        {
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            client.Timeout = TimeSpan.FromSeconds(20);
        });
        services.AddHttpClient<ReleaseUpdateService>(client =>
        {
            client.BaseAddress = new Uri("https://api.github.com/");
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            client.Timeout = TimeSpan.FromSeconds(20);
        });
        return services;
    }

    private static void ConfigureAdaptiveJson(HttpClient client, string baseUrl)
    {
        client.BaseAddress = new Uri(baseUrl);
        client.Timeout = Timeout.InfiniteTimeSpan;
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static void AddUserAgent(HttpClient client) =>
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PremiereCalendar/1.0 (+https://github.com/Belgian-Coder/PremiereCalendar)");
}
