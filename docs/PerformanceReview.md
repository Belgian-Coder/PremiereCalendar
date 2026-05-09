# Performance Review

Last reviewed: 2026-05-08.

## .NET 11 Notes

The app now targets `net11.0` and uses the locally installed .NET 11 preview SDK under `.dotnet`. The deployed build is published self-contained for `win-x64`, so the hosted app does not require a machine-wide .NET 11 runtime install.

The .NET 11 release notes drove the current dense-list change:

- Blazor `Virtualize<TItem>` adapts to item-size changes over time and raises the default overscan count from `3` to `15`.
- Kestrel has HTTP parser allocation/throughput improvements for malformed HTTP/1.1 traffic, which does not materially affect normal LAN browsing.
- Zstandard compression is added to ASP.NET Core response compression.

The variable-height `Virtualize` improvement is the item that directly helps the dense calendar. The app keeps one selected day mounted, uses two-card virtualized rows, provides a conservative initial `ItemSize`, and uses app-level overscan of 4 rows. That keeps scrolling buffered while staying well under the mounted-card and DOM-node budgets in dense-week validation.

Response compression is enabled for dynamic text responses and static text assets. The middleware negotiates `zstd`, Brotli, or gzip based on `Accept-Encoding`; HTTPS compression is left at the framework default because the app does not need to compress secret-bearing HTTPS responses. Static assets are mapped with `MapStaticAssets().ShortCircuit()` so matched asset requests skip the remaining middleware pipeline and still keep ASP.NET Core's build-time compression, fingerprinting, ETag, and immutable-cache behavior.

References:

- `https://learn.microsoft.com/en-us/aspnet/core/release-notes/aspnetcore-11`
- `https://learn.microsoft.com/en-us/aspnet/core/blazor/components/virtualization`
- `https://learn.microsoft.com/en-us/aspnet/core/fundamentals/static-files`
- `https://learn.microsoft.com/en-us/aspnet/core/performance/response-compression`
- `https://developer.themoviedb.org/docs/rate-limiting`

## Hosting

This app does not run under IIS, so there is no IIS application-pool idle timeout. When installed as a Windows Service, the .NET process should remain running until the service is stopped, the machine restarts, or the process fails.

The service installer configures:

- Automatic service start.
- Service failure recovery with restart attempts.
- optional LAN firewall access for the configured port.
- Cleanup of any manually started `PremiereCalendar.exe` process before the service starts, so the service can bind to the configured port.
- Cleanup of old user Startup-folder shortcuts that would otherwise start duplicate manual app instances.

If the app is only started as a background process, service recovery is not active. Run the root `Install-PremiereCalendar.ps1` wrapper from an elevated PowerShell session to make hosting persistent.

## Cache And Images

Calendar data is cached by week plus discovery criteria, not by transient display-only choices. Refresh bypasses the matching week cache for row discovery, but it now reuses fresh enriched rows from that same week cache as a seed. This keeps known sources, trailers, external IDs, runtime, and ratings available without repeating expensive detail/enrichment calls for already-known canonical IDs.

Broad date-only TMDb Discover calls are complete by default up to TMDb's 500-page ceiling. They stay usable by streaming page 1 first, then fetching later pages in `Tmdb:PageBatchSize` chunks with bounded page and enrichment concurrency. `Tmdb:MaxUnfilteredPagesPerQuery` and `Tmdb:MaxPagesPerQuery` are still available as hard technical caps, but lowering them means accepting incomplete results.

Image optimization status:

- Remote image URLs are allow-listed.
- Posters go through `/cached-image`.
- Card posters request width-specific variants using `w=185`.
- Resized variants are stored on disk as JPEGs.
- TMDb card-poster requests that already target the requested `w=185` width are stored directly instead of decoded and re-encoded.
- Browser responses include cache headers, ETags, Last-Modified, range support, and stale fallback.
- Resized image fetches now use a bounded temporary source file instead of a `MemoryStream`, reducing managed byte-array pressure for large remote posters.
- Deployment updates should use `deploy/Publish-PremiereCalendar.ps1` so the hosted `App_Data` folder is preserved. The local calendar and image caches are runtime data, not publish artifacts.

Remaining practical limit: ImageSharp still has to decode the image into image memory to resize it. The app limits source bytes before decode with `ImageCache:MaxBytes`.

## Streaming

`PremiereService.StreamPremieresAsync` is real streaming at the source, provider, and enrichment-batch level. Trakt, TVmaze schedules, and TMDb day batches run as separate bounded source work instead of one combined external-calendar bucket. TMDb fetches page 1 first, then later page chunks; raw TMDb metadata is emitted before detail enrichment, and enriched chunks follow in smaller batches so the page receives `PremiereLoadProgress` after about 10 newly enriched cards instead of waiting for an entire large page batch.

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
- `Tvmaze:ScheduleFetchConcurrency` defaults to 4. TVmaze documents at least 20 calls per 10 seconds and recommends backing off on `429`; schedule calls now retry `429` with `Retry-After` or a small fallback delay. `Tvmaze:MaxConcurrentRequests` also defaults to 4 across schedule, lookup, search, and image-list calls.
- `Watchmode:MaxConcurrentRequests` defaults to 2 because the free plan is request-limited and availability fallback can otherwise multiply quickly across many cards.
- `CalendarWarmup:MaximumRemoteWindowsPerWake` defaults to 4. The warmer still checks all priority windows, but fresh cache metadata is skipped and only a few missing/stale windows can start remote work per wake.
- `ImageCache:MaxConcurrentDownloads` defaults to 4 so browser lazy loading cannot start unbounded remote poster downloads.
- Foreground refresh ownership is guarded so an older canceled load cannot clear loading state or overwrite progress for a newer load.
- Nearby-week prefetch runs through a hosted background worker with a bounded priority queue, rather than being owned by the Blazor circuit.
- Calendar cache files are written atomically through a temporary file and then replaced after the write succeeds.
- Best-effort JavaScript calls catch `JSDisconnectedException`, so filter storage, day scrolling, and reconnect-related browser work do not fault the circuit during disconnects.

Latest measured dense refresh case:

- URL: `/series?week=2026-05-04&seriesLang=en%2Cnl`
- Existing week cache: 823 items for the current local settings.
- First refreshed source rows: about 0.17 seconds.
- Trakt source complete: about 0.02 seconds.
- TMDb day-source refresh complete: about 4.3 seconds.
- TVmaze schedule source complete: about 4.6 seconds.
- Full refresh complete: about 4.75 seconds.

Latest hosted smoke after the stability pass:

- URL: `/series?week=2026-05-04&seriesLang=en%2Cnl`
- Foreground refresh completed without browser console errors or circuit disconnects.
- Final hosted refresh result: 342 cards.
- TMDb source detail: 7 day batches, pages 2-6 of 6, processed 101 of 101 rows, 28.3 seconds in that hosted smoke run.
- Trakt source detail: 0 cards, no candidates returned, 52 milliseconds.
- TVmaze schedule detail: 0 accepted cards, resolved 71 of 71 candidates, 663 milliseconds.
- Cached desktop revisit at 1920x1080: 22 mounted cards, 1,176 DOM nodes, no horizontal overflow.
- Cached mobile revisit at 390x844: 18 mounted cards, 990 DOM nodes, no horizontal overflow.

The current full-completion limit is a mix of TMDb Discover/detail work and TVmaze schedule mapping. TMDb does not expose a larger page-size parameter in the app's Discover flow, so optimization is done by page batching, source-level parallelism, and bounded request rates rather than asking TMDb for more than 20 rows per page. When a language filter is selected, TVmaze candidates with known non-matching language are now skipped before TMDb resolution.

Live broad-week checks show that TVmaze can be expensive when every episode is enabled without narrowing filters, but it is not wasted globally. For `2026-05-04` with English and Dutch selected, TVmaze contributed 80 source cards and the final total was 823 cards versus 767 from TMDb. For an unfiltered `2026-04-27` every-episode refresh, TVmaze contributed 193 source cards and the final total was 2,219 versus 2,089 from TMDb, but full completion took about 115 seconds because both TMDb and TVmaze had a very large episode set. Keep TVmaze enabled when maximum episode/source coverage matters; disable schedule discovery in Settings when broad unfiltered refresh speed matters more.

Nearby-week prefetch now runs after the visible week completes. The default queue warms next week, previous week, weeks +2, +3, +4, and then week -2 with the same saved filters. These calls use the normal week cache path and `forceRefresh: false`, so already-warmed weeks return from disk while interrupted or missing weeks resume on a later visible page load. The queue behaves as a sliding window: when the user moves to Previous or Next, pending weeks for the newly visible window are promoted ahead of older far-away pending weeks.

## Browser Rendering

The calendar now renders one selected day at a time. Dense days use .NET 11 `Virtualize`; moderate days render 10 cards and auto-load more as the user scrolls. The card grid uses one shared gap variable so left/right columns and virtualized rows keep consistent spacing across desktop and mobile. Day switching reuses the mounted `CalendarDay` component instead of forcing a keyed teardown, scrolls the board immediately on the browser click, and avoids `content-visibility:auto` on cards to reduce visible paint flicker.
