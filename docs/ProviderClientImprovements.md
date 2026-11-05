# Optional provider retrieval

Fanart v3 missing resources use HTTP 200 with an empty JSON object. The client now recognizes that documented legacy response as verified absence. HTTP 404 remains absence. Newer Fanart endpoint versions do not treat an unexplained empty object as absence. Other malformed bodies remain failed refreshes, preserve the previous good response and apply a short payload failure backoff scoped to the requested title.

OMDb distinguishes verified success, its established `Movie not found!` response and failures. Fixed diagnostic `stage` categories on the existing failure event identify non-object JSON, missing/invalid response markers, unrecognized errors, invalid fields and invalid JSON. They contain no body, error text, URL, credential or title, and do not add a duplicate failure event. Invalid payloads back off for one minute only for that title and credential partition; they do not become a provider-wide persisted quota deadline. An explicit OMDb force can retry this soft payload cooldown within the current refresh generation; ordinary callers remain deferred. Authentication and quota deadlines retain their existing provider-wide policy and cannot be bypassed by forcing. A failed forced request also returns an available good memory response without claiming fresh success. Loading a persisted response into memory preserves its original expiry.

## Reuse across restarts

The existing bounded `ProviderResponseCache` now stores selected successful optional responses, including verified absence:

| Response | Freshness |
| --- | --- |
| Fanart movie/TV artwork or verified absence | 7 days |
| TVmaze lookup/search match | 7 days |
| TVmaze lookup/search absence | 6 hours |
| TVmaze image list | 7 days; HTTP 404 absence uses 6 hours |
| Watchmode positive identity mapping | 30 days, independent of availability |
| Watchmode identity absence and availability | Existing configured availability lifetime, normally 12 hours |

Endpoint, credential and relevant request/settings values partition reuse through hashes. Credentials do not appear in keys or payloads. Reads preserve the saved expiry. Forced success replaces a previous positive result even when the new result confirms absence. Failures preserve previous good data and do not persist a successful empty result. Existing storage size limits, I/O budgets and provider backoff behavior still apply.

Shared per-memory-cache flights coalesce concurrent calls from transient clients. One canceled waiter does not cancel another waiter. Separate forced flights and generation fencing prevent an older normal response, including one started while a force is fetching, from overwriting the forced result in memory or durable storage. TVmaze schedule priming retains its existing publication gate and short schedule-derived freshness.

Wikimedia retains both relevant claims from each fetched entity: image filename and Rotten Tomatoes ID. A later caller for the other claim reuses that response with existing positive/absence lifetimes. Commons file/license requests share flights and a seven-day result cache across entities that reference the same file. Failed or malformed responses are not absence. Entity batching remains limited to 50 IDs and two concurrent batches.

## Provider limits and observed diagnosis

Official documentation checked on 2026-11-05:

- [OMDb key registration](https://www.omdbapi.com/apikey.aspx) lists a free-key limit of 1,000 requests daily. The configured account tier is not inferred from that public limit.
- [Fanart API reference](https://api.fanart.tv/) documents legacy v3 HTTP 200 `{}` for missing resources. It says most keys have no rate restriction; configured limits can return 429 and `Retry-After`. Existing application concurrency and delay settings remain unchanged.
- [TVmaze API](https://www.tvmaze.com/api) documents at least 20 requests per 10 seconds per IP, temporary stricter limits under load, and 429 handling. Existing request throttling is retained.
- [Watchmode terms](https://api.watchmode.com/tc) describe account-specific monthly quota and rate limits; [pricing](https://api.watchmode.com/pricing) points to rate/quota response headers. No universal quota or account entitlement is assumed.
- [Wikidata data access](https://www.wikidata.org/wiki/Help:Data_access) specifies groups of up to 50 entities for the Action API. Batching and concurrency are unchanged.

On 2026-11-05, two bounded controls used the existing configured credentials without changing settings: a known-valid OMDb title returned HTTP 200/verified success, and a deliberately nonexistent Fanart movie on the installed v3 endpoint returned HTTP 200/empty object. Only status and fixed classifications were retained. No raw body or credential was saved or emitted. This confirms the Fanart legacy handling defect. It does not prove that all prior Fanart failures had that body, or establish the cause of the earlier 63 OMDb invalid responses. The new diagnostic categories allow the next recurrence to distinguish causes safely.

`ProviderOptionalCacheIntegrationTests` covers restart/expiry preservation, forced absence, last-good retention, both normal/forced orderings, cross-client coalescing, canceled waiters, credential partitions, Watchmode identity versus availability expiry and Wikimedia claim/Commons reuse. Existing client and schedule-priming tests remain relevant. Integrated execution is owned by the root task; no hosted CI or live deployment is part of this change.

## Trakt and Watchmode operational follow-up

Read-only inspection on 2026-11-05 checked persisted provider circuits before considering external requests. The latest two performance files contained actual Trakt 403 and Watchmode 429 response events around 17:45 UTC. Counts of outcome, progress, retry and HTTP events overlap; they must not be added together as independent requests. This bounded sample does not verify the originally reported totals of 100 Trakt 403s or 142 Watchmode 429s.

Trakt had an active denial circuit until **18:00:07 UTC**. No external Trakt probe was sent. The configured client ID was present and had the expected 64-character hexadecimal shape, which does not establish validity or approval. Source inspection confirms the canonical `api.trakt.tv` endpoint, API version 2 and an application User-Agent. Current [Trakt status documentation](https://developer.trakt.tv/docs/status-codes) associates 403 with an invalid key or unapproved app; [limited-access documentation](https://developer.trakt.tv/docs/limted-access) also explains endpoint-specific approval. The application's global calendar paths match the documented calendar family. The precise account/approval versus upstream edge cause remains unconfirmed while respecting the active circuit. No product defect or need to buy a plan was established.

Watchmode's saved cooldown had expired. One request to the documented **zero-credit `/v1/status/` endpoint** returned HTTP 429, `Retry-After: 60`, `X-RateLimit-Limit: 120` and `X-RateLimit-Remaining: 0`. Its body matched only the fixed `quota_denial` category; no raw text was retained. It did not return a monthly quota balance. Consequently this establishes a current provider limit response, but does not distinguish monthly credit exhaustion from the short request limit or another account restriction. The existing `X-API-Key` authentication matches the [official OpenAPI specification](https://api.watchmode.com/openapi.json). No further request was sent.

The application can reduce demand through durable identity/availability reuse, honor retry/circuit deadlines and preserve good data during denials. It cannot repair a rejected Trakt client registration or increase a Watchmode allowance through retries. Trakt account/app approval and Watchmode account quota/reset details require provider-account verification if denials persist after the documented waits. No account, billing, credential, provider setting or production cache state was changed during this investigation.
