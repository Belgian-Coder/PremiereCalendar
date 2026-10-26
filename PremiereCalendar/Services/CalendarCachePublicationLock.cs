using System.Diagnostics;

namespace PremiereCalendar.Services;

internal static class CalendarCachePublicationLock
{
    private static readonly AsyncLocal<PublicationScope?> Current = new();
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static async Task RunAsync(
        string directory, Func<CancellationToken, Task> publish, CancellationToken cancellationToken)
    {
        var fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        cancellationToken.ThrowIfCancellationRequested();
        for (var scope = Current.Value; scope is not null; scope = scope.Parent)
        {
            if (scope.Active && PathComparer.Equals(scope.Directory, fullDirectory))
            {
                await publish(cancellationToken);
                return;
            }
        }

        Directory.CreateDirectory(fullDirectory);
        var lockPath = Path.Combine(fullDirectory, ".publish.lock");
        var started = Stopwatch.GetTimestamp();
        FileStream handle;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                handle = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                break;
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(30))
                    throw new TimeoutException("Calendar cache publication is still busy.", exception);
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            }
        }

        await using (handle)
        {
            var parent = Current.Value;
            var active = new PublicationScope(fullDirectory, parent);
            Current.Value = active;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                await publish(cancellationToken);
            }
            finally
            {
                // Detached execution contexts may retain this object. They must
                // acquire a new OS lock after the original owner has released it.
                active.Active = false;
                Current.Value = parent;
            }
        }
    }

    private static bool IsSharingViolation(IOException exception)
        => (exception.HResult & 0xffff) is 11 or 32 or 33;

    private sealed class PublicationScope(string directory, PublicationScope? parent)
    {
        public string Directory { get; } = directory;
        public PublicationScope? Parent { get; } = parent;
        public volatile bool Active = true;
    }
}
