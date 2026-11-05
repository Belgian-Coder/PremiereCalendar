# Discovery request reuse

Normal requests reuse provider responses without changing requested providers, dates, filters, page limits, ordering, or feed precedence. Forced refresh stays separate from normal network work. Successful refresh publication fences an older normal fetch so that it cannot overwrite the refreshed cache.

| Work | Reuse identity | Retention and bound | Forced refresh |
|---|---|---|---|
| TMDb paged movie/TV discovery | Source base URL, media, date range, complete discovery filters, page caps and batch size; an active normal query shares each page batch | Completed batches live only while a query has stream subscribers; completed result cache remains 6 hours | Uses an independent operation and page-flight identity; bypasses completed results |
| TMDb detail/season missing response | Source base URL and exact detail or season key, and an HTTP 404 with TMDb `success: false` and status code 6 or 34 | Memory only, 15 minutes, at most 256 missing keys per shared memory-cache lifetime; least recently used keys are removed | Bypasses missing entries; successful details clear them |
| SIMKL calendar feed | Full source URI, including client/application query parameters, and calendar item type; archive URI contains its month | Full successfully parsed feed, 5 hours, at most 8 feeds per client | Fetches independently of a normal feed and replaces only after success |
| IMDb local dataset lookup | Store instance and case-insensitive IMDb ID | No completed result cache; 10 ms coalescing, up to 64 IDs per batch, 2 active batches and 512 admitted callers | Dataset reads remain fresh; provider refresh does not retain an older dataset result |

TMDb discovery still yields its first page and subsequent page batches as they finish. A staggered subscriber replays already completed batches and shares pending batches. Cancelling one subscriber ends its own wait; the final subscriber cancels and observes pending batch work. The shared operation is removed when its subscribers finish.

TMDb reuse state belongs to the shared memory-cache lifetime, so transient typed HTTP clients can reuse work across calendar requests. The state is weakly associated with that cache and does not require a separate service registration. Different source base URLs and paging policies remain separate.

A TMDb timeout, transport error, unsuccessful non-404 response, invalid JSON, empty body, or unverified 404 body does not create a retained missing entry. A verified 404 invalidates any older persistent success with a typed tombstone, preventing an obsolete detail from returning after the short memory entry expires. The tombstone is a cache miss after restart; the negative result itself is not persisted. Existing forced-fetch failures preserve previously cached successful details.

SIMKL parses each feed completely before caching it, then applies each caller's requested date range locally. Adjacent weeks can therefore reuse one monthly archive fetch. Current feeds still precede archive feeds within each media type, duplicate selection remains unchanged, all intersecting archive months remain eligible, and movie-only/series-only requests still omit the other media's feeds before HTTP. Identical normal feed misses serialize and recheck the cache; the existing two-feed request bound remains in effect. Failed or incomplete feeds do not replace a successful cached feed.

## Provider limits

Verified against the official TMDb documentation on 2026-11-05: the old 40-per-10-seconds rule has been retired; TMDb describes a changeable bulk-request ceiling around 40 requests per second and requires clients to respect HTTP 429. Its error reference limits page numbers to 1–500. This change does not raise the configured request rate, concurrency, discovery caps, or page batch size. See [TMDb rate limiting](https://developer.themoviedb.org/docs/rate-limiting) and [TMDb error codes](https://developer.themoviedb.org/docs/errors).

## Regression coverage

`TmdbDiscoveryReuseTests` covers staggered normal streams, per-subscriber cancellation, forced/normal overlap in both start orders, verified missing reuse and bypass for movie/TV/season details, non-retention of unverified failures, preservation after a failed forced fetch, and missing-cache capacity. `SimklCalendarEfficiencyTests` covers adjacent and concurrent week reuse, per-week filtering, forced refresh fencing, and preservation after a failed forced feed. The integration cache-bound test exercises feed eviction across several months.

`ImdbRatingLookupBatcherTests` covers deduplication and result association, per-waiter cancellation, final-batch cleanup, store-failure propagation, two-batch concurrency, queue cancellation, restart, and store isolation. `SeriesEnrichmentParallelismTests` uses explicit barriers to check branch overlap, final metadata and scores, and early/later cancellation cleanup.

These are local test cases; they do not establish live provider timing or installed-runtime acceptance.
