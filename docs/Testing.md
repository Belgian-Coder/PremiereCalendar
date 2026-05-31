# Testing

Normal automated tests must not call live external providers. TMDb, OMDb, TVmaze, Fanart.tv, Trakt, Watchmode, SIMKL, TheTVDB, Wikimedia, Sonarr, and Radarr behavior must be covered with fake HTTP handlers, test doubles, and JSON fixtures.

## Layers

Unit tests cover pure behavior:

- Monday start-of-week calculation.
- TMDb query construction.
- Trailer selection.
- OMDb rating and poster parsing.
- Canonical calendar IDs.
- Score filtering and normalization.
- Media-specific filters for the combined All view, including release type, certification, availability, provider IDs, watch region, multi-language selections, and TV network matching.
- Series episode-scope filtering, including default air-date discovery and new-series-only filtering.
- Premiere de-duplication.
- Artwork priority and Fanart.tv language/likes ordering.
- External discovery candidate acceptance, TMDb mapping requirements, duplicate collapse, and provider failure isolation.
- Source-batch timeout and bounded day-source concurrency so a slow TMDb batch does not block all other progress.
- Request-storm guards for TMDb concurrency and bounded background warmup windows.
- Belgium-origin language-free discovery, Belgian TV network discovery, and optional source-region ordering.
- French-language and Belgium-origin local filtering.
- View-sync URL validation, device grouping, latest URL persistence, and duplicate publish suppression.
- Item diagnostics for date semantics, merge contributions, missing-data reasons, source anomalies, score backfill, missing-ID repair, source health, and update evidence.

Integration tests cover service and HTTP behavior with fake handlers:

- TMDb query parameters, including saved-filter Discover criteria.
- TMDb official filter value endpoints for genres, languages, countries, watch providers, and certifications.
- TMDb paging and caching.
- TMDb pagination beyond five pages and explicit page-cap behavior.
- TMDb `429 Too Many Requests` retry behavior, complete broad Discover paging by default, explicit page-cap behavior, and page-batch streaming.
- Broad and saved-filter TMDb TV/movie date-window discovery behavior, including TV air-date versus first-air-date modes.
- Multi-language TMDb fan-out, where selected original languages create one Discover request per selected language.
- Criteria-specific week cache behavior.
- `IAsyncEnumerable` source-batch streaming during fresh week loads, with the older task API forwarding the same progress updates.
- Source-batch progress payloads used by clickable loaded-source chips.
- Details enrichment from `videos`, `external_ids`, TV networks, and TMDb watch providers.
- OMDb disabled, false-response, and caching behavior.
- Full `PremiereService.GetPremieresAsync` flow from fixture JSON.
- TVmaze exact-ID enrichment.
- TVmaze image-list and schedule response parsing.
- TVmaze global web-schedule discovery behavior.
- Fanart.tv movie and TV artwork parsing.
- Trakt movie and new-show calendar parsing.
- Trakt, TVmaze, and SIMKL merge candidates mapping to existing and new TMDb items without losing source attribution.
- Trakt no-client-ID skip behavior.
- Watchmode availability fallback parsing.
- SIMKL OAuth PIN exchange and sync-state behavior without live polling.
- TheTVDB login and artwork parsing.
- Wikimedia/Wikidata/Commons reusable-image fallback parsing.
- TMDb external-ID lookup with `/find/{external_id}`.
- Week cache read/write, warm-cache behavior, and stale fallback behavior.
- Adaptive warmup, foreground load budgets, cache metadata freshness, cleanup retention, and keyed single-flight coalescing.
- Image cache host validation, disk reuse, forced refresh behavior, and width-specific resized JPEG variants.
- Response compression negotiation for zstd, Brotli, and gzip HTML responses.
- App host smoke checks.

Component tests cover Blazor rendering:

- Seven day buttons, one mounted selected day, empty-day states, selected-day switching, dense-day `Virtualize` rendering, and the 10-card incremental path for smaller days.
- Premiere card metadata, provider/channel source chips, data provenance tags, scores, and trailer link behavior.
- Premiere card provenance details, including date confidence, merge inspector, and missing-data reasons.
- Premiere card render fingerprinting for unchanged inputs and score/image state changes.
- Calendar day render fingerprinting for unchanged day inputs and score/image state changes.
- Separate YouTube trailer search links on every card.
- Score-filter interactions.
- Calendar search, source dropdown filtering, filter Save/Cancel draft isolation, query-string restoration, and week navigation behavior.
- Multi-select language dropdown filtering and shareable `seriesLang`/`movieLang` query serialization.
- Navbar route changes between All, Series, and Movies without recreating the calendar component.
- Series-only and movie-only routes with route-specific filter copy.
- Adjacent-week prefetch trigger after visible week load.
- Incremental loaded-source display while source batches report progress.
- View sync Settings controls, explicit URL publishing, route-specific plain URL takeover, and same-route live-follow navigation.
- Settings source health, score backfill, missing-ID repair, update evidence, and rollback configuration rendering.
- Mobile filter drawer review text without reintroducing noisy header filter chips.

## Run

```powershell
dotnet test
```

## Fixtures

Integration fixtures live under:

```text
tests/PremiereCalendar.IntegrationTests/Fixtures/
```

They represent stable canned API responses. Add or update fixtures whenever external-source behavior changes.

## Manual Smoke Checks

After configuring real secrets, run the app and verify:

- This week loads without exceptions.
- Next week and previous week refresh correctly.
- Opening a week warms previous/next caches without blocking the visible week.
- Trailer links open direct YouTube video URLs.
- OMDb scores appear only when enabled and available.
- Provider/channel source chips appear when TMDb or TVmaze returns source data.
- Missing scores display as `n/a`, never as zero.
- Card provenance explains the date source and missing score or ID reasons.
- Settings source health lists provider cache state, OMDb state, IMDb state, and recent background jobs.
- Settings backfill and repair actions complete without changing unrelated weeks.
- GitHub source update evidence shows current/remote commits, dirty state, latest log tail, and the latest backup path.

## Browser Performance Checks

Dense-week UI checks use `/series?week=2026-04-20` because that week has hundreds of series rows and exercises the virtualized day path.

The current day-by-day budget is:

- Exactly seven sticky day buttons.
- Exactly one mounted `CalendarDay`.
- Under 2,000 DOM nodes.
- Under 40 mounted cards.
- No horizontal overflow at `1920x1080` or `390x844`.
- Poster cache URLs include `w=185`.
- No literal image-cache parameter names appear in rendered image URLs.
- Clicking another day changes the selected day and moves the selected day board back into view.

Latest local Playwright validation on 2026-05-16 after the quiet filter/header refresh:

- README screenshots were regenerated from the deployed app at `http://localhost:5298` and saved under `docs/images/readme/`.
- Desktop checks covered `/series?week=2026-05-11` in dark and light themes, `/movies?week=2026-05-11` in dark theme, the movie filter pane in dark theme, and `/settings` with the Local status center.
- Mobile `390x844` checks covered `/series?week=2026-05-11`, including the compact day jump strip and command-bar controls.
- Filter checks covered active filter URLs, verified `q=24` renders badge `1`, verified `q=24` plus runtime renders badge `2`, verified `seriesScope=new` plus `seriesLang=en,nl` renders badge `2`, verified stale unsupported cross-media query parameters do not inflate the filter badge or saved-filter restore state, verified sort-only changes do not render a filter badge, verified the old active-filter chip strip was absent, and verified there was no header-level clear-filters button.
- Filter pane checks verified runtime fields and the in-pane clear controls are present.
- Actions palette checks pressed `Ctrl+K`, verified the palette opened, pressed Escape, and verified the palette closed.
- Settings checks verified the Local status center rendered and provider delta failures stay visible above routine job entries.
- Layout checks reported no horizontal overflow. The deployed first-viewport measurements were command bar `82.75px` and calendar top `209.125px` on desktop, and command bar `150.8125px` and calendar top `342.359375px` on mobile with source diagnostics visible.

Additional deployed Playwright validation on 2026-05-19 after filter URL and view-sync hardening:

- Desktop `/series?week=2026-05-11&day=2026-05-17&seriesScope=new&seriesLang=en,nl` rendered badge `2`, no horizontal overflow, no console errors, and preserved the two-card desktop grid.
- Desktop `/movies?week=2026-05-11&movieScope=new&movieLang=en` rendered badge `1`, proving unsupported `movieScope` no longer inflates the count.
- Mobile `390x844` `/series?week=2026-05-11&day=2026-05-17&seriesScope=new&seriesLang=en,nl` rendered badge `2`, no horizontal overflow, and no console errors.
- Post-deploy health check returned `200 Healthy`; the Windows Service was `Running` with `Automatic` start type.
- Regression tests now cover view-sync overview failures so optional sync storage issues do not break Calendar or Settings rendering.

Current automated validation on 2026-05-31 after data-trust diagnostics:

- `dotnet test PremiereCalendar.slnx --no-restore` passed 518 tests.
- New tests cover date semantics, source merge attribution, anomaly detection, score backfill, missing external-ID repair, source health, update evidence, rollback script behavior, provenance rendering, Settings diagnostics, and mobile filter review.
- Deployed Playwright validation covered desktop `1440x1000` All, Series, Movies, card provenance, Settings diagnostics, command palette, and filter drawer, plus iPhone `390x844` Series, Movies, card provenance, Settings diagnostics, source health, and filter drawer.
- Browser validation reported no console warnings or errors, no horizontal overflow, no visible command clipping, filter panes within the viewport, and two-column desktop card grids.
