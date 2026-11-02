namespace PremiereCalendar.Services;

// Callers serialize access with their capacity/state lock. Priority is read when
// choosing the next admission, so promotion does not require replacing a waiter.
internal sealed class ProviderAdmissionQueue<T>
{
    private const int MaximumForegroundBurst = 8;
    private readonly LinkedList<Entry> _entries = new();
    private int _foregroundBurst;

    internal int Count => _entries.Count;

    internal sealed class Entry(T value, Func<ProviderWorkPriority> priority, CancellationToken cancellationToken)
    {
        private TaskCompletionSource? _signal;
        internal T Value { get; } = value;
        internal Func<ProviderWorkPriority> Priority { get; } = priority;
        internal CancellationToken CancellationToken { get; } = cancellationToken;
        internal LinkedListNode<Entry>? Node { get; set; }
        internal Task WaitTask => (_signal ??= new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;

        internal void Pulse()
        {
            var signal = _signal;
            _signal = null;
            signal?.TrySetResult();
        }
    }

    internal Entry Enqueue(T value, Func<ProviderWorkPriority> priority, CancellationToken cancellationToken)
    {
        var entry = new Entry(value, priority, cancellationToken);
        entry.Node = _entries.AddLast(entry);
        return entry;
    }

    internal Entry? PeekNext()
    {
        Entry? foreground = null;
        Entry? background = null;
        foreach (var entry in _entries)
        {
            if (entry.CancellationToken.IsCancellationRequested) continue;
            if (entry.Priority() == ProviderWorkPriority.Foreground) foreground ??= entry;
            else background ??= entry;
            if (foreground is not null && background is not null) break;
        }
        return background is not null && (foreground is null || _foregroundBurst >= MaximumForegroundBurst)
            ? background
            : foreground;
    }

    internal void Admit(Entry entry)
    {
        // A background admission resets the burst. Count only foreground work
        // admitted while background demand exists, bounding that demand to eight
        // foreground admissions without penalizing a later foreground arrival.
        _foregroundBurst = entry.Priority() == ProviderWorkPriority.Foreground
            && _entries.Any(other => !other.CancellationToken.IsCancellationRequested
                && other.Priority() != ProviderWorkPriority.Foreground)
            ? Math.Min(MaximumForegroundBurst, _foregroundBurst + 1)
            : 0;
        Remove(entry);
    }

    internal bool Remove(Entry entry)
    {
        if (entry.Node is null) return false;
        _entries.Remove(entry.Node);
        entry.Node = null;
        if (_entries.Count == 0) _foregroundBurst = 0;
        return true;
    }

    internal IEnumerable<Entry> Drain()
    {
        while (_entries.First is { } first)
        {
            Remove(first.Value);
            yield return first.Value;
        }
    }

    internal void PulseAll()
    {
        foreach (var entry in _entries) entry.Pulse();
    }
}
