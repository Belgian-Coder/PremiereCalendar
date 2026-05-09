using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public sealed record PremiereLoadProgress(
    string SourceName,
    int SourceItemCount,
    int TotalItemCount,
    IReadOnlyList<PremiereItem> Items,
    bool IsFinal = false,
    bool FromCache = false,
    int? CompletedWork = null,
    int? TotalWork = null,
    string? ProgressText = null,
    long? ElapsedMilliseconds = null)
{
    public string ProviderKey { get; init; } = SourceName;
    public string Phase { get; init; } = IsFinal ? "complete" : FromCache ? "cache" : "loading";
    public int? AcceptedCount { get; init; } = SourceItemCount;
    public int? TotalCandidateCount { get; init; } = TotalWork;
    public int? ProcessedCount { get; init; } = CompletedWork;
    public int? FilteredCount { get; init; }
    public int? UnmappedCount { get; init; }
    public int? NetNewCount { get; init; } = SourceItemCount;
    public bool IsBackground { get; init; }
    public IReadOnlyList<PremiereItem> SourceItems { get; init; } = Items;
    public bool HasSourceErrors { get; init; }
    public IReadOnlyList<string> FailedSourceNames { get; init; } = [];
}
