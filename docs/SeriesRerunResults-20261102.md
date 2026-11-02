# Series rerun metrics: 1.2.10

## Scope limitation

This execution used the recovered **All Episodes** payload: `ShowSeries=true`, `ShowMovies=false`, `SeriesDateMode=AllEpisodes`, dates October 19–25, 2026, and cache key `b9798e131cfd057cb213`. The user clarified that the intended product scope is new movies and new series, excluding new seasons or episodes. This trace therefore documents the requested legacy workload and its performance; it does **not** validate the intended new movies/new series workload. Both before and after caches contained only series episode rows: no movie rows and no series-premiere rows.

Subsequent read-only inspection recovered the actual frequently used profile: **new series only, English and Dutch, runtime 0–360 minutes**, key `4da65c5f968e894b2adf` (994 recorded uses before this investigation). The movie profile uses English/Dutch and runtime 45–360 minutes. The unrestricted diagnostic rerun was not representative of those saved selections. With the actual series profile, October 19–25 held 49 complete premieres; a forced refresh on the same installed v1.2.10 completed in **15.689 seconds** (job `8640262634324fea82dc2519d12f2007`), preserving all 49. Date-only navigation to November 2–8 retained the filters and showed 31 complete premieres. These follow-up observations retained caches warmed by earlier work; they do not establish cold-cache timings or a controlled code speedup.

## Result at a glance

- The ordinary page visit was a fresh complete cache hit: 6,223 rows in 422.613 ms, with no correlated provider calls or attached durable job.
- The forced refresh completed durably with 6,219 rows. Scheduler execution active time was 1,288.701 s (21m28.701s), with a 10.502 s pause between execution segments; enqueue-to-completion wall time was about 1,299.229 s (21m39.229s).
- The foreground page stopped observing the run at 345.022 s (5m45.022), showing 5,615 loaded rows and a 6,485-row visible total. Durable background work resumed and completed later. The browser still showed the old 6,223-row freshness badge after job completion; a manual reload showed the final 6,219-row cache.
- Four canonical episode IDs were removed and none added. IMDb score coverage improved on 78 common IDs; two common rows lost runtime and TVMaze runtime values.
- The execution is not a controlled speed comparison with the old job: the old run was normal mode and this one forced refresh; the cache/provider state and overlapping work also differed.

## Workload and controls

| Run | Refresh flag | Durable job | Result / elapsed |
|---|---:|---|---|
| Ordinary visit | false | None attached | 6,223 fresh cached rows; page complete in 422.613 ms |
| Forced rerun | true | `9c29cb3e4f3e44d6a44964a612353257` | 6,219 rows; complete at 21:28:36.068Z |
| Historical exact job | false | `8763b821d785448384edbe347e20d6d1` | 574.351 s resumed segment; 704.593 s enqueue-to-completion wall time |

The forced payload matches the old payload on dates and filters; `ForceRefresh` is the only changed payload field. No cache deletion or app restart occurred. The old job’s 574.351 s value is its resumed segment only. Its full 704.593 s enqueue-to-completion interval includes an earlier canceled attempt and a roughly 105 s gap. The old provider table aggregates the full job ID, including the first attempt. These timings describe different conditions and do not establish a speedup or regression.

The completed forced execution had two scheduler segments:

| Segment | Start–finish UTC | Active duration | Outcome |
|---|---|---:|---|
| Foreground observation | 21:06:56.865–21:12:41.912 | 345.047 s | Canceled/preempted when the foreground/background observation budget ended |
| Durable resume | 21:12:52.414–21:28:36.068 | 943.654 s | Success |
| Pause | 21:12:41.912–21:12:52.414 | 10.502 s | Requeued before resume |

The two `calendar.pipeline` spans were 345.046 s and 943.330 s respectively. The UI’s 345.022 s deadline is a presentation/observation boundary, not durable job completion. The last source finished at 21:14:32.956Z; the job completed 14m03s later. Logs show no open source, mapping batch, or enrichment-stage operation at completion. **Inference from the forced-refresh code path:** this post-source interval is consistent with the repair/backfill pass that checks missing external IDs and IMDb scores and can issue Rotten Tomatoes lookups. Diagnostics do not mark that helper as a distinct stage, so its duration was not directly measured.

One other scheduler job overlapped the first part of the run. A read-only database query identified it as an adjacent-week prefetch (October 26–November 1), priority 200:

| Other job | Segment | Duration | Outcome |
|---|---|---:|---|
| `74fa6d32dbbe410c9c6beba24994cfb6` | 21:05:54.938–21:06:56.857Z | 61.919 s | Canceled |
| Same job | 21:06:57.155–21:12:52.153Z | 354.998 s | Success |
| Pause | 21:06:56.857–21:06:57.155Z | 0.297 s | — |

Its measured scheduler durations match the start/finish timestamp deltas within 0.002 ms. Forced-job segment durations match within 0.007 ms.

## Page and cache results

| Measurement | Value |
|---|---:|
| Normal page complete | 422.613 ms; 6,223 rows; first result from fresh cache |
| Normal file-cache read | Fresh hit, 6,223 rows, 152.45 ms |
| Forced initial/resumed cache read | Fresh hit, 6,223 rows, 91.91 / 104.15 ms |
| Forced foreground first result | 372.63 ms; 20 rows; not from cache |
| Forced resumed pipeline first result | 722.97 ms; 5,396 rows; not from cache |
| Foreground page deadline | 345.022 s; 5,615 loaded rows; visible total 6,485 |
| Final forced pipeline | 943.330 s in resumed segment; 6,219 rows; complete |
| Complete week-cache file write | Saved 6,219 rows in 82.35 ms |
| Cache publication | Success, 6,219 rows, 87.39 ms; scheduler publish 85.33 ms |

The browser snapshot taken before completion still showed 6,485 rows with the old 6,223-row freshness badge. After the durable job completed, the page did not automatically display the final state in this observation; a manual reload showed 6,219 complete rows dated November 2, 21:28, with daily counts `925/949/1045/1033/937/597/733`. Browser console snapshots contained no warnings or errors. The page snapshot and the final cache are separate observations.

## Cache identity and field coverage

| Measure | Before | After | Change |
|---|---:|---:|---:|
| Rows / unique canonical IDs | 6,223 | 6,219 | −4 |
| Unique TMDb series entities | 2,946 | 2,944 | −2 |
| Series episode rows | 6,223 | 6,219 | −4 |
| Series premieres / movies | 0 / 0 | 0 / 0 | — |
| Sorted canonical-ID set SHA-256 | `d8cc1f5d5c7440565dd75ab648114ab479e8df45a923d03c6248e9cc8eba6b25` | `3c8df2c1d4ec2b118ef2944b370360b8c181b5e6305395d9b147849c91432a02` | Different sets |

The after-cache contains no duplicate canonical IDs. Four old IDs disappeared and no IDs were added:

- `tv:238695:s2026e135`
- `tv:238695:s2026e136`
- `tv:238695:s2026e137`
- `tv:244418:s05e21`

Daily rows changed from `925/950/1046/1033/938/598/733` to `925/949/1045/1033/937/597/733`.

IMDb score coverage rose from 3,244 to 3,318 rows (+74 net). Comparing only the 6,219 canonical IDs present in both snapshots isolates **78 gained IMDb scores and zero lost**; IMDb vote-count coverage gained the same 78 and lost zero on common IDs. RT critic and audience score coverage was unchanged (159 and 194 rows). TMDb score coverage was unchanged on common IDs. Two common rows lost `runtimeMinutes` and two lost `tvmazeAverageRuntimeMinutes`; these are field regressions to account for alongside the score gains.

The complete before/after coverage tables and common-ID gain/loss counts are in [cache-comparison.md](D:/Projects/PremiereCalendar/artifacts/investigations/series-rerun1210-20261102/cache-comparison.md).

The provider status table below totals **9,968 observed HTTP responses**, plus 41 no-status timeout/cancellation header outcomes. These are correlated to this job; failures without headers are counted separately.

## Provider outcomes

Status codes below are observed response headers. A no-status headers event is a timeout/cancellation transport outcome. Attempts without a matching `provider.headers.completed` event are not counted as confirmed sends or responses; the diagnostics do not identify their cause.

| Provider | Observed response statuses | No-status header outcomes | Completed request latency p50 / p95 / max |
|---|---|---|---|
| Rotten Tomatoes | 2,317×200; 3×404 | 33 timeout headers; 20 additional timeout attempts without a headers event; 4,042 failed attempts without a headers event | Successful: 5.491 / 13.747 / 19.984 s; timeout: 20.003 / 20.011 / 20.078 s |
| TMDb | 3,650×200; 94×404 | 1 canceled header | Successful: 17.61 / 130.90 / 1,456.15 ms |
| TVmaze | 1,558×200; 492×404; 1×429 retry | 1 canceled header | Successful: 206.91 / 402.10 / 2,930.20 ms |
| Watchmode | 5×429 retry | 4 canceled headers; 62 canceled attempts and 745 failed attempts without a headers event | Failed attempts: 0.04 / 0.08 / 2,597.96 ms |
| Wikimedia | 1,842×200; 2×404 | 2 canceled headers | Successful: 334.05 / 720.57 / 1,743.86 ms |
| Trakt | 2×403 | — | Request wrapper classified both as `success` transport outcomes; source completed with 0 rows |
| SIMKL | 2×200 | — | Successful: 231.18 / 289.91 / 289.91 ms |

HTTP 404s are consumed responses in the request telemetry; they are not provider data successes. Trakt’s 403 illustrates the same boundary: the transport wrapper records HTTP statuses below 500 as a successful request, while the source yielded zero items. Watchmode and Rotten Tomatoes have many failed attempt outcomes without matching headers records; do not infer that all were network sends or attribute a cause from these logs.

Rotten Tomatoes was the dominant long-tail provider. Its acquisition duration was p50 4.776 s, p95 15.520 s, max 19.995 s. Separate concurrency waits were p50 272 ms, p95 796 ms, max 1.975 s; cooldown waits were p50 17.476 s, p95 19.874 s, max 19.995 s. The RT search fallback stage reached p95 14.195 s and max 36.768 s; search HTTP reached p95 13.356 s and max 20.079 s. These intervals overlap across concurrent work and must not be summed into pipeline time. The `provider.acquire.completed` value is acquisition duration, not an isolated wait duration.

## Slow stages, source progress, and checkpoints

Source batch item counts can overlap or repeat across canceled/resumed attempts; they are not unique-card totals. The final cache count is the authoritative unique result count.

| Stage / event | Count | p50 / p95 / max |
|---|---:|---:|
| SIMKL source completion | 1; 3,859 source items | 222.950 s / 222.950 s / 222.950 s |
| TMDb-series source batches, successful | 7; batch sum 3,778, sizes 355–609 | 149.366 s / 190.528 s / 190.528 s |
| TMDb-series source batches, canceled | 2; 440 reported batch items | 3.587 s / 4.820 s / 4.820 s |
| TVmaze schedule source | 1; 480 rows | 99.413 s / 99.413 s / 99.413 s |
| TMDb-item mapping batches | 29 complete, 2 canceled | 24.359 s / 77.606 s / 79.212 s |
| External-candidate mapping batches | 5 complete | 3.516 s / 221.907 s / 221.907 s |
| `external.ratings` enrichment stage | 3,643 operations | 1.45 ms / 4.625 s / 40.511 s |
| `series.item` enrichment stage, success | 4,029 operations | 0.01 ms / 19.835 s / 38.729 s |
| Rotten Tomatoes search fallback | 2,000 absent; 25 complete; 48 timeout; 3,784 failed | 5.560 s / 14.195 s / 36.768 s |

The diagnostic stream recorded 138 persisted checkpoints: 137 non-final and one final. The non-final checkpoints have the following distribution. Count and total values ranged from 0 to 3,905; raw checkpoint bytes ranged 41,059–33,478,454 and encoded stored bytes 9,371–7,411,958. Checkpoint duration was p50 143.28 ms, p95 354.70 ms, max 410.23 ms; serialization p50/p95/max 44.74/99.36/110.15 ms, encoding 40.45/104.56/138.90 ms, and store 61.52/151.92/216.32 ms. The final checkpoint took 414.97ms (119.02ms serialization, 116.40ms encoding, 179.53ms storage), storing 7,410,553 encoded bytes from 33,472,339 raw bytes. Checkpoint snapshots and stage durations overlap and are not additive.

Provider response-cache write diagnostics included TMDb: 2,501 saved, 8 failed at `budget_exceeded`, 27 skipped at `backoff_active`; Rotten Tomatoes: 2,036 saved, 3 failed at `budget_exceeded`, 15 skipped at `backoff_active`. TMDb in-memory cache telemetry recorded 3,770 bypasses during forced work.

## Diagnostics integrity

- Final log capture: 10 instance-specific files, 552,071 lines scanned, 208,424 correlated events retained, zero malformed records or unterminated tails. The scanner selected files by process instance filename and then filtered by load/job IDs before retaining records.
- Final job state: `Completed`, attempts 0, no last error.
- Post-run health: performance diagnostics healthy; 509,876 events written, 0 dropped, 0 writer failures, queue 0; operational logging failure count 0; PostgreSQL healthy at schema 3.
- The original healthy deployment was 1.2.10 (`a60979b410be3f45d3eacc71d113500cc436290d`); no app restart occurred during the observation.

## Evidence files

- [Metrics analyzer](D:/Projects/PremiereCalendar/artifacts/investigations/series-rerun1210-20261102/Program.cs), built successfully with zero warnings/errors after the final changes.
- [Final event report](D:/Projects/PremiereCalendar/artifacts/investigations/series-rerun1210-20261102/force-final/metrics-report.md), [summary JSON](D:/Projects/PremiereCalendar/artifacts/investigations/series-rerun1210-20261102/force-final/metrics-summary.json), and [sanitized correlated events](D:/Projects/PremiereCalendar/artifacts/investigations/series-rerun1210-20261102/force-final/correlated-events-redacted.ndjson).
- [Execution segments](D:/Projects/PremiereCalendar/artifacts/investigations/series-rerun1210-20261102/force-final/execution-segments.json), [cache comparison](D:/Projects/PremiereCalendar/artifacts/investigations/series-rerun1210-20261102/cache-comparison.md), and [after-cache distribution](D:/Projects/PremiereCalendar/artifacts/investigations/series-rerun1210-20261102/cache-after.md).
- Browser captures: [before completion](D:/Projects/PremiereCalendar/artifacts/investigations/series-rerun1210-20261102/browser-before-completion.png), [terminal before reload](D:/Projects/PremiereCalendar/artifacts/investigations/series-rerun1210-20261102/browser-terminal-before-reload.png), and [after reload](D:/Projects/PremiereCalendar/artifacts/investigations/series-rerun1210-20261102/browser-final.png).
- Captured payloads, cache snapshots, health snapshots, and job state JSON are in the same validation folder.

