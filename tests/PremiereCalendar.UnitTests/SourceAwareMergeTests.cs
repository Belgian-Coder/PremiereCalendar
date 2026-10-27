using PremiereCalendar.Models;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class SourceAwareMergeTests
{
    [Fact]
    public void BatchedSourceRemovalCannotReuseObsoleteAttribution()
    {
        var accumulator = new PremiereService.SourceAwarePremiereMergeAccumulator();
        var first = Movie(1, "Shared") with { SourceNames = ["A"] };
        var second = first with { SourceNames = ["B"] };
        accumulator.ReplaceSource("a", [first]);
        accumulator.ReplaceSource("b", [second]);
        Assert.Equal(2, Assert.Single(accumulator.ToMergedItems()).SourceNames.Length);
        accumulator.ReplaceSource("a", []);
        accumulator.ReplaceSource("b", [second]);
        Assert.Equal("B", Assert.Single(Assert.Single(accumulator.ToMergedItems()).SourceNames));
    }

    [Fact]
    public void GrowingSnapshotsReuseUnchangedRowsButApplyCorrectionsAndRemovals()
    {
        var accumulator = new PremiereService.SourceAwarePremiereMergeAccumulator();
        var first = Movie(1, "First");
        accumulator.ReplaceSource("a", [first]);
        var original = Assert.Single(accumulator.ToMergedItems());
        accumulator.ReplaceSource("a", [first, Movie(2, "Second")]);
        Assert.Same(original, accumulator.ToMergedItems().Single(item => item.TmdbId == 1));
        accumulator.ReplaceSource("a", [first with { Title = "Corrected" }]);
        Assert.Equal("Corrected", Assert.Single(accumulator.ToMergedItems()).Title);
        accumulator.ReplaceSource("a", []);
        Assert.Empty(accumulator.ToMergedItems());
    }

    [Fact]
    public void RepeatedSingletonRowsPreserveBaselineAmbiguousCandidateAttribution()
    {
        var accumulator = new PremiereService.SourceAwarePremiereMergeAccumulator();
        var first = Movie(1, "Shared title");
        var second = Movie(2, "Shared title");
        var external = Movie(0, "Shared title") with
        {
            CanonicalId = "external:candidate", VerificationState = PremiereVerificationState.Unverified,
            SourceNames = ["External"]
        };
        accumulator.ReplaceSource("a", [first, second]);
        accumulator.ToMergedItems();
        accumulator.ReplaceSource("external", [external]);
        Assert.Contains("External", accumulator.ToMergedItems().Single(item => item.TmdbId == 1).SourceNames);
        accumulator.ReplaceSource("a", [first, second]);
        // Baseline removes/reinserts the rows before first-match reconciliation.
        var result = accumulator.ToMergedItems();
        Assert.Contains("External", result.Single(item => item.TmdbId == 2).SourceNames);
        Assert.DoesNotContain("External", result.Single(item => item.TmdbId == 1).SourceNames);
    }

    [Fact]
    public void RepeatedCrossSourceRowsPreserveLastReplacementTieOrderAndAttribution()
    {
        var accumulator = new PremiereService.SourceAwarePremiereMergeAccumulator();
        var first = Movie(1, "First");
        var second = Movie(1, "Second");
        accumulator.ReplaceSource("a", [first]);
        accumulator.ReplaceSource("b", [second]);
        Assert.Equal("First", Assert.Single(accumulator.ToMergedItems()).Title);
        accumulator.ReplaceSource("a", [first]);
        Assert.Equal("Second", Assert.Single(accumulator.ToMergedItems()).Title);
        accumulator.ReplaceSource("b", []);
        Assert.Equal("First", Assert.Single(accumulator.ToMergedItems()).Title);
    }

    private static PremiereItem Movie(int id, string title) => new()
    {
        CanonicalId = $"movie:{id}", MediaType = PremiereMediaType.Movie,
        Type = PremiereItemType.MovieFirstRelease, TmdbId = id, Title = title,
        PremiereDate = new DateOnly(2026, 10, 26)
    };
}
