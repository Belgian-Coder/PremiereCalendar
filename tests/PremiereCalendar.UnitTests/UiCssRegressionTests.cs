namespace PremiereCalendar.UnitTests;

public sealed class UiCssRegressionTests
{
    [Fact]
    public void AppCss_DoesNotAnimateCalendarCardsOnRender()
    {
        var css = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "PremiereCalendar", "wwwroot", "app.css"));

        Assert.DoesNotContain("animation: calendar-day-enter", css, StringComparison.Ordinal);
        Assert.DoesNotContain("animation: premiere-card-enter", css, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "PremiereCalendar.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
