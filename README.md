# Premiere Calendar

Premiere Calendar is a web page you run yourself. It shows upcoming movie and series premieres by week.

The calendar focuses on first-debut series and new movies. New sessions default to English/Dutch, with a 45-minute movie minimum and no series runtime minimum. Changing the date keeps the selected filters; explicit language and runtime overrides remain available.

Use it to:

- Browse new movies and series.
- Filter by language, provider, score, runtime, and more.
- See where each card came from.
- Inspect why a date, source, score, or external ID is missing.
- Add verified movies to Radarr and verified series to Sonarr, if you use those apps.

TMDb is required for live data. Everything else is optional.

The app prepares the current and next month each night at 02:00 Brussels time for the default Series and Movies filters. Completed week caches stay fresh for 30 hours by default, so ordinary date navigation can use local data. Missed nightly work catches up after restart; progress and timings are logged automatically. See [configuration](docs/Configuration.md#adaptive-warmup-and-load-budget) for schedule overrides.

Reusable item metadata and scores are stored in the application database for three calendar months since last use, independently of week files. Weeks share those items; change feeds target known changed titles, metadata has a seven-day fallback refresh, and scores refresh after 24 hours. See [item caching](docs/ItemEnrichmentCache.md) for retention, failure handling and configuration.

## Screenshots

| Dark mode | Light mode |
| --- | --- |
| ![New-series calendar in dark mode](docs/images/readme/series-dark.png) | ![New-series calendar in light mode](docs/images/readme/series-light.png) |

| Movies | Filters |
| --- | --- |
| ![Movie calendar in dark mode](docs/images/readme/movies-dark.png) | ![Filter pane in dark mode](docs/images/readme/filters-dark.png) |

| Local status |
| --- |
| ![Settings local status center](docs/images/readme/settings-local-status-light.png) |

## Deploy With Docker

The only supported runtime is the Docker/PostgreSQL stack (there is no Windows service or
self-updating installer, and no SQLite mode). Pin
`PREMIERECALENDAR_IMAGE` to an accepted immutable GHCR digest, provide the
PostgreSQL password through the configured Docker secret file, and start
`compose.yaml` with Docker Compose. The app binds to loopback port `18084`
by default; publish it to the trusted private network through the documented
reverse proxy.

If TMDb settings are missing, the calendar automatically opens Settings and shows a setup notice until the required token is saved.

## TMDb Token

TMDb is required. Without it, the app opens but cannot load real calendar data.

1. Create or sign in to a TMDb account.
2. Open your TMDb account API settings.
3. Copy the API Read Access Token, not the short API key.
4. Paste it in the app Settings page and click Save.

## Check It Worked

| Check | Good result |
| --- | --- |
| Containers | App and PostgreSQL report healthy |
| App page | `http://localhost:18084` opens on the Docker host |
| Readiness | `http://localhost:18084/health/ready` says Healthy |
| Version | `http://localhost:18084/health/version` reports PostgreSQL healthy |
| Calendar | All, Series, or Movies loads cards after the TMDb token is saved |

## Open From Another Computer

Use the private HTTPS hostname exposed by the reverse proxy. Keep the app and
PostgreSQL ports off the public internet; only the proxy should cross the
container-host boundary.

Premiere Calendar has no built-in user login. Keep the service on a trusted LAN or VPN, and avoid exposing it directly to the public internet.

## Develop From Source

Local development is container-only: Docker Engine runs inside Ubuntu 24.04 on WSL, and Compose supplies both the app SDK and a dedicated PostgreSQL database. No host .NET or PostgreSQL installation is required for the development workflow.

```powershell
.\eng\wsl-docker.ps1 up
```

Docker must already be running in Ubuntu-24.04; the helper does not start services. Open `http://localhost:5299`. Use `check`, `test`, `logs`, or `down` as needed; `-Plan` previews a command without WSL/Docker execution. `reset` removes this checkout's development containers and named volumes and requires explicit permission for that deletion. The development database listens on `127.0.0.1:55432`; see [Configuration](docs/Configuration.md#application-database).

See [Docker and PostgreSQL](docs/DockerAndPostgres.md) for production Compose, migration, health, security, backup, and recovery details. The Docker/PostgreSQL stack is the only deployment.

## Navigation And Shortcuts

| What you want | What to do |
| --- | --- |
| Go to the previous day | Press Left Arrow |
| Go to the next day | Press Right Arrow |
| Pick a day in the week | Click the day button |
| Go to another week | Use Previous, This week, or Next |
| Move from the top of a day to yesterday | Keep scrolling upward until the message appears |
| Move from the bottom of a day to tomorrow | Keep scrolling downward until the message appears |
| See which sources loaded | Open Source details when source diagnostics are shown |
| See cache age | Check the data freshness pill above the day strip |
| Save filter combinations | Open Actions, enter a preset name, and click Save preset |
| Apply a saved filter preset | Open Actions, choose a preset, and click Apply preset |
| Open Actions | Press Ctrl+K, or Cmd+K on macOS |
| Close Actions | Press Escape |
| Copy the current filtered view | Open Actions and click Copy view link |
| Export visible premieres | Open Actions and choose ICS, CSV, or JSON |
| Fit more cards on screen | Open Actions and choose Use compact cards |
| Change filters | Click the filter button; a small badge shows how many filters are active |
| Review active mobile filters | Open the filter button; the compact review is at the top of the pane |
| Change settings | Click the cog icon |

The arrow keys do not take over while you are typing in a text box.

## Sync Viewing Between Devices

View sync is optional. It lets two or more browsers follow the same calendar URL.

1. Open Settings.
2. Find View sync.
3. Give this browser a name, such as `Office PC`.
4. Create or select a group.
5. Turn on Sync this browser.
6. Save view sync.

Do the same on another computer and choose the same group. When one grouped browser changes day, week, route, or filters, the other grouped browsers follow the latest view. Use Ungroup this device to stop syncing that browser.

Settings shows one block per sync group. Each block lists the attached browsers and the latest saved URLs for All, Series, and Movies. The current browser is marked `me`, and the active group is highlighted.

All, Series, and Movies keep separate synced views. A browser on Series follows only the latest Series URL, not Movies or All. Opening All, Series, or Movies without filters first uses that group's saved URL for the same route. Local saved filters are only the fallback when the browser is not in a group or that group has no saved URL for the route. Settings, About, external links, credentials, and host names are not synced.

## What The Cards Mean

- Normal cards are verified through TMDb.
- Unverified cards are shown below normal cards when an outside source found something that does not match TMDb yet.
- `Source` chips show provider, channel, or streaming source names when known.
- Scores are ordered IMDb, TMDb, Rotten Tomatoes audience, then Rotten Tomatoes critics. Missing and zero scores are omitted from the render tree.
- Open the provenance details on a card to see date confidence, source merge contributions, matched IDs, and missing-data reasons.

## Providers

| Provider | Needed? | What it does |
| --- | --- | --- |
| TMDb | Required | Main calendar data, posters, trailers, metadata |
| IMDb datasets | Included | IMDb scores and vote counts, cached locally |
| Trakt | Optional | Extra movie and new-series candidates |
| TVmaze | Optional | Extra series schedule data |
| Watchmode | Optional | Streaming availability fallback |
| OMDb | Optional | Rotten Tomatoes, Metacritic, plot and poster fallback |
| Fanart.tv, TheTVDB, Wikimedia | Optional | Poster fallback when TMDb has no poster |
| SIMKL | Optional | Account/library sync state |
| Sonarr, Radarr | Optional | Add buttons on verified cards |

## Cache

The app keeps calendar data, images, IMDb scores, OMDb responses, view-sync groups, and small provider sync markers on disk. This means cached data survives restarts, crashes, and updates.

The database retains item metadata and scores for three calendar months since last use. Metadata, scores, artwork and availability refresh independently; a failed provider keeps its own last-good values and success time. Nightly work prepares the current and next month using the saved premiere-only English/Dutch profiles. A partial result keeps its coverage warning. Detailed timed diagnostics run automatically; see [cache verification and investigation](docs/CacheFreshnessDiagnostics.md).

Use Update when you want to reuse fresh local cache where possible. Use Refresh sources when you want the visible week checked against providers again. Refresh sources keeps useful existing data where possible and fills in changes or missing details. TMDb, TVmaze, and SIMKL have change/activity endpoints; the app records those checks so later cache decisions have dates to compare against. IMDb scores come from the daily IMDb dataset, not a per-item change API.

Settings includes a Local status center with a cache inspector, background job timeline, source health drilldown, settings backup/restore box, score backfill, and missing-ID repair. Updating means deploying a new digest-pinned container image. Builds, tests, scans and releases run locally. GitHub Actions must not be used, including manual dispatches; both hosted workflows are disabled. GitHub may host source and published artifacts without running CI.

## Common Problems

| Problem | Try this |
| --- | --- |
| No cards load | Add a TMDb token in Settings, then click Refresh sources |
| IMDb scores are missing | Wait for the IMDb dataset import to finish, then click Refresh sources |
| Rotten Tomatoes or Metacritic is missing | Enable OMDb with a working API key, then use Settings > Backfill scores |
| IMDb/TVDB IDs are missing | Use Settings > Repair IDs after TMDb is configured |
| A source looks slow | Open Settings and check the Local status background job timeline |
| TVmaze is slow | Use narrower filters or disable TVmaze schedule discovery in Settings |
| Settings look wrong | Change them in the app Settings page, not in `appsettings.json` |
| Posters are missing | Check optional artwork providers, then click Refresh sources |

More help: [Troubleshooting](docs/Troubleshooting.md).

## Test It

Every test runs against PostgreSQL; none are skipped when it is missing. The script
starts a throwaway PostgreSQL container, runs the unit, component and integration
tests in the development image, and removes everything afterwards:

```powershell
pwsh -File D:\Projects\PremiereCalendar\eng\Test-WithPostgres.ps1
```

Pass `-Project IntegrationTests` or `-Filter "FullyQualifiedName~ViewSync"` to narrow
the run. See [Testing](docs/Testing.md) for the environment variables and details.
Tests use fake providers. They must not call live external services.

Builds and test reports stay under this checkout's ignored `artifacts/`.
Container checks retain incremental build outputs across runs; Linux packages
are shared with worktrees through this project's primary `artifacts/packages/linux`.
Native Development caches/logs stay in `.local-data/`; Compose preserves its
named application-data and PostgreSQL volumes. Previous sibling build/data
directories are untouched. See [Testing](docs/Testing.md#scoped-feedback-and-output-ownership)
for scoped checks and release acceptance.

Check workspace hygiene after local work. Preview cleanup only when a deliberate
reset of this checkout's build/test output has been approved:

```powershell
.\eng\Workspace-Maintenance.ps1 -Mode Check
.\eng\Workspace-Maintenance.ps1 -Mode Clean -WhatIf
```

After every commit and release, follow the [bounded cleanup checkpoint](AGENTS.md#cleanup-checkpoint).
It preserves warm caches and evidence; the separate `Clean` reset above is not
the checkpoint's default action.

The check fails for unexpected `bin`/`obj`/`TestResults` directories, build output
over 1.5 GB, or native Development data over 512 MB. Cleanup preserves package
caches, SDKs, credentials, sibling paths and other worktrees. A deliberate native
local-data reset additionally requires `-RemoveDevelopmentData`.

## More Documentation

Most users only need this README and [Troubleshooting](docs/Troubleshooting.md).

Technical references:

- [Configuration](docs/Configuration.md)
- [How It Works](docs/HowItWorks.md)
- [Architecture](docs/Architecture.md)
- [Performance Notes](docs/PerformanceReview.md)
- [Measured refresh with saved filters](docs/SavedFilterTimingResults-20261102.md)
- [Installed selective-cache validation and service totals](docs/CacheRefreshLiveValidation-20261103.md)
- [Testing](docs/Testing.md)
- [Docker and PostgreSQL](docs/DockerAndPostgres.md)
