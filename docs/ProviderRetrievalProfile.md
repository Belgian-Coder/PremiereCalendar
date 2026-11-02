# Provider retrieval profile — installed v1.2.9

A real forced refresh on November 2, 2026 confirms that provider retrieval and candidate enrichment dominate the remaining movie refresh time. The complete October 19–25 movie week finished in 57.306 seconds, preserving all 134 titles with English/Dutch and minimum runtime 45 minutes selected. Reloading returned the complete cache with daily counts 7/20/21/15/32/21/18 and no browser console errors or warnings.

This was a forced refresh with existing local caches, not an empty-installation cold run or a controlled before/after comparison. It measures deployed source `7c48557e4ee8f9d6f0440bd999285ac9de9732bb`.

## Observed work

| Surface | Measurement | Meaning |
| --- | ---: | --- |
| Full page load | 57.306 s | Final complete result, not browser paint |
| SIMKL discovery/mapping source | 53.441 s | Includes downstream resolution and enrichment |
| Actual SIMKL HTTP requests | 2; 64/71 ms | The feed download itself was fast |
| SIMKL movie candidates entering enrichment | 1,034 | Many lack enough metadata for early request filtering |
| Candidates rejected by the later request filter | 989 | Rejected after detail resolution |
| TMDb HTTP requests started, entire job | 1,105 | 1,102 successful; three cancelled attempts |
| TMDb successful request duration, median / p95 | 18.78 / 132.40 ms | Begins inside HTTP handler; excludes the separate client limiter |
| Movie detail stage duration, median / p95 | 736.50 / 995.88 ms | Includes client waiting and cache/persistence work |
| Rotten Tomatoes HTTP requests | 157 | Includes lookups outside the 46 traced movie RT stages |
| RT request duration, median / p95 | 1,500.39 / 3,114.56 ms | Includes admission and response consumption |
| RT admission duration, median / p95 | 869.79 / 2,574.97 ms | Capacity waiting matters alongside remote response time |
| RT headers duration, median / p95 | 631.70 / 995.35 ms | After admission to response headers |
| Checkpoint writes | 37; summed 369.33 ms | Not the dominant cost in this run |
| Final checkpoint | 861,442 → 212,185 bytes | Complete payload; 13.77 ms serialize/encode/store |

TMDb's outer limiter runs before `HttpClient.SendAsync`, outside the provider HTTP diagnostics. Its default token budget is 20 requests/second. The observed request volume divided by that budget is about 55 seconds, consistent with the wall time. This is evidence of excessive request volume meeting the local rate budget, not evidence of a slow TMDb server. There is no separately measured outer-limiter duration yet; the detail-stage gap also includes other client work.

Do not add overlapping source, item, request and progress durations to infer wall time. Percentiles are independent distributions; subtracting their medians would not measure a queue. Extraction retains the exact foreground job rather than mixing adjacent-week traffic.

The response-cache writer recorded 14 TMDb and one RT best-effort write budget expiry. These are cache persistence failures rather than failed provider HTTP responses; the full week still completed. They can increase later retrieval work and deserve investigation separately from network speed.

## Evidence

The installed writer reported zero dropped performance events, writer failures or operational logging failures after the run. Extraction retained 18,983 records for instance `89fd75064c3d4904b16d5824a865713e`, load `d2c09de2d9b2487f8ac3db5e7e826228`, job `4c8eeafdf25a40deb3eac02774857fc9`.

The local and NAS v1.2.9 release bundles include `live-provider-129-events.ndjson`, `live-provider-129-summary.json`, `profile-live-provider.ps1`, installed health/browser acceptance and a screenshot. Diagnostics contain opaque correlation keys and numeric timing/status fields, without titles, provider URLs, credentials or response bodies.

## Next retrieval work

Source inspection and a read-only check of the [public SIMKL October archive](https://data.simkl.in/calendar/2026/10/movie_release.json) found 1,162 distinct TMDb identities inside the selected week, with no language/runtime fields. Date filtering already runs before enrichment in `SimklClient`. This is legitimate global release coverage. The 1,034 enrichment entries above are the subset reaching the item-mapping path; other candidates can reuse existing resolved items. Rejecting unknown languages, low-rank entries or titles absent from TMDb's date-filtered discovery would risk losing valid releases.

The next priorities are:

1. Add an explicit timing around the outer TMDb limiter, with separate admission and remote-response durations. Validate using an exhausted small token bucket so waiting is visible without attributing it to the remote server. Current figures establish the request-volume problem but do not directly measure this wait.
2. Reuse a successfully fetched canonical Rotten Tomatoes page within a refresh when Wikidata and search fallback resolve to that same page. Keep successful empty responses separate from transport/parser failures, preserve alternate-page fallback and both critic/audience scores, and fetch afresh in the next forced refresh. This is a concrete source-level opportunity; its actual request savings are not established by the present opaque request logs.
3. Preserve complete eligibility checks and forced-refresh freshness. Normal updates already reuse fresh memory/persistent TMDb details. A forced refresh deliberately bypasses them. A lightweight details endpoint still costs a token per candidate and would require a second call for accepted films; it is unlikely to improve this token-limited workload. Changing forced refresh to retain older eligibility data is a product/freshness decision, not a transparent performance optimization.

The RT fallback can repeat a page after a successful no-score Wikidata lookup when search selects the same canonical URL (`RottenTomatoesClient.cs`, page lookup and search fallback). Existing refresh memoization is keyed to the higher-level identity, so it does not eliminate this within-lookup page repetition. New coverage should assert one direct page fetch for the repeated URL, retained alternate-page scores, retryable failures and a new fetch in the following force generation.

Do not divide 157 RT requests by 46 traced movie RT stages to infer requests per lookup: external-candidate score hydration also invokes RT outside those stages, and 108 request starts lack an item operation ID. Separate lookup-path accounting is needed before making that comparison. These follow-up opportunities are documented, not included in the installed v1.2.9 binary.
