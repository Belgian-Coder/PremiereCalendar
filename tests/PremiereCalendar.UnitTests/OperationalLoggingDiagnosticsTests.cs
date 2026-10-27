using System.Collections.Concurrent;
using System.Text.Json;
using PremiereCalendar.Hosting;
using Serilog.Debugging;

namespace PremiereCalendar.UnitTests;

public sealed class OperationalLoggingDiagnosticsTests
{
    [Theory]
    [InlineData("System.UnauthorizedAccessException: Permission denied", "permission")]
    [InlineData("Access to the path '/private' is denied.", "permission")]
    [InlineData("The shared file mutex could not be acquired", "mutex")]
    [InlineData("The named synchronization primitive is unavailable", "mutex")]
    [InlineData("System.IO.IOException: No space left on device", "io")]
    [InlineData("Read-only file system", "io")]
    [InlineData("System.IO.DirectoryNotFoundException", "io")]
    [InlineData("Unknown sink failure", "other")]
    public void ClassifiesWithoutRetainingRawMessage(string message, string expected)
    {
        var notices = new List<string>();
        var state = new OperationalLoggingDiagnosticState(notices.Add);
        state.Record(message + " api_key=TOP-SECRET; Password=PRIVATE-CREDENTIAL; https://private.example/user/title");
        Assert.Equal(1, state.Snapshot.FailureCount);
        Assert.Equal(expected, state.Snapshot.Reason);
        Assert.NotNull(state.Snapshot.LastFailureUtc);
        Assert.Equal(expected, Assert.Single(notices));
        var retained = JsonSerializer.Serialize(state.Snapshot) + string.Join('|', notices);
        foreach (var secret in new[] { "TOP-SECRET", "PRIVATE-CREDENTIAL", "private.example", "api_key", "user/title", message })
            Assert.DoesNotContain(secret, retained, StringComparison.Ordinal);
    }

    [Fact]
    public void ConcurrentFailuresCountEveryEventAndEmitOnlyOneFixedNotice()
    {
        var notices = new ConcurrentBag<string>();
        var state = new OperationalLoggingDiagnosticState(notices.Add);
        Parallel.For(0, 10000, _ => state.Record("System.IO.IOException secret-payload"));
        Assert.Equal(10000, state.Snapshot.FailureCount);
        Assert.Equal("io", state.Snapshot.Reason);
        Assert.Equal("io", Assert.Single(notices));
    }

    [Fact]
    public void UnavailableStderrDoesNotBreakLoggingOrRetryNotices()
    {
        var attempts = 0;
        var state = new OperationalLoggingDiagnosticState(_ => { attempts++; throw new IOException("sensitive IO exception"); });
        state.Record("System.IO.IOException sensitive-input");
        state.Record("System.IO.IOException sensitive-input");
        Assert.Equal(2, state.Snapshot.FailureCount);
        Assert.Equal(1, attempts);
        Assert.Equal("io", state.Snapshot.Reason);
    }

    [Fact]
    public void EnablingSelfLogFeedsPublicProcessSnapshot()
    {
        OperationalLoggingDiagnostics.Enable();
        var before = OperationalLoggingDiagnostics.Snapshot.FailureCount;
        SelfLog.WriteLine("System.IO.IOException omitted-sensitive-input");
        Assert.True(OperationalLoggingDiagnostics.Snapshot.FailureCount > before);
        var retained = JsonSerializer.Serialize(OperationalLoggingDiagnostics.Snapshot);
        Assert.DoesNotContain("sensitive", retained, StringComparison.Ordinal);
        Assert.DoesNotContain("IOException", retained, StringComparison.Ordinal);
    }
}
