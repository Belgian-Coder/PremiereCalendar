namespace PremiereCalendar.Options;

public sealed class WikimediaOptions
{
    public string WikidataBaseUrl { get; set; } = "https://www.wikidata.org/";
    public string CommonsApiUrl { get; set; } = "https://commons.wikimedia.org/w/api.php";
    public bool Enabled { get; set; } = true;
}
