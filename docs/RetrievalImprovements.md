# Provider retrieval and automatic timings

This follows the [installed v1.2.9 profile](ProviderRetrievalProfile.md). Exact-image and installed acceptance are recorded in the [v1.2.10 release record](releases/1.2.10.md).

The [saved-filter timing report](SavedFilterTimingResults-20261102.md) measures the owner's English/Dutch new-series view: 49 complete premieres and 449 responses in 15.689 seconds on installed v1.2.10. The [unrestricted legacy episode rerun](SeriesRerunResults-20261102.md) separately records the foreground observation cutoff, checkpoint resume and final score-backfill interval. Neither establishes a controlled cold-cache code speedup.

## Request reduction and throughput

Concurrent Wikidata entity requests coalesce briefly, then fetch claims for up to 50 distinct entities in one `wbgetentities` request. Rotten Tomatoes identifiers and image claims share this path. At most two batches run concurrently, with a bounded pending queue. Each caller retains its cancellation and correlation context; cancelling one caller does not cancel others that still need the batch. A fully cancelled batch stops. Missing, malformed or failed entity responses remain retryable rather than becoming verified negative cache entries. Later forced requests fetch again and obsolete normal results cannot overwrite a newer force result.

Wikidata documents a [50-ID limit for ordinary clients](https://www.wikidata.org/w/api.php?action=help&modules=wbgetentities). This is a genuine multi-entity network request, rather than just launching 50 separate calls concurrently.

Rotten Tomatoes direct-page results also reuse successful exact-page responses within a refresh, including a verified page with no scores. This avoids fetching the same page again when Wikidata and title-search fallback resolve to it. Search can still supply critic scores or identify an alternate page; failed or malformed responses remain retryable. Reuse does not extend across separate refresh generations.

TVMaze's [schedule responses already contain full show metadata](https://www.tvmaze.com/api). Freshly fetched broadcast/web schedules now seed the existing strong IMDb/TVDB lookup aliases for those shows. This avoids fetching the same show again during a normal enrichment pass. Seeding fills missing aliases only, skips conflicting identities, and uses the schedule's original 60-minute expiry. It does not seed title searches, extend freshness on schedule cache hits, or bypass an explicit forced lookup. Shows outside the fetched schedules retain their existing lookup path; a full all-show index is not introduced.

TMDb's detail request already combines videos, images, external IDs, keywords, availability and release metadata through [`append_to_response`](https://developer.themoviedb.org/docs/append-to-response). That combines subresources for one title; it is not a documented multi-movie detail endpoint. The [daily exports](https://developer.themoviedb.org/docs/daily-id-exports) contain identifiers and limited attributes, not the language/runtime metadata needed to replace individual eligibility checks.

The configurable default TMDb allowance increases from 20 to 30 requests/second, keeping four concurrent requests and existing retry, cooldown, cancellation and 429 handling. TMDb's [current rate guidance](https://developer.themoviedb.org/docs/rate-limiting) describes an approximate upper limit around 40 requests/second which can change; 30 is an application budget, not a service guarantee. Existing explicit configuration overrides remain effective. Provider coverage, filtering and forced-refresh freshness are preserved.

## Automatic persistent timings

Detailed performance recording remains enabled by default without OTLP, tracing sampling or a manual capture action. Completion records include monotonic duration, outcome and load/job/source/operation correlation where applicable. Existing source, queue/claim, provider attempt/headers/body, item enrichment, cache publication and checkpoint events remain.

Additional stages include:

- TMDb local admission before HTTP: duration, queue count at entry, active requests, concurrency limit and configured token budget. This is separate from server/response timings.
- TMDb lookup, memory-cache decision and JSON decoding, including failed and cancelled completions. Cache keys are hashed; response content is not logged.
- Rotten Tomatoes identifier resolution, page lookup/reuse, page HTTP, scorecard parsing, search HTTP/matching and fallback, with durations and outcomes.
- Wikidata batch wait/fetch/parse, counts and each caller's completion/cache outcome, linked to caller contexts without raw entity IDs.
- Candidate mapping batch boundaries and external-candidate ratings hydration. Mapping elapsed time includes time between streamed batches, so it must not be added to nested request timings.

The asynchronous writer keeps application work independent of disk I/O. Default detailed-log retention increases to 64 files of 25 MiB, approximately 1.56 GiB; it remains bounded and configurable. The writer's loss, failure and flush-health counters remain visible at `/health/version`. Rotation eventually replaces old files; export the correlated evidence before that boundary when investigating an incident.

`eng/Read-CalendarPerformance.ps1` now retains up to 100,000 matching records by default and supports `-InstanceId` to avoid scanning unrelated process files. For large series jobs, `-MaximumEvents` can increase the bound to 500,000; truncation always warns. Example:

```powershell
./eng/Read-CalendarPerformance.ps1 -LogDirectory '\\NAS\Docker\PremiereCalendar\app-data\logs\performance' `
  -InstanceId '<process GUID>' -LoadId '<load GUID>' -Summary
```

Durations overlap. Progress times are cumulative, HTTP body times include consumption, and independent percentiles cannot be subtracted to derive a queue. A complete cache read is not evidence of cold-provider performance.

## Validation

Final Linux validation passed 576 unit, 329 integration and 174 component tests: 1,079 passes and one expected Windows-only skip. Integration used actual PostgreSQL. Read-only Astra High review found a batch abandonment/join race and unverified RT challenge-page reuse; both were fixed before final validation. Background fairness and full queue timing were also checked. No UI early-render change is included.

The same harness ran against v1.2.9 source `7c48557` and the candidate assemblies, three runs each including first-run initialization:

| Controlled fixture | v1.2.9 median | Candidate median | Preserved result |
| --- | ---: | ---: | --- |
| 50 distinct Wikidata entities, 100 ms fake HTTP, two HTTP slots | 2,582.44 ms, 50 requests | 118.62 ms, one request | All 50 identifier results identical |
| 120 local TMDb admissions, 50 ms simulated work per lease | 5,267.57 ms at 20/s | 3,421.42 ms at 30/s | 120 successful leases; peak four in both |

The full ordered identifier projection SHA-256 is identical across all six runs: `1f9659da81e0340ec01ce72897a4fbe75ffe2b8f4f53436e09da5c5760882c6d`. These are controlled phase measurements, not real provider latency or full-calendar speed claims. Harness source, raw JSON, final test logs and review accompany the release bundle.

## The longer loading report

The earlier 57.306-second movie refresh was one specific forced load, not a bound on all loading. Retained logs also contain foreground job `8763b821d785448384edbe347e20d6d1` in process `79f972f1ce994c2fb2358bcac7f84a77`: after a cancelled attempt it resumed at 18:39:26 UTC and finished at 18:49:01, 9 minutes 34 seconds later, with 6,223 items. Its first cached non-final result arrived in about 199 ms. This is a different series-heavy workload and cache key, not established as the user's exact session.

Exact-job correlation shows 6,417 successful TVMaze request outcomes (including HTTP 404 responses), plus 98 shared-cooldown retry events for HTTP 429. Watchmode's 1,927 failed request outcomes had median 0.06 ms and no matching HTTP status; they must not be presented as 1,927 network sends or attributed to a specific transport failure. The long-load evidence is retained separately from the small movie refresh. The new batching/throughput fixtures do not prove that this large series workload is now fast.

## Installed provider refresh — November 2

The deployed v1.2.10 forced refresh for October 19–25, English/Dutch and minimum runtime 45 minutes completed in 50.109 seconds with all 134 titles and daily counts 7/20/21/15/32/21/18. Reloading confirmed the same complete cache. The previous 57.306-second observation was a separate run with existing caches and different provider/background conditions; these two observations do not establish a controlled full-load speedup. The nine-minute series workload was not rerun as a cold-provider acceptance test.

The new capture retained 25,033 correlated records with zero malformed lines. Attributed HTTP request starts were TMDb 1,105, Rotten Tomatoes 157 and Wikimedia 11. The movie workload did not demonstrate a large batch reduction: the controlled 50-to-one identifier fixture applies when multiple identifiers are pending together, not to every real request pattern. TVMaze schedule reuse is exercised by its separate integration coverage and applies to shows present in fetched schedules.

The SIMKL source including mapping took 47.257 seconds. RT search HTTP stages had median 3,880.53 ms including admission/consumption, while response headers after admission had median 618 ms. TMDb header median was 9.12 ms; its separately measured local admission p95 was 806.89 ms. These overlapping distributions describe separate stages and are not additive. RT search and capacity waits remain a significant cost. No attributed HTTP 429 header response occurred in this movie run.

Load `9976ca7ecea449ee812e5fc58ef6cf79`, job `a002d19dbc3a4f87a7c995e88ac47325`, instance `3305c36599924f00a628147152dd13e3` identify this exact capture. Post-refresh health showed 58,709 persisted events, zero dropped events, writer failures, operational-log failures or queued events. The release bundle contains the raw correlated events, numeric summary, extraction script, installed health/browser checks and screenshot. The tab recorded transient 502 negotiation errors during the app replacement; final reload verification had no new console errors or warnings.
