namespace PremiereCalendar.Services;

public sealed class SystemStatusService(
    ProviderWorkStore workStore,
    AdaptiveProviderPolicy providerPolicy,
    DatabaseRecoveryState databaseState,
    PremiereTelemetry telemetry)
{
    public async Task<SystemStatusSnapshot> GetAsync(CancellationToken cancellationToken)
    {
        var jobs = await workStore.GetStateCountsAsync(cancellationToken);
        var queued = Count(jobs, "Queued") + Count(jobs, "RetryWaiting");
        var running = Count(jobs, "Running");
        telemetry.SetSchedulerCounts(queued, running);
        return new SystemStatusSnapshot(
            jobs,
            providerPolicy.GetSnapshots(),
            databaseState.Snapshot,
            BuildVersionInfo.Current);
    }

    private static int Count(IReadOnlyDictionary<string, int> values, string key)
        => values.TryGetValue(key, out var value) ? value : 0;
}

public sealed record SystemStatusSnapshot(
    IReadOnlyDictionary<string, int> Jobs,
    IReadOnlyList<ProviderAdaptiveSnapshot> Providers,
    DatabaseStatusSnapshot Database,
    BuildVersionInfo Runtime);
