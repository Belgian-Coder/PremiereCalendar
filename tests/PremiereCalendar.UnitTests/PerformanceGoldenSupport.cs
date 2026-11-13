using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

/// <summary>Deterministic, representative item graphs shared by golden/equivalence tests and local harnesses.</summary>
internal static class PerformanceGoldenSupport
{
    private static readonly DateTimeOffset BaseTime = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    public static List<PremiereItem> Items(int count, int seed = 17)
    {
        var random = new Random(seed);
        var items = new List<PremiereItem>(count);
        for (var index = 0; index < count; index++) items.Add(Item(index, random));
        return items;
    }

    public static PremiereItem Item(int index, Random random)
    {
        var series = index % 3 != 0;
        var id = 1000 + index;
        var imdb = random.Next(4) == 0 ? null : "tt" + (1_000_000 + id).ToString("D7");
        return new PremiereItem
        {
            CanonicalId = series ? $"tv:{id}:{1 + index % 4}:{1 + index % 12}" : $"movie:{id}",
            Type = series ? (index % 2 == 0 ? PremiereItemType.SeriesPremiere : PremiereItemType.SeriesEpisode) : PremiereItemType.MovieFirstRelease,
            MediaType = series ? PremiereMediaType.Series : PremiereMediaType.Movie,
            TmdbId = id,
            ImdbId = imdb,
            TvdbId = series && random.Next(3) != 0 ? 70000 + id : null,
            WikidataId = random.Next(5) == 0 ? "Q" + id : null,
            VerificationState = index % 17 == 0 ? PremiereVerificationState.Unverified : PremiereVerificationState.Verified,
            VerificationNote = index % 17 == 0 ? "Needs a second source + review" : null,
            ExternalUrl = index % 11 == 0 ? $"https://example.test/item/{id}?a=1&b=2+3" : null,
            Title = index % 7 == 0 ? $"Café Étoile — 日本 {id}" : $"Synthetic title number {id}",
            OriginalTitle = index % 5 == 0 ? $"Original title {id} <&>" : null,
            PremiereDate = new DateOnly(2026, 11, 2).AddDays(index % 7),
            Overview = random.Next(6) == 0 ? null : string.Concat(Enumerable.Repeat($"An overview sentence for {id} that is long enough to matter. ", 1 + index % 4)),
            PosterUrl = $"https://image.example.test/t/p/w500/poster{id}.jpg",
            BackdropUrl = index % 2 == 0 ? $"https://image.example.test/t/p/w1280/backdrop{id}.jpg" : null,
            ImageSource = "TMDb poster",
            TrailerUrl = index % 4 == 0 ? $"https://video.example.test/watch?v=abc{id}" : null,
            TmdbUrl = $"https://www.example.test/{(series ? "tv" : "movie")}/{id}",
            ImdbUrl = imdb is null ? null : $"https://www.example.test/title/{imdb}/",
            OriginalLanguage = index % 3 == 0 ? "nl" : "en",
            OriginCountries = index % 3 == 0 ? ["BE", "NL"] : ["US"],
            SourceNames = ["TMDb", "TVmaze"],
            Sources = [new() { Name = "TMDb", Id = id, Kind = "primary" }, new() { Name = "TVmaze", Id = null, Kind = "schedule" }],
            GenreIds = [18, 35 + index % 5],
            Genres = ["Drama", "Comedy"],
            Keywords = index % 2 == 0 ? ["first", "second", "third"] : [],
            MovieReleaseTypes = series ? [] : [2, 3],
            Certifications = index % 4 == 0 ? ["12", "PG"] : [],
            TvStatus = series ? "Returning Series" : null,
            TvType = series ? "Scripted" : null,
            EpisodeTitle = series ? $"Episode {1 + index % 12}" : null,
            SeasonNumber = series ? 1 + index % 4 : null,
            EpisodeNumber = series ? 1 + index % 12 : null,
            EpisodeSource = series ? "TMDb" : null,
            RuntimeMinutes = random.Next(3) == 0 ? null : 20 + random.Next(120),
            TmdbScore = Math.Round(random.NextDouble() * 10, 3),
            TmdbVoteCount = random.Next(5000),
            ImdbScore = random.Next(4) == 0 ? null : Math.Round(random.NextDouble() * 10, 1),
            ImdbVoteCount = random.Next(4) == 0 ? null : random.Next(400000),
            RottenTomatoesScore = random.Next(3) == 0 ? null : random.Next(101),
            RottenTomatoesAudienceScore = random.Next(3) == 0 ? null : random.Next(101),
            MetacriticScore = random.Next(3) == 0 ? null : random.Next(101),
            DateSemantics = new(new DateOnly(2026, 11, 2).AddDays(index % 7), (PremiereDateSourceKind)(index % 8), (PremiereDataConfidence)(index % 3), "Chosen by rule " + index % 5),
            MergeContributions = index % 3 == 0
                ? [new() { Source = "TVmaze", MatchMethod = "imdb", Reason = "Matched", TmdbId = id, ImdbId = imdb, TvdbId = 70000 + id,
                    CandidateDate = new DateOnly(2026, 11, 2), ExternalProviderId = "external:" + id }]
                : [],
            MissingDataIssues = index % 5 == 0 ? [new() { Kind = "score", Message = "Score missing", Severity = "warning" }] : [],
            NetworkName = series ? "Network " + index % 9 : null,
            WebChannelName = null,
            TvmazeAverageRuntimeMinutes = series ? 30 + index % 30 : null,
            TvmazeRating = series ? Math.Round(random.NextDouble() * 10, 1) : null,
            OfficialSiteUrl = index % 6 == 0 ? $"https://official.example.test/{id}" : null,
            TvmazeUrl = series ? $"https://www.example.test/shows/{id}" : null,
            LastUpdatedUtc = BaseTime.AddMinutes(index),
            EnrichmentVersion = index % 2 == 0 ? new(index, index * 2L, BaseTime.AddHours(index % 24), BaseTime.AddHours(-(index % 24)), null) : null
        };
    }

    public static ProviderWorkCheckpoint Checkpoint(int itemCount, string message = "synthetic checkpoint", int seed = 17)
    {
        var items = Items(itemCount, seed);
        var half = items.Count / 2;
        var completed = new Dictionary<string, IReadOnlyList<PremiereItem>>(StringComparer.Ordinal)
        {
            ["tmdb:tv"] = items.Take(half).ToArray(),
            ["tmdb:movie"] = items.Skip(half).ToArray(),
            ["empty"] = []
        };
        var progress = new PremiereLoadProgress("TMDb", 1, 2, [], CompletedWork: itemCount / 2, TotalWork: itemCount, ProgressText: "Synthetic progress")
        {
            ProviderKey = "tmdb", Phase = "enriching", SourceItems = [], CheckpointKey = "tmdb:tv", AcceptedCount = itemCount,
            IsBackground = true, FailedSourceNames = ["TVmaze"], RetryAfterUtc = BaseTime.AddMinutes(2)
        };
        return new ProviderWorkCheckpoint(message, itemCount / 2, itemCount, progress, completed)
        {
            SourceCompletedUtc = new Dictionary<string, DateTimeOffset> { ["tmdb:tv"] = BaseTime, ["tmdb:movie"] = BaseTime.AddMinutes(5) },
            SourceSummaries = new Dictionary<string, ProviderWorkProgress>
            {
                ["tmdb:tv:complete"] = new("job-1", ProviderWorkState.Running, "source", itemCount / 2, itemCount, progress with { Phase = "complete" })
            }
        };
    }
}
