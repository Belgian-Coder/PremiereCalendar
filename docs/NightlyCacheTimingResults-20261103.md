# Nightly cache timing results — 2026-11-03 Brussels

Installed v1.2.12, source `6bc05e5709a8792e8f4dd2fb286964d395c69e48`, process `28600c6ab33a4d369ce18e17fa7be1c7`. This is the real startup catch-up for the most recent scheduled night, covering every full week intersecting November and December 2026. Profiles use English/Dutch, first-debut series, series runtime 0–360 and movies 45–360. Provider caches were retained. These results do not establish a cold-provider or controlled cross-version speedup.

| Measure | Result |
| --- | ---: |
| Verified jobs / complete caches / successful nightly receipts | 20 / 20 / 20 |
| Enqueue through last durable completion | 111.261 s |
| Sum of serial scheduler execution spans | 110.843 s |
| Cached rows across full boundary-week files | 657 |
| Observed HTTP response headers | 1,841 |
| HTTP non-2xx responses | 82 |
| Charged scheduler retries | 0 |
| Provider shared-cooldown retries following Watchmode 429 | 2 |
| Calendar snapshot reads: fresh / missing | 15 / 5 |
| Captured relevant events / malformed / loss markers | 62,524 / 0 / 0 |

The run began at 23:18:22.9234511 UTC and completed at 23:20:14.1849144 UTC on November 2. Rows include boundary dates outside November/December and are not 657 distinct titles. The first actual 02:00 scheduled occurrence remains future; controlled-clock and exact-runtime tests covered Brussels DST and scheduling, while this run proves live catch-up.

## Per-window timings

Queue time is enqueue to scheduler execution start. Active time is the scheduler execution span; the final column includes preceding jobs waiting in the same queue. Do not add queued durations to obtain total wall time. Pipeline final row counts match every cache receipt.

| Week | Profile | Final rows | Queue, s | Active, s | Enqueue to complete, s |
| --- | --- | ---: | ---: | ---: | ---: |
| 2026-11-02–2026-11-08 | Series | 31 | 0.015 | 1.982 | 1.998 |
| 2026-11-02–2026-11-08 | Movies | 64 | 1.997 | 1.923 | 3.920 |
| 2026-10-26–2026-11-01 | Series | 68 | 3.922 | 4.670 | 8.592 |
| 2026-10-26–2026-11-01 | Movies | 137 | 8.591 | 1.392 | 9.983 |
| 2026-11-09–2026-11-15 | Series | 28 | 9.984 | 1.335 | 11.318 |
| 2026-11-09–2026-11-15 | Movies | 86 | 11.321 | 1.355 | 12.676 |
| 2026-11-16–2026-11-22 | Series | 14 | 12.676 | 0.605 | 13.281 |
| 2026-11-16–2026-11-22 | Movies | 44 | 13.281 | 1.769 | 15.050 |
| 2026-11-23–2026-11-29 | Series | 9 | 15.051 | 0.765 | 15.815 |
| 2026-11-23–2026-11-29 | Movies | 23 | 15.818 | 1.381 | 17.199 |
| 2026-11-30–2026-12-06 | Series | 11 | 17.199 | 1.315 | 18.515 |
| 2026-11-30–2026-12-06 | Movies | 38 | 18.515 | 25.378 | 43.893 |
| 2026-12-07–2026-12-13 | Series | 10 | 43.967 | 0.552 | 44.519 |
| 2026-12-07–2026-12-13 | Movies | 21 | 44.518 | 22.136 | 66.654 |
| 2026-12-14–2026-12-20 | Series | 7 | 66.748 | 0.454 | 67.202 |
| 2026-12-14–2026-12-20 | Movies | 9 | 67.202 | 8.797 | 75.999 |
| 2026-12-21–2026-12-27 | Series | 9 | 76.091 | 0.350 | 76.441 |
| 2026-12-21–2026-12-27 | Movies | 11 | 76.442 | 11.302 | 87.744 |
| 2026-12-28–2027-01-03 | Series | 15 | 87.745 | 0.414 | 88.158 |
| 2026-12-28–2027-01-03 | Movies | 22 | 88.160 | 22.969 | 111.130 |

The five calendar-cache misses were movie weeks beginning November 30 and December 7/14/21/28. Their active durations account for approximately 90.6 s of the 110.8 s serial execution total. Other jobs reused existing calendar enrichment while rebuilding their discovery snapshots. Queue delay p50/p95/max was 16.509/87.745/88.160 s.

## Service responses

These are observed completed response headers, rather than inferred network sends. A logger outcome labelled success can still have HTTP 403/404; the status counts below are authoritative for HTTP status.

| Provider | Responses | Status counts | Header p95, ms |
| --- | ---: | --- | ---: |
| fanart | 91 | 200 × 91 | 93.3 |
| rottentomatoes | 298 | 200 × 289, 404 × 9 | 960.6 |
| simkl | 38 | 200 × 38 | 72.5 |
| thetvdb | 2 | 200 × 2 | 228.5 |
| tmdb | 1175 | 200 × 1173, 404 × 2 | 176.6 |
| trakt | 40 | 403 × 40 | 153.0 |
| tvmaze | 153 | 200 × 124, 404 × 29 | 240.1 |
| watchmode | 2 | 429 × 2 | 510.8 |
| wikimedia | 42 | 200 × 42 | 700.6 |

Trakt returned 40 HTTP 403s and zero source rows; a source outcome of success does not prove service authorization. Watchmode returned two 429s with shared-cooldown retries. Failed/cancelled attempt completions are retained separately in the JSON evidence; they must not be counted as additional confirmed HTTP responses.

Rotten Tomatoes complete request p50/p95/max was 3,689.9/6,696.9/8,958.4 ms, while its response-header p95 was960.6 ms. Request, admission/wait and body-consumption intervals have different boundaries and overlap other work. Rotten Tomatoes admission waits numbered 3,089, p95 674.2ms; their aggregate duration is not serial wall time. Persisted response-cache reads observed TMDb 2,481 hits/685 misses and Rotten Tomatoes 514 hits/244 misses. Snapshot misses and provider-cache misses are distinct layers.

## Enrichment stages

Operations can overlap, nest and repeat for source candidates. Counts are not unique titles or HTTP sends; sums of durations must not be added to the 111.261 s cohort wall time.

| Stage | Operations | p95, ms | Max, ms |
| --- | ---: | ---: | ---: |
| cache.hydration | 15 | 1.81 | 1.81 |
| external.ratings | 888 | 1.97 | 1575.87 |
| movie.artwork | 546 | 164.99 | 620.19 |
| movie.details | 3197 | 512.96 | 2914.75 |
| movie.item | 3528 | 3641.29 | 14003.55 |
| movie.ratings | 546 | 1.60 | 3.27 |
| movie.rotten_tomatoes | 546 | 8020.71 | 13833.14 |
| series.item | 274 | 458.01 | 1752.89 |

## Normal cached visit

The browser reload reached 31 completed series rows in 410 ms, including automation overhead. A selected cached page phase (`a8ca0a64ac434579b3e4c608657c2862`) recorded direct_complete/fresh 31 rows, 8.545 ms page completion, 19.249 ms render commit, zero provider events and zero attached/scheduler jobs. Browser and server event timings are distinct boundaries; their absolute clocks and intervals are not treated as interchangeable.

The movie view showed 64 complete current-week rows (daily 4/5/9/12/19/9/6). Moving to the following week showed 86 rows while retaining both filters. Browser console checks after the post-install reload found zero warnings/errors. Final health recorded 70,479 written diagnostic events with zero dropped events, writer failures or operational logging failures, and an empty queue.

## Evidence and future investigation

Detailed per-job IDs, claim/execution/publication/completion timestamps, provider latencies, source batches, cache outcomes and stage events are retained under `artifacts/release/1.2.12/installed-nightly/analysis/` and the matching NAS release directory. `nightly-final-summary.json` provides the combined results; `cohort-timings.json` maps every week/profile to its job ID; `correlated-events-redacted.ndjson` preserves the 62,524 selected events. The source analyzer read 70,479 complete records across two files, with zero malformed records, zero unterminated tails and zero diagnostics.loss markers. URLs, titles, query text, credentials and response bodies are excluded.

The app continues to log nightly plan snapshots and per-window outcomes/durations automatically. A saved plan coverage field reflects its last wake (15-minute default); durable job completion, cache metadata and successful receipts establish results between wakes. No explicit force-refresh or extra provider measurement was launched for this report.

Two correlation methods disagree on the exact Watchmode no-header attempt total (165 versus 163). That total remains unresolved; no extra HTTP sends are inferred from it. The 1,841 observed response headers and 20 verified caches are independently established.
