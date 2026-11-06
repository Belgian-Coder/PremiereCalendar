# Full-week refresh performance â€” 6 November 2026

## Behavior

**Refresh sources** now rereads release lists while reusing fresh item metadata,
ratings, availability and artwork. Missing or expired enrichment still runs.
It no longer forces poster downloads. The existing streaming API's
`forceRefresh: true` contract continues to refresh all components.

Expired snapshots no longer remotely enrich old rows before discovering which
titles still belong in the week. Fresh media retained from a mixed fresh/stale
All cache still receives normal metadata and ratings repair. Verified movie
runtime and language can reach the page before optional enrichment finishes;
source completion and final cache publication still wait for authoritative work.

The established two-source TMDb admission cap, four simultaneous TMDb HTTP
requests, 30 requests/second, mapping limits, pagination and the 45-second source
inactivity timeout are preserved. A wider source-fanout trial was rejected after
review showed that healthy Commons requests could queue past the source timeout.

The shared item cache also fixes a publication race: an optional continuation
can follow an equivalent accepted core predecessor from its session. Generation,
payload, timestamps and revision must match; ordinary store compare-and-swap
still rejects unrelated or invalidated data. Trakt defaults off; existing saved
integration settings remain authoritative until explicitly changed.

A concurrent filter preflight could also advance the core revision before a
successful full refresh saved its optional results. A narrowly fenced, single
retry now publishes the original full proposal, including ownership, completion
and success clocks, through the existing store compare-and-swap. It requires a
freshly verified, equivalent raw core and rejects foreign changes. This replaces
an earlier ratings-only workaround that failed to preserve optional proofs.

Revision continuations carry a null-capable ratings JSON/clock precondition into
the locked store transaction. If a ratings-only repair wins, that session keeps
the accepted ratings through duplicate reuse, projection synchronization and
final hydration. A new independent session can refresh normally.

## Controlled measurements

The final candidate preserves the original two-source TMDb cap. Docker had
networking disabled; providers were in-process HTTP fixtures with bounded pools
and fixed delays. The real TMDb client retained four HTTP slots and 30 requests
per second. TVmaze was an immediate local fake. The seven-day workload used two
languages, a 45-minute movie minimum, 28 movies and, for All, 14 new-series
premieres. Priming is excluded.

| Scenario | Completion before → after | Change | Every current row visible before → after |
|---|---:|---:|---:|
| Refresh sources, enrichment fresh | 2,714 → 493 ms | 5.51× faster | 2,703 → 489 ms |
| Stale movie week | 7,496 → 2,357 ms | 3.18× faster | 7,467 → 2,173 ms |
| Update movie snapshot | 10,465 → 3,686 ms | 2.84× faster | 10,331 → 3,468 ms |
| Force every movie component through the legacy API | 2,814 → 2,924 ms | 4% slower | 2,782 → 2,743 ms |
| Force every All/new-series component through the legacy API | 4,070 → 3,546 ms | 15% faster | 3,947 → 3,332 ms |

These are one recorded control run and a later final-candidate run on the same
SDK and fixture, not production percentiles. Host scheduling and cold setup
vary. Force-all results are mixed, so no broad cold/force-all gain is claimed.
The earlier cap-two candidate took 667 ms for warm refresh and was 9–14% slower
for the two force-all scenarios. Both comparisons are retained. The intended
gain comes from release-list refresh and avoiding redundant old-row enrichment.

The warm button still made all 14 discovery requests. Each enrichment provider
went from 28 requests to zero, including TMDb details. Stale/Update scenarios
reduced TMDb requests from 98 to 42 and each optional provider from 84 to 28;
the 56 removed old-only titles caused zero detail requests in the candidate.

All five complete business projections, row counts and successful source
coverage matched. Comparison normalizes fixture wall clocks and the internal
metadata revision counter; generation and every business field remain compared.
A held-provider, two-language **single-day** fixture verifies runtime/language
eligibility before optional completion and cancellation cleanup within the
established two-source cap. It does not claim all seven cold sources can finish
core mapping while their optional requests are held indefinitely.

The performance control is installed source `6f8d1bc` **plus the same normal
core-publication correctness fix**, not an unmodified installed build. The
original baseline loses availability under the deterministic interleaving; its
failing evidence is retained. The initial new source-refresh test also primed
an RT failure as fresh data; its fixture now primes successful scores, preserving
normal failure retries.

Raw evidence is retained under
`D:\Projects\PremiereCalendar.Build.FullRefresh20261106\snapshots-20261106-175823-337`:
`results-v4/baseline/1`, final `results-v13-perf/candidate/1` and
`comparison-final-v13.json`. The earlier cap-two comparison remains in v7.
Earlier v4/v6 candidate timings used an experimental
12-source fanout that was rejected after review; they are historical trial
evidence and are not the release candidate's performance claim. No real provider
traffic was used for these measurements.

This follow-up records timing, request counts, observed concurrency and complete
result/source projections. It does not establish new allocation or database-write
speedups. Existing 2,000-row, scheduler-retention and browser-budget evidence is
separate from these small controlled provider scenarios.

## Saved trace diagnosis

The prior installed trace first returned stale rows in about 195 ms but started
fresh discovery only after 63.8 seconds. It showed 150 cached-row decisions in
each enrichment stage before discovery. The old source unconditionally hydrated
those rows first. This strongly supports removing that preflight, although that
helper did not have its own duration measurement. SIMKL later took about 45
seconds despite short HTTP bodies, so not all remote completion delay can be
attributed to the preflight.

The durable job took 108.9 seconds across two page observations. Provider totals
in the saved window are not reliably attributable to that individual job.
Trakt's local denial took about 65 ms with no wire request; disabling it alone
does not explain the speedup.

## Release acceptance

The current test roster has 1,833 passing cases and one expected Linux file-lock
skip: 1,083 unit, 191 component and 559 integration passes, including isolated
SQLite and PostgreSQL coverage. Evidence combines the broad run with affected
reruns; earlier failures and their exact passing replacements remain recorded.
All 426 fingerprinted source/config inputs match the tested snapshot.

An existing one-second progress fixture also failed in isolation. Instrumentation
showed healthy 200 ms requests completing while cold setup and first-batch work
consumed that margin. Its final version uses a two-second idle budget and twice
the work: 256 requests at concurrency 16 require at least 3.2 seconds. It checks
every completion, elapsed time beyond the budget, no source errors and a complete
empty cache after filtering. Production deadlines are unchanged; raw diagnostic
failures are retained.

Concurrent ratings repairs are protected by a JSON-and-clock precondition checked
inside the store transaction. Proven equivalent metadata can still publish.
The accepted scores survive source filtering, final synchronization and hydration,
including provider failures that return no new ratings payload. Accepted
provider-clock merge winners also drive source filtering. Preserving ratings
does not discard a later valid metadata proposal or advance its original
revision proof. Metadata-only writes carry their known ratings provenance;
obsolete queued ratings cannot undo an accepted winner. All eight added cases
first failed against the earlier handling. The focused 67-case correction run
and all five repeated performance scenarios passed without provider access.

The broader core/cache run passed 456 cases. Its remaining fixture held the
clock constant across two independent forced refreshes, so the store correctly
retained the earlier equal-clock rating. The fixture now advances one minute,
well within the freshness window; its unchanged new-score and forced-request
assertions pass. That original failure is retained with its passing replacement.
All eight affected SQLite/PostgreSQL persistence cases also passed again.

Implementation validation, final independent reviews, local security scans and
installed acceptance are recorded separately in the release evidence. Pending
release/install work must not be represented as completed production acceptance.
No GitHub Actions or hosted CI is used.

## Installed acceptance, 7 November 2026

v1.2.16 is installed from source `8ca58680a5509f8452a56a4a0766fc113dcbff31`, digest-pinned on stack 20 revision 15, schema 4. Database and backup container identities/start times are unchanged; no production restore occurred. Final health reports zero diagnostic drops, writer/serialization/archive failures and operational logging failures. Trakt is explicitly disabled in saved settings.

The reported 31 August–6 September movie week reached a complete cache with 151 titles. The initial live recovery fetch still took 70.6 seconds in SIMKL (TMDb 36.2 seconds). This is not evidence of a broad cold-load speedup. A subsequent source-list refresh reused fresh enrichment and completed with the same 151 titles; source rows reported TMDb 204 ms and SIMKL 381 ms. Saved traces measure the complete source-list refresh at 876 ms (final results at 867 ms); the initial recovery page took 70.889 seconds. Cached server results arrived in 82 ms and completed in 221 ms. These are separate page operations, not an overlapping-duration sum. Cached browser reload to the first visible card took 547 ms; Wednesday/Thursday switches took 557–616 ms including automation observation. Ten cards and 437 DOM nodes were rendered, with no overflow or browser warnings/errors. These are individual live observations, not production percentiles.

Both final independent Astra high and Claude CLI Opus 5.5 high reviews completed with no open actionable P0–P2 findings. Local acceptance: 1,833 tests passed and one expected Linux skip; security scans found zero vulnerabilities. Detailed traces and installed browser evidence are retained in the local/NAS release bundle. The next actual nightly execution has not yet been observed.
