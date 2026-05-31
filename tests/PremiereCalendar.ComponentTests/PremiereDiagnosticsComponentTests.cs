using Bunit;
using Microsoft.Extensions.DependencyInjection;
using PremiereCalendar.Components.Shared;
using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.ComponentTests;

public sealed class PremiereDiagnosticsComponentTests : BunitContext
{
    [Fact]
    public void PremiereCard_ShowsMergeInspectorAndMissingDataOnlyInsideProvenance()
    {
        Services.AddLogging();
        var item = new PremiereItem
        {
            CanonicalId = "tv:42",
            Type = PremiereItemType.SeriesPremiere,
            MediaType = PremiereMediaType.Series,
            TmdbId = 42,
            Title = "Diagnostics Show",
            PremiereDate = new DateOnly(2026, 5, 25),
            DateSemantics = new PremiereDateSemantics(
                new DateOnly(2026, 5, 25),
                PremiereDateSourceKind.TmdbSeasonOneEpisodeOne,
                PremiereDataConfidence.High,
                "Season 1 episode 1 air date from TMDb."),
            MergeContributions =
            [
                new PremiereMergeContribution
                {
                    Source = "TMDb",
                    Status = "accepted",
                    MatchMethod = "TMDb ID",
                    Reason = "Canonical calendar row.",
                    TmdbId = 42
                },
                new PremiereMergeContribution
                {
                    Source = "Trakt",
                    Status = "accepted",
                    MatchMethod = "IMDb ID",
                    Reason = "Merged into the TMDb row.",
                    ImdbId = "tt0042"
                }
            ],
            MissingDataIssues =
            [
                new PremiereMissingDataIssue
                {
                    Kind = "score.imdb",
                    Severity = "info",
                    Message = "IMDb score is missing because no IMDb rating was available."
                }
            ]
        };

        var component = Render<PremiereCard>(parameters => parameters.Add(x => x.Item, item));

        var details = component.Find("details.source-details");
        Assert.Contains("Data confidence", details.TextContent);
        Assert.Contains("Season 1 episode 1", details.TextContent);
        Assert.Contains("Merge inspector", details.TextContent);
        Assert.Contains("Trakt", details.TextContent);
        Assert.Contains("Missing data", details.TextContent);
        Assert.DoesNotContain("Merge inspector", component.Find(".card-title-row").TextContent);
    }

    [Fact]
    public void CalendarFilterDialog_ShowsMobileFilterReview()
    {
        var filters = new CalendarFilters
        {
            SeriesFilters =
            {
                OriginalLanguages = ["en", "nl"],
                SeriesDateMode = SeriesDateMode.NewSeriesOnly
            }
        };

        var component = Render<CalendarFilterDialog>(parameters => parameters
            .Add(x => x.PageMode, CalendarPageMode.Series)
            .Add(x => x.Filters, filters)
            .Add(x => x.Items, []));

        var review = component.Find("[data-testid='mobile-filter-review']");
        Assert.Contains("Active filters", review.TextContent);
        Assert.Contains("2 filters", review.TextContent);
        Assert.Contains("New only", review.TextContent);
        Assert.Contains("EN, NL", review.TextContent);
    }
}
