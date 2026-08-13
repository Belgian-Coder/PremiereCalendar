using Microsoft.Extensions.DependencyInjection.Extensions;
using PremiereCalendar.Options;
using PremiereCalendar.Services;

namespace PremiereCalendar.Hosting;

public static class FeatureServiceExtensions
{
    public static IServiceCollection AddPremiereOptions(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TmdbOptions>(configuration.GetSection("Tmdb"));
        services.Configure<OmdbOptions>(configuration.GetSection("Omdb"));
        services.Configure<TvmazeOptions>(configuration.GetSection("Tvmaze"));
        services.Configure<FanartOptions>(configuration.GetSection("Fanart"));
        services.Configure<TraktOptions>(configuration.GetSection("Trakt"));
        services.Configure<TheTvdbOptions>(configuration.GetSection("TheTvdb"));
        services.Configure<WikimediaOptions>(configuration.GetSection("Wikimedia"));
        services.Configure<RottenTomatoesOptions>(configuration.GetSection("RottenTomatoes"));
        services.Configure<WatchmodeOptions>(configuration.GetSection("Watchmode"));
        services.Configure<SimklOptions>(configuration.GetSection("Simkl"));
        services.Configure<CalendarCacheOptions>(configuration.GetSection("CalendarCache"));
        services.Configure<CalendarWarmupOptions>(configuration.GetSection("CalendarWarmup"));
        services.Configure<CalendarLoadOptions>(configuration.GetSection("CalendarLoad"));
        services.Configure<CacheMaintenanceOptions>(configuration.GetSection("CacheMaintenance"));
        services.Configure<ImdbDatasetOptions>(configuration.GetSection("ImdbDataset"));
        services.Configure<ProviderDeltaSyncOptions>(configuration.GetSection("ProviderDeltaSync"));
        services.Configure<ApplicationUpdateOptions>(configuration.GetSection("ApplicationUpdate"));
        services.Configure<ProviderSchedulerOptions>(configuration.GetSection("ProviderScheduler"));
        return services;
    }

    public static IServiceCollection AddPremierePersistence(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<FileCalendarCache>();
        services.AddSingleton<ICalendarCache>(provider => provider.GetRequiredService<FileCalendarCache>());
        services.AddSingleton<ICalendarCacheMaintenance>(provider => provider.GetRequiredService<FileCalendarCache>());
        services.AddSingleton<CalendarLoadCacheOrchestrator>();
        services.AddTransient<IImageCache>(provider => provider.GetRequiredService<FileImageCache>());
        services.AddTransient<IImageCacheMaintenance>(provider => provider.GetRequiredService<FileImageCache>());
        services.AddSingleton<IIntegrationSettingsStore, SqliteIntegrationSettingsStore>();
        services.AddSingleton<IAppStateStore, SqliteAppStateStore>();
        services.AddSingleton<ICalendarFilterUsageStore, SqliteCalendarFilterUsageStore>();
        services.AddSingleton<ISimklSyncStateStore, SqliteSimklSyncStateStore>();
        services.AddSingleton<IImdbRatingsStore, SqliteImdbRatingsStore>();
        services.AddSingleton<IOmdbCacheStore, SqliteOmdbCacheStore>();
        services.AddSingleton<IProviderCacheStateStore, SqliteProviderCacheStateStore>();
        services.AddSingleton<IViewSyncStore, SqliteViewSyncStore>();
        services.AddSingleton<IViewSyncService, ViewSyncService>();
        return services;
    }

    public static IServiceCollection AddPremiereScheduling(this IServiceCollection services)
    {
        services.AddSingleton<ProviderAdaptiveStateStore>();
        services.AddSingleton<AdaptiveProviderPolicy>();
        services.AddSingleton<AdaptiveProviderHandlerFactory>();
        services.AddSingleton<ProviderWorkStore>();
        services.AddSingleton<ProviderWorkScheduler>();
        services.AddSingleton<IProviderWorkScheduler>(provider => provider.GetRequiredService<ProviderWorkScheduler>());
        services.AddHostedService<ProviderWorkSchedulerHostedService>();
        services.AddSingleton<AdjacentWeekPrefetcher>();
        services.AddSingleton<IAdjacentWeekPrefetcher>(provider => provider.GetRequiredService<AdjacentWeekPrefetcher>());
        services.AddHostedService(provider => provider.GetRequiredService<AdjacentWeekPrefetcher>());
        services.AddScoped<CurrentWeekCalendarWarmupRunner>();
        services.AddScoped<CacheMaintenanceRunner>();
        services.AddHostedService<CurrentWeekCalendarWarmupService>();
        services.AddSingleton<ImdbDatasetRefreshService>();
        services.AddHostedService(provider => provider.GetRequiredService<ImdbDatasetRefreshService>());
        services.AddSingleton<ProviderDeltaSyncService>();
        services.AddHostedService(provider => provider.GetRequiredService<ProviderDeltaSyncService>());
        return services;
    }

    public static IServiceCollection AddPremiereCalendarServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<CacheInspectorService>();
        services.AddSingleton<BackgroundJobTimelineService>();
        services.AddSingleton<SystemStatusService>();
        services.AddSingleton<IWeekDiagnosticsStore, AppStateWeekDiagnosticsStore>();
        services.AddSingleton<WeekDiagnosticsService>();
        services.AddSingleton<SourceHealthService>();
        services.AddSingleton<CalendarPresetService>();
        services.AddSingleton<CalendarVisitChangeService>();
        services.AddSingleton<SettingsBackupService>();
        services.AddSingleton<IApplicationUpdateProcessStarter, DefaultApplicationUpdateProcessStarter>();
        services.AddSingleton<IApplicationUpdateService, ApplicationUpdateService>();
        services.AddSingleton<ISingleFlightCoordinator, SingleFlightCoordinator>();
        services.AddSingleton<ProviderRequestThrottler>();
        services.AddSingleton<CalendarPageCoordinator>();
        services.AddSingleton<CalendarLoadCoordinator>(provider => provider.GetRequiredService<CalendarPageCoordinator>());
        services.AddSingleton<IFilterCatalogService, TmdbFilterCatalogService>();
        services.AddSingleton<TmdbRequestLimiter>();
        services.AddSingleton<TrailerSelector>();
        services.AddSingleton<RatingMapper>();
        services.AddSingleton<ScoreBackfillService>();
        services.AddSingleton<MissingExternalIdRepairService>();
        services.AddSingleton<CalendarDataMaintenanceService>();
        services.AddSingleton<IArtworkProvider, FanartArtworkProvider>();
        services.AddSingleton<IArtworkProvider, TvmazeArtworkProvider>();
        services.AddSingleton<IArtworkProvider, TheTvdbArtworkProvider>();
        services.AddSingleton<IArtworkProvider, WikimediaArtworkProvider>();
        services.AddSingleton<IPremiereDiscoveryProvider, TraktDiscoveryProvider>();
        services.AddSingleton<IPremiereDiscoveryProvider, SimklCalendarDiscoveryProvider>();
        services.AddSingleton<IPremiereDiscoveryProvider, TvmazeScheduleDiscoveryProvider>();
        services.AddScoped<PremiereService>();
        services.AddScoped<IPremiereService>(provider => provider.GetRequiredService<PremiereService>());
        services.AddScoped<IPremiereLoadPipeline>(provider => provider.GetRequiredService<PremiereService>());

        if (configuration.GetValue<bool>("BrowserTesting:Enabled"))
        {
            services.Replace(ServiceDescriptor.Scoped<IPremiereService, DeterministicBrowserPremiereService>());
            services.Replace(ServiceDescriptor.Singleton<IFilterCatalogService, DeterministicBrowserFilterCatalogService>());
            services.Replace(ServiceDescriptor.Singleton<IIntegrationSettingsStore, DeterministicBrowserIntegrationSettingsStore>());
        }
        return services;
    }
}
