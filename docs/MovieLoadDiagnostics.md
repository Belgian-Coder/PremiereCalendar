# Slow movie-week load and persistent diagnostics

## Observed incident

Read-only investigation of the installed 1.2.3 container on 27 October 2026 confirmed the reported route: movies, 31 August–6 September 2026, selected day 2 September, English/Dutch, minimum runtime 45 minutes. The screenshot showed 141 titles and “not cached yet”.

Saved foreground jobs for this week repeatedly finished with `HasSourceErrors=true` and failed source `Simkl`. Two recent runs took approximately 45 and 70 seconds; queue waits were about 2 milliseconds. In the most recent run, Simkl's complete discovery/mapping source reached its 45-second deadline, while nonempty TMDb day sources took approximately 3–6 seconds. Simkl retained 31 source rows before failure. Existing records do not separate Simkl feed HTTP time from candidate mapping/enrichment time, so a network-only explanation is not established.

The source failure prevented complete week-cache publication. The two saved files for this historical week both contained series, with no matching movie-week envelope. Cards streamed before the source deadline, so visible results did not imply a committed complete cache. This explains both the badge and repeated work on later visits.

The inspected 1.2.3 instance's normal application NDJSON sink was inactive: the newest file ends with the previous instance's shutdown, and the current .NET process has no NDJSON descriptor. Docker's separate rotating stdout logs retain a smaller diagnostic window. The configured directory, permissions and disk capacity were checked. The underlying exception was subsequently reproduced in an isolated hardened container, as described below.

## Corrections

- A failed source can retain nonempty results as an explicitly partial week cache. Partial rows are provisional, never a fresh complete hit; refresh work still runs. Existing complete envelopes are preserved, including concurrent publications. Later successful refreshes can replace partial caches normally.
- Movie-only Simkl discovery requests movie feeds rather than TV feeds. All existing current and monthly archive coverage remains; at most two feed requests run concurrently. Successful feed batches can stream before a required-feed error is reported explicitly. Navigation/source cancellation cancels and observes pending work.
- Independent successful feed batches reach mapping before waiting for later feeds, including batches below the normal 100-candidate threshold. Ready movie feeds no longer wait for an earlier TV feed, while current-before-archive precedence stays deterministic within each media type. A real one-second source-deadline regression retains 31 successful rows. Typed circuit/cooldown deadlines survive feed errors and multiple failures, and invalid JSON cannot hide later successful feeds or caller cancellation.
- Cache metadata can update after provisional cards arrive, without delaying those cards or allowing an obsolete route to overwrite the badge.

The operational file-sink failure was reproduced independently in the hardened container: .NET 11 named-mutex initialization tried to create `/home/app/.dotnet` on the read-only root filesystem. The unchanged shared Serilog sink persisted zero of three probe events there, and all three with writable temporary HOME. The runtime image now sets `HOME=/tmp`, preserving read-only root and shared logging. The full application with that override persisted normal operational logs and reported zero logging failures, while the independent performance writer stayed healthy. Application data remains on its persistent mount.

## What is saved

Dedicated performance NDJSON is default-on and works without OTLP or sampled tracing. The configurable default is 25 MiB per file and 14 retained files, approximately 350 MiB maximum. Capture never waits for filesystem I/O: a background writer consumes a bounded queue of 4,096 records, at most 32 MiB of payload. Queue overflow is counted and later persisted as a loss marker. Buffer flushes run every two seconds and at shutdown; abrupt termination can lose queued/buffered records. Flush and shutdown waits are bounded to five seconds by default. Each process has an instance ID, UTC timestamps, monotonic elapsed times and event sequence numbers. Overlapping app instances need separate diagnostic directories.

Events include:

- page settings/preconditions, first result, foreground/background handoff, cancellation, failure, cache metadata, and render request/commit;
- service load/pipeline start, first result, progress, outcome, cache key/decision/publication and source progress/outcomes;
- queue/claim wait, execution, checkpoint/persistence, accepted durable state changes, lease renewal/loss and owned cache publication;
- individual provider attempts, capacity/cooldown waits, headers, body consumption or disposal, retries and final outcomes;
- per-item enrichment and movie detail/rating/Rotten Tomatoes/artwork stages; Simkl feed categories and outcomes.

Load IDs link page and service events. Attachment events link demanders to durable job IDs; source keys, operation IDs and request IDs join provider and enrichment records. Numeric public TMDb IDs can identify slow enrichment work. No titles, full URLs, query strings, credentials, response bodies or raw exception messages are written to this store. These detailed identifiers do not become metric labels.

`/health/version` exposes performance writer health, written/dropped events, failures, queue state, writer stalls, bounded flush/shutdown timeouts and last success/failure times. Health reads also avoid filesystem waits. A startup diagnostic verifies that the independent writer attempts actual persistence. Operational logging failures expose only a counter, UTC time and fixed reason code through Serilog self-diagnostics; raw sink messages are discarded.

Render completion means **server render commit**, not a browser paint or image-load measurement. HTTP body duration includes consumer delay between headers and consumption/disposal. Stage durations overlap; their sums are not calendar wall-clock time. Existing benchmark allocations remain separate from these saved production timings.

## Investigating a later load

On the installed container the default directory is `/app/App_Data/logs/performance`, backed by NAS app data. For Windows or a mounted NAS share:

```powershell
./eng/Read-CalendarPerformance.ps1 -LogDirectory '<performance directory>' -WeekStart '2026-08-31' -Summary
./eng/Read-CalendarPerformance.ps1 -LogDirectory '<performance directory>' -LoadId '<load GUID>'
```

The reader follows load-to-job links and shares active files safely with the writer/retention process. It keeps at most 10,000 matching events by default; `-MaximumEvents` can increase this to 100,000. Truncation emits a warning. Summary percentiles describe retained matching events, not all production traffic or a complete retention period.

Local Debug capture of 10,000 detailed events took approximately 293–339 ms, followed by 8–17 ms for durable drain/flush; producer allocations were approximately 1,957 bytes per event, with roughly 5.3 MiB saved including warmup. A default-capacity burst persisted all 10,000 with no drops. Controlled blocked-writer tests verify bounded overflow, health responsiveness and persisted loss markers. A permanently hung OS write may retain its background handle until the call returns or the process exits; capture and health remain responsive. Installed NAS overhead and retention duration depend on traffic and storage. Fine-grained history begins after installation and cannot reconstruct missing details from earlier loads.


## Validation of the combined candidate (2026-10-27)

The reviewed source passed 888 Windows tests (486 unit, 224 integration, 172 component, six browser) in one run. Linux passed 881 tests (485 unit, 224 integration, 172 component), with the Windows-only file-sharing lock test explicitly skipped. PostgreSQL coverage used an actual isolated PostgreSQL 18 container; it did not silently skip. Browser cases retained the existing day-switch, card-window, DOM, overflow and error budgets.

A hardened Linux candidate with baked-in `HOME=/tmp` passed PostgreSQL schema/readiness, root HTML and Blazor asset checks as UID 1654 with a read-only root, dropped capabilities and no-new-privileges. Both normal and performance NDJSON files actually received records; health reported zero operational logging failures, zero dropped performance events and no writer failures. The local NuGet audit passed with vulnerability warnings promoted to errors. Trivy found zero fixable HIGH/CRITICAL vulnerabilities; a CycloneDX SBOM was generated locally. No GitHub workflow was used.

Raw evidence is retained in `D:\Projects\PremiereCalendar.Build.DiagnosticsValidation`: `acceptance-reviewed-windows.log`, `acceptance-reviewed-linux.log`, `container-final-smoke.log`, `nuget-final-audit.log`, `trivy-final.json`, `sbom-final.cdx.json`, and the review reports. Tests and synthetic timings establish the candidate's behavior, not production first-card or completion percentiles. Live installation and traffic evidence must be recorded separately.

Remaining All-view limit: two slow TV requests can still occupy both fetch slots before an archive movie request starts. Already completed movie feeds stream independently, and movie-only loads never request TV feeds. Adaptive scheduling fairness is deferred; a fixed one-slot-per-media cap could waste capacity after one media type finishes.
