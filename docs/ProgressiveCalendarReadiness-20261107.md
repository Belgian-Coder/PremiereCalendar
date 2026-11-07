# Progressive calendar readiness

The reported Update at 17:18 Europe/Brussels on 7 November returned 188 cached
movies for 12–18 October in 110 ms, but remained active for 35.45 seconds. Two
optional enrichment stages took 20.35 and 14.89 seconds. The correlated job made
265 HTTP requests: Rotten Tomatoes 134, OMDb 114, Wikimedia 16 and Watchmode one.
No release discovery ran. All 24 OMDb failures were parsed `Response=False`
payloads with unrecognized errors; their raw error messages were not retained.

Version 1.2.17 separates presentation readiness from terminal work. A complete,
fresh cached calendar becomes available after local hydration. Its durable job
and existing SignalR observation continue, publishing accepted repairs in bounded
batches. Active filters are reapplied and card order is retained. Previously
published local values form the baseline for all unprocessed batches.

`IsCalendarReady` controls presentation; `IsFinal` still controls terminal
persistence and recovery. Enrichment progress uses its own phase and cannot
become a successful source checkpoint. Replayed or deferred stale snapshots do
not inherit readiness. Navigation detaches the old observation without allowing
late old-page updates to change the current page.

Readiness does not promise immutable membership: core validation may require new
discovery. Cold and expired discovery remain truthful about pending coverage.
Required filters still apply; unknown scores are not fabricated or implicitly
accepted when the user excludes them. This change targets responsiveness rather
than claiming that optional providers finish faster.

Validation uses held-provider tests to prove that cached readiness happens before
provider completion, intermediate repairs reach the page, and final result
coverage is retained. Browser observations and server trace timings must be
reported separately. Installed measurements will be appended after deployment.

Independent review found an initial batch-baseline rollback with the shared item
cache disabled. The corrected implementation retains local hydrated ratings for
unprocessed rows; a regression covers rating-filter eligibility through every
batch. Raw failed runs and subsequent passing replacements are retained rather
than counted as additional passing cases.
