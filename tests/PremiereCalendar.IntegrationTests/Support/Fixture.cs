namespace PremiereCalendar.IntegrationTests.Support;

internal static class Fixture
{
    public static string Read(string relativePath)
    {
        return File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", relativePath));
    }
}
