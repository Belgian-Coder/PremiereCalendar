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
| Refresh sources, enrichment fresh | 2,714 → 667 ms | 4.07× faster | 2,703 → 664 ms |
| Stale movie week | 7,496 → 3,333 ms | 2.25× faster | 7,467 → 3,131 ms |
| Update movie snapshot | 10,465 → 5,526 ms | 1.89× faster | 10,331 → 5,221 ms |
| Force every movie component through the legacy API | 2,814 → 3,077 ms | 9% slower | 2,782 → 2,812 ms |
| Force every All/new-series component through the legacy API | 4,070 → 4,625 ms | 14% slower | 3,947 → 4,335 ms |

These are one recorded control run and a later final-candidate run on the same
SDK and fixture, not production percentiles. Host scheduling and cold setup
vary. There is no demonstrated cold/force-all completion gain; the intended
large gain comes from the approved release-list refresh and avoiding redundant
old-row enrichment.

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
`results-v4/baseline/1`, final `results-v7/candidate/1` and
`comparison-final-v7.json`. Earlier v4/v6 candidate timings used an experimental
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

The current test roster has 1,825 passing cases and one expected Linux file-lock
skip: 1,075 unit, 191 component and 559 integration passes, including isolated
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
including provider failures that return no new ratings payload. The focused
59-case correction run passed without provider network access.

Implementation validation, final independent reviews, local security scans and
installed acceptance are recorded separately in the release evidence. Pending
release/install work must not be represented as completed production acceptance.
No GitHub Actions or hosted CI is used.
