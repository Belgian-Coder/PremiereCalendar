using System.Diagnostics;
using PremiereCalendar.Models;

namespace PremiereCalendar.Services;

public sealed partial class PremiereService
{
    private async Task<IReadOnlyDictionary<string, IReadOnlyList<PremiereItem>>> ValidateSourceCheckpointsAsync(
        IReadOnlyDictionary<string, IReadOnlyList<PremiereItem>> checkpoints, CancellationToken cancellationToken)
    {
        if (checkpoints.Count == 0 || _itemCacheSession.Value is not { } session) return checkpoints;
        var started = Stopwatch.GetTimestamp();
        var rows = checkpoints.SelectMany(source => source.Value.Select(item => (source.Key, Item: item))).ToArray();
        var dirty = session.ForceDiscoveryRefresh;
        IReadOnlyList<PremiereItem> reconciled = rows.Select(row => row.Item).ToArray();
        if (!dirty)
        {
            await session.PrepareAsync(rows.Select(row => new ItemEnrichmentIdentity(row.Item.MediaType, row.Item.TmdbId)),
                reconciled, cancellationToken);
            await session.FlushAsync(cancellationToken, rejectGenerationConflicts: false);
            var validation = await session.ReconcileAsync(reconciled, includeConsumed: false, cancellationToken);
            reconciled = validation.Items;
            dirty |= validation.DirtyMedia.Count > 0;
            foreach (var row in rows)
            {
                var item = row.Item;
                if (item.EnrichmentVersion is not { } version
                    || session.Find(new(item.MediaType, item.TmdbId)) is not { } current
                    || version.Generation == current.Record.Generation) continue;
                dirty = true;
                _telemetry.RecordDiagnostic("source.checkpoint.item", details: new()
                {
                    ItemId = item.TmdbId, MediaType = item.MediaType.ToString(), Stage = "checkpoint",
                    Outcome = "discarded", Reason = "metadata_generation_changed",
                    DurationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds
                });
            }
        }
        _telemetry.RecordDiagnostic("source.checkpoint", details: new()
        {
            Count = checkpoints.Count, Total = rows.Length, Outcome = dirty ? "discarded" : "reused",
            Reason = dirty ? "discovery_required" : "item_versions_current",
            DurationMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds
        });
        if (dirty)
        {
            // A completed source also represents membership (including removals).
            // Rebuild the cohort with fresh discovery; final reconciliation still
            // owns every invalidation and canonical-date publication fence.
            session.ForceDiscoveryRefresh = true;
            return new Dictionary<string, IReadOnlyList<PremiereItem>>(StringComparer.Ordinal);
        }
        var result = checkpoints.ToDictionary(source => source.Key, _ => (IReadOnlyList<PremiereItem>)[], StringComparer.Ordinal);
        foreach (var source in rows.Select((row, index) => (row.Key, Item: reconciled[index])).GroupBy(row => row.Key))
            result[source.Key] = source.Select(row => row.Item).ToArray();
        return result;
    }
}
