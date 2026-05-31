using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public static class PremiereDiagnosticsFactory
{
    public static PremiereItem ApplyMissingDataIssues(PremiereItem item)
    {
        var issues = BuildMissingDataIssues(item);
        return item with { MissingDataIssues = issues };
    }

    public static PremiereMissingDataIssue[] BuildMissingDataIssues(PremiereItem item)
    {
        var issues = new List<PremiereMissingDataIssue>();
        if (string.IsNullOrWhiteSpace(item.ImdbId))
        {
            issues.Add(new PremiereMissingDataIssue
            {
                Kind = "external-id.imdb",
                Message = "IMDb ID is missing, so IMDb and OMDb-based scores may be unavailable."
            });
        }

        if (item.MediaType == PremiereMediaType.Series && item.TvdbId is not > 0)
        {
            issues.Add(new PremiereMissingDataIssue
            {
                Kind = "external-id.tvdb",
                Message = "TVDB ID is missing, which limits TVmaze/TheTVDB matching confidence."
            });
        }

        if (item.ImdbScore is null)
        {
            issues.Add(new PremiereMissingDataIssue
            {
                Kind = "score.imdb",
                Message = "IMDb score is missing because no IMDb dataset or OMDb rating was available."
            });
        }

        if (item.RottenTomatoesScore is null && item.RottenTomatoesAudienceScore is null)
        {
            issues.Add(new PremiereMissingDataIssue
            {
                Kind = "score.rotten-tomatoes",
                Message = "Rotten Tomatoes scores are missing because enrichment did not return a critic or audience score."
            });
        }

        if (string.IsNullOrWhiteSpace(item.PosterUrl))
        {
            issues.Add(new PremiereMissingDataIssue
            {
                Kind = "artwork.poster",
                Message = "Poster artwork is missing from configured artwork sources."
            });
        }

        if (string.IsNullOrWhiteSpace(item.TrailerUrl))
        {
            issues.Add(new PremiereMissingDataIssue
            {
                Kind = "video.trailer",
                Message = "No TMDb trailer was available."
            });
        }

        if (item.SourceNames.Length == 0)
        {
            issues.Add(new PremiereMissingDataIssue
            {
                Kind = "source.availability",
                Message = "No provider, network, or channel source was returned."
            });
        }

        return issues.ToArray();
    }

    public static PremiereMergeContribution TmdbContribution(PremiereMediaType mediaType, int tmdbId)
    {
        return new PremiereMergeContribution
        {
            Source = "TMDb",
            Status = "accepted",
            MatchMethod = "TMDb ID",
            Reason = mediaType == PremiereMediaType.Series
                ? "Canonical TMDb series row."
                : "Canonical TMDb movie row.",
            TmdbId = tmdbId
        };
    }

    public static PremiereMergeContribution ExternalContribution(ExternalPremiereCandidate candidate, string matchMethod, string reason)
    {
        return new PremiereMergeContribution
        {
            Source = candidate.Source,
            Status = "accepted",
            MatchMethod = matchMethod,
            Reason = reason,
            TmdbId = candidate.TmdbId,
            ImdbId = candidate.ImdbId,
            TvdbId = candidate.TvdbId,
            CandidateDate = candidate.PremiereDate,
            ExternalProviderId = candidate.ExternalProviderId
        };
    }
}
