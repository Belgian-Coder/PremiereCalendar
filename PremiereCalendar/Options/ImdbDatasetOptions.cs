namespace PremiereCalendar.Options;

public sealed class ImdbDatasetOptions
{
    public bool Enabled { get; set; } = true;
    public string RatingsUrl { get; set; } = "https://datasets.imdbws.com/title.ratings.tsv.gz";
    public int RefreshIntervalHours { get; set; } = 24;
    public bool RefreshOnStartup { get; set; } = true;
    public int StartupDelaySeconds { get; set; } = 30;
    public int RequestTimeoutSeconds { get; set; } = 120;
    public string DownloadDirectory { get; set; } = "App_Data/tmp/imdb";
    public long MaxDownloadBytes { get; set; } = 512L * 1024 * 1024;
}
