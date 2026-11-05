# November 5 performance implementation and evidence

Status: v1.2.15 is installed on the NAS. Both requested high-effort reviews, local security/runtime gates and real-backup preservation checks passed. Cached display is fast; the observed full durable refresh still took 108.95 seconds and remained partial under provider restrictions. Builds/tests/scans ran locally; GitHub workflows remained disabled.

The original 20 requests and the additional 20-item table overlap. The grouped coverage below describes the resulting behavior rather than counting the same change twice. All configured providers remain enabled; none was removed to improve timings. Result/data preservation remains part of acceptance, and no production PostgreSQL, application-data or backup deployment was performed for these measurements.

## Coverage

| Area | Original and additional coverage | Result and evidence |
| --- | --- | --- |
| Enrichment critical path | Series parallelism; independent optional work | Independent ratings, artwork and availability overlap, with cancellation cleanup and unchanged full-row projections. Controlled measurements below exercise both artwork paths. |
| Provider failures | OMDb/Fanart diagnosis; early deferrals; Trakt 403 and Watchmode 429 | Fanart v3 `{}` is verified absence. OMDb invalid payloads retain good data and back off per title; force bypasses only that soft cooldown. Auth/quota deadlines remain enforced. External allowance boundaries are described below. |
| Component retry ownership | Separate ratings, TVmaze and artwork retries; deduplication | Provider identity and component clocks govern retries independently. A failed optional branch does not make successful core metadata stale or block an unrelated provider. |
| Optional response reuse | Persistent optional caches; Watchmode identity reuse | Fanart, TVmaze and Watchmode retain successful responses/absence with original expiry and forced-publication fences. Watchmode identity and availability have separate lifetimes. |
| Wikimedia | Full relevant claims; Commons deduplication | One entity fetch retains image and Rotten Tomatoes claims. Shared Commons requests retain file/license results without turning failures into absence. |
| Discovery reuse | TMDb paging/404 handling; SIMKL monthly feeds; IMDb batches | Normal subscribers share paged TMDb work with demand priority; verified missing entries are bounded and forced requests remain separate. SIMKL shares full feeds across transient clients. Local IMDb lookups coalesce in bounded batches. |
| Eligibility and preservation | Early movie eligibility; provider/projection preservation | Core preflight runs only for actual language/runtime filters. Distinct flight modes retain OMDb plot/cover ownership, optional completeness and proof clocks. A pacing-only setting does not cold-miss existing item partitions. |
| Foreground scheduling | Latest prefetch; bounded interest cleanup; observation expiry and handoff priority | Observations release only their own demand. A different active foreground request can checkpoint/preempt passive work, including when that request was queued before the final observer detached. |
| Durable source reuse | Cross-job checkpoints; fair retries; housekeeping | Exact compatible requests inherit fresh successful sources from a terminal partial attempt. Failed/expired sources remain retryable. Idle wakeups, count refreshes and obsolete queued prefetch work are bounded. |
| Startup and outages | Recovery supervision; database traffic circuit | Scheduler and database-using producers wait for initialization. Transient startup outages retry; best-effort prefetch failures do not stop foreground hosting. A genuine cache-store outage defers provider work before HTTP. |
| Store operations | Pure reconciliation; accepted writes; reduced database traffic | Validation reads do not touch retention or take write locks. Accepted winners return from the write transaction. Admission/open/commands have bounded deadlines; bulk budgets scale by work. Failed flushes restore pending components without replacing newer proposals. |
| Delta synchronization | Dirty-partition skips; cadence/budget/history checkpoints | Independent media/provider cadences retain progress in bounded sessions. Legitimate continuation waits do not consume failure attempts. Corrupt sessions rebuild; locally irrelevant TVmaze IDs do not create item marker traffic. |
| TVmaze fencing | Batched/local invalidation; active lookup protection | Unresolved/active lookups retain generation fences. Exact publication tokens expire after 30 minutes; stale proposals cannot publish, while foreign winners and saved idle absence remain intact. |
| Warmup horizon | Two-month warmup | The current and next calendar months are eligible; a preceding-month Monday boundary cannot expand the horizon. |
| Posters | Immediate stale posters; shared resource limits | Immutable stale bodies return while one bounded refresh is queued per key. Forced refresh ordering, download/decode/staging limits and joined shutdown remain covered. |
| Detailed diagnostics | Async capture; fine-grained retention; traffic/completion summaries | Every event remains eligible for capture. Bounded snapshots serialize on the writer; a separate archive worker compresses closed traces. Crash recovery preserves raw traces. The analyzer separates HTTP responses, attempts, requests, deferrals and completion causes, with explicit interval/budget uncertainty. |

Implementation details and configuration are in [discovery reuse](DiscoveryReuseImprovements.md), [provider clients](ProviderClientImprovements.md), [scheduler/recovery](SchedulerRecoveryImprovements.md), [images/traces](TraceImageImprovements.md) and [configuration](Configuration.md).

## Controlled before/after measurements

These are local fixtures with fixed fake provider delays, not live-provider percentiles. Baseline and candidate used the same measurement harness; their provider-measurement runs each passed four cases. No external HTTP or provider credits were used by these fixtures. Timed runs precede the final correctness corrections; later affected checks validate those corrections. The benchmarks were not repeated.

| Workload | Baseline | Candidate | Preservation check |
| --- | --- | --- | --- |
| SIMKL adjacent movie weeks, 100 ms fake HTTP | 453.04 ms; 5 sends | 321.57 ms; 3 sends | Same two week projections and three distinct feeds |
| SIMKL adjacent series weeks, 100 ms fake HTTP | 328.96 ms; 5 sends | 226.47 ms; 3 sends | Same two week projections and three distinct feeds |
| Series enrichment, backdrop artwork, 200 ms branch delays | 872.48 ms | 450.00 ms | Same full normalized row and provider-call counts |
| Series enrichment, candidate artwork, 200 ms branch delays | 1264.12 ms | 727.28 ms | Same full normalized row and provider-call counts |

Full projection hashes match between versions in every targeted case; only `LastUpdatedUtc` is normalized. Each series case retains one discover, one detail, two season, one OMDb, one TVmaze lookup, one TVmaze search, one RT, one artwork and one availability call. See the [baseline TRX](../artifacts/Improvements20261105/provider-measurements/baseline-providers.trx), [candidate TRX](../artifacts/Improvements20261105/provider-measurements/candidate-providers.trx) and their [baseline](../artifacts/Improvements20261105/provider-measurements/baseline.log)/[candidate](../artifacts/Improvements20261105/provider-measurements/candidate.log) logs.

The separate 2,000-row scheduler/cache workload used one matched pair per state, each in a separate process, without overlapping builds or tests:

| State | Baseline first / complete, ms | Candidate first / complete, ms | Baseline / candidate allocated bytes |
| --- | --- | --- | --- |
| Fresh | 153.35 / 154.05 | 141.21 / 141.88 | 14,299,984 / 14,276,136 |
| Stale | 150.72 / 3731.43 | 172.79 / 3623.32 | 389,781,496 / 390,323,104 |
| Cold | 698.73 / 2864.62 | 829.88 / 3151.18 | 196,673,024 / 196,509,848 |

All cases retain 2,000 rows. The fresh pass adds no job, performs no discovery HTTP and produces no measured job/lease/checkpoint row mutations. Stale/cold cases retain seven discovery calls and 18/16/11 job/lease/checkpoint mutations in both versions. Those mutation counts come from SQLite triggers; they are not physical disk-I/O measurements. The candidate's normalized identity/date/title/TMDb-score projection hash is identical across its three states.

Cold first-result and completion times are slower in this pair. There is **no established broad cold-load speedup**. The targeted provider fixtures establish the specific reuse/parallelism benefit; a single matched bulk pair cannot establish production latency distributions. Earlier samples with different run order or concurrent compilation are exploratory and are excluded from these claims.

Matched evidence: baseline [fresh](../artifacts/Improvements20261105/matched/baseline-fresh.trx), [stale](../artifacts/Improvements20261105/matched/baseline-stale.trx), [cold](../artifacts/Improvements20261105/matched/baseline-cold.trx); candidate [fresh](../artifacts/Improvements20261105/matched/candidate-fresh.trx), [stale](../artifacts/Improvements20261105/matched/candidate-stale.trx), [cold](../artifacts/Improvements20261105/matched/candidate-cold.trx).

## Executed validation

The final LF-source build completed once, then Unit, Component and Integration ran with `--no-build --no-restore`. Their fresh compiled rosters were reconciled only against those LF-run TRX files at the linked source-fingerprint checkpoint. This gives **1,738 passed and one expected Linux skip**, with no failed cases or missing evidence:

| Final roster | Passed | Skipped |
| --- | --- | --- |
| Unit | 997 | 1 |
| Component | 191 | 0 |
| Integration | 550 | 0 |

The final evidence uses only the three LF-run results, without mixing earlier compilation checkpoints. It also includes three current unit cases omitted by the older result-name reconciliation. Its [summary](../artifacts/Improvements20261105/acceptance/test-summary.json), [per-case evidence map](../artifacts/Improvements20261105/acceptance/test-evidence-map.json) and [tested-source fingerprints](../artifacts/Improvements20261105/acceptance/tested-source-fingerprints.json) are authoritative for these counts. Review corrections passed affected checks; final evidence follow-up receipts are retained separately. The earlier execution groups below overlap; adding their counts would inflate the result. The LF pass supersedes their acceptance counts; they remain diagnostic history.

| Check | Recorded result | Local evidence |
| --- | --- | --- |
| Final LF source pass | 997 unit + 191 component + 550 integration passed; 1 expected Linux skip | [LF build](../artifacts/Improvements20261105/lf-acceptance/build.log), [unit](../artifacts/Improvements20261105/lf-acceptance/lf-Unit.trx), [component](../artifacts/Improvements20261105/lf-acceptance/lf-Component.trx), [integration](../artifacts/Improvements20261105/lf-acceptance/lf-Integration.trx) |
| Earlier local solution build | 0 errors; 3 warnings (`SYSLIB0066`, `CS9124`, `xUnit2031`) | [Build log](../artifacts/Improvements20261105/final/build.log) |
| Grouped unit suite | 935 passed; 1 expected Linux skip | [Unit log](../artifacts/Improvements20261105/final/Unit.log), [TRX](../artifacts/Improvements20261105/final/final-Unit.trx) |
| Grouped component suite | 191 passed | [Component log](../artifacts/Improvements20261105/final/Component.log), [TRX](../artifacts/Improvements20261105/final/final-Component.trx) |
| Grouped integration suite | 538 passed; 6 failed at that checkpoint | [Integration log](../artifacts/Improvements20261105/final/Integration.log), [TRX](../artifacts/Improvements20261105/final/final-Integration.trx) |
| Affected provider/delta corrections | 27 unit and 77 integration cases passed, including the six corrected failures | [Unit TRX](../artifacts/Improvements20261105/corrections/corrected-Unit.trx), [integration TRX](../artifacts/Improvements20261105/corrections/corrected-Integration.trx) |
| Latest scheduler-ordering correction | 92 passed; 1 expected Linux skip | [Ordering log](../artifacts/Improvements20261105/corrections/scheduler-ordering.log), [TRX](../artifacts/Improvements20261105/corrections/corrected-scheduler-ordering.trx) |
| Final-review corrections | 404 passed initially; two new seed-force assertions corrected, then both cases passed with provider/result/clock assertions intact | [Affected TRX](../artifacts/Improvements20261105/review-corrections/review-corrections-unit.trx), [two-case final TRX](../artifacts/Improvements20261105/review-corrections/force-checks.trx) |
| Latest correction batch | 397 passed; four Fanart test paths had the provider disabled. After enabling that fixture, all 13 outage cases passed; production code was unchanged between these runs | [Affected TRX](../artifacts/Improvements20261105/final-corrections/final-corrections-unit.trx), [outage final TRX](../artifacts/Improvements20261105/final-corrections/storage-guard-unit.trx) |
| Provider-cache compatibility | 66 integration cases passed | [Integration TRX](../artifacts/Improvements20261105/final-corrections/final-corrections-integration.trx) |
| Compiled-source provenance | Portable PDB checksums match 231 current application source documents; zero mismatches, no extra build | [Checksum report](../artifacts/Improvements20261105/acceptance/compiled-source-checks.json) |
| Partition compatibility and bulk fixture | 4 passed | [Measurement log](../artifacts/Improvements20261105/measurement.log), [TRX](../artifacts/Improvements20261105/measurement/candidate-performance-partition.trx) |
| Targeted provider measurements | Baseline 4 passed; candidate 4 passed | Provider-measurement links above |
| Trace analyzer | 23 standalone PowerShell checks passed; console result recorded in handoff | [Audit script](../tests/PerformanceLogAudit.Tests.ps1); [execution handoff](../../PremiereCalendar.Build.Improvements20261105/HANDOFF.md) |

Real PostgreSQL configuration was present: 58 PostgreSQL-named integration cases executed, with no database-configuration skips. The expected Linux skip is the Windows cross-process publication file-lock test; it does not establish Windows/NAS filesystem acceptance. Focused coverage also includes image shutdown/force races, archive crash/lease/retention races, serialization and queue bounds, failed-flush preservation and a real 2,500-ID SQLite accepted-write/pure-read batch.

Artifact links point to local ignored evidence, not files shipped in source releases. The [local handoff](../../PremiereCalendar.Build.Improvements20261105/HANDOFF.md) records selection and execution context. Exploratory failure runs remain available for diagnosis and are not presented as passing acceptance.

## Round-one review disposition

Claude CLI round one used `claude-opus-5-5` with high effort; Astra independently adjudicated the findings. Review opinions are separate from the executed checks above. The source changed during that round, so it cannot serve as final approval of the current snapshot. See the [Claude report](../../PremiereCalendar.Build.Improvements20261105/claude-round1.json) and [adjudication handoff](../../PremiereCalendar.Build.Improvements20261105/HANDOFF.md).

| Finding | Disposition |
| --- | --- |
| 1. Passive foreground jobs could hold both workers | Fixed priority-based preemption, including queued-before-detach ordering; latest scheduler group passes. |
| 2. Movie passes could lose OMDb plot/poster | Fixed core/full ownership and final merge; prior good overview and correct cover ownership remain protected. |
| 3. TVmaze pacing treated as a correctness regression | The historical observed rate is not a provider entitlement. Pacing is configurable, defaults to 510 ms and is documented; live latency remains an acceptance boundary. |
| 4. One five-second budget covered whole bulk operations; failed flush lost drained writes | Split admission/open/command limits, scale aggregate work budgets, read in chunks and restore failed writes without replacing concurrent proposals. |
| 5. Missing startup health gates; Npgsql restart classification | Health gates and best-effort prefetch recovery fixed. The `57P01`/`57P02` classification subclaim was false for the actual Npgsql 10 dependency; no redundant override added. |
| 6. Core and full movie work shared a flight identity | Added explicit phase/mode isolation. |
| 7. One title's malformed payload backed off a provider | Scoped soft payload cooldown to title/credential identity; authentication and quota remain provider-wide. |
| 8. Interrupted archive companions could cause raw-trace deletion | Clean companions individually and apply the per-file limit to the authoritative body. |
| 9. Archiving blocked the writer and contention reported false loss | Added an independent bounded archive worker and separate archive health; lease contention skips without recording loss. |
| 10. TMDb shared subscribers could retain lower priority or failed work | Register each subscriber's demand and evict failed batch flights with replacement protection. |
| 11. Reservation failure/expiry could weaken lookup protection | Fail-closed reservation deferral is intentional. Exact publication tokens and CAS fencing protect expired results; abandoned markers retire. Long sessions can lose reuse after 30 minutes rather than publish stale data. |
| 12. Routine delta continuation consumed failure attempts | Persist a normal continuation deadline without charging a failed attempt. |
| 13. Corrupt history session prevented later feeds | Validate/rebuild the session and preserve independent feed execution. |

The 13 findings were adjudicated and valid defects corrected; this is not a claim that all 13 were independently confirmed bugs. The later frozen-snapshot reviews below supersede that round.

## Fresh full-review corrections

The subsequent frozen-snapshot reviews used Astra high and Claude CLI `claude-opus-5-5 --effort high`; the CLI outputs confirm the actual model. Ten P2 issues were confirmed across the reviews and their follow-up. All have corrections and passing affected regressions. Both final-candidate reviews report no actionable P0–P2 findings. Raw reports and byte manifests are retained with the local release evidence.

The first clean source checkpoint had an 84-entry program/test/tool manifest. Its exact bytes preceded the later checksum correction and LF-only normalization. Its SHA256 is `6f23916d47916faa4bb1509f6991ec098b463123bdeecfd6e527544e6b7d1a97`; diff SHA256 is `d9713109988132151281a4dd69951cc93bd5eadbaee554ab2039a581cfa2d1d8`. The [Astra receipt](../../PremiereCalendar.Build.Improvements20261105/astra-final-candidate.md) and [fresh Claude report](../../PremiereCalendar.Build.Improvements20261105/claude-final-candidate.json) distinguish read-only source review from parent-executed checks. Claude read new files directly; hash verification was performed separately by Astra and the parent.

| Finding | Correction and preservation coverage |
| --- | --- |
| RT retry identity omitted premiere year | Include incoming/default/actual year; a corrected year retries while the previous year's deadline and good scores remain intact. |
| Concurrent RT years could share one ratings flight | Include the incoming RT binding in rating-bearing flights; both held concurrent requests receive their own matching scores. Metadata-only work can still share. |
| A first TVmaze mapping could escape a change-feed fence | Fence unmapped lookups before reading known IDs; regressions cover publication on either side of the fence. |
| Overlapping prefetch could lose a still-wanted week | Recheck desired-key membership across awaits/retries and adopt current priority; a blocked-cache navigation regression retains the overlapping week. |
| Core-only movie results could bypass full/forced completion | Explicit force uses the full path with eligibility checks. Core-only results cannot satisfy full memo hits and locally finalize matched OMDb plot without renewing reused clocks. |
| Reservation storage errors escaped without a retry deadline | Share preparation's outage classification, evict only the failed reservation instance, and preserve caller/lifetime cancellation. Local saturation defers one session; genuine outages use the shared breaker. |
| A foreign year could erase an accepted RT component during reconciliation | Retain proof from trusted persisted reads and accepted winners, never local proposals. Restore only a matching component at unchanged generation/revision with verified ownership; preserve current OMDb precedence and original clocks, without a database write. Invalidation, manual/unverified changes and same-binding verified absence remain authoritative. |
| A forced filter rejection could retain an old or bare discovery stub | Persist the already-fetched core projection and filter that fresh row before forced backfill. Criteria-dependent forced flights have normalized eligibility variants; same-criteria joiners and later eligible profiles see the fresh core. Optional data/clocks remain retained. |
| Core-only state could override optional-provider cooldowns | The marker now means pending local completion. Clear it when the full local merge returns, even after provider failures; independent incomplete flags and retry deadlines remain intact. |
| Response-cache storage failures could trigger optional HTTP | Typed reads distinguish misses from unavailable storage. Optional clients defer before HTTP with the existing deadline, retain available memory, preserve empty-list/null contracts and keep cancellation neutral. Legacy nullable reads remain compatible. |

## Restored-backup gate and source-byte compatibility

The first local v1.2.15 image was rejected before installation. Its empty-database smoke and security checks passed, but startup against an isolated restore of the actual schema-4 backup rejected migration 1's checksum. Windows `git archive` had converted C# SQL raw-string line endings to CRLF; the stored history used LF. Production remained on v1.2.14 and no migration history was rewritten.

Migration checksums now use canonical LF SQL. Only historical migrations 1–4 also accept their exact CRLF-SQL checksum representation; name, version, completeness and schema-shape guards remain enforced. Golden LF/CRLF/mixed-history tests cover both PostgreSQL and SQLite, including rejection and data preservation. The C# LF attribute and a pre-build archive/blob/test-fingerprint comparison make the source representation explicit.

The checksum supplement passed 10 unit/SQLite cases and 13 actual PostgreSQL cases. Astra found no actionable issue in the correction. Claude confirmed the runtime correction and found an evidence gap between CRLF-tested and LF-shipped source. One final LF-bound build/test pass completed with 1,738 passed and one expected skip. A stricter archive fingerprint gate and evidence packing checks bind the counted LF tests, compiler document hashes, committed blobs and source archive; stale CRLF evidence is rejected. Both high-effort follow-up reviews cleared the final LF evidence and packing correction. The corrected image subsequently passed the actual restored-backup gate and was installed; candidate 1 remains rejected. The rejected bundle remains preserved separately from candidate 2.

## Installed v1.2.15 acceptance

The NAS app was updated at **2026-11-05 21:24:31 UTC**, stack 20 revision 14. Built source is `6f8d1bcf3d8eb44607da0f0ac36384b267bfc4db`; the archive-loaded image configuration identity is `sha256:b87ae77208bbb88ffd7177002c2172601a619c09027d5c19261792962041e2ca`. PostgreSQL and backup container IDs/start times, mounts, environment, saved settings and application data were retained. No production database restore occurred.

Local release gates passed: NuGet and Trivy reported zero vulnerabilities; Trivy reported zero secrets/misconfigurations; CycloneDX 1.7 lists 488 components. Hardened clean-database startup, HTTP/assets, timezone/DST, nightly planning and legacy-checkpoint CLI checks passed. The exact image also started on the separately restored real schema-4 backup, preserving migration history, all public table counts, preference fingerprints and all 7,155 enrichment rows. Direct/private installed identity, schema, health, assets and hardening checks passed. Both requested high-effort reviewers cleared source and follow-up corrections. GitHub workflows remained disabled.

The deployment guard initially stopped safely because PowerShell had converted an ISO timestamp to a locale-formatted `DateTime` before parsing it again, interpreting November 5 as May 11. Reading that field as text and parsing invariantly fixed the guard; the unchanged 26-hour limit accepts the actual two-hour-old backup and rejects a 27-hour control. Astra and Claude independently cleared the two-line operational correction. No additional image rebuild was needed.

| Installed browser observation | Result |
| --- | --- |
| Reported movie week, cached reload to first visible card | 486 ms |
| Wednesday → Thursday / Thursday → Wednesday | 315 / 262 ms |
| Render window / observed DOM size | 10 cards / 437–444 nodes |
| Filters / horizontal overflow / browser warnings or errors | 2 retained / none / none |
| Cached rows before background refresh | 150 titles, cache dated November 1 |
| Rows after background refresh | 151 titles; partial; explicit Trakt warning and next check at 22:30 |

Browser timings include automation overhead and are single observations. The screenshot captures the cached view before its background update, not the final partial state.

**Full refresh remains slow.** The same durable job continued across the reload and took **108.95 seconds total**; the second page observation waited 62.20 seconds. Server first rows arrived in 195.26 ms initially and 58.62 ms after reload. The final outcome was degraded, with Trakt deferred and Watchmode returning two 429 responses. That job recorded 916 TMDb, 258 Rotten Tomatoes, 33 Wikimedia, eight Fanart and three SIMKL HTTP responses. The two page records share that one job; their traffic must not be added together. This unmatched live run establishes no full-refresh improvement over the user's earlier 77-second observation. The benefit established here is prompt cached display and durable continuation, alongside the matched provider fixtures above.

A retained startup/browser trace snapshot contains 33,412 complete events (15,434,025 bytes), with zero omitted/malformed records or loss markers in the observed interval. Installed health reported zero dropped events, writer/serialization/archive failures and operational logging failures, with the event queue drained. This is a bounded observation, not a production soak or evidence for data already removed by retention.

Sanitized runtime evidence, browser measurements/screenshot, trace analysis, raw review receipts, tests, SBOM, checksums and archive are retained in the local/NAS v1.2.15 release bundle. The full fine-grained application traces remain in the NAS performance-log directory; the bounded investigation snapshot is local under `artifacts/Improvements20261105/installed-traces`.

## Remaining acceptance boundaries

Trakt's observed 403 and Watchmode's observed 429 cannot be repaired by granting allowance inside this application. The Trakt denial circuit was respected without an extra probe. One Watchmode zero-credit status probe returned 429 with `Retry-After: 60` and zero remaining request allowance; it did not establish monthly credit exhaustion. No billing, approval, credential or provider-enable settings were changed. See [sanitized external-provider findings](ProviderClientImprovements.md#trakt-and-watchmode-operational-follow-up).

Installed runtime and browser observations are recorded above. Actual next-night execution, Windows/NAS cross-process locking and production latency distributions remain unverified by this release. Provider account restrictions remain external; a partial result is not represented as complete.
