using System.Runtime.CompilerServices;

namespace PremiereCalendar.UnitTests;

public sealed class DayAutoloadScriptTests
{
    [Fact]
    public void AutoloadRequestsOneBoundedBatchInsteadOfShowAll()
    {
        var script = ReadRepoFile("PremiereCalendar/wwwroot/day-autoload.js");

        Assert.Contains("[data-day-load-more]", script, StringComparison.Ordinal);
        Assert.Contains("rootMargin: \"400px 0px\"", script, StringComparison.Ordinal);
        Assert.DoesNotContain("[data-day-load-all]", script, StringComparison.Ordinal);
    }

    private static string ReadRepoFile(string relativePath, [CallerFilePath] string sourceFile = "")
    {
        var repoRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourceFile)!, "..", ".."));
        return File.ReadAllText(Path.Combine(repoRoot, relativePath));
    }
}
