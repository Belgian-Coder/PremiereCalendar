# Usage and cache freshness review — 3 November 2026

Read-only inspection of installed 1.2.13, source `53ebe855254f849811b1a954b0913f53a788bce7`, schema 4. No application settings, production records, containers or providers were changed or deliberately refreshed. Times below use Europe/Brussels (UTC+1).

The installed process began at 15:45 and produced 106,612 parseable performance events through 18:23:26, with zero malformed records. Health reported a healthy database, zero dropped events, zero writer failures and zero operational logging failures. These logging-health results do not mean all providers or application operations succeeded.

## Actual recent browsing

Twelve recorded page loads occurred between 18:15:43 and 18:22:17. All served cached rows first. Eight were superseded by subsequent navigation, two movie loads completed, and two Series loads ended partial. Navigation cancellation is distinct from a provider failure.

| Local start | Profile / week beginning | First nonempty server result | Initial rows | Observed page outcome |
|---|---|---:|---:|---|
| 18:15:43 | Movies / 12 Oct | 65.8 ms | 181 | Superseded |
| 18:15:44 | Series / 2 Nov | 23.2 ms | 31 | Superseded |
| 18:15:45 | Series / 2 Nov | 26.1 ms | 31 | 33 rows, partial, 13.39 s |
| 18:15:48 | Movies / 12 Oct | 74.2 ms | 181 | Complete, 27.96 s |
| 18:22:02 | Movies / 19 Oct | 62.7 ms | 134 | Superseded |
| 18:22:04 | Movies / 12 Oct | 91.0 ms | 181 | Superseded |
| 18:22:04 | Movies / 5 Oct | 84.9 ms | 157 | Superseded |
| 18:22:06 | Movies / 12 Oct | 81.7 ms | 181 | Complete, 8.19 s |
| 18:22:10 | Series / 9 Nov | 18.1 ms | 28 | Superseded |
| 18:22:14 | Series / 2 Nov | 19.0 ms | 31 | Superseded |
| 18:22:15 | Series / 26 Oct | 29.7 ms | 68 | Superseded |
| 18:22:17 | Series / 2 Nov | 20.1 ms | 31 | 33 rows, partial, 3.68 s |

These are server-side first-result and page-operation durations, not measured browser paint or end-to-end network times. Initial rows precede enrichment completion.

There were eight distinct foreground jobs. Including waiting and preemption, their observed lifetimes ranged from 6.33 to 51.50 seconds. Fifteen job preemptions occurred while navigation changed priorities. Adjacent-week prefetch continued after the page results: activity associated with the first burst ran from 18:15:43 until 18:17:51 (about 2 minutes 8 seconds), and the second from 18:22:02 until 18:23:26 (about 84 seconds). Overlapping stages/jobs must not be added together as elapsed time.

## Provider work and remaining latency

From 18:15 to the captured 18:23:26 cutoff, foreground and background work together recorded 1,839 HTTP responses with status codes. Of these, 1,121 were attributed to the eight foreground jobs:

| Provider | Foreground status-coded responses |
|---|---|
| Rotten Tomatoes | 374 × 200, 7 × 404 |
| OMDb | 279 × 200, 9 × 401 |
| TMDb | 240 × 200 |
| TVmaze | 122 × 200, 33 × 404 |
| Wikimedia | 39 × 200 |
| Trakt | 8 × 403 |
| Simkl | 7 × 200 |
| Watchmode | 3 × 429 |

Requests without response headers/status are excluded. Repeated responses are not unique titles. Not every 404 indicates a fault; confirmed absence is a valid provider outcome. OMDb authentication denials, Trakt access denials and Watchmode rate limits remain material service failures.

The first Movies/12-Oct job recorded 261 status-coded responses; the repeat seven minutes later recorded another 69, despite already having all 181 cached rows. Its repeated decisions included 45 ratings-due observations and 125 availability-due observations; these are decision occurrences, not unique titles. This is evidence of remaining retry/reuse inefficiency. It is not evidence that all 69 responses were avoidable.

Rotten Tomatoes requests reached approximately 5.55 seconds each in the first movie job. Their summed durations overlap; they are not a separate 399-second elapsed wait. Retrying incomplete availability and ratings, source discovery and navigation preemption also contributed.

Database cache operations were comparatively small during this usage window:

| Operation | Samples | Median | Observed p95 | Maximum |
|---|---:|---:|---:|---:|
| Item read | 81 | 10.82 ms | 17.16 ms | 23.13 ms |
| Reconciliation | 70 | 12.09 ms | 21.33 ms | 26.17 ms |
| Item write, including empty writes | 180 | 0.003 ms | 9.64 ms | 199.01 ms |

These are sample statistics from this capture, not a production SLO or a cold-cache benchmark.

## Evidence that items are refreshed

A read-only PostgreSQL inspection at approximately 18:26 found 2,531 retained item records, compared with 2,500 at release acceptance. Seventeen records had an invalidation generation and a successful metadata timestamp at or after their invalidation. Three records had successful metadata timestamps since 18:00, and 216 had successful ratings timestamps since 18:00. These are distinct stored rows. They establish accepted refreshes, not that every numerical score or field changed value.

For example, Series TMDb ID 336241 had metadata invalidated at 09:00:27 and refreshed at 18:22:58, generation 1/revision 3. ID 338689 had an earlier invalidation and metadata refreshed at 18:22:58, generation 1/revision 5. Their separate older external-ratings clocks were preserved. Twenty-one retained records still had pending metadata invalidations at the query cutoff. Inactive retained rows are allowed to remain stale until used; three-month retention is separate from freshness.

The existing accepted release tests, rather than a newly executed suite, provide controlled value-change evidence:

- `GlobalItemCacheRefreshesOnlyScoresAfterTwentyFourHours`: score 7.1 → 8.4 without metadata/artwork requests.
- `InvalidatedMetadataBypassesOnlyMetadataAndKeepsRatingsSuccessClock`: runtime 112 → 144 while keeping the ratings clock.
- `CompleteWeekReadOverlaysNewerAcceptedMetadataAndRatingsWithoutHttp`: newer runtime 144 and score 8.8 overlay an older week file.
- `InvalidatedCompleteWeekRediscoveryRemovesMovedItemsAndBypassesDiscoveryCache`: a changed debut/date causes rediscovery and membership correction.
- Failed-provider, changed-identity and concurrent-old-writer tests protect retained good values and accepted newer winners.

All these cases passed in the accepted 1.2.13 test results: 650 unit, 191 component and 388 integration tests, including real PostgreSQL, plus one expected platform skip. This review did not modify live provider values to manufacture a before/after example. There is no saved production field-level baseline proving which individual numerical scores changed during this browsing session.

## Confirmed gaps

1. **Repeated Series partial publication.** Both current-week Series jobs ended with `item_cache.reconcile=discovery_required` after all five source iterators reported success. The final reconciliation had 33 rows and 51 consumed/reserved identities. `PremiereService.cs` converts dirty item-cache validation into `HasSourceErrors`, then logs the generic `source_failure` reason. The previous complete week file remains the 31-row 02:15 snapshot, so later loads keep needing refresh. The exact offending identity/branch is not logged. Possible dirty triggers include invalidation-version mismatch, canonical debut-date mismatch, missing stored row or a sticky same-session validation failure. The available evidence does not distinguish them.
2. **Forced refresh does not consistently reach optional providers.** Item-cache metadata mapping passes `forceRefresh:false`; TMDb/TVmaze and scores explicitly override it, but Watchmode and artwork do not consistently do so. Early invalidation or explicit refresh may reuse a 12-hour Watchmode availability response. Normal seven-day metadata expiry generally outlasts that response cache, but this does not establish correct forced behavior. Artwork can retain an existing fallback poster before considering fallback providers.
3. **Optional metadata failure can delay independent corrections.** If Watchmode/TVmaze fails, the whole previous metadata payload can be retained, including otherwise available corrected TMDb fields. Its old success clock is preserved, so it is retried rather than falsely marked fresh, but independently valid corrections can be delayed.
4. **Absent scores are retained.** A newly available numeric score replaces the old value. Missing/null results preserve an old matching score; a successful empty result can therefore leave that score with an advanced component clock. Verified withdrawal and transient absence are not fully represented separately.
5. **Diagnostic success labels need precision.** `source.finished=success` means iterator completion without an exposed batch error; Trakt can swallow 403 into an empty result. Current reconciliation logs omit the dirty reason/ID, and operational logs contained 125 Watchmode fallback warnings plus 12 render-mode section warnings in this usage period. No Error/Fatal entries were present in that operational-log window. Logging health alone does not establish provider completeness.

The next corrective work should first identify and resolve the repeatable Series reconciliation mismatch, then make optional refresh flags and independent provider-field updates consistent, and avoid repeated known-failing enrichment work while preserving last-good data. No new release or production fix was deployed as part of this inspection.

Local analysis inputs/outputs are under `D:\Projects\PremiereCalendar.Build.Validation-20261102\itemcache133\`: `recent-inspection.json`, `usage-review-20261103-1822.json`, `recent-jobs.txt` and the read-only PostgreSQL execution logs in the parent validation directory. The closed 1.2.13 release bundle was not changed.
