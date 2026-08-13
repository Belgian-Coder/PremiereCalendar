using System.Net.Http.Headers;
using Microsoft.Extensions.Options;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.Hosting;

public static class ProviderClientServiceExtensions
{
    public static IServiceCollection AddPremiereProviderClients(this IServiceCollection services)
    {
        services.AddHttpClient<ITmdbClient, TmdbClient>((sp, client) => ConfigureJson(client, sp.GetRequiredService<IOptions<TmdbOptions>>().Value.BaseUrl, 30))
            .AddHttpMessageHandler(sp => sp.GetRequiredService<AdaptiveProviderHandlerFactory>().Create("tmdb", sp.GetRequiredService<IOptions<TmdbOptions>>().Value.MaxConcurrentRequests));
        services.AddHttpClient<IOmdbClient, OmdbClient>((sp, client) => ConfigureJson(client, sp.GetRequiredService<IOptions<OmdbOptions>>().Value.BaseUrl, 30))
            .AddHttpMessageHandler(sp => sp.GetRequiredService<AdaptiveProviderHandlerFactory>().Create("omdb", 2));
        services.AddHttpClient<IImdbDatasetImporter, ImdbDatasetImporter>((sp, client) =>
        {
            client.BaseAddress = new Uri("https://datasets.imdbws.com/");
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(sp.GetRequiredService<IOptions<ImdbDatasetOptions>>().Value.RequestTimeoutSeconds, 30, 600));
            AddUserAgent(client);
        }).AddHttpMessageHandler(sp => sp.GetRequiredService<AdaptiveProviderHandlerFactory>().Create("imdb-dataset", 1));
        services.AddHttpClient<ITvmazeClient, TvmazeClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<TvmazeOptions>>().Value;
            ConfigureJson(client, options.BaseUrl, 30);
            AddUserAgent(client);
        }).AddHttpMessageHandler(sp => sp.GetRequiredService<AdaptiveProviderHandlerFactory>().Create("tvmaze", sp.GetRequiredService<IOptions<TvmazeOptions>>().Value.MaxConcurrentRequests));
        services.AddHttpClient<IFanartClient, FanartClient>((sp, client) => ConfigureJson(client, sp.GetRequiredService<IOptions<FanartOptions>>().Value.BaseUrl, 30))
            .AddHttpMessageHandler(sp => sp.GetRequiredService<AdaptiveProviderHandlerFactory>().Create("fanart", 2));
        services.AddHttpClient<ITraktClient, TraktClient>((sp, client) =>
        {
            ConfigureJson(client, sp.GetRequiredService<IOptions<TraktOptions>>().Value.BaseUrl, 30);
            AddUserAgent(client);
        }).AddHttpMessageHandler(sp => sp.GetRequiredService<AdaptiveProviderHandlerFactory>().Create("trakt", 2));
        services.AddHttpClient<ITheTvdbClient, TheTvdbClient>((sp, client) => ConfigureJson(client, sp.GetRequiredService<IOptions<TheTvdbOptions>>().Value.BaseUrl, 30))
            .AddHttpMessageHandler(sp => sp.GetRequiredService<AdaptiveProviderHandlerFactory>().Create("thetvdb", 2));
        services.AddHttpClient<IWikimediaClient, WikimediaClient>((sp, client) =>
        {
            ConfigureJson(client, sp.GetRequiredService<IOptions<WikimediaOptions>>().Value.WikidataBaseUrl, 100);
            AddUserAgent(client);
        }).AddHttpMessageHandler(sp => sp.GetRequiredService<AdaptiveProviderHandlerFactory>().Create("wikimedia", 2));
        services.AddHttpClient<IRottenTomatoesClient, RottenTomatoesClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<RottenTomatoesOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl);
            client.Timeout = TimeSpan.FromSeconds(Math.Clamp(options.RequestTimeoutSeconds, 5, 120));
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
            AddUserAgent(client);
        }).AddHttpMessageHandler(sp => sp.GetRequiredService<AdaptiveProviderHandlerFactory>().Create("rottentomatoes", 2));
        services.AddHttpClient<IWatchmodeClient, WatchmodeClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<WatchmodeOptions>>().Value;
            ConfigureJson(client, options.BaseUrl, options.RequestTimeoutSeconds);
            AddUserAgent(client);
        }).AddHttpMessageHandler(sp => sp.GetRequiredService<AdaptiveProviderHandlerFactory>().Create("watchmode", 2));
        services.AddHttpClient<ISimklClient, SimklClient>((sp, client) =>
        {
            var options = sp.GetRequiredService<IOptions<SimklOptions>>().Value;
            ConfigureJson(client, options.BaseUrl, options.RequestTimeoutSeconds);
            AddUserAgent(client);
        }).AddHttpMessageHandler(sp => sp.GetRequiredService<AdaptiveProviderHandlerFactory>().Create("simkl", 2));
        services.AddHttpClient<FileImageCache>(client =>
        {
            client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
            client.Timeout = TimeSpan.FromSeconds(20);
            AddUserAgent(client);
        }).AddHttpMessageHandler(sp => sp.GetRequiredService<AdaptiveProviderHandlerFactory>().Create("images", 4));
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

    private static void ConfigureJson(HttpClient client, string baseUrl, int timeoutSeconds)
    {
        client.BaseAddress = new Uri(baseUrl);
        client.Timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 5, 120));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private static void AddUserAgent(HttpClient client) =>
        client.DefaultRequestHeaders.UserAgent.ParseAdd("PremiereCalendar/1.0 (+https://github.com/Belgian-Coder/PremiereCalendar)");
}
