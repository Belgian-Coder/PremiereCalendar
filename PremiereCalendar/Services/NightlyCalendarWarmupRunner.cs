using Microsoft.Extensions.Options;
using PremiereCalendar.Models;
using PremiereCalendar.Options;

namespace PremiereCalendar.Services;

public enum NightlyCalendarWarmupOutcome { Complete, AlreadyFresh, Deferred, Superseded }

internal sealed class NightlyCalendarWarmupSupersededException()
    : OperationCanceledException("This nightly calendar window has been superseded by a newer night.");

public sealed class NightlyCalendarWarmupRunner(
    IPremiereLoadPipeline pipeline,
    ICalendarCacheMaintenance cache,
    TimeProvider timeProvider,
    ILogger<NightlyCalendarWarmupRunner> logger,
    PremiereTelemetry telemetry,
    IOptionsMonitor<CalendarWarmupOptions> options,
    IAppStateStore stateStore)
{
    public async Task<NightlyCalendarWarmupOutcome> RunAsync(NightlyCalendarWarmupPayload payload, string jobId, ProviderWorkResumeState resume,
        Func<PremiereLoadProgress, Task> publish, CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        var outcome = "failed";
        var rows = 0;
        logger.LogInformation("Nightly calendar window {NightDate} {Profile} {StartDate} through {EndDate} started, job {ProviderWorkJobId}.",
            payload.NightDate, payload.Profile, payload.Start, payload.End, jobId);
        try
        {
            ThrowIfSuperseded(payload);
            // A receipt proves this night actually rebuilt the snapshot; a plain
            // cache timestamp could come from an older foreground checkpoint.
            if (await NightlyCalendarWarmupPlanner.IsFreshAsync(cache, stateStore, payload, cancellationToken))
            {
                outcome = "already_fresh";
                return NightlyCalendarWarmupOutcome.AlreadyFresh;
            }
            var publishedOwnedSnapshot = false;
            var guardedResume = resume with
            {
                RefreshCalendarSnapshot = true,
                PublishCacheAsync = async (publish, token) =>
                {
                    Task GuardedPublish(CancellationToken ownedToken)
                    {
                        // This runs inside the scheduler's publication/lease
                        // fence, after waiting for any competing cache writer.
                        ThrowIfSuperseded(payload);
                        return publish(ownedToken);
                    }
                    if (resume.PublishCacheAsync is not { } publishOwned)
                        throw new InvalidOperationException("Nightly calendar snapshots require a scheduler publication fence.");
                    await publishOwned(GuardedPublish, token);
                    ThrowIfSuperseded(payload);
                    publishedOwnedSnapshot = true;
                }
            };
            PremiereLoadProgress? final = null;
            await foreach (var progress in pipeline.StreamCoreAsync(payload.Start, payload.End, false, payload.Filters,
                               guardedResume, cancellationToken).WithCancellation(cancellationToken))
            {
                rows = progress.Items.Count;
                if (progress.IsFinal) final = progress;
                await publish(progress);
            }
            if (final is null) throw new InvalidOperationException("Nightly calendar window ended without final discovery progress.");
            if (final.HasSourceErrors && final.RetryAfterUtc > timeProvider.GetUtcNow())
            {
                outcome = "provider_deferred";
                return NightlyCalendarWarmupOutcome.Deferred; // The scheduler durably defers to the recovery deadline.
            }
            if (final.HasSourceErrors || !publishedOwnedSnapshot
                || !await NightlyCalendarWarmupPlanner.HasCompleteSnapshotAsync(cache, payload, cancellationToken))
                throw new InvalidOperationException("Nightly calendar window did not publish a complete cache for this night.");
            ThrowIfSuperseded(payload);
            // Outside the ownership transaction: this uses its own DB connection.
            // A crash before this write safely repeats TTL-respecting discovery.
            await stateStore.SetValueAsync(payload.ReceiptKey, payload.ReceiptValue, cancellationToken);
            outcome = "complete";
            return NightlyCalendarWarmupOutcome.Complete;
        }
        catch (NightlyCalendarWarmupSupersededException)
        {
            outcome = "superseded";
            return NightlyCalendarWarmupOutcome.Superseded;
        }
        catch (OperationCanceledException)
        {
            outcome = "interrupted";
            throw;
        }
        finally
        {
            var elapsed = timeProvider.GetElapsedTime(started);
            logger.LogInformation("Nightly calendar window {NightDate} {Profile} {StartDate} through {EndDate} finished: {Outcome}, {RowCount} rows, {DurationMilliseconds} ms, job {ProviderWorkJobId}.",
                payload.NightDate, payload.Profile, payload.Start, payload.End, outcome, rows, elapsed.TotalMilliseconds, jobId);
            telemetry.RecordJob("NightlyCalendarWindow", outcome, elapsed);
        }
    }

    private void ThrowIfSuperseded(NightlyCalendarWarmupPayload payload)
    {
        var settings = options.CurrentValue;
        var occurrence = NightlyCalendarWarmupPlanner.MostRecentOccurrence(timeProvider.GetUtcNow(), settings);
        if (!settings.Enabled || !settings.NightlyEnabled || payload.NightDate != occurrence.NightDate
            || payload.RefreshNotBeforeUtc != occurrence.CutoffUtc)
            throw new NightlyCalendarWarmupSupersededException();
    }
}
