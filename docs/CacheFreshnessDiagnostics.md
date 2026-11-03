# Verify cache freshness and reuse

Detailed performance logging runs automatically. The application records item decisions, provider outcomes, component fetches, database projection acceptance and reconciliation reasons, correlated by load/job/operation IDs. Inspect `/health/version` first: `performanceDiagnostics.healthy` must be true, and drops, writer failures and operational logging failures must be zero. A healthy logger does not imply healthy providers.

## Read a recent session

Run from the repository in PowerShell. Choose an explicit UTC cutoff and, when available, the process instance ID from a performance-log filename:

```powershell
./eng/Read-CalendarPerformance.ps1 `
  -LogDirectory '\\NAS\Docker\PremiereCalendar\app-data\logs\performance' `
  -SinceUtc '2026-11-03T17:15:00Z' `
  -InstanceId '<instance GUID>' -CacheAudit |
  ConvertTo-Json -Depth 12
```

Use `-LoadId '<load GUID>'` or `-JobId '<job GUID>'` to follow one operation. `-WeekStart '2026-11-02'` selects a week and follows its recorded job attachments. Omit `-CacheAudit` to inspect ordered individual events. The default retained-selection bound is 100,000 events; increase `-MaximumEvents` up to 500,000 when necessary.

`Pages` separates the first nonempty cached result from final completion or navigation cancellation. Durations are server-side, rather than browser paint times. `StatusCodedResponses` counts observed HTTP responses; failed attempts with no observed status are not confirmed network downloads. `SchedulerOutcomes` includes background jobs, whose durations can overlap and must not be summed as elapsed time.

The audit explicitly reports omitted events, malformed/incomplete records, invalid timestamps, file rotation and diagnostic loss markers. No events in a window are **no evidence**, rather than proof of success. A missing refresh event alone cannot prove a cached item was current. Keep the requested time window and process identity with any saved result.

## Interpret item and provider outcomes

| Event | Evidence |
|---|---|
| `item_cache.item` | Whether this occurrence reused data or required metadata, availability or scores. Repeated decisions are not unique titles. |
| `item_cache.decision` | Component-level refresh/reuse/cooldown decision and fixed reason. |
| `item_cache.component` | Provider/component fetch success, verified absence, reuse or preservation after failure. A successful fetch can still lose a concurrent write race. |
| `item_cache.result` | Whether proposed rating values changed, with a changed-field count and no raw scores; combine with accepted projection evidence. |
| `item_cache.write.component` | Accepted provider payload and clock after the database merge, including `retained_newer` or `merged_projection` when concurrent work changed another component. Requested and accepted fetch clocks distinguish a rejected older proposal from a successful independent update. This does not claim which writer won an identical-result race. |
| `item_cache.write.item` | Aggregate projection acceptance or concurrency rejection, with revision and fixed reason. |
| `item_cache.reconcile.item` | Numeric item ID and reason for requiring discovery, including an invalidation-version or canonical-date mismatch. An initial stale-read mismatch can be expected; a repeated final publication block needs investigation. |
| `calendar.cache.incomplete` | Distinguishes source failure from item-cache validation or both. A partial snapshot must not be described as complete. |
| `calendar.cache.degraded` | Whether otherwise validated results and unavailable-source notices were persisted for bounded reuse. Coverage remains partial. |
| `calendar.cache.validated_partial` | Whether the final validated partial projection was saved or fenced out by a newer file generation. Proven out-of-week premieres must not return through an older file union, including when no rows remain. |
| `source.retry.policy` | A mixed optional access denial and unrelated non-deferred failure ends as a non-reusable partial result instead of borrowing the denial deadline to requeue forever. A genuine quota/circuit recovery deadline remains deferred. |
| `provider.outcome` | Verified response or absence, disabled/not-configured provider, access denial, rate limit, invalid response, timeout or locally deferred work. |
| `provider.admission.deferred` | A shared provider cooldown/circuit prevented a send; retry time is recorded when known. |

Use the fixed reason, numeric HTTP status and `retryAtUtc` together. For example, 401/403 access denials and 429 quota responses are not confirmed title absence; repeated local deferrals are not additional HTTP requests. Deliberately disabled providers are distinct from verified missing scores. Unknown/challenge/error HTML must not clear retained scores.

`direct_degraded` cache decisions and `scheduler.job.completed` with outcome `degraded` mean the operation ended with explicitly incomplete optional-source coverage. They are not complete discovery. Repeated visits before the stored access-denial deadline should keep the warning and avoid discovery jobs. After expiry, a visit or normal warmup may retry; recovered complete snapshots remove the notice.

An outcome of `partial` ends an operation with other source failures and does not authorize degraded cache reuse. An optional access-denial deadline cannot by itself keep an unrelated non-deferred failure requeueing forever. Actual quota/circuit recovery deadlines still defer work. HTTP response records remain the evidence for service calls; a pre-send local deferral has no response.

Per-item write-result timings refer to their shared batch write and winner reread. Several records can therefore carry the same overlapping batch duration; summing them would overstate elapsed database work.

An item-level cache hit can still contain a deferred component. A rating result of `reused` means its values did not change; it does not by itself prove that no request was made. Use component decisions and status-coded HTTP responses for that distinction.

To prove a stale component was replaced, follow its item ID from a due decision through successful fetch or verified absence to an accepted component projection and the final reconciled result. To prove failure preservation, inspect `preserved_failed` and the provider-specific clock/value regression tests; a failed provider must not gain a success timestamp. To prove reuse, repeat the same week within its component freshness windows and compare due decisions and status-coded requests, while accounting for explicitly deferred providers and independently scheduled background work.

Metadata and ratings remain retained in the database for three calendar months since last use. Their successful-refresh clocks and provider retry deadlines are separate from that retention period. Expired or invalidated values can remain available as last-good data while a failed provider is deferred; they must not be labeled newly fetched.

Availability provenance is separate from clock tracking: an unrelated refresh cannot make legacy sources Watchmode-owned. A provider's verified-empty artwork result cannot withdraw another owner's image. Remembered owner-absence evidence applies only to the retained poster/source and resets when a new image wins. Retained images and legacy availability keep their original successful clocks.

The standalone analyzer regression can be run with:

```powershell
./tests/PerformanceLogAudit.Tests.ps1
```

Its fixture checks load/job correlation, status-coded response counting, explicit retry deadlines, component clearing/reconciliation/rejection evidence, hidden files, malformed tails, truncation and an empty window. It leaves its temporary fixture directory for inspection.
