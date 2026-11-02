namespace PremiereCalendar.Services;

/// <summary>Flows the scheduler's live priority to provider admission queues.</summary>
public static class ProviderExecutionContext
{
    private static readonly AsyncLocal<Func<ProviderWorkPriority>?> Priority = new();
    private static readonly Func<ProviderWorkPriority> Foreground = () => ProviderWorkPriority.Foreground;

    public static IDisposable Push(Func<ProviderWorkPriority> priorityAccessor)
    {
        ArgumentNullException.ThrowIfNull(priorityAccessor);
        var previous = Priority.Value;
        Priority.Value = priorityAccessor;
        return new Scope(previous);
    }

    public static Func<ProviderWorkPriority> CapturePriorityAccessor() => Priority.Value ?? Foreground;

    private sealed class Scope(Func<ProviderWorkPriority>? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            Priority.Value = previous;
        }
    }
}
