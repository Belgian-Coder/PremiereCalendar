# Calendar speed improvements — November 2, 2026

This implements the five approved follow-ups for local release 1.2.9. The proposed first-nonempty rendering change was excluded; no component code changed. Installed release identity and acceptance are recorded separately in the release record.

## Behavior

- Returning to an already running calendar job attaches to and promotes that execution. It keeps its provider work, lease owner and checkpoints. A genuinely different foreground request can still preempt background execution, and the last watcher can still detach after promotion.
- Provider admission uses the live priority of the waiting work. A shared single-flight request inherits its highest-priority remaining waiter. FIFO lanes favour watched foreground work, with one background admission after eight foreground admissions under contention. Existing provider concurrency, rate, cooldown, circuit and retry limits are retained.
- A refresh generation reuses successful exact-key RT/OMDb responses and valid TMDb details through mapping and backfill, including verified missing scores/IDs. Successful absence is distinct from timeout, malformed data and stale fallback. A later explicit force refresh fetches again. Different media/title/year/external identities remain independent, and memo validation respects cache replacement and original freshness.
- Missing RT-selected cached scores hydrate in two joined workers. Duplicate identities resolve once; input order, saved non-null scores and local IMDb batching are preserved. A valid durable RT response, including verified absence, can hydrate a complete week after restart without HTTP or a foreground job attachment. Other score selections retain the fast memory-only optional path.
- Large checkpoints use Brotli Fastest only when the full encoded envelope is smaller. The decoded content remains the complete compact v3 snapshot. Compression does not change the two-second cadence, immediate source/final/preempt persistence, ownership fence or resume semantics. Numeric diagnostics separate raw/stored bytes, serialization, encoding and storage time.

## Recovery and downgrade

The database schema stays at version 3. The optional v4 checkpoint storage envelope is understood by the new binary and has bounded, exact-length decompression. Existing v1-v3 checkpoints remain readable; oversized or incompressible checkpoints stay raw.

An older binary cannot decode v4 checkpoint rows. Before a downgrade:

1. Take and strictly verify the normal PostgreSQL backup and preserve the current stack configuration.
2. Stop only the app and ensure its workers have stopped. Keep PostgreSQL and the backup sidecar running.
3. Run the new release binary against the same configured database:

   `dotnet PremiereCalendar.dll database prepare-legacy-checkpoints`

4. The command refuses unexpired worker leases. It locks jobs/leases transactionally, validates all compressed payloads, and changes only checkpoint encoding to exact v3 JSON. Malformed data aborts the entire conversion. It preserves job rows, unknown JSON properties, states, dates, attempts and lease metadata; it deletes nothing.
5. Verify the command's success/counts, select the previous accepted image pin and start only the app. Check readiness, version, database and logging health.

Run this command using the new image, after stopping the app and before starting the older image. Do not use the older image to perform conversion. The owning NAS runbook records the installed pins, backup paths and Portainer revision.

## Validation

The final local Linux runs passed 573 unit, 267 integration and 174 component tests: 1,014 passes, with one expected Windows-only file-lock skip. The integration run used isolated PostgreSQL, including the downgrade transaction, lease refusal and blocked-claim checks. Focused provider-admission/shared-flight tests and client/cache tests also passed. The source review caught malformed OMDb payloads incorrectly qualifying for memo reuse; explicit success/verified-absence checks and retry regressions were added before final validation.

Coverage includes same-job reattachment before/after execution starts, watcher multiplicity, distinct-job preemption, live provider-priority changes, capacity/fairness/cooldown/cancellation, same-generation no-score reuse, later-generation refresh, different/new identities, normal/forced publication races, durable restart hits, hydration ordering and cancellation, full checkpoint metadata round-trip, legacy reads, corruption bounds, unchanged persistence cadence and ownership loss.

Controlled phase and real-PostgreSQL checkpoint measurements are retained with the release evidence. They are separate from a full cold-provider workload and browser paint. The historical captured-response replay directory was unavailable in this checkout; new deterministic fixtures compare the deployed 1.2.8 source with the candidate for the measured phases. No full cold-provider speedup is claimed from a fast complete-week cache read.

| Controlled phase | v1.2.8 baseline | Candidate | Preserved result |
| --- | ---: | ---: | --- |
| Eight distinct cached-score lookups, 12 rows, 200 ms per lookup | 1,620.01 ms | 809.13 ms | Eight calls, full normalized 12-row JSON identical; peak lookups 1 → 2 |
| Three passes of forced RT/OMDb successful no-score responses, 200 ms per provider | 608.60 ms | 206.90 ms | RT sends 3 → 1, OMDb sends 3 → 1; full response projection identical |
| 150-row checkpoint encoding plus lease-fenced PostgreSQL write | 27.72 ms | 24.47 ms | Complete v3 content; 307,920 → 20,103 stored bytes |
| 3,300-row checkpoint encoding plus lease-fenced PostgreSQL write | 215.94 ms | 75.76 ms | Complete v3 content; 7,257,017 → 500,277 stored bytes |

The first two rows are medians of three runs against the same fixed-delay fixtures, including the first run. The baseline uses source `2cf9da4` and the same benchmark compiled against that application, then runs against each application's assemblies separately. Only `LastUpdatedUtc` is removed from the full item projection. Its SHA-256 is identical across all six hydration runs: `85da31ece86e6228d20b360f50f2320225b015531de208b15c8bf2b95171978f`. The no-score response projection also matches across all runs.

Checkpoint medians use 25 samples per fixture/format against isolated PostgreSQL 18. They include production encoding and the real lease-fenced store call, with transaction count/cadence unchanged. The benchmark also retains a separate prototype comparison. Wire roundtrips were not instrumented. Source, raw JSON, logs and the acceptance review are retained under `D:\Projects\PremiereCalendar.Build.Validation-20261102\speed-implementation` and accompany the local release bundle.
