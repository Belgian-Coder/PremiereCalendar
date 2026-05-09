# Architecture

For an end-to-end operational walkthrough, see [How It Works](HowItWorks.md).

## App Model

Premiere Calendar is a .NET 11 preview Blazor Web App using interactive server rendering. This keeps TMDb and OMDb credentials on the server and avoids a separate backend project for the first version.

## Data Flow

1. `Calendar.razor` selects the current Monday-to-Sunday window and route mode: combined, series, or movies.
2. `IPremiereService.GetPremieresAsync` converts saved filters to `PremiereDiscoveryCriteria`, checks the criteria-specific week cache, and launches the needed TMDb discovery calls. With no saved filters, discovery is broad and TV defaults to episode air dates; with saved filters, supported filters are sent to TMDb Discover.
3. Optional `IPremiereDiscoveryProvider` implementations add external candidate rows from Trakt calendars, series-only TVmaze schedules, and Watchmode releases. Providers that implement `IStreamingPremiereDiscoveryProvider` can report candidate batches as their own API calls complete. A candidate is only accepted after its TMDb ID is present or resolved through TMDb `/find/{external_id}`.
4. `PremiereService` checks the local week cache unless the user clicked Refresh. Refresh also tells source clients to bypass their in-memory API caches for that request.
5. `TmdbClient` calls TMDb Discover endpoints, waits for the local TMDb request limiter, fetches page 1 to discover the total page count, fetches remaining pages with bounded parallelism, retries `429 Too Many Requests` with `Retry-After` support, and caches discover results for six hours when the request is not a forced refresh. The limiter combines a requests-per-second token bucket with a hard concurrent-request cap so local page/detail fan-out cannot pile up a giant upstream queue. TMDb, TVmaze, Watchmode, OMDb, and image-cache misses use keyed single-flight coalescing so concurrent identical requests share one upstream call.
6. `PremiereService` normalizes rows into canonical calendar items keyed as `tv:{id}` for new-series premieres, `tv:{id}:air:{yyyyMMdd}` or `tv:{id}:s{season}e{episode}` for series episodes, or `movie:{id}` for movies.
7. `PremiereService` enriches each result with TMDb details, videos, external IDs, genres, keywords, TV networks, watch-provider names/categories, movie release types, certifications, TV status/type, and additional TMDb artwork. Detail enrichment is concurrency-limited and cached for twelve hours.
8. `OmdbClient` optionally enriches IMDb, Rotten Tomatoes, Metacritic, IMDb votes, plot, and poster artwork by exact IMDb ID. OMDb failures or missing ratings return `n/a` behavior instead of breaking the page.
9. Artwork providers only run when TMDb has no usable poster. Fanart.tv, TVmaze image lists, TheTVDB, and Wikimedia/Commons are cached, failure-isolated, and selected according to the artwork priority rules.
10. `TvmazeClient` optionally enriches TV items by exact TheTVDB ID or IMDb ID, including network/web-channel names and TVmaze artwork when available. When TMDb details have no usable external IDs or image, it can make a conservative exact-title TVmaze search for artwork.
11. `PremiereService` filters each source batch with the saved UI filters, yields source/enrichment progress through `StreamPremieresAsync`, then writes complete normalized week results to local file cache. Broad unfiltered TMDb work is split into day-sized source batches and launched through a bounded source queue. Within each TMDb source, page 1 is fetched first, later pages are fetched in page batches, and enrichment results are emitted in smaller card chunks so the UI does not wait for a full large page batch to finish. External discovery providers also stream as each provider completes. If foreground loading exceeds the configured budget, the UI stops waiting, renders the best partial result, and marks that state as a diagnostic instead of storing it as a fresh complete cache.
12. `PremiereCard` routes poster and backdrop image URLs through `/cached-image`, which stores allowed remote image bytes under the local image cache. Poster cards request `w=185`, so `FileImageCache` stores a width-specific JPEG variant for the displayed size instead of repeatedly sending full remote poster bytes. Remote image downloads share a server-side concurrency cap. The card renders a tiny placeholder first and moves the real URL into `data-lazy-src`; a browser intersection observer fills `src` only when the card approaches the viewport.
13. `PremiereFilter` applies final local filtering and sorting before the UI renders. This is still required for filters that cannot be pushed to TMDb and for consistency when external providers add candidates.
14. `AdjacentWeekPrefetcher` warms nearby week caches after the visible week loads, using the same saved filters as the visible request. `CurrentWeekCalendarWarmupService` also runs on startup and at a configurable wake interval, but routine warmup uses `forceRefresh: false` and warms stale or missing full-week media cache keys that the UI can reuse. The priority starts with today/tomorrow/yesterday/rest-of-week and expands to the rest of the month, adjacent months, and months +2 through +6. Each wake checks cache metadata first and only starts a bounded number of missing or stale windows, while cycle and per-window budgets keep background work from running indefinitely.

## TMDb Query Rules

Series discovery has two modes. Every-episode mode uses `air_date.gte` and `air_date.lte` and runs one request per day so the card date matches the selected day. New-series-only mode uses `first_air_date.gte` and `first_air_date.lte`.

Movie discovery uses `primary_release_date.gte` and `primary_release_date.lte`; it does not apply runtime or release-type filtering. Fresh week orchestration runs movie discovery as day-sized batches for incremental progress. Live TMDb Discover Movie result rows expose the mapped date as `release_date`, so normalization reads `release_date` first and falls back to `primary_release_date` for fixtures or future response variants.

TMDb is the canonical identity source. TMDb discovery always uses the selected date window and technical safety parameters. When filters are saved, the app also sends TMDb-supported filters such as media type, language, genres, watch providers, monetization types, TMDb vote filters, runtime, keyword IDs, movie release types, movie certifications, and selected TV network IDs. Trakt and Watchmode release discovery can add movie or series candidates, while TVmaze schedule discovery can add series candidates only; each candidate must resolve to a TMDb ID through existing IDs or TMDb external-ID lookup before it becomes a card. Other sources enrich existing TMDb rows only. SIMKL is account/library sync state, not a calendar discovery provider.

Provider and channel source names are best-effort enrichment. For TV, the app prefers TVmaze network/web-channel names, then TMDb TV networks, then TMDb watch providers. For movies, the app uses TMDb watch providers. When no source regions are configured, watch-provider names are read from every TMDb region returned in the detail response and de-duplicated before rendering.

## TMDb Filter Coverage

The filter design was checked against the official TMDb Discover Movie and Discover TV API references:

- Movie reference: `https://developer.themoviedb.org/reference/discover-movie`
- TV reference: `https://developer.themoviedb.org/reference/discover-tv`

The visible filter pane mirrors TMDb's public movie/TV pages where it fits a weekly calendar: sort, media type, where-to-watch providers and country, availability types, date window through the selected week, genres, certification, language, user score, minimum votes, runtime, keywords, movie release types, TV network text, TV status, and TV type. Language is a multi-select checkbox dropdown; the service fans out to one TMDb Discover request per selected original language. The app adds one calendar-specific TV scope control for every episode versus new series only.

The UI is split by route because TMDb's movie and TV surfaces differ. `/series` locks the media request to series and uses TV-oriented network controls. `/movies` locks the media request to movies and uses movie-oriented provider and release-type controls. `/` remains the combined calendar, keeps the media checklist, and renders the TV filter group first followed by the movie filter group.

Filter values come from TMDb value endpoints where available: movie/TV genre lists, configuration languages, configuration countries, movie/TV watch-provider lists, and movie/TV certification lists. Catalog labels take precedence over item-derived fallback labels so values render as readable names such as `Dutch (NL)` rather than bare ISO codes when TMDb supplies the catalog entry. Selected provider values include TMDb provider IDs, so they can be sent to Discover when a watch country is selected. Account-specific TMDb `Show Me` and `My Services` values are not copied because this app does not authenticate against a TMDb user session.

## Identity Rules

Calendar items use TMDb IDs as primary identity:

- `tv:{tmdbId}` for series premieres.
- `tv:{tmdbId}:air:{yyyyMMdd}` for show-level episode air-date rows.
- `tv:{tmdbId}:s{season}e{episode}` for exact episode rows when a source supplies season and episode numbers.
- `movie:{tmdbId}` for movie first releases.

Title matching is not used for MVP enrichment or de-duplication.

## Trailer Rules

`TrailerSelector` only accepts YouTube videos with non-empty keys. It prefers trailers over teasers, then official videos over unofficial videos. It builds direct YouTube URLs and never links to search results.

Every card also has a separate `YouTube search` link built from the title plus the word `trailer`. This is intentionally distinct from the verified trailer link.

## Shareable URLs

The calendar writes the active week and non-default filter state to query parameters. A copied URL restores the same week, media type selection on `/` when it is not the default All view, sort order when it changes, per-media language selections, origin countries, genres, selected provider/channel sources, watch countries, availability types, certifications, runtime windows, and keyword text. Default values such as date sorting, ascending direction, all languages, all origins, score `0-10`, minimum votes `0`, and runtime `0-360` are intentionally omitted from generated URLs. Multi-language values are serialized as comma-separated route-specific parameters such as `seriesLang=en,nl` and `movieLang=fr,nl`. The series row-scope control is app-specific and maps to TMDb `air_date` versus `first_air_date` requests. On `/series` and `/movies`, the route itself is the media type. The browser also stores the last saved non-default filter query in local storage per route, without the week, so opening `/`, `/series`, or `/movies` without query parameters restores the last saved filters for that route without pinning an old week. When `/` restores without explicit query parameters, it overlays the separately saved `/series` and `/movies` media filter groups onto the saved All query, so the combined view can reuse the media-specific filter caches.

The server cache is keyed by week plus the server-discovery criteria hash. `FileCalendarCache` stores the normalized Monday-to-Sunday result list for that request slice after source batches have been filtered by saved request filters. Local title/source text and UI sort are applied in memory and do not create extra source cache files. Refresh rebuilds the matching slice and bypasses source memory caches. TMDb page fetching, day-source scheduling, and enrichment are bounded-parallel operations; `Tmdb:PageBatchSize` controls how many TMDb pages are fetched in one remote batch, while `Tmdb:EnrichmentProgressBatchSize` controls how many enriched cards are emitted per UI update. Progress updates carry source-card counts, completed/total work, and a short detail string so the UI can show per-source progress bars while broad refreshes continue. `Tmdb:MaxPagesPerQuery` and `Tmdb:MaxUnfilteredPagesPerQuery` remain hard technical caps and default to TMDb's documented 500-page ceiling. Nearby-week prefetch runs in the background with the current saved request filters. The UI uses stale-while-refresh behavior for same-week refreshes and saved-filter reloads: previous relevant cards remain visible with an updating indicator until new source progress arrives.

When exact episode rows exist for a show on a day, generic TMDb `tv:{id}:air:{yyyyMMdd}` rows for that same show/day are suppressed. Distinct exact episodes such as `tv:{id}:s01e09` and `tv:{id}:s01e10` are kept, because every-episode mode should still show every episode that aired that day. This de-duplication is also applied when reading older week-cache files, so stale cached generic rows do not keep reappearing after the merge rule changes.

## Artwork Rules

Poster artwork is resolved in this order:

1. TMDb poster.
2. TMDb detail/image poster.
3. OMDb poster from already-fetched rating enrichment, only when OMDb is enabled and TMDb does not have a poster.
4. TVmaze image from already-fetched TV enrichment.
5. Fanart.tv poster or suitable fanart fallback.
6. Optional TheTVDB series artwork.
7. Wikimedia/Commons image with reusable license metadata.
8. TMDb backdrop as a final visual fallback.

The card shows the artwork source when an image is available.

Remote image bytes are served through `FileImageCache` instead of being linked directly from cards. This reduces repeated CDN calls across browser sessions while still keeping TMDb, OMDb, Fanart.tv, TVmaze, TheTVDB, and Wikimedia URLs as the source of record. When a `w` query parameter is present, the cache key includes the normalized width and the cached file is resized to that width as a JPEG variant. The `/cached-image` endpoint validates HTTPS image URLs against the same allowed-host list even if local image caching is disabled and the endpoint redirects directly to the source image. Artwork-only providers are skipped as soon as a usable poster exists from TMDb, OMDb, or TVmaze enrichment.

## UI Components

- `Calendar.razor` handles page state, query-string filter state, load errors, and a memoized `_visibleItems` list that is recalculated only when loaded items, saved filters, source-progress scope, or route mode changes.
- `CalendarFilterDialog.razor` owns draft filter state while the pane is open. Cancel/close discards the draft, and Save sends one normalized `CalendarFilters` value back to the page.
- `CalendarFilterState.cs` centralizes filter cloning, normalization, and route-mode locking so the page and dialog do not duplicate filter mutation rules.
- `MediaFilterPanel.razor` owns the per-media TMDb-style dropdown/checkmark filter groups.
- `ScoreFilter.razor` owns the TMDb score-range controls.
- `CalendarWeek.razor` renders the sticky day selector and only the currently selected day section. The day buttons update Blazor state directly and use one intentional JS call to move the selected day board back to the top.
- `CalendarDay.razor` renders empty-day states, 10-card batches for moderate days, and .NET 11 Blazor `Virtualize` rows for dense days over 40 items. It has a render fingerprint so unchanged day inputs skip rerendering.
- `PremiereCard.razor` renders metadata, provider/channel source names, descriptions, scores, data provenance tags, and outbound links. It has a render fingerprint based on rendered item fields, score source, and image-cache refresh state.
- `wwwroot/dom-observer.js` provides one requestAnimationFrame-batched initializer for lazy images, day auto-loading, and filter-pane swipe handlers. The feature scripts scope scans to newly added nodes instead of each running a document-wide `MutationObserver`.

## Request Pipeline

The app uses ASP.NET Core response compression for dynamic text responses and static text assets. On .NET 11, response compression can negotiate Zstandard in addition to Brotli and gzip when the browser sends a matching `Accept-Encoding` header. Static files are exposed through `MapStaticAssets().ShortCircuit()`, which keeps build-time compression, fingerprinted URLs, ETags, and immutable-cache headers while avoiding unnecessary middleware work for matched asset requests.

Navigation is a top bar so the calendar can use the full browser width. It exposes combined, series-only, movie-only, and About routes plus a lightbulb theme toggle. On desktop, the selected day board is capped to a `1920px` calendar width, with cards laid out at most two per row. The day selector stays sticky above the board, highlights the selected day, and has a compact filter icon at the left edge. Filters open in a front-of-screen pane with draft state; close/cancel/swipe discards changes, and Save applies them to the calendar and URL. Multi-select filter groups use compact dropdowns with checkboxes and the large source/language lists are searchable and capped to avoid rendering thousands of options at once. Theme state is browser-only local storage mirrored to a non-secret cookie for server-rendered first paint; it is saved synchronously on click, guarded against duplicate script initialization, and re-applied after navigation so route changes do not reset dark mode. The loaded-source chips are buttons that can scope the visible calendar to one loaded source batch. On mobile, the day selector remains sticky while the selected day and cards use the full available screen width.

## Known Limits

TMDb `air_date`, `first_air_date`, and `primary_release_date` are metadata dates, not Belgian streaming availability dates. TMDb air-date discovery identifies shows airing on a day but does not expose exact episode titles; exact season/episode labels appear only when an external schedule source supplies them. TVmaze schedules improve broadcast/web premiere coverage, but still only become cards when they map back to TMDb. OMDb enrichment is optional and may not return IMDb or Rotten Tomatoes scores for every title.
