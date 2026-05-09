namespace PremiereCalendar.Models;

public enum ScoreSource
{
    Tmdb,
    Imdb,
    RottenTomatoes,
    Metacritic
}

public enum LanguageFilter
{
    Both,
    English,
    Dutch,
    French
}

public enum OriginGroupFilter
{
    AllConfigured,
    Belgium,
    UnitedStates,
    UnitedKingdom,
    Australia,
    DutchLanguage
}

public enum PremiereSortMode
{
    PremiereDate,
    Title,
    Score,
    VoteCount,
    Runtime
}

public enum SeriesDateMode
{
    AllEpisodes,
    NewSeriesOnly
}

public enum SortDirection
{
    Ascending,
    Descending
}

public sealed class MediaFilterSet
{
    public SeriesDateMode SeriesDateMode { get; set; } = SeriesDateMode.AllEpisodes;
    public List<string> OriginalLanguages { get; set; } = [];
    public List<string> OriginCountries { get; set; } = [];
    public List<int> GenreIds { get; set; } = [];
    public List<string> SelectedSources { get; set; } = [];
    public string WatchRegion { get; set; } = "";
    public string SourceText { get; set; } = "";
    public List<string> MonetizationTypes { get; set; } = [];
    public List<int> MovieReleaseTypes { get; set; } = [];
    public List<string> Certifications { get; set; } = [];
    public string CertificationCountry { get; set; } = "";
    public List<string> TvStatuses { get; set; } = [];
    public List<string> TvTypes { get; set; } = [];
    public int RuntimeMinMinutes { get; set; } = 0;
    public int RuntimeMaxMinutes { get; set; } = 360;
    public string KeywordText { get; set; } = "";
    public string SearchText { get; set; } = "";

    public bool HasCriteria =>
        SeriesDateMode != SeriesDateMode.AllEpisodes
        || OriginalLanguages.Count > 0
        || OriginCountries.Count > 0
        || GenreIds.Count > 0
        || SelectedSources.Count > 0
        || !string.IsNullOrWhiteSpace(WatchRegion)
        || !string.IsNullOrWhiteSpace(SourceText)
        || MonetizationTypes.Count > 0
        || MovieReleaseTypes.Count > 0
        || Certifications.Count > 0
        || !string.IsNullOrWhiteSpace(CertificationCountry)
        || TvStatuses.Count > 0
        || TvTypes.Count > 0
        || RuntimeMinMinutes > 0
        || RuntimeMaxMinutes < 360
        || !string.IsNullOrWhiteSpace(KeywordText)
        || !string.IsNullOrWhiteSpace(SearchText);
}

public sealed class CalendarFilters
{
    public DateOnly WeekStart { get; set; } = StartOfWeek(DateOnly.FromDateTime(DateTime.Today));
    public DateOnly? PriorityDate { get; set; }
    public bool ShowSeries { get; set; } = true;
    public bool ShowMovies { get; set; } = true;

    public PremiereSortMode SortMode { get; set; } = PremiereSortMode.PremiereDate;
    public SortDirection SortDirection { get; set; } = SortDirection.Ascending;

    public ScoreSource ScoreSource { get; set; } = ScoreSource.Tmdb;
    public double MinScore { get; set; } = 0;
    public double MaxScore { get; set; } = 10;
    public bool IncludeUnknownScores { get; set; } = true;
    public int MinVoteCount { get; set; } = 0;

    public LanguageFilter Language { get; set; } = LanguageFilter.Both;
    public OriginGroupFilter OriginGroup { get; set; } = OriginGroupFilter.AllConfigured;

    public List<int> GenreIds { get; set; } = [];
    public List<string> SelectedSources { get; set; } = [];
    public string NetworkText { get; set; } = "";
    public int RuntimeMinMinutes { get; set; } = 0;
    public int RuntimeMaxMinutes { get; set; } = 360;
    public string KeywordText { get; set; } = "";
    public string SearchText { get; set; } = "";

    public MediaFilterSet SeriesFilters { get; set; } = new();
    public MediaFilterSet MovieFilters { get; set; } = new();

    public static DateOnly StartOfWeek(DateOnly date)
    {
        var diff = ((int)date.DayOfWeek + 6) % 7;
        return date.AddDays(-diff);
    }
}
