# Performance Review

Last reviewed: 2026-11-06. The installed v1.2.15 baseline from source `6f8d1bc` returned cached rows quickly, but its observed full durable refresh took 108.95 seconds and remained partial under provider restrictions. The [new full-week comparison](FullRefreshPerformance-20261106.md) separately measures source-list refresh with fresh enrichment reuse, stale snapshots and the unchanged force-all API. Release evidence records installation status; fixture gains alone do not establish production timing.

## November 5 candidate

The candidate reduces repeated provider work, overlaps independent enrichment, makes background recovery and scheduling bounded, and separates trace archival from event persistence. Provider coverage, forced-refresh fences and existing data remain protected. Controlled fixtures demonstrate SIMKL sends falling from five to three across adjacent weeks and shorter series enrichment critical paths with matching full projections. The matched 2,000-row fixture does **not** establish a broad cold-load speedup: cold first-result and completion times were slower in the candidate pair.

The recorded compiled-checkpoint test rosters reconcile to 1,738 passing cases and one expected Linux skip; no roster case lacks result evidence. See [November 5 implementation and evidence](PerformanceImprovements-20261105.md) for the overlapping request coverage, exact measurements, executed checks, review dispositions and remaining external-provider/install boundaries. Historical hosted measurements below describe earlier binaries and workloads.

## Calendar loading and recovery

The supported runtime is Docker with PostgreSQL. Historical Windows hosting measurements below describe the compatibility deployment, not current container acceptance.

Fresh complete calendar caches are read before provider scheduling, using the same shared series/movie keys and filter rules as background loads. Local IMDb and cached Rotten Tomatoes hydration remain available. Stale rows and rows needing remote score hydration are provisional; their durable job supplies subsequent progress. A complete cache hit creates no provider job or provider HTTP request.

Provider cancellation caused by navigation, background preemption, or shutdown releases capacity without lowering concurrency or opening a circuit. Acquisitions share persisted-state initialization and cooldown waits. Long provider deadlines survive restart and defer durable work through `NextAttemptUtc` rather than retrying early.

Scheduler checkpoints retain the union of successfully completed sources and their completion times. Failed sources remain retryable, and legacy checkpoints conservatively rerun uncertain source states. Successful sources older than `WeekCacheHours` are fetched again after long deferrals. Source completion means iterator exhaustion: processing counters reaching their current total do not establish completion. Source completion and final snapshots persist immediately; other progress persists at most once every two seconds. Dense source snapshots coalesce at 250 ms, retaining every row and always flushing errors and completion. Diagnostics retain the latest update per source instead of every growing snapshot.

Claims use unique ownership tokens, independently renewed leases, transactional lease/job updates, and fenced persistence. Calendar file publication holds the owned job lock, so an expired worker cannot overwrite a reclaimed job's cache. Recovery checks run every five seconds and terminal retention cleanup runs daily. Queued jobs without watchers yield priority. Detaching an observation demotes its demand without cancelling the running work or another browser's interest. Competing active foreground demand can checkpoint and preempt that now-passive work; returning to it restores priority. Progress subscriptions retain bounded source summaries and the latest canonical snapshot, and late subscribers replay durable state.

Watched demand flows through shared provider admission, including a single-flight request shared with background work. Provider queues retain FIFO order within foreground/background lanes and admit an oldest background waiter after eight foreground admissions during contention. TMDb's configured token bucket/concurrency and adaptive provider cooldown, circuit and capacity limits remain unchanged. In-flight background requests finish normally.

One refresh generation reuses verified successful provider outcomes for exact lookup identities, including successful absence, across mapping and final backfill. Different identities and a later explicit refresh remain independent. Transient failures, malformed responses and stale fallback do not become successful generation memos. Cached RT-selected scores hydrate in two bounded lanes with deduplicated lookup identities; input order, existing non-null score precedence and batched local IMDb reads are retained.

Checkpoint storage can use a bounded Brotli envelope containing the complete version 3 JSON, without dropping canonical progress, source summaries or completion times. Small/unhelpful or very large payloads stay raw. The two-second progress durability interval and immediate source/final/preempt persistence are unchanged. Older checkpoints remain readable. Before downgrading to an older binary that cannot read the envelope, stop workers and run the new binary's transactional `database prepare-legacy-checkpoints` command; see [speed implementation and validation](SpeedImprovements.md).

Calendar metadata reads use an optional item-count header; older envelopes retain their full-parse fallback. All-view warmup checks both media keys. Images share decode and staging limits across cache instances, reject oversized decoded images and use content-based ETags. An existing immutable stale image returns immediately while a bounded, deduplicated background refresh runs; explicit refresh still waits for its fetch. Cache readers allow atomic replacement while an existing reader finishes its original file.

Backfill batches IMDb lookups, deduplicates detail and rating requests by their provider keys, and uses bounded workers while preserving input order and known values.

Movie enrichment overlaps independent ratings, score and artwork work. Successful TMDb detail and Rotten Tomatoes responses retain their existing freshness across restarts, avoiding repeated filtering/enrichment HTTP requests. Forced refreshes bypass both tiers, with publication fences preventing stale normal work from undoing them. See [movie-week speed investigation](MovieWeekSpeedValidation.md) for controlled before/after measurements and the remaining cold-provider cost.

See [speed and reliability validation](SpeedReliabilityValidation.md) for the controlled workload, results, and container acceptance evidence.

## .NET 11 Notes

The app targets `net11.0` and `global.json` pins .NET SDK `11.0.100-rc.3.26522.148`. November 5 validation uses the pinned SDK in the existing local WSL Docker build container; build outputs stay in the sibling build volume and ignored local artifacts. The historical Windows compatibility build is self-contained.

The .NET 11 release notes drove the current dense-list change:

- Blazor `Virtualize<TItem>` adapts to item-size changes over time and raises the default overscan count from `3` to `15`.
- Kestrel has HTTP parser allocation/throughput improvements for malformed HTTP/1.1 traffic, which does not materially affect normal LAN browsing.
- Zstandard compression is added to ASP.NET Core response compression.

The calendar does not use fixed-height `Virtualize` for rich cards. Poster, description, provider, and provenance content produces genuinely variable heights, and live testing showed recycled rows flashing while a streamed result set grew. The selected day now uses a measured two-way window instead: ten cards appear initially, scrolling reveals ten more, and no more than 40 rich cards remain mounted. When the oldest batch is retired, JavaScript measures its actual rendered height and a spacer preserves the scroll position. Scrolling back to that spacer restores the prior batch and retires an equal batch from the bottom.

Top-level All/Series/Movies navigation resets the desktop calendar to its command bar before replacing the measured day window. This prevents a new media route from briefly mounting cards for the previous route's scroll offset and then jumping to its first batch.

All streamed calendar loads preserve existing card positions, update matching cards in place, and append newly discovered cards within their day as provider batches arrive. Because intermediate provider snapshots are not necessarily cumulative, a card already shown during a load is retained even when a later snapshot temporarily omits it. Mode/week/discovery-criteria changes clear the previous scope's cards, counts, progress and freshness before asynchronous navigation; same-view Update and Refresh sources retain the current presentation. An explicit sort change still applies a clean full sort. The November speed work deliberately leaves first-result render throttling unchanged.

All days use the bounded measured card window instead of fixed-height row virtualization. Rendering ten cards initially and revealing explicit batches preserves card identity and prevents flashing without materializing an entire dense day. The 40-card ceiling applies even after long downward scrolling; the earlier implementation bounded each increment but could eventually mount every card.

Automatic scroll loading advances only one ten-card batch when the sentinel is within 400 pixels of the viewport. A matching upper sentinel restores earlier batches. A live Series trace previously showed 435 rich cards and 27,554 DOM elements mounted at once, including a 2.1-second style recalculation that blocked scrolling; the rolling ceiling removes that unbounded main-thread growth.

Streamed item/metadata updates preserve the number of cards the user has already revealed. The component clamps that count only when the result set becomes smaller, preventing a background update from collapsing (for example) 70 visible cards back to 10 and moving the scroll position unexpectedly.

Closed card provenance is demand-rendered. The disclosure summary remains keyboard-accessible, but its confidence, merge, missing-data, and source markup is not added to the DOM until that card's Provenance disclosure is opened for the first time. A live Series baseline measured about 27 provenance descendants inside an average 68-descendant card, so this removes roughly 40% of the initial per-card DOM without hiding primary metadata or reintroducing fixed-height virtualization.

The public edge is already HTTP/2. Caddy intentionally advertises HTTP/1.1 and HTTP/2 (HTTP/3 is disabled for compatibility), and live Chromium network events confirmed `h2` for the document, Blazor negotiation, static assets, and cached images. Caddy's private Premiere upstream remains plain HTTP on the trusted LAN; changing that single backend hop would not reduce browser-side DOM, style, or paint work.

Response compression is enabled for dynamic text responses and static text assets. The middleware negotiates `zstd`, Brotli, or gzip based on `Accept-Encoding`; HTTPS compression is left at the framework default because the app does not need to compress secret-bearing HTTPS responses. Static assets are mapped with `MapStaticAssets().ShortCircuit()` so matched asset requests skip the remaining middleware pipeline and still keep ASP.NET Core's build-time compression, fingerprinting, ETag, and immutable-cache behavior.

References:

- `https://learn.microsoft.com/en-us/aspnet/core/release-notes/aspnetcore-11`
- `https://learn.microsoft.com/en-us/aspnet/core/blazor/components/virtualization`
- `https://learn.microsoft.com/en-us/aspnet/core/fundamentals/static-files`
- `https://learn.microsoft.com/en-us/aspnet/core/performance/response-compression`
- `https://developer.themoviedb.org/docs/rate-limiting`

## Hosting

The app runs only as a container (restart policy `unless-stopped`; see [Docker and PostgreSQL](DockerAndPostgres.md)). It does not run under IIS, so there is no IIS application-pool idle timeout. Earlier Windows service hosting notes were removed together with the Windows distribution.

## Cache And Images

Calendar data is cached by week plus discovery criteria, not by transient display-only choices. Normal loads reuse fresh enriched item projections across occurrences and retain known sources, trailers, external IDs, runtime and ratings. Explicit forced refresh keeps its provider-cache bypass and publication fences; a failed forced fetch cannot replace a previous good result with false absence.

Broad date-only TMDb Discover calls are complete by default up to TMDb's 500-page ceiling. They stay usable by streaming page 1 first, then fetching later pages in `Tmdb:PageBatchSize` chunks with bounded page and enrichment concurrency. `Tmdb:MaxUnfilteredPagesPerQuery` and `Tmdb:MaxPagesPerQuery` are still available as hard technical caps, but lowering them means accepting incomplete results.

Image optimization status:

- Remote image URLs are allow-listed.
- Posters go through `/cached-image`.
- Card posters request width-specific variants using `w=185`.
- Browsers advertise image formats through `Accept`; width-specific poster requests are stored as WebP when supported and JPEG/original bytes remain the fallback. APNG retains its bounded original PNG bytes to preserve animation; CSS controls its displayed size.
- Format is part of the cache key and `/cached-image` returns `Vary: Accept`, so WebP and fallback representations cannot collide.
- Browser responses include cache headers, ETags, Last-Modified, range support and immediate stale fallback with a one-minute browser lifetime while a bounded refresh is queued.
- Resized image fetches now use a bounded temporary source file instead of a `MemoryStream`, reducing managed byte-array pressure for large remote posters.
- Deployment updates replace the digest-pinned container image and keep the `app-data` volume (`/app/App_Data`) and the PostgreSQL volume, as described in [Docker and PostgreSQL](DockerAndPostgres.md). The local calendar and image caches are runtime data, not image contents.

Resizing still requires decoded image memory. The app limits source bytes before decode with `ImageCache:MaxBytes`, checks the total decoded frame pixels, and shares the decode limiter across cache instances. Magick.NET replaces ImageSharp and retains JPEG, WebP, animation, and TIFF conversion without a license key.

## Streaming

`PremiereService.StreamPremieresAsync` is real streaming at the source, provider, and enrichment-batch level. Trakt, TVmaze schedules, and TMDb day batches run as separate bounded source work instead of one combined external-calendar bucket. TMDb fetches page 1 first, then later page chunks; raw TMDb metadata is emitted before detail enrichment, and enriched chunks follow in smaller batches so the page receives `PremiereLoadProgress` after about 10 newly enriched cards instead of waiting for an entire large page batch.

IMDb's daily gzip import stages the bounded compressed download on disk, releases the HTTP response and provider slot, and then streams rows into one database transaction. A slow database import no longer consumes the network timeout. It does not retain roughly 1.7 million `ImdbRatingRecord` objects before writing. Calendar enrichment gathers the visible IMDb IDs and resolves them in bounded database `IN` batches instead of opening one query per card. SIMKL calendar JSON is deserialized as an async stream, filtered to the requested date range before retention, and stored in an eight-entry range-scoped LRU rather than the shared unbounded memory cache.

Benefit today:

- The UI can show the first completed external provider, the first enriched TMDb cards for a day, or a later TMDb page chunk before all sources finish.
- Moderate days render 10 cards first, so a completed source produces useful visible content immediately while later batches continue.
- Query-result chips are backed by the source batch payload, so users can isolate one loaded source immediately. The chip count is the number of accepted source cards. A source with zero cards can still show useful detail such as `no candidates returned` or `0 of N candidates matched request filters`.
- Cached weeks return one final cache update quickly.

Operational safeguards:

- `Tmdb:SourceFetchConcurrency` controls how many source batches run at once.
- `Tmdb:EnrichmentProgressBatchSize` controls how many enriched cards are grouped into one progress update.
- `Tmdb:ExternalCandidateBatchSize` controls how many external-provider candidates are accumulated before TMDb ID resolution/enrichment. The default is 100, which keeps TVmaze schedule mapping parallel enough without waiting for a whole very large candidate set.
- `Tmdb:MaxRequestsPerSecond` defaults to 20, below TMDb's documented upper-limit guidance, and the client retries `429` responses.
- `Tmdb:MaxConcurrentRequests` defaults to 4. This is a separate hard cap from the rate limiter and prevents page/detail enrichment fan-out from creating large local queues that then time out.
- `Tmdb:RequestTimeoutSeconds` bounds each HTTP request.
- `Tmdb:SourceTimeoutSeconds` lets one slow source fail closed instead of leaving Refresh in an updating state indefinitely.
- `Tvmaze:ScheduleFetchConcurrency` and `Tvmaze:MaxConcurrentRequests` default to 4. The shared `Tvmaze:MinimumRequestIntervalMilliseconds` defaults to 510 ms across schedule, lookup, search and image-list sends, alongside adaptive admission and `Retry-After`. This deliberately stays within the documented guarantee of at least 20 calls per 10 seconds; it does not guarantee allowance when other applications share the public IP. The pacing setting preserves existing item-cache partitions.
- `Watchmode:MaxConcurrentRequests` defaults to 2 because the free plan is request-limited and availability fallback can otherwise multiply quickly across many cards.
- `CalendarWarmup:MaximumRemoteWindowsPerWake` defaults to 4. The warmer still checks all priority windows, but fresh cache metadata is skipped and only a few missing/stale windows can start remote work per wake.
- `ImageCache:MaxConcurrentDownloads` defaults to 4 so browser lazy loading cannot start unbounded remote poster downloads.
- Foreground refresh ownership is guarded so an older canceled load cannot clear loading state or overwrite progress for a newer load.
- Nearby-week prefetch runs through a hosted background worker with a bounded priority queue, rather than being owned by the Blazor circuit.
- Calendar cache files are written atomically through a temporary file and then replaced after the write succeeds.
- Best-effort JavaScript calls catch `JSDisconnectedException`, so filter storage, day scrolling, and reconnect-related browser work do not fault the circuit during disconnects.

Historical dense refresh case (earlier binary):

- URL: `/series?week=2026-05-04&seriesLang=en%2Cnl`
- Existing week cache: 823 items for the current local settings.
- First refreshed source rows: about 0.17 seconds.
- Trakt source complete: about 0.02 seconds.
- TMDb day-source refresh complete: about 4.3 seconds.
- TVmaze schedule source complete: about 4.6 seconds.
- Full refresh complete: about 4.75 seconds.

Historical hosted smoke after the earlier stability pass:

- URL: `/series?week=2026-05-04&seriesLang=en%2Cnl`
- Foreground refresh completed without browser console errors or circuit disconnects.
- Final hosted refresh result: 342 cards.
- TMDb source detail: 7 day batches, pages 2-6 of 6, processed 101 of 101 rows, 28.3 seconds in that hosted smoke run.
- Trakt source detail: 0 cards, no candidates returned, 52 milliseconds.
- TVmaze schedule detail: 0 accepted cards, resolved 71 of 71 candidates, 663 milliseconds.
- Cached desktop revisit at 1920x1080: 22 mounted cards, 1,176 DOM nodes, no horizontal overflow.
- Cached mobile revisit at 390x844: 18 mounted cards, 990 DOM nodes, no horizontal overflow.

The current full-completion limit is a mix of TMDb Discover/detail work and TVmaze schedule mapping. TMDb does not expose a larger page-size parameter in the app's Discover flow, so optimization is done by page batching, source-level parallelism, and bounded request rates rather than asking TMDb for more than 20 rows per page. When a language filter is selected, TVmaze candidates with known non-matching language are now skipped before TMDb resolution.

Live broad-week checks show that TVmaze can be expensive when every episode is enabled without narrowing filters, but it is not wasted globally. For `2026-05-04` with English and Dutch selected, TVmaze contributed 80 source cards and the final total was 823 cards versus 767 from TMDb. For an unfiltered `2026-04-27` every-episode refresh, TVmaze contributed 193 source cards and the final total was 2,219 versus 2,089 from TMDb, but full completion took about 115 seconds because both TMDb and TVmaze had a very large episode set. These historical runs show why coverage and provider cost must be measured together. The November 5 changes preserve TVmaze and the other configured providers.

Nearby-week prefetch now runs after the visible week completes. The default queue warms next week, previous week, weeks +2, +3, +4, and then week -2 with the same saved filters. These calls use the normal week cache path and `forceRefresh: false`, so already-warmed weeks return from disk while interrupted or missing weeks resume on a later visible page load. The queue behaves as a sliding window: when the user moves to Previous or Next, pending weeks for the newly visible window are promoted ahead of older far-away pending weeks.

## Browser Rendering

The calendar renders one selected day at a time. Every day starts with 10 cards and uses the measured 40-card two-way window described above. The card grid uses one shared gap variable so both desktop columns remain consistent. Day switching keys the selected day so the short entrance animation reliably runs, scrolls the board immediately on browser navigation, and avoids `content-visibility:auto` on cards because it caused visible paint flicker in this variable-height layout.
## Follow-up implementation

See [follow-up validation](FollowUpPerformanceValidation.md) for the cache-maintenance race correction, concurrent bulk IMDb import, reduced progressive merging work, page completion/reconnect recovery, and measured limits.

## Progressive cached-calendar readiness

See [v1.2.17 readiness validation](ProgressiveCalendarReadiness-20261107.md) for
separate calendar readiness and enrichment completion, incremental SignalR card
updates, filter preservation, and the reported 35.45-second Update investigation.
