# Image refresh and detailed trace storage

The production image cache serves an existing immutable image generation immediately,
including an expired poster. An expired response has a one-minute browser lifetime
and schedules one refresh per metadata path. The application owns the shared
`ImageRefreshQueue`; its defaults allow two workers and 64 waiting images. When the
queue is full, the stale image remains available and a later request can retry.

Explicit refresh still waits for the key gate and fetches the remote image. A queued
ordinary refresh rechecks freshness under that gate, so a completed explicit refresh
can satisfy it. A refresh already running completes before a waiting explicit
refresh. Downloads publish content-addressed immutable bodies before atomically
moving the metadata pointer. Failed or cancelled work leaves the previous pointer
and body intact. Existing decode, download and staging limits remain shared.

Shutdown stops admission, cancels refresh requests and awaits every worker. A browser
disconnect does not cancel a shared refresh. Direct callers constructing a cache
without the hosted queue retain the synchronous fallback behavior.

Detailed diagnostics capture every event; the OpenTelemetry trace sample ratio does
not sample this stream. Producers enqueue immutable fixed-schema snapshots without
JSON serialization or file I/O. Queue admission enforces both an event count and an
estimated retained-memory budget (`PerformanceQueueMemoryMegabytes`, default 16).
The estimate includes record overhead and all referenced strings; a single snapshot
cannot exceed 16 KiB. The writer alone serializes records and retains the 8192-byte
NDJSON record limit. Oversized or unrepresentable records count as drops; serialization
failures have their own health counter and do not stop later events. FIFO flush
barriers, disk failure counters and durable `diagnostics.loss` records remain active.

Closed detailed traces older than `PerformanceArchiveAfterMinutes` (default 10) are
eligible for gzip compression, at most one trace per minute. Copying and indexing
honor independent archive-worker cancellation so a backlog cannot monopolize event persistence. Compression can be
disabled with `PerformanceArchiveClosedFiles`. Operational logs are unaffected.
Each archive has a small versioned index with UTC and process-elapsed bounds,
instance identity, record/loss/malformed counts and completeness. The raw trace stays
authoritative until the archive and index are flushed and published; interrupted
publication is safe to retry. Incompressible traces remain raw. Writers and archivers share an exclusive per-trace lease, including on Unix,
so another process's active trace cannot be archived or removed by retention.

Retention counts logical traces, includes archive/index/interrupted-publication
bytes, and uses the same total retained-byte budget as before. Compression stages
one trace at a time; its temporary allowance is bounded by twice the configured
per-file limit plus 4096 bytes beyond that retained budget. Existing crash remnants
are counted in retention. Only generated trace names and historical date-based
trace names are eligible for retention; unrelated files remain untouched.

`eng/Read-CalendarPerformance.ps1` reads current NDJSON, old NDJSON and gzip archives.
When both raw and gzip exist it reads the raw trace once. Valid complete indexes
allow it to skip archives entirely before the requested UTC window. Missing or
invalid indexes fall back to a full scan. It reports archive counts, skipped
archives, malformed/unterminated records, rotation races, loss markers and omitted
events. `CompleteObservedWindow` describes the observed retained files only; it
cannot establish evidence that retention has already removed. Unindexed archives
remain readable but cannot certify complete evidence.

Focused coverage exercises stale response latency during a blocked refresh,
deduplication, queue limits, explicit-refresh ordering, shutdown, serialization
failure recovery, snapshot memory limits, lossless compression, interrupted index
publication and mixed raw/archive analyzer compatibility. Source validation does
not establish installed or browser-visible performance.

## Bounded traffic and completion queries

Use an explicit UTC interval for reproducible calendar-month analysis. `SinceUtc` is
inclusive and `UntilUtc` is exclusive; its default is captured once at invocation.
Archive indexes can skip files entirely before or after that interval.

```powershell
./eng/Read-CalendarPerformance.ps1 -LogDirectory ./traces -CacheAudit `
    -SinceUtc '2026-11-01T00:00:00Z' -UntilUtc '2026-12-01T00:00:00Z'
```

`Traffic.Providers` separates actual status-coded HTTP responses, logical request
starts/completions, attempt starts/completions, header failures without a status and
local admission deferrals. A retry can produce several responses for one logical
request. A local deferral produces no HTTP response. `Pages[].ProviderTraffic`
attributes durable job traffic to every page attached to that job; shared-job page
counts overlap, while the overall window counts each event once.

Optional `-ProviderResponseBudgets @{ tmdb = 500; fanart = 100 }` compares observed
responses with limits supplied by the caller for the selected window. No default
limits are invented. A known count above a limit is `exceeded`; missing provider
evidence or dropped, malformed, rotated or omitted records make a count below the
limit `unknown`. `within_budget` means the supplied limit was met by the observed
retained window; retention may already have removed older evidence.

`Completion` separates background deadlines, navigation cancellation, disposal and
unfinished page observations. It lists each job's final durable outcome, full
execution durations and durable-write operation durations. These use existing
trace events: a durable state transition taking milliseconds does not mean the
provider execution also took milliseconds. Checkpoint and publication operations
may overlap other work and must not be added into an end-to-end duration.

Archival is supervised separately from active event writing. One worker and one pending trigger bound maintenance work; archive lease contention skips the attempt without reporting trace loss. `ArchiveFailures`, `ArchiveActive`, `ArchivePending` and `ArchiveWorkerRunning` distinguish maintenance problems from writer/serialization/drop counters. Per-trace leases and a short publication gate prevent retention from deleting a live archive generation or an archive from resurrecting an evicted trace.
