# Saved-filter timing results — 2026-11-02

Installed **v1.2.11** preserves the actual English/Dutch filters, new-series first-debut scope, series runtime 0–360 and movie runtime 45–360. Date-only navigation and reload retained those selections.

| Installed forced refresh | Enqueue to complete | Final complete rows | HTTP responses |
|---|---:|---:|---:|
| Series, November 2–8 | 11.776s | 31 | 207 |
| Series, October 19–25 | 29.629s | 49 | 455 |
| Movies, October 19–25 | 48.888s | 134 | 1,289 |

These are separate live observations with existing caches retained. They do not establish a cold-cache guarantee or controlled cross-version speedup. The earlier v1.2.10 observation below took 15.689s for the same 49-row scope with 449 responses; provider waits and process/cache state differed. The unrestricted [legacy all-episodes trace](SeriesRerunResults-20261102.md) was not representative of the saved user filters.

[Installed detailed report](D:/Projects/PremiereCalendar/artifacts/release/1.2.11/installed-profiles/combined-installed-results.md) contains per-provider statuses, source and checkpoint timing, and the separately recorded final repair/backfill stages. Trakt returned HTTP403 and contributed no rows; non-2xx responses are not data successes. Wait/stage totals overlap and are not additive to elapsed wall time. The persisted detailed event writer remained healthy, with zero dropped events, writer failures and operational logging failures.

## Earlier v1.2.10 scope and run

This capture is the completed forced refresh job `8640262634324fea82dc2519d12f2007` on instance `3305c36599924f00a628147152dd13e3`. It used the actual saved series filters: new series only, English and Dutch, October 19–25, 2026, runtime 0, remaining filters at defaults. The authoritative cache key was `4da65c5f968e894b2adf`.

The scheduler record spans 21:41:01.5912322Z to 21:41:17.2800948Z (15.6888626 seconds). Instrumented pipeline wall time was 15,677.53 ms. First partial result appeared at 83.95 ms with 13 rows; the final pipeline result contained 49 rows. The pre-run authoritative cache already contained 49 rows (36 English, 13 Dutch), so this was a refresh over a warm local cache, not a cold-cache performance test.

## Result and cache comparison

The cache stayed at 49 rows with the same 49 canonical IDs and same sorted-ID SHA-256 (`45cb02760eecdf5d4e6e3c2baf8e2492e706885c2516dc0520fb2a754009fc02`). Unique TMDb series remained 49 and movies remained 0. Per-day rows were unchanged: 6, 6, 4, 10, 9, 7, 7 (Oct 19–25). No canonical IDs were added or removed.

On common IDs, IMDb score and vote-count coverage each gained one item (21→22, +2.04 percentage points). Episode and season number each appeared on three common rows (0→3, +6.12 points). All other measured fields were unchanged, including TMDb score/votes (49/49), Rotten Tomatoes critic/audience (5/49 and 3/49), runtime (23/49), and poster/backdrop (45/49 and 38/49). The cache file SHA-256 changed from `A37F50AD0D3773A6DBFBD954F2894B5DF94C4591C013FA53E2986991D33555CB` to `A9B64D9C0EC0CBEE1E22516B4A6C56AF75300D015CE092DF3C7E2FBE0919C575`; the cache comparator found no row identity changes.

## Provider traffic and waits

Observed HTTP responses (status counts):

| Provider | Responses |
|---|---|
| TMDb | 267 × 200 |
| Rotten Tomatoes | 84 × 200 |
| Simkl | 2 × 200 |
| Trakt | 2 × 403 |
| TVMaze | 50 × 200; 25 × 404 |
| Wikimedia | 19 × 200 |

There were 449 request starts, 449 completed header events, and 449 completed attempts. No attempt lacked an observed headers-completed event. The logger labels all 449 attempts `success/consumed`, including the two Trakt 403 responses and 25 TVMaze 404 responses; those HTTP codes are still failures/non-success responses at the transport/API level and should not be summarized as all-200 success. The source-level Trakt completion was recorded as success with 0 items, so preserve the 403 evidence alongside the source outcome.

Rotten Tomatoes acquisition waits were the visible long tail: 84 acquire completions, p50 407.50 ms, p95 1,218.54 ms, max 1,476.28 ms; summed acquisition wait was 40,220.22 ms. There were 402 RT concurrency-wait events (p50 27.22 ms, p95 558.49 ms, max 656.68 ms; summed 40,209.44 ms). These waits overlap other work and are not additive to pipeline wall time. TMDb had 267 acquires (p95 0.82 ms, max 3.84 ms) and 85 concurrency waits (max 3.82 ms); other provider acquire waits were negligible.

## Sources, stages, and persistence

Source completion records: TMDb series 2 batches / 49 items (durations 1,023.39 and 5,080.31 ms); Simkl 1 / 24 (13,325.09 ms); TVMaze schedules 1 / 3 (6,182.02 ms); Trakt 1 / 0 (155.48 ms). These source item counts are source-reported batches and may overlap; they are not a distinct-result total.

The run recorded 95 series-item enrichments and 71 external-rating stages. RT emitted 75 search HTTP events; 22 matched and 53 were absent. RT search fallback absent outcomes totaled 70 operations, with p95 1,688.15 ms and max 2,059.87 ms. TMDb recorded 267 lookup/header/parse operations, 269 lookup completions, and 74 TMDb response-cache writes. RT response-cache writes were 75. The result was saved as a 49-row complete cache and published; 11 persisted checkpoints were observed (10 nonfinal, one final). Cache file read recorded one fresh hit for 49 prior rows. Durations across stages/providers overlap and are reported independently.

## Interpretation and limits

The before/after comparison shows refresh work with stable row identity and small metadata coverage gains. The comparison does not establish a cold-cache speedup. A separate broader `new-series-only` forced run produced 196 rows in 37.7393466 seconds, but its scope differs from these saved English/Dutch user filters and should be treated only as separate workload context, not a controlled comparison.

The analyzer read 15 files / 789,003 lines and retained 5,811 events for this job. It found zero complete-record parse failures and one unterminated final-line tail in the active performance file; that tail was excluded. The correlated event export is privacy-filtered and omits URLs, titles, queries, response bodies, and credentials.

Detailed machine-readable output: `metrics-summary.json`, `cache-comparison.json`; readable analyzer output: `metrics-report.md`, `cache-comparison.md`; selected sanitized events: `correlated-events-redacted.ndjson`.

The original artifacts are retained in `artifacts/investigations/saved-filter-timings-20261102/actual-user-force1210/`; [detailed timings](D:/Projects/PremiereCalendar/artifacts/investigations/saved-filter-timings-20261102/actual-user-force1210/metrics-report.md), [machine-readable summary](D:/Projects/PremiereCalendar/artifacts/investigations/saved-filter-timings-20261102/actual-user-force1210/metrics-summary.json), [cache comparison](D:/Projects/PremiereCalendar/artifacts/investigations/saved-filter-timings-20261102/actual-user-force1210/cache-comparison.md) and [current-week browser proof](D:/Projects/PremiereCalendar/artifacts/investigations/saved-filter-timings-20261102/user-current-week.png).

