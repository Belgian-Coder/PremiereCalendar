using System.Diagnostics;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class ProviderWorkSchedulerTests
{
    [WindowsPublicationFact]
    public async Task PublicationGateHonorsAnotherProcessFileLockAndCancellation()
    {
        var directory = Path.Combine(_root, "external-publication");
        Directory.CreateDirectory(directory);
        var ready = Path.Combine(_root, "holder-ready");
        var release = Path.Combine(_root, "holder-release");
        var script = Path.Combine(_root, "hold-publication.ps1");
        await File.WriteAllTextAsync(script, """
            param([string]$Directory, [string]$Ready, [string]$Release)
            $stream = [System.IO.File]::Open((Join-Path $Directory '.publish.lock'), [System.IO.FileMode]::OpenOrCreate, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
            try {
                [System.IO.File]::WriteAllText($Ready, 'ready')
                while (-not [System.IO.File]::Exists($Release)) { Start-Sleep -Milliseconds 20 }
            } finally { $stream.Dispose() }
            """);
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, directory, ready, release })
            start.ArgumentList.Add(argument);
        using var holder = Process.Start(start)!;
        try
        {
            await WaitUntilAsync(() => Task.FromResult(File.Exists(ready)));
            var invoked = false;
            using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(150));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CalendarCachePublicationLock.RunAsync(directory, _ =>
            {
                invoked = true;
                return Task.CompletedTask;
            }, cancellation.Token));
            Assert.False(invoked);
            Assert.False(holder.HasExited);
        }
        finally
        {
            await File.WriteAllTextAsync(release, "release");
            try { await holder.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { holder.Kill(entireProcessTree: true); await holder.WaitForExitAsync(); }
        }
        Assert.True(holder.ExitCode == 0, await holder.StandardError.ReadToEndAsync());
        await CalendarCachePublicationLock.RunAsync(directory, _ => Task.CompletedTask, CancellationToken.None);
    }

    [Fact]
    public async Task PublicationGateReentersWhileActiveAndReacquiresForAnExpiredDetachedContext()
    {
        var directory = Path.Combine(_root, "reentrant-publication");
        var startDetached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var detachedEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task detached = Task.CompletedTask;
        var nested = false;
        await CalendarCachePublicationLock.RunAsync(directory, async token =>
        {
            await CalendarCachePublicationLock.RunAsync(directory, _ =>
            {
                nested = true;
                return Task.CompletedTask;
            }, token);
            detached = Task.Run(async () =>
            {
                await startDetached.Task;
                await CalendarCachePublicationLock.RunAsync(directory, _ =>
                {
                    detachedEntered.TrySetResult();
                    return Task.CompletedTask;
                }, CancellationToken.None);
            });
        }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(nested);
        var holderEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHolder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = CalendarCachePublicationLock.RunAsync(directory, async _ =>
        {
            holderEntered.TrySetResult();
            await releaseHolder.Task;
        }, CancellationToken.None);
        await holderEntered.Task;
        try
        {
            startDetached.TrySetResult();
            Assert.NotSame(detachedEntered.Task, await Task.WhenAny(detachedEntered.Task, Task.Delay(150)));
        }
        finally { releaseHolder.TrySetResult(); }
        await holder.WaitAsync(TimeSpan.FromSeconds(5));
        await detached.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(detachedEntered.Task.IsCompletedSuccessfully);
    }

    private sealed class WindowsPublicationFactAttribute : FactAttribute
    {
        public WindowsPublicationFactAttribute()
        {
            if (!OperatingSystem.IsWindows()) Skip = "This cross-process FileShare.None check uses Windows PowerShell.";
        }
    }
}
