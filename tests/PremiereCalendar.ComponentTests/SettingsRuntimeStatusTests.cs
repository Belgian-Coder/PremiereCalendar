using Bunit;
using PremiereCalendar.Components.Shared;
using PremiereCalendar.Services;

namespace PremiereCalendar.ComponentTests;

public sealed class SettingsRuntimeStatusTests : BunitContext
{
    [Fact]
    public void UnhealthyDatabaseShowsThePostgreSqlRecoveryGuidanceInsteadOfWindowsServiceAdvice()
    {
        var status = Snapshot(healthy: false);

        var cut = Render<SettingsRuntimeStatus>(parameters => parameters.Add(component => component.Status, status));

        var text = cut.Find("[data-testid='runtime-status']").TextContent;
        Assert.Contains(DatabaseRecoveryState.OperatorGuidance, text, StringComparison.Ordinal);
        Assert.Contains("database verify", text, StringComparison.Ordinal);
        Assert.Contains("checksum-verified pg_dump", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Windows Service", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("backup path", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HealthyDatabaseShowsNoRecoveryGuidance()
    {
        var cut = Render<SettingsRuntimeStatus>(parameters => parameters.Add(component => component.Status, Snapshot(healthy: true)));

        var text = cut.Find("[data-testid='runtime-status']").TextContent;
        Assert.Contains("healthy", text, StringComparison.Ordinal);
        Assert.DoesNotContain("pg_dump", text, StringComparison.Ordinal);
    }

    private static SystemStatusSnapshot Snapshot(bool healthy) => new(
        new Dictionary<string, int>(),
        [],
        new DatabaseStatusSnapshot(4, 4, healthy, "PostgreSQL status message.", null),
        BuildVersionInfo.Current);
}
