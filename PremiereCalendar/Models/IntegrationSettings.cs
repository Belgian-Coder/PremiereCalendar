namespace PremiereCalendar.Models;

public sealed record IntegrationSettings
{
    public SonarrIntegrationSettings Sonarr { get; set; } = new();
    public RadarrIntegrationSettings Radarr { get; set; } = new();
    public SourceIntegrationSettings Sources { get; set; } = new();
}

public sealed record SonarrIntegrationSettings
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string RootFolderPath { get; set; } = "";
    public int? QualityProfileId { get; set; }
    public string SeriesType { get; set; } = "standard";
    public string Monitor { get; set; } = "all";
    public bool SeasonFolder { get; set; } = true;
    public bool SearchOnAdd { get; set; } = true;
    public string TagOnAdd { get; set; } = "import";
}

public sealed record RadarrIntegrationSettings
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public string RootFolderPath { get; set; } = "";
    public int? QualityProfileId { get; set; }
    public string MinimumAvailability { get; set; } = "released";
    public bool Monitored { get; set; } = true;
    public bool SearchOnAdd { get; set; } = true;
    public string TagOnAdd { get; set; } = "import";
}

public sealed record SourceIntegrationSettings
{
    public TmdbSourceSettings Tmdb { get; set; } = new();
    public TvmazeSourceSettings Tvmaze { get; set; } = new();
    public TraktSourceSettings Trakt { get; set; } = new();
    public OmdbSourceSettings Omdb { get; set; } = new();
    public FanartSourceSettings Fanart { get; set; } = new();
    public TheTvdbSourceSettings TheTvdb { get; set; } = new();
    public WikimediaSourceSettings Wikimedia { get; set; } = new();
    public WatchmodeSourceSettings Watchmode { get; set; } = new();
    public SimklSourceSettings Simkl { get; set; } = new();
}

public sealed record TmdbSourceSettings
{
    public string BearerToken { get; set; } = "";
}

public sealed record TvmazeSourceSettings
{
    public bool Enabled { get; set; } = true;
    public bool EnableScheduleDiscovery { get; set; } = true;
    public string[] ScheduleCountries { get; set; } = [];
}

public sealed record TraktSourceSettings
{
    public bool Enabled { get; set; } = true;
    public string ClientId { get; set; } = "";
}

public sealed record OmdbSourceSettings
{
    public bool Enabled { get; set; }
    public string ApiKey { get; set; } = "";
}

public sealed record FanartSourceSettings
{
    public bool Enabled { get; set; }
    public string ApiKey { get; set; } = "";
}

public sealed record TheTvdbSourceSettings
{
    public bool Enabled { get; set; }
    public string ApiKey { get; set; } = "";
}

public sealed record WikimediaSourceSettings
{
    public bool Enabled { get; set; } = true;
}

public sealed record WatchmodeSourceSettings
{
    public bool Enabled { get; set; } = true;
    public string ApiKey { get; set; } = "";
    public string[] Regions { get; set; } = [];
    public bool EnableReleaseDiscovery { get; set; } = true;
    public bool EnableAvailabilityEnrichment { get; set; } = true;
    public int? CacheHours { get; set; }
}

public sealed record SimklSourceSettings
{
    public bool Enabled { get; set; } = true;
    public string ClientId { get; set; } = "";
    public string ClientSecret { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public int? MinimumActivityCheckMinutes { get; set; }
}

public sealed record ArrOption(int Id, string Name);

public sealed record ArrRootFolder(int Id, string Path, long? FreeSpace);

public sealed record ArrConnectionOptions(
    IReadOnlyList<ArrRootFolder> RootFolders,
    IReadOnlyList<ArrOption> QualityProfiles);

public enum ArrIntegrationTarget
{
    Sonarr,
    Radarr
}

public sealed record ArrAddResult(
    bool Succeeded,
    bool AlreadyExists,
    ArrIntegrationTarget Target,
    string Title,
    string Message);
