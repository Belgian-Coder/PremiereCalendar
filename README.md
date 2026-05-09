# Premiere Calendar

Local-first .NET 11 preview Blazor Web App for weekly movie and series premiere discovery.

The app uses TMDb as the canonical identity source and can optionally enrich ratings, artwork, and discovery coverage through free external APIs when configured. Runtime source API keys are edited on the Settings page and stored server-side in the local SQLite parameter database, with user-secrets, environment variables, or another ASP.NET Core configuration provider still available as first-run fallback values.

TMDb Discover remains the primary row source. With no saved filters, the app runs broad TV episode-air-date and movie date-window discovery with no hidden language, country, network, provider, genre, runtime, or certification presets. The series filter can switch between every episode airing in the week and new-series-only discovery. Once filters are saved, supported filters are sent to TMDb Discover so the app fetches the relevant slice instead of always fetching the whole week. Multi-select original-language filters fan out to one TMDb request slice per selected language. Movie rows use TMDb `primary_release_date`, series episode rows use TMDb `air_date`, and new-series rows use TMDb `first_air_date`. Trakt and Watchmode can add movie or series candidates, while TVmaze schedule discovery is series-only; those candidates are only accepted after they map back to a TMDb movie or TV ID as appropriate. OMDb, Fanart.tv, expanded TVmaze images, optional TheTVDB, and Wikimedia/Commons enrich existing TMDb-backed rows. Cards show provider/channel source names from TVmaze and TMDb details/watch-provider data when available.

## Screenshots

Theme comparison:

| Dark mode | Light mode |
| --- | --- |
| ![Series calendar in dark mode](docs/images/readme/series-dark.png) | ![Series calendar in light mode](docs/images/readme/series-light.png) |

Additional views use dark mode:

| Movies | Filters |
| --- | --- |
| ![Movie calendar in dark mode](docs/images/readme/movies-dark.png) | ![Filter drawer in dark mode](docs/images/readme/filters-dark.png) |

## Projects

- `PremiereCalendar/` - Blazor Web App with interactive server rendering.
- `tests/PremiereCalendar.UnitTests/` - pure rule and mapping tests.
- `tests/PremiereCalendar.IntegrationTests/` - fake HTTP and app host integration tests.
- `tests/PremiereCalendar.ComponentTests/` - bUnit tests for Blazor components.
- `docs/` - how the app works, architecture, configuration, and testing notes.

## Performance Notes

- Discover, detail, OMDb, TVmaze, Fanart.tv, Trakt, Watchmode, TheTVDB, Wikimedia, and SIMKL responses are cached through server-side memory caching where applicable. Identical concurrent misses are coalesced with a keyed single-flight coordinator so broad views do not duplicate the same upstream call while cache entries are being filled.
- Normalized weekly calendar results are cached locally under `App_Data/cache/calendar` by week plus the selected server-side discovery criteria hash. View-only choices such as local title/source text and local sort do not create duplicate source cache files.
- The theme is not part of the server week cache because it does not affect API results; the browser stores it in `localStorage` under `premiere-calendar:theme` and mirrors it to a non-secret `premiere-calendar-theme` cookie so the server can render the correct first paint.
- While a fresh week is loading, source and enrichment batches are streamed to the UI through `IAsyncEnumerable<PremiereLoadProgress>` as soon as they finish so cards can appear before every source is done. External providers report as they complete, and large TMDb result batches are enriched and emitted in smaller chunks. The loaded-source chips can be clicked to inspect only that loaded batch.
- Fresh unfiltered TMDb discovery is split into day-sized source batches. On a broad week, the service starts external calendars plus a bounded number of TMDb day batches first, then queues the rest, so a completed day can render the first visible cards while later days continue loading.
- TMDb page loading fetches page 1 first to learn the total page count. Later pages are fetched in configurable page batches (`Tmdb:PageBatchSize`, default 10 pages, about 200 TMDb rows) with bounded page concurrency, but enrichment is emitted in smaller progress chunks (`Tmdb:EnrichmentProgressBatchSize`, default 10 cards) so broad refreshes do not wait for a full 200-row enrichment batch before updating the UI.
- External providers are streamed separately. Trakt, series-only TVmaze schedules, and Watchmode releases appear as separate loaded-source chips when they apply to the current media mode, and TVmaze/Watchmode candidates are batched before TMDb ID resolution so broad refreshes can use enrichment concurrency efficiently. A zero-card source chip means that provider did not add any accepted card for the current filter set; the detail line explains whether no candidates came back or candidates were filtered out.
- After the visible week loads, the background prefetch queue and adaptive cache warmer use the same media-specific week cache keys as the UI. Warmup starts from the current day, then tomorrow, yesterday, the rest of the current week, the rest of the month, adjacent months, and months +2 through +6, but it stores full Monday-to-Sunday weeks so All, Series, and Movies can reuse the same cache files. The warmer only starts a small number of missing or stale week windows per wake (`CalendarWarmup:MaximumRemoteWindowsPerWake`, default 4), so startup and 15-minute wake checks cannot turn into a cold-cache request storm.
- TMDb calls pass through a local token-bucket limiter (`Tmdb:MaxRequestsPerSecond`, default 20) plus a hard concurrent request cap (`Tmdb:MaxConcurrentRequests`, default 4), retry `429 Too Many Requests` with `Retry-After` support, and use request/source timeouts so one slow source cannot leave Refresh stuck indefinitely.
- Refreshes and saved-filter reloads keep the previous visible results on screen when they are still relevant, with a small updating indicator, so the calendar does not feel blank while the new source batches stream in.
- Foreground loads have a default 30-second budget. If a slow source exceeds that budget, the page stops the active foreground stream, renders the best cached or partial results it already has, and records a visible load-budget diagnostic instead of leaving the UI in a loading state.
- After a visible week loads, the app warms nearby full-week caches in the background using the current saved filters. By default it queues next week, previous week, weeks +2, +3, +4, and then week -2, skipping work that is already in flight or already cached. When you move to Previous or Next, the prefetch window slides too: newly relevant pending weeks are promoted ahead of older far-away queued work.
- The page memoizes the filtered visible item list and the filter pane owns draft state in `CalendarFilterDialog`; typing or toggling draft filters does not rebuild the calendar board until Save.
- The week view renders one selected day at a time. The sticky day bar uses Blazor click handlers instead of scroll-position detection, so selecting a day updates the mounted day component and scrolls the board back to the top. Moderate days show 10 cards at a time and auto-load the next batch as the user scrolls near the sentinel; dense days use .NET 11 Blazor `Virtualize` over two-card rows so the browser only keeps the viewport and overscan rows mounted.
- `PremiereCard` and `CalendarDay` use render fingerprints to skip repeated renders when their visible inputs have not changed. Dense-day virtualization keeps an initial row size for first render accuracy while relying on .NET 11's runtime item-size adaptation for variable-height rows.
- Poster/backdrop image bytes are cached locally under `App_Data/cache/images` and served through `/cached-image`. Cards request display-width image variants with `w=185`, so the server stores a smaller JPEG poster variant instead of sending full remote poster bytes to the browser. Remote image downloads are globally capped (`ImageCache:MaxConcurrentDownloads`, default 4), so lazy loading many cards does not fan out unbounded poster requests.
- Resized image cache writes stream the remote source to a bounded temporary file before ImageSharp decodes it, avoiding large managed `MemoryStream` buffers while still enforcing `ImageCache:MaxBytes`.
- Cards use a browser-side intersection observer so offscreen posters do not request image bytes until they are near the viewport.
- Browser-side lazy images, day auto-loading, and filter swipes share one requestAnimationFrame-batched DOM observer instead of each script rescanning the document after every virtualized row mount. Day switching no longer needs scroll observers.
- ASP.NET Core response compression is enabled for text responses and negotiates zstd, Brotli, or gzip when supported by the client. Static assets use `MapStaticAssets().ShortCircuit()` so fingerprinted/compressed asset requests skip unnecessary middleware.
- Cached images are served from disk streams with browser cache validators instead of being read fully into managed memory for every request.
- Large source and language filter dropdowns render a searchable capped list first, while selected values remain visible.
- The Refresh button bypasses the fresh local week cache and in-memory source API caches for the visible week, while source failures can still fall back to the last cached week.
- Detail enrichment is concurrency-limited so a busy week does not fan out unbounded API requests. Watchmode and TVmaze HTTP calls also use shared provider-level concurrent request caps (`Watchmode:MaxConcurrentRequests` and `Tvmaze:MaxConcurrentRequests`) across foreground loads, prefetch, and warmup.
- External artwork providers are skipped when TMDb already has a poster, since TMDb poster artwork has first priority.

## Run Locally

From the repository root, the easiest local foreground run is:

```powershell
.\Run-PremiereCalendar.ps1
```

That script builds the solution, opens `http://localhost:5298`, and runs the app in the current terminal until Ctrl+C. Use `-Port 5301` when `5298` is already in use, or run `Run-PremiereCalendar.cmd` from `cmd.exe`.

Manual SDK bootstrap and run commands:

```powershell
New-Item -ItemType Directory -Force -Path .\artifacts | Out-Null
Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile .\artifacts\dotnet-install.ps1
powershell -ExecutionPolicy Bypass -File .\artifacts\dotnet-install.ps1 -Channel 11.0 -Quality preview -InstallDir .\.dotnet
.\.dotnet\dotnet.exe run --project .\PremiereCalendar\PremiereCalendar.csproj
```

The app binds to `http://0.0.0.0:5298` by default so it can be reached from other LAN devices when the VM firewall allows inbound TCP 5298.

The main routes are `/` for the combined calendar, `/series` for series-only filters, `/movies` for movie-only filters, and `/about` for source status and documentation notes. The lightbulb icon in the top bar toggles light and dark themes and stores the preference locally in the browser; route changes re-apply the saved value rather than resetting to the system/default theme.

The cog icon opens `/settings`, where local Sonarr/Radarr integrations and source API settings can be configured. Those settings are stored in the local SQLite database under `App_Data/data/premiere-calendar.db` by default. Enabled integrations add `Add to Sonarr` or `Add to Radarr` actions to the matching calendar cards. The settings page auto-loads root folders and quality profiles from enabled Sonarr/Radarr integrations when the URL and API key are present, so profiles are shown by name; the load buttons can refresh those options manually. Each integration can also apply a configurable tag on add; it defaults to `import` and is created in Sonarr/Radarr if missing.

The TMDb API read access token can be entered on the Settings page after first launch. User-secrets or environment variables are still useful when bootstrapping a new install non-interactively:

```powershell
dotnet user-secrets set "Tmdb:BearerToken" "YOUR_TMDB_V4_READ_ACCESS_TOKEN" --project .\PremiereCalendar\PremiereCalendar.csproj
```

Optional OMDb enrichment can also be enabled from the Settings page. Configuration fallback remains available:

```powershell
dotnet user-secrets set "Omdb:Enabled" "true" --project .\PremiereCalendar\PremiereCalendar.csproj
dotnet user-secrets set "Omdb:ApiKey" "YOUR_OMDB_API_KEY" --project .\PremiereCalendar\PremiereCalendar.csproj
```

Optional free source expansion can also be configured from the Settings page. Configuration fallback remains available:

```powershell
dotnet user-secrets set "Fanart:Enabled" "true" --project .\PremiereCalendar\PremiereCalendar.csproj
dotnet user-secrets set "Fanart:ApiKey" "YOUR_FANART_TV_KEY" --project .\PremiereCalendar\PremiereCalendar.csproj
dotnet user-secrets set "Trakt:ClientId" "YOUR_TRAKT_CLIENT_ID" --project .\PremiereCalendar\PremiereCalendar.csproj
```

TVmaze schedule discovery is enabled by default for the global web schedule only. Set the TVmaze schedule countries on the Settings page when you explicitly want country-specific broadcast schedule calls. Trakt is active by default once a Trakt client ID is stored in settings or configuration fallback.

## Validate

```powershell
dotnet test
```

Automated tests never call live external providers. TMDb, OMDb, TVmaze, Fanart.tv, Trakt, Watchmode, SIMKL, TheTVDB, Wikimedia, Sonarr, and Radarr behavior is covered with fake HTTP handlers, fakes, and JSON fixtures.

## VM Hosting

The published app can run from:

```text
D:\Apps\PremiereCalendar
```

For a one-command source-tree install or update as a Windows Service, run:

```powershell
.\Install-PremiereCalendar.ps1
```

The root installer builds a self-contained `win-x64` publish, copies it to `D:\Apps\PremiereCalendar`, preserves `App_Data`, installs or updates the `PremiereCalendar` Windows Service, opens inbound TCP `5298` for `LocalSubnet`, starts the service, and health-checks `/health`. It restarts itself elevated when service installation requires administrator rights. Use `Install-PremiereCalendar.cmd` from `cmd.exe` or Explorer when PowerShell execution policy is inconvenient.

Useful options:

```powershell
.\Install-PremiereCalendar.ps1 -Port 5301
.\Install-PremiereCalendar.ps1 -TargetDirectory 'D:\Apps\PremiereCalendar'
.\Install-PremiereCalendar.ps1 -SkipServiceInstall
```

The lower-level deployment scripts remain available under `deploy/` when you need separate publish and service-install steps.
The installer configures the service as `PremiereCalendar`, binds it to port `5298`, copies current user-secrets into service environment variables, and opens inbound TCP 5298 for `LocalSubnet`.
It also configures service failure recovery so Windows restarts the app after process failures. This app is not IIS-hosted, so it does not have an IIS application-pool idle timeout.

## Release Installer

For a portable release zip that can be installed without this source tree:

```powershell
.\Build-ReleasePackage.ps1
```

The root release wrapper calls `deploy\Build-ReleasePackage.ps1`. The release builder runs tests, publishes a self-contained Windows x64 build, strips API secrets from packaged `appsettings.json`, copies install/uninstall scripts, and writes a zip plus SHA256 checksum under `artifacts\release`. `Build-ReleasePackage.cmd` is also available from `cmd.exe`.

On the target machine, extract the zip and run from an elevated PowerShell session:

```powershell
.\Install-PremiereCalendar.ps1 -TmdbBearerToken 'YOUR_TMDB_V4_READ_ACCESS_TOKEN'
```

The release installer creates or updates the Windows Service, stores caches under `C:\ProgramData\PremiereCalendar`, opens the LAN firewall rule, preserves service secrets on updates, and health-checks the app before finishing. See [Release Installer](docs/ReleaseInstaller.md) for the complete install, update, and uninstall process.

## Documentation

- [How it Works](docs/HowItWorks.md)
- [Architecture](docs/Architecture.md)
- [Configuration](docs/Configuration.md)
- [Troubleshooting](docs/Troubleshooting.md)
- [Release Installer](docs/ReleaseInstaller.md)
- [Performance Review](docs/PerformanceReview.md)
- [Testing](docs/Testing.md)
