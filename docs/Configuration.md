# Configuration

Configuration is handled with two layers:

- Runtime app settings edited on the Settings page are stored in the PostgreSQL application database.
- Infrastructure values still use standard ASP.NET Core providers. Non-secret defaults live in `PremiereCalendar/appsettings.json`; API credentials must not be committed.

Startup validates the PostgreSQL connection string (and rejects the removed SQLite provider and path settings, see [Database](#application-database)), plus enabled image-cache limits. Invalid startup-critical values fail before the service begins accepting requests.

When a reverse proxy terminates HTTPS, add each trusted proxy IP under `Hosting:ForwardedProxies`. Loopback proxies are trusted by default. The household deployment pins `192.168.68.22`; update it if the proxy moves. Forwarded headers are processed before HTTPS redirection and HSTS; browser responses also receive content-type, referrer-policy, and content-security-policy headers. This hosting hardening does not add application authentication or change access to Settings and integration actions.

```json
{
  "Hosting": {
    "ForwardedProxies": [ "192.168.68.22" ]
  }
}
```

Infrastructure values such as `AppDatabase__ConnectionString`, `CalendarCache__Directory`, and `ImageCache__Directory` are set as container environment variables (see `compose.yaml`). Source API credentials are configured only in the app Settings page and are stored in the application database.

Local application settings edited through the Settings page are stored in the PostgreSQL parameter tables. Development launches store caches and logs in the sibling `PremiereCalendar.LocalData` directory so the source checkout stays clean, and use the Compose development database (`127.0.0.1:55432`, password in `.secrets/dev-postgres-password`).

Startup owns the database schema centrally. It applies pending migrations transactionally, records their checksums in `SchemaMigrations`, validates the migration history and the expected table shape, and retries transient connection failures. `/health/ready`, `/health/version`, and Settings > Local status report the active and target schema versions. A schema newer than the application, a changed migration checksum, or a missing required column stops initialization with an operator error instead of being retried. Back up PostgreSQL with `pg_dump` before upgrading; the application does not create database backups itself.

Offline verification command (run it with the application's configuration, for example `docker compose run --rm app dotnet PremiereCalendar.dll database verify`):

```text
dotnet PremiereCalendar.dll database verify
```

Durable provider work is stored in PostgreSQL and controlled by `ProviderScheduler`. Foreground jobs retain a reserved worker and can checkpoint/requeue adjacent-week, warmup, delta-sync, and IMDb-maintenance work without charging a retry. Provider concurrency remains below each configured hard ceiling and adapts to throttling, transient failures, latency, `Retry-After`, and persisted circuit state.

Structured operational logs are rolling NDJSON files under `Telemetry:LogDirectory`, limited to 50 MB each with 14 retained files by default. Trace and metric OTLP export remains disabled until `Telemetry:OtlpEndpoint` is an absolute endpoint. Metrics use provider/operation names and bounded outcome tags; correlation and per-item identifiers are confined to diagnostic events. Telemetry omits query strings, filter contents, media titles, credentials, and backup payloads.

Serilog internal failures are counted process-wide in `OperationalLoggingDiagnostics.Snapshot`, exposed as `operationalLogging` by `/health/version`: failure count, last UTC failure time, and a fixed `permission`, `mutex`, `io`, or `other` reason code. The first failure also emits one fixed stderr notice. Raw internal messages, paths, exception text, and event payloads are discarded. These coarse codes identify the area to investigate; they do not establish the underlying cause. Counts reset when the process restarts.

Detailed performance events are also saved by default in `Telemetry:LogDirectory/performance/performance-*.ndjson`, independently of OTLP, `Telemetry:Enabled`, and trace sampling. They include UTC timestamps, process elapsed time, event order, opaque load/job/operation/request IDs, fixed source and stage codes, counts, cache outcomes, and measured durations. A load can be followed into a queued job through its explicit linking event; provider attempts retain their parent job/source/operation context when assigning a request ID. An enrichment operation may record the public numeric TMDb item ID at its start, without its title. Cache keys use fixed partition names (`premieres-v1` or historical `default`) or criteria hashes, so filter contents are absent. Media titles, exception messages, HTTP addresses, headers, and response bodies have no diagnostic fields.

Use `eng/Read-CalendarPerformance.ps1 -CacheAudit` for correlated page timings, provider status/retry outcomes, item/component reuse and accepted writes, and reconciliation failures. The audit exposes missing/truncated/lost evidence rather than treating an empty log as success. See [cache freshness verification](CacheFreshnessDiagnostics.md) for commands and interpretation.

| Setting under `Telemetry` | Default | Behavior |
| --- | --- | --- |
| `PerformanceDiagnosticsEnabled` | `true` | Save detailed performance events locally. |
| `PerformanceDiagnosticsDirectory` | empty | Use the `performance` subdirectory of `LogDirectory`; an explicit path overrides it. |
| `PerformanceFileSizeLimitMegabytes` | `25` | Roll before the next event would exceed this file size, from 1 to 1024 MB. |
| `PerformanceRetainedFileCount` | `14` | Keep at most this many diagnostic files, from 1 to 100. The default disk bound is 350 MB. |
| `PerformanceFlushIntervalSeconds` | `2` | Flush pending file writes to disk, from 1 to 60 seconds; orderly shutdown also flushes. |
| `PerformanceQueueCapacity` | `4096` | Maximum queued immutable snapshots, from 256 to 32768; the separate memory budget also applies. Serialized events remain limited to 8 KiB. |
| `PerformanceQueueMemoryMegabytes` | `16` | Conservative queued snapshot memory bound; rejected snapshots/events are counted without sampling. |
| `PerformanceArchiveClosedFiles` | `true` | Compress eligible closed traces and publish a UTC/process index. Active writers are protected by leases. |
| `PerformanceArchiveAfterMinutes` | `10` | Minimum age before a closed trace is eligible for archival. |
| `PerformanceFlushTimeoutSeconds` | `5` | Maximum wait for an explicit durable flush barrier, from 1 to 30 seconds. Normal event capture never waits for this barrier. |
| `PerformanceShutdownTimeoutSeconds` | `5` | Maximum wait for host stop/disposal to drain the writer, from 1 to 15 seconds. |

Each application start opens a new file when its first event arrives. Records and filenames share an `instanceId`; filenames also include a UTC creation timestamp and rotation sequence. Size rotation removes oversized diagnostic files from an older configuration and then the oldest diagnostic files first; it never removes operational logs or application data. Keep the diagnostic directory dedicated to one running application instance; during rolling restart, give overlapping instances separate directories.

Capture snapshots the fixed schema and attempts a bounded queue write immediately. One background writer owns event serialization and active trace writes; slow or hung storage cannot block provider/UI capture or diagnostic health reads. A full queue rejects the new event and increments `DroppedEvents`; it does not silently sample or evict older records. Health reports queue depth/capacity, fullness, writer status, I/O failures, and flush/shutdown timeouts. `ActiveIoMs` measures the current file operation with a monotonic clock, capped at one day; `WriterStalled` makes health unhealthy once that operation exceeds the configured flush timeout, even if the queue is empty. The `premierecalendar.diagnostics.*` gauges also expose written/dropped/queued events, queue fullness, writer failures, and health. After recovery, a periodic or explicit flush saves a `diagnostics.loss` record with cumulative and newly observed loss/failure counts; these markers count as written events. Counts otherwise reset on process restart.

Closed-file compression, parsing and archive fsync run on a separate cancellable worker with one pending trigger, so they do not delay the event writer's FIFO barriers. Archive failures are reported separately from writer failures and dropped events; lease contention is a skip. Retention keeps the configured trace count and byte bounds, with a bounded staging allowance. Interrupted archive companions are cleaned without deleting a valid authoritative raw file. The analyzer reads raw/gzip traces, uses valid indexes to skip unrelated time windows, and reports missing or incomplete evidence. See [trace and image behavior](TraceImageImprovements.md).

An explicit flush waits for a FIFO durability barrier up to its configured timeout. Host stop normally drains accepted records, flushes, and closes files before returning. If storage remains hung beyond the shutdown limit, stop returns with `ShutdownTimedOut`; the background writer closes its handle and discards remaining queued records when the OS operation returns. A crash can lose queued events and unflushed file data. When investigating a slow week, copy retained files promptly and group by `loadId`, then the linked `jobId`, parent `operationId`, and per-attempt `requestId`. No OTLP collector is required to preserve this evidence.

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
- `Tmdb:SourceTimeoutSeconds` - inactivity timeout for a streaming source: renewed only when completed work or distinct rows advance. A stalled batch is skipped after this interval; finite sources that keep progressing may take longer overall. Caller cancellation and per-provider HTTP timeouts remain separate. Non-streaming sources retain a single batch deadline.
- `Tmdb:MaxRequestsPerSecond` - local TMDb request-rate limit. Keep it below TMDb's documented upper-limit guidance and respect `429` responses.

The same TMDb token is also used by `TmdbFilterCatalogService` to load official filter values for genres, languages, countries, watch providers, and certifications. These catalog responses are cached in memory and have local fallback values so the filter pane still opens if a catalog request fails.

Successful movie/TV details and season-one responses persist in the application database for their existing 12-hour freshness lifetime; Rotten Tomatoes scores use their configured lifetime. External-ID and title resolution also persist: positive results remain fresh for 90 days and negative results for six hours. Restart reads keep the original expiry, and forced refresh bypasses both cache tiers. Response envelopes are limited to 256 KiB; larger responses remain usable without persistence. Cache I/O is best effort with a 500 ms cooperative budget and five-second backoff per provider. PostgreSQL serves at most four response-cache operations concurrently; remote request limits are unchanged. Scheduled maintenance retains these response-cache rows for three calendar months. Retention does not extend their freshness. See [item caching](ItemEnrichmentCache.md) and [earlier movie-week measurements](MovieWeekSpeedValidation.md).

## OMDb

OMDb is disabled by default. Enable it from the Settings page only when an API key is configured.

When disabled, Rotten Tomatoes and Metacritic show as `n/a`, OMDb plot/poster fallback is skipped, and the app still uses TMDb and local IMDb dataset scores normally.

OMDb responses are cached in PostgreSQL. If OMDb reports a limit or quota problem, the app records a cooldown and serves stale cached data when it has it.

## IMDb Ratings Dataset

IMDb scores and vote counts come from the official non-commercial `title.ratings.tsv.gz` dataset. No API key is needed.

```json
"ImdbDataset": {
  "Enabled": true,
  "RatingsUrl": "https://datasets.imdbws.com/title.ratings.tsv.gz",
  "RefreshIntervalHours": 24,
  "DownloadDirectory": "App_Data/tmp/imdb",
  "MaxDownloadBytes": 536870912
}
```

The app downloads it on startup when due, then decompresses and imports it into the database as a line-by-line async stream. The compressed download is staged on disk, limited to 512 MiB by default, and deleted when the import finishes or fails. On later imports, a best-effort sweep removes owned scratch files older than 24 hours, including Linux hard-termination leftovers. Fresh and unrelated files are preserved. The HTTP timeout ends after downloading; database import time uses only the caller's cancellation token. Relative download paths resolve beneath the application content root. Keep this directory on a writable volume with enough free space; container `/tmp` can be too small for the full dataset.

The importer does not retain the full dataset in managed memory. Calendar enrichment resolves exact IMDb IDs in bounded database batches. IMDb does not provide a per-item change endpoint for this dataset.

## Health Check

The app exposes a basic health endpoint:

```text
GET /health
```

## Application Database

PostgreSQL is the only supported database. Configure a password-free Npgsql connection string and, optionally, a password file (normally a Docker secret):

```json
"AppDatabase": {
  "ConnectionString": "Host=database;Port=5432;Database=premierecalendar;Username=premierecalendar",
  "PasswordFile": "/run/secrets/postgres_password"
}
```

`PasswordFile` overrides any password in the connection string and is read whenever a new physical connection is opened, so a rotated secret is picked up without a restart. A missing or unreadable password file stops startup instead of retrying. Never put the database password in committed configuration. See [Docker and PostgreSQL](DockerAndPostgres.md).

SQLite was removed. `AppDatabase:Provider` is no longer needed (`PostgreSql` is still accepted so existing container configuration keeps working), but any other provider value, such as `Sqlite`, and any `AppDatabase:Path` value fail startup with an explicit error, so a container that still carries SQLite configuration cannot silently start against an empty database. `AppDatabase:MigrationBackupDirectory` and `MigrationBackupRetentionCount` are ignored.

Stored parameters currently include Sonarr and Radarr enable flags, URLs, API keys, root folder paths, quality profile IDs, tag-on-add values, add behavior, and source API settings for TMDb, TVmaze, Trakt, Watchmode, SIMKL, OMDb, Fanart.tv, TheTVDB, and Wikimedia. The same parameter table stores local app state such as saved filter presets, visit-change snapshots, and the background job timeline.

The Settings page can export and import a JSON backup of integration settings and local calendar state. Export includes saved presets and visit state. API secrets are redacted by default; enable `Include API secrets` only for a private backup you intend to restore as-is. Import replaces the saved settings and matching local calendar-state keys from the provided JSON without overwriting volatile diagnostics such as the background job timeline.

The same database also stores IMDb ratings, OMDb response cache, and provider-sync markers such as TMDb/TVmaze change checks.

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

This lets the app listen on all network interfaces. Containers set `Urls` to `http://0.0.0.0:8080` and publish it on the loopback port described in [Docker and PostgreSQL](DockerAndPostgres.md).

## TVmaze

TVmaze enrichment and schedule discovery can be controlled from the Settings page. They are enabled by default:

```json
"Tvmaze": {
  "BaseUrl": "https://api.tvmaze.com/",
  "Enabled": true,
  "EnableScheduleDiscovery": true
}
```

TVmaze schedules support the calendar's new-series discovery: ordinary later episodes are skipped before TMDb mapping, while a `S01E01` candidate may support a series-premiere card. Known earlier series debuts are excluded, including identities obtained from external IDs or cached enrichment. A canonical date that a provider does not supply cannot be independently proved by this check. `Tvmaze:ScheduleCountries` is optional; leave it unset for only global web-schedule discovery and no hidden broadcast-country preset, or set explicit country codes if you want extra broadcast schedule calls.

`Tvmaze:ScheduleFetchConcurrency` controls how many schedule endpoints are queried at once when schedule discovery is enabled. The default is 4, below TVmaze's documented minimum allowance of 20 calls per 10 seconds. Schedule requests retry `429` responses with `Retry-After` or a short fallback delay.

Disable it from the Settings page if you want TMDb-only plus optional OMDb.

## Fanart.tv

Fanart.tv artwork is disabled until a free API key is configured on the Settings page.

Fanart.tv is used only as artwork fallback when TMDb has no poster. The app prefers poster-like assets, English artwork, Dutch artwork, neutral-language artwork, and then higher-like counts.

## Trakt

Trakt discovery is disabled by default, even after a client ID is configured. Enable it on the Settings page to opt in. A saved `Sources.Trakt.Enabled` value in the local integration settings takes precedence over `Trakt:Enabled`; the configuration value (including the `Trakt__Enabled` environment variable) is used when no saved value exists.

Trakt calendar rows are candidate rows only. The app accepts them only when a TMDb movie/TV ID is already present or can be resolved through TMDb external-ID lookup.

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
  "WeekCacheHours": 30,
  "AdjacentWeekPrefetchEnabled": true,
  "FuturePrefetchWeeks": 4,
  "PastPrefetchWeeks": 2
}
```

`WeekCacheHours` controls how long a normalized full-week file is considered fresh. Week cache keys include saved criteria that change source requests, while local title/source text and UI sort are applied in memory after loading so they do not duplicate cache files. When adjacent prefetch is enabled, loading one week queues non-blocking background fetches using the current saved source-request filters. The default priority order warms next week, previous week, weeks +2, +3, +4, then week -2. Fresh matching week files return from cache immediately, so interrupted background prefetch naturally resumes missing or stale weeks the next time a visible week loads.

The UI Refresh button bypasses the fresh week cache and the in-memory source API caches for the visible week, then fetches fresh data. If a source refresh fails, the app can still read the last cached week as a fallback. The cache directory is ignored by git.

Full-week JSON and image bytes remain on disk. Reusable per-title metadata, scores and provider response envelopes live in the application database, so different weeks and filters can share them. The supported deployment uses PostgreSQL; SQLite remains available for local/test use.

```json
"ItemEnrichmentCache": {
  "Enabled": true,
  "RetentionMonths": 3,
  "MetadataRefreshDays": 7,
  "RatingsRefreshHours": 24,
  "MetadataFailureRetryMinutes": 5,
  "RatingsFailureRetryMinutes": 5
}
```

Retention uses calendar-month arithmetic since the item's last use. It does not make a three-month-old score fresh. Changed cached IDs are invalidated sooner by provider change checks; periodic discovery still reconciles dates and week membership. See [item cache operations](ItemEnrichmentCache.md).

## Provider Change Checks

Some providers expose cheap "what changed" endpoints. The app records their successful coverage in the database and uses TMDb/TVmaze changes to invalidate matching reusable item metadata:

- TMDb movie/TV change lists.
- TVmaze show update timestamps.
- SIMKL activity timestamps for account sync.

```json
"ProviderDeltaSync": {
  "Enabled": true,
  "WakeIntervalMinutes": 60,
  "TmdbLookbackDays": 14,
  "InitialLookbackDays": 2,
  "ChangeHistoryConcurrency": 4,
  "UseTmdbChanges": true,
  "UseTvmazeUpdates": true
}
```

OMDb and the IMDb dataset do not expose per-item change endpoints. OMDb is cached by IMDb ID; the IMDb dataset is refreshed as a whole on its interval.

TMDb checks start at the previous successful poll, bounded to 14 days of recovery. Only already-cached IDs returned by the change lists need individual change-history checks. Actual relevant timestamps make overlapping windows idempotent; cast/crew-only changes do not refresh card metadata. Failed or incomplete checks do not advance coverage. TVmaze uses day/week/month recovery windows and fresh checked feeds. Gaps beyond provider recovery remain covered by the seven-day metadata fallback rather than claiming complete change history.

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
  "NightlyEnabled": true,
  "NightlyTimeZone": "Europe/Brussels",
  "NightlyLocalTime": "02:00:00",
  "CleanupRetentionDays": 60
}
```

`WindowBudgetSeconds` defaults to 90 seconds. Large series weeks can take longer than a foreground
cache hit to assemble; keep this comfortably above the observed provider-fetch duration so a warmup
does not repeatedly time out and retry the same missing week on every wake.

Nightly warmup builds every full week intersecting the current and next calendar month, using the factory Series and Movies profiles: English/Dutch, first-debut series, series runtime 0–360 and movies 45–360 minutes. It prioritizes the current week and interleaves both profiles. Explicit user selections remain unchanged; different filters have separate caches. Set the three `Nightly*` values through normal configuration providers, for example `CalendarWarmup__NightlyLocalTime=03:00:00`. `Enabled=false` disables both warmers; `NightlyEnabled=false` disables only nightly planning. An explicit `CalendarCache:WeekCacheHours` override takes precedence over the 30-hour default.

At 02:00 Brussels time, the app persists a daily plan and queues independent durable week jobs without the periodic warmer's four-window cap. Startup catches up the most recent scheduled night. On daylight-saving transitions, a missing local time moves to the first valid minute; a repeated time runs at its earlier occurrence. Job handles and source checkpoints survive restart, foreground work keeps priority, and retries remain bounded by the provider scheduler. Terminal failures remain uncovered until the next night's plan.

Nightly work rebuilds the normalized calendar snapshot while respecting provider response lifetimes and cached card enrichment. It does not force every external API call. A healthy discovery replaces removed rows; a failed source retains provisional fallback rows. A week counts as covered only when its cache is complete, was published at or after that night's cutoff, and has a persisted successful nightly receipt, including legitimately empty weeks. Old-night leaves are superseded before execution/publication when a newer schedule occurrence is due. The receipt proves that night's rebuild; subsequent ordinary refreshes can still replace the cache. An explicit shorter cache TTL can expire the result before the following night. Operational logs record plan coverage and each window's profile, range, job ID, outcome, row count and duration; detailed provider/stage timings continue in the performance logs described above. A successful enqueue alone is not proof of complete coverage.

The daily plan uses application-state key `calendar-warmup:nightly-plan:v1`. Receipts use `calendar-warmup:nightly-receipt:v1:<cutoff ticks>:<profile hash>:<week start>:<week end>` and remain as small historical proof records. A typical two-month plan adds 20 rows per day, approximately 7,300 per year. No new retention/deletion policy is introduced.

Plan coverage is a snapshot from the last scheduler wake (15 minutes by default); individual job completion, window duration logs and receipts are persisted immediately. Recheck cache envelopes and receipts when investigating work completed since that snapshot.

Foreground UI loads have their own budget:

```json
"CalendarLoad": {
  "ForegroundLoadBudgetSeconds": 30
}
```

If the budget expires, the UI renders the best cached or partial result and marks the source progress as stopped. Partial foreground diagnostics are not treated as a fresh complete week cache.

## Image Cache

`ImageCache:MaxConcurrentDecodes` defaults to 2 and is shared across requests. Staged resize work is bounded by the decode limit plus `MaxConcurrentDownloads`. `MaxDecodedPixels` defaults to 40,000,000 across all frames; oversized animations are rejected before full decoding. `RequestTimeoutSeconds` defaults to 20 and bounds cache-miss fetch, response-body reads, decoding, hashing, and metadata publication. A timed-out miss returns a short-lived stale image when available, otherwise a transient response. Caller cancellation propagates.

Magick.NET handles raster conversion on Windows and Linux without a license key or fee. Its shipped policy restricts codecs to raster formats and disables delegates, filters, and indirect reads. Native pixel caches have a shared 512 MiB ceiling, with disk spill disabled, one native processing thread per operation, and a 1,000-frame sequence limit. The configured image-pixel limit still applies. Cancellation is checked around native operations and between frames; an individual synchronous native call may finish before cancellation is observed. Generated configuration lives in writable `App_Data/cache/ImageMagick`; the application atomically refreshes its policy from the published copy on initialization.

Image bodies use immutable content-hash generations selected by an atomic metadata file. Existing URL/width/format keys and legacy cache files remain readable; orphaned generations are removed by normal retention cleanup.

Animated PNG is served as its original `image/png` bytes, including for width-specific WebP requests. Structural checks validate checksums, frame sequences, canvas bounds and the configured pixel/frame limits; the native decoder also validates the default PNG image. This preserves animation without another decoder or external process. CSS still sizes the image, while server resizing is skipped for APNG. Native memory-capacity failures use stale fallback or a retryable 503 response with `no-store`.

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

## Provider admission and recovery

Series overlaps independent ratings, Rotten Tomatoes, TVmaze, artwork and availability work while retaining provider admission, concurrency and retry controls. Artwork waits for inputs it needs; cancellation observes every started branch. TMDb retains its configured 30 requests/second and concurrency caps. The limit is not a promise that another application sharing the public IP cannot cause throttling.

`Tvmaze:MinimumRequestIntervalMilliseconds` defaults to **510** and is shared across typed clients, calendar jobs and HTTP retries. TVmaze guarantees at least 20 calls per 10 seconds per IP; higher edge-cache throughput is not an entitlement. Operators can tune the interval only with measured response/admission evidence. `Retry-After`, adaptive cooldowns and provider-wide access/quota backoff remain effective. Spacing wait time is recorded separately. See [TMDb limits](https://developer.themoviedb.org/docs/rate-limiting) and [TVmaze limits](https://www.tvmaze.com/api#rate-limiting).

PostgreSQL transient startup recovery and scheduler/delta settings are listed in [scheduler recovery](SchedulerRecoveryImprovements.md). Readiness remains unhealthy until schema/connectivity validation succeeds. Unsupported/corrupt schema and authentication failures do not become endless transient retries. Background producers wait for initialization; runtime service failures remain visible.

Item-cache validation reads do not touch retention or take reservation locks. Accepted write projections are read in the same database transaction. Admission and connection establishment have independent five-second limits; SQL commands have five-second limits, while aggregate bulk budgets scale with chunk count up to 120 seconds and obey caller cancellation. Mapping reservations are prepared in 250-ID chunks. Admission/bulk saturation defers the current session; a genuine failed connection opens a short shared store backoff before more provider work is attempted. Failed flush proposals are retained without replacing newer concurrent components.

TVmaze lookup tokens protect unresolved metadata across processes and remain held through session publication. Tokens expire after 30 minutes; publication verifies the exact token, identity, revision and validity. Expired proposals cannot publish; only the unchanged original row is invalidated, preserving later winners and last-good data.
