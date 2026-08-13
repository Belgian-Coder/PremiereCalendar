# Configuration

Configuration is handled with two layers:

- Runtime app settings edited on the Settings page are stored in the local SQLite parameter database.
- Infrastructure values still use standard ASP.NET Core providers. Non-secret defaults live in `PremiereCalendar/appsettings.json`; API credentials must not be committed.

Startup validates the application database path and enabled image-cache limits. Invalid startup-critical values fail before the service begins accepting requests.

When a reverse proxy terminates HTTPS, add each trusted proxy IP under `Hosting:ForwardedProxies`. Loopback proxies are trusted by default. The household deployment pins `192.168.68.22`; update it if the proxy moves. Forwarded headers are processed before HTTPS redirection and HSTS; browser responses also receive content-type, referrer-policy, and content-security-policy headers. This hosting hardening does not add application authentication or change access to Settings and integration actions.

```json
{
  "Hosting": {
    "ForwardedProxies": [ "192.168.68.22" ]
  }
}
```

Release installs write infrastructure values such as `AppDatabase__Path`, `CalendarCache__Directory`, and `ImageCache__Directory` to the Windows Service `Environment` registry value. Source API credentials are configured only in the app Settings page and are stored in the SQLite settings database. The release package builder clears API credentials from packaged `appsettings.json`.

Local application settings edited through the Settings page are stored in a SQLite parameter database. The default path is `App_Data/data/premiere-calendar.db`; release installs set `AppDatabase__Path` to `D:\Apps\PremiereCalendarData\data\premiere-calendar.db` by default.

Startup owns the database schema centrally. It runs `PRAGMA quick_check`, checkpoints WAL before a pending migration, creates a verified pre-migration backup, applies migrations transactionally, records their checksums in `SchemaMigrations`, advances `PRAGMA user_version`, and checks integrity again. `/health/ready`, `/health/version`, and Settings > Local status report the active and target schema versions. Migration failure restores only the pre-migration snapshot and leaves readiness unhealthy; unrelated corruption is never restored automatically.

Offline verification and recovery commands are:

```powershell
PremiereCalendar.exe database verify
PremiereCalendar.exe database restore --backup "D:\absolute\path\verified-backup.db"
```

Stop the `PremiereCalendar` Windows Service before restore. Restore rejects relative paths, verifies integrity and supported schema first, requires exclusive access, preserves the previous database and WAL sidecars with a `.damaged-<timestamp>` suffix, and stages the replacement on the same volume before activation.

Durable provider work is stored in SQLite and controlled by `ProviderScheduler`. Foreground jobs retain a reserved worker and can checkpoint/requeue adjacent-week, warmup, delta-sync, and IMDb-maintenance work without charging a retry. Provider concurrency remains below each configured hard ceiling and adapts to throttling, transient failures, latency, `Retry-After`, and persisted circuit state.

Structured operational logs are rolling NDJSON files under `Telemetry:LogDirectory`, limited to 50 MB each with 14 retained files by default. Trace and metric OTLP export remains disabled until `Telemetry:OtlpEndpoint` is an absolute endpoint. Telemetry uses provider/operation names and bounded outcome tags; it does not record query strings, filter contents, media titles or IDs, credentials, or backup payloads.

## TMDb

Required for live data. Enter the TMDb API read access token on the Settings page. If the token is missing, the calendar redirects to Settings with a setup notice.

Default non-secret settings:

- `Tmdb:BaseUrl` - TMDb API base URL.
- `Tmdb:ImageBaseUrl` - TMDb image base URL.
- `Tmdb:PosterSize` - poster image size path segment.
- `Tmdb:BackdropSize` - backdrop image size path segment.
- `Tmdb:SourceRegions` - optional source-display region order. It is unset by default, so source chips are merged from every TMDb region returned in detail responses. Set it only if you explicitly want source-chip ordering and release/certification preference for specific regions. Use the UI watch-country and origin-country filters for actual country filtering.
- `Tmdb:MaxPagesPerQuery` - hard technical cap for saved-filter discover queries. Default is 500.
- `Tmdb:MaxUnfilteredPagesPerQuery` - hard technical cap for broad date-only discover queries with no server-side filters. Default is 500; lowering it makes broad results incomplete.
- `Tmdb:SourceFetchConcurrency` - how many discovery source batches can run at once during a fresh week load.
- `Tmdb:PageBatchSize` - how many TMDb Discover pages are fetched before yielding another partial progress update. Default 10 pages, about 200 TMDb rows.
- `Tmdb:PageFetchConcurrency` - how many TMDb discover pages after page 1 can be fetched in parallel.
- `Tmdb:MaxEnrichmentConcurrency` - how many normalized items can run detail/rating/artwork enrichment in parallel.
- `Tmdb:EnrichmentProgressBatchSize` - how many enriched cards are emitted per partial progress update during fresh refreshes. Default 10, so broad days start rendering quickly even when later page batches contain hundreds of raw TMDb rows.
- `Tmdb:ExternalCandidateBatchSize` - how many external-provider candidates are accumulated before TMDb ID resolution/enrichment. Default 100.
- `Tmdb:RequestTimeoutSeconds` - HTTP timeout for TMDb requests.
- `Tmdb:SourceTimeoutSeconds` - timeout for one source batch before it is skipped and progress continues.
- `Tmdb:MaxRequestsPerSecond` - local TMDb request-rate limit. Keep it below TMDb's documented upper-limit guidance and respect `429` responses.

The same TMDb token is also used by `TmdbFilterCatalogService` to load official filter values for genres, languages, countries, watch providers, and certifications. These catalog responses are cached in memory and have local fallback values so the filter pane still opens if a catalog request fails.

## OMDb

OMDb is disabled by default. Enable it from the Settings page only when an API key is configured.

When disabled, Rotten Tomatoes and Metacritic show as `n/a`, OMDb plot/poster fallback is skipped, and the app still uses TMDb and local IMDb dataset scores normally.

OMDb responses are cached in SQLite. If OMDb reports a limit or quota problem, the app records a cooldown and serves stale cached data when it has it.

## IMDb Ratings Dataset

IMDb scores and vote counts come from the official non-commercial `title.ratings.tsv.gz` dataset. No API key is needed.

```json
"ImdbDataset": {
  "Enabled": true,
  "RatingsUrl": "https://datasets.imdbws.com/title.ratings.tsv.gz",
  "RefreshIntervalHours": 24
}
```

The app downloads it on startup when due, decompresses and imports it into SQLite as a line-by-line async stream, and reuses it for exact IMDb ID matches. The importer does not retain the full dataset in managed memory. Calendar enrichment resolves the needed IDs in bounded SQLite batches. IMDb does not provide a per-item change endpoint for this dataset.

## Health Check

The app exposes a basic health endpoint:

```text
GET /health
```

## Local Settings Database

The Settings page persists local app parameters in a SQLite database:

```json
"AppDatabase": {
  "Path": "App_Data/data/premiere-calendar.db"
}
```

Stored parameters currently include Sonarr and Radarr enable flags, URLs, API keys, root folder paths, quality profile IDs, tag-on-add values, add behavior, and source API settings for TMDb, TVmaze, Trakt, Watchmode, SIMKL, OMDb, Fanart.tv, TheTVDB, and Wikimedia. The same parameter table stores local app state such as saved filter presets, visit-change snapshots, and the background job timeline. In release installs this path is overridden to `C:\ProgramData\PremiereCalendar\data\premiere-calendar.db` so updates can replace binaries without touching local settings.

The Settings page can export and import a JSON backup of integration settings and local calendar state. Export includes saved presets and visit state. API secrets are redacted by default; enable `Include API secrets` only for a private backup you intend to restore as-is. Import replaces the saved settings and matching local calendar-state keys from the provided JSON without overwriting volatile diagnostics such as the background job timeline.

The same SQLite file also stores IMDb ratings, OMDb response cache, and provider-sync markers such as TMDb/TVmaze change checks.

## Application Updates

Settings includes a `Signed GitHub release update` action for installed releases. It uses the non-secret `ApplicationUpdate` infrastructure settings from `appsettings.json`, not the SQLite settings database:

```json
"ApplicationUpdate": {
  "Enabled": true,
  "UpdaterScriptPath": "D:\\Apps\\PremiereCalendar\\updater\\install-github-release.ps1",
  "LogDirectory": "D:\\Apps\\PremiereCalendarData\\logs\\application-updates",
  "PowerShellPath": "powershell.exe",
  "InstallRoot": "D:\\Apps\\PremiereCalendar",
  "DataRoot": "D:\\Apps\\PremiereCalendarData",
  "Repository": "Belgian-Coder/PremiereCalendar"
}
```

The app starts the fixed updater script as a detached Windows PowerShell process. The UI cannot provide arbitrary commands or asset URLs. The updater accepts only the configured GitHub `owner/name`, downloads the stable release manifest first, selects its exact package, enforces size and archive-path limits, verifies the pinned certificate/signature/hash, and writes a transcript under `ApplicationUpdate:LogDirectory`. Activation and rollback behavior is documented in [Signed GitHub Releases and Updates](ReleaseUpdates.md).

## Sonarr And Radarr

The app can add series to Sonarr and movies to Radarr from each calendar card. Configure both from the Settings page opened through the cog icon in the top bar.

When an enabled integration has a URL and API key, the Settings page automatically fetches root folders and quality profiles from the target app on open. Use `Load Sonarr options` or `Load Radarr options` to refresh those values manually. After options are loaded, quality profiles are selected by name while the stored setting remains the profile ID required by the Sonarr/Radarr API. If the target app cannot be reached, the settings page keeps a raw quality-profile ID field as a fallback so the configuration is still editable.

Sonarr parameters:

- enabled
- URL
- API key
- root folder path
- quality profile ID
- tag on add, default `import`; leave empty to add without a tag
- series type
- monitor mode
- season-folder setting
- search-after-add setting

Radarr parameters:

- enabled
- URL
- API key
- root folder path
- quality profile ID
- tag on add, default `import`; leave empty to add without a tag
- minimum availability
- monitored setting
- search-after-add setting

Radarr adds use the movie TMDb ID directly through Radarr lookup. Sonarr adds use the TVDB ID because Sonarr is TVDB-based; the app uses the TVDB ID already enriched from TMDb external IDs. If a series card does not have a TVDB ID, the Sonarr add action returns a clear failure notification instead of guessing.

When a tag-on-add value is configured, the app looks for a matching Sonarr/Radarr tag case-insensitively, creates it if missing, and includes the tag ID in the add payload.

When an integration is disabled, the corresponding card button is not rendered. Successful and failed add attempts show a top-right notification that disappears after three seconds.

## Hosting URL

The default host URL is configured as:

```json
"Urls": "http://0.0.0.0:5298"
```

This lets the app listen on all network interfaces. Other LAN devices still require the Windows firewall to allow inbound TCP `5298`.

## TVmaze

TVmaze enrichment and schedule discovery can be controlled from the Settings page. They are enabled by default:

```json
"Tvmaze": {
  "BaseUrl": "https://api.tvmaze.com/",
  "Enabled": true,
  "EnableScheduleDiscovery": true
}
```

TVmaze schedule entries can add exact episode rows in the app's every-episode mode when they can map back to TMDb through IMDb or TheTVDB IDs. In new-series-only mode, ordinary later episodes are skipped before TMDb mapping; a `S01E01` candidate may still support a series-premiere card. `Tvmaze:ScheduleCountries` is optional; leave it unset for only global web-schedule discovery and no hidden broadcast-country preset, or set explicit country codes if you want extra broadcast schedule calls.

`Tvmaze:ScheduleFetchConcurrency` controls how many schedule endpoints are queried at once when schedule discovery is enabled. The default is 4, below TVmaze's documented minimum allowance of 20 calls per 10 seconds. Schedule requests retry `429` responses with `Retry-After` or a short fallback delay.

Disable it from the Settings page if you want TMDb-only plus optional OMDb.

## Fanart.tv

Fanart.tv artwork is disabled until a free API key is configured on the Settings page.

Fanart.tv is used only as artwork fallback when TMDb has no poster. The app prefers poster-like assets, English artwork, Dutch artwork, neutral-language artwork, and then higher-like counts.

## Trakt

Trakt discovery is active by default once a free Trakt app client ID is configured on the Settings page.

Trakt calendar rows are candidate rows only. The app accepts them only when a TMDb movie/TV ID is already present or can be resolved through TMDb external-ID lookup. Set `Trakt:Enabled` to `false` to keep Trakt disabled even when a client ID is present.

## Watchmode

Watchmode is optional and API-key gated through Settings. It is used only as a streaming availability fallback when TMDb has no provider data for a verified card. Release discovery is disabled by default and is not registered in the calendar discovery pipeline because the free API is too request-limited for broad week refreshes.

The configured Watchmode API key is sent with the `X-API-Key` request header. The app does not put the key in Watchmode query strings.

Useful non-secret defaults:

- `Watchmode:Enabled` - enables the availability fallback client.
- `Watchmode:Regions` - fallback regions used when a card has no TMDb watch providers.
- `Watchmode:EnableAvailabilityEnrichment` - controls availability fallback.
- `Watchmode:MaxConcurrentRequests` - provider-level concurrency cap.

## TheTVDB

TheTVDB artwork is disabled until an API key is configured on the Settings page.

TheTVDB is used only as an optional series artwork fallback. Check TheTVDB API terms for any required attribution when enabling it.

## Wikimedia/Commons

Wikimedia fallback is enabled by default, requires no key, and can be disabled on the Settings page:

```json
"Wikimedia": {
  "WikidataBaseUrl": "https://www.wikidata.org/",
  "CommonsApiUrl": "https://commons.wikimedia.org/w/api.php",
  "Enabled": true
}
```

The app only queries Wikimedia when TMDb has a Wikidata ID and no TMDb poster was found. It uses Wikidata `P18` images only when Commons returns reusable license metadata.

## Calendar Cache

Week-level normalized results are stored in a local file cache by default:

```json
"CalendarCache": {
  "Enabled": true,
  "Directory": "App_Data/cache/calendar",
  "WeekCacheHours": 6,
  "AdjacentWeekPrefetchEnabled": true,
  "FuturePrefetchWeeks": 4,
  "PastPrefetchWeeks": 2
}
```

`WeekCacheHours` controls how long a normalized full-week file is considered fresh. Week cache keys include saved criteria that change source requests, while local title/source text and UI sort are applied in memory after loading so they do not duplicate cache files. When adjacent prefetch is enabled, loading one week queues non-blocking background fetches using the current saved source-request filters. The default priority order warms next week, previous week, weeks +2, +3, +4, then week -2. Fresh matching week files return from cache immediately, so interrupted background prefetch naturally resumes missing or stale weeks the next time a visible week loads.

The UI Refresh button bypasses the fresh week cache and the in-memory source API caches for the visible week, then fetches fresh data. If a source refresh fails, the app can still read the last cached week as a fallback. The cache directory is ignored by git.

Keep the calendar and image cache payloads on disk rather than moving them into SQLite. The database is used for small mutable settings; week JSON files and poster bytes are larger, file-oriented payloads that benefit from streamed disk reads, simple cleanup, browser cache validators, and preservation across app updates without growing the settings database.

## Provider Change Checks

Some providers expose cheap "what changed" endpoints. The app records those markers in SQLite:

- TMDb movie/TV change lists.
- TVmaze show update timestamps.
- SIMKL activity timestamps for account sync.

```json
"ProviderDeltaSync": {
  "Enabled": true,
  "WakeIntervalMinutes": 60,
  "TmdbLookbackDays": 14,
  "UseTmdbChanges": true,
  "UseTvmazeUpdates": true
}
```

OMDb and the IMDb dataset do not expose per-item change endpoints. OMDb is cached by IMDb ID; the IMDb dataset is refreshed as a whole on its interval.

## Adaptive Warmup And Load Budget

The background warmer runs on startup and then wakes on a schedule to fill stale or missing full-week media caches. A wake does not imply forced remote work; the default routine path uses `forceRefresh: false` and reuses fresh disk or memory cache entries.

```json
"CalendarWarmup": {
  "Enabled": true,
  "RunOnStartup": true,
  "WakeIntervalMinutes": 15,
  "MinimumRemoteRefreshMinutes": 60,
  "MaximumProfilesPerWake": 5,
  "TopFilterProfileCount": 4,
  "CycleBudgetSeconds": 600,
  "WindowBudgetSeconds": 90,
  "StaleOnlyRemoteRefresh": true,
  "CleanupRetentionDays": 60
}
```

`WindowBudgetSeconds` defaults to 90 seconds. Large series weeks can take longer than a foreground
cache hit to assemble; keep this comfortably above the observed provider-fetch duration so a warmup
does not repeatedly time out and retry the same missing week on every wake.

Foreground UI loads have their own budget:

```json
"CalendarLoad": {
  "ForegroundLoadBudgetSeconds": 30
}
```

If the budget expires, the UI renders the best cached or partial result and marks the source progress as stopped. Partial foreground diagnostics are not treated as a fresh complete week cache.

## Image Cache

Poster and backdrop image bytes are cached through a local endpoint by default:

```json
"ImageCache": {
  "Enabled": true,
  "Directory": "App_Data/cache/images",
  "CacheDays": 30,
  "MaxBytes": 5242880,
    "AllowedHosts": [ "image.tmdb.org", ".media-amazon.com", "static.tvmaze.com", "assets.fanart.tv", "webservice.fanart.tv", "artworks.thetvdb.com", "upload.wikimedia.org", "commons.wikimedia.org", "cdn.watchmode.com" ]
}
```

Cards render image sources as `/cached-image?url=...`, so browser requests hit the local app first. Poster cards include `w=185`; the endpoint folds that width and the negotiated browser format into the cache key. WebP-capable browsers receive a resized WebP representation, while JPEG/original bytes remain the fallback. The endpoint allow-lists image hosts, stores the bytes on disk, and serves subsequent requests locally as streamed files with browser cache headers and ETags. Cards lazy-load the real cached-image URL with an intersection observer, so offscreen posters do not request bytes during the first render. If image caching is disabled, the endpoint still validates the source URL before redirecting to the remote image.

The UI Refresh button still fetches fresh calendar data. During that refreshed render, image URLs include refresh parameters so the image cache can update the poster files for visible cards.
