# Follow-up Instructions: Source Enrichment and De-duplication Strategy

> Historical implementation brief. The current app keeps TMDb as canonical but now also documents Watchmode, SIMKL, adaptive cache warmup, provider settings, and troubleshooting in the maintained docs.

## Context

This is a follow-up to the previous implementation plan for a local-hosted .NET 10 Blazor Web App that shows a weekly calendar of upcoming series and movie premieres.

The app is **not** a watch tracker. It should not require the user to maintain watched lists, watchlists, personal calendars, or availability preferences.

The app should answer one primary question:

> What new English/Dutch series premieres and movie first releases are coming out in the selected week?

## Non-negotiable product scope

The app must **not** become a general media tracker.

Do **not** implement:

- watched/unwatched tracking
- personal watchlists
- all weekly TV episodes
- all season episodes
- availability by Belgian/NL streaming service
- local streaming release monitoring
- user check-ins
- social features
- recommendation feeds based on watch history

The calendar should contain only these event types:

| Event type | Meaning |
|---|---|
| `SeriesPremiere` | First episode / first-air-date of a new TV series |
| `MovieFirstRelease` | First/primary release of a movie, regardless of release type |

## Core principle

TMDb must remain the **single canonical calendar source**.

Other sources may enrich an existing TMDb calendar item, but they must **not** create independent calendar rows.

Correct model:

```text
TMDb creates calendar rows
→ External IDs are fetched from TMDb
→ OMDb / TVmaze / IMDb datasets enrich exact matches
→ Enrichment failures do not remove the TMDb calendar item
```

Incorrect model:

```text
TMDb calendar
+ TVmaze calendar
+ Trakt calendar
+ TheTVDB search results
+ IMDb search results
= merged calendar
```

Do not independently merge multiple premiere feeds. That creates duplicates, near-duplicates, bad title matches, regional title variants, remake collisions, translated-title collisions, and incorrect premiere dates.

## Canonical identifiers

Use TMDb IDs as the primary identity of calendar items.

Use these keys:

```text
tv:{tmdb_tv_id}
movie:{tmdb_movie_id}
```

Examples:

```text
tv:123456
movie:987654
```

Never use title as the primary key.

Bad identity fields:

```text
The Office
Ghosts
Alien
Rivals
```

Good identity fields:

```text
mediaType + tmdbId
```

## Required source roles

### TMDb: primary source

TMDb decides:

- which items appear in the calendar
- whether an item is a TV premiere or movie first release
- title
- description
- first-air date / release date
- language filter
- origin-country filter
- runtime filter
- poster/backdrop
- trailers/videos
- TMDb score
- external IDs

TMDb is the only source that should create `CalendarItem` records.

### OMDb: optional rating enrichment

OMDb may enrich existing TMDb items, but only by exact IMDb ID.

Allowed fields:

- IMDb rating
- IMDb vote count
- Rotten Tomatoes rating, when present
- Metacritic rating, when present
- optional fallback plot

Rules:

```text
If TMDb external IDs contain imdb_id:
    call OMDb by IMDb ID
else:
    skip OMDb enrichment
```

Do **not** search OMDb by title in the MVP. Title search is too error-prone.

### TVmaze: optional TV-only enrichment

TVmaze may enrich existing TMDb TV items, but only by exact external ID.

Allowed fields:

- network
- web channel
- average runtime
- official site
- TVmaze rating
- TVmaze URL
- optional show-level summary fallback

Rules:

```text
If TMDb TV external IDs contain tvdb_id:
    lookup TVmaze by TheTVDB ID
else if TMDb TV external IDs contain imdb_id:
    lookup TVmaze by IMDb ID
else:
    skip TVmaze enrichment
```

Do **not** import TVmaze episode lists into the calendar.

Do **not** turn TVmaze schedule results into calendar rows.

TVmaze is a verifier/enricher, not the calendar authority.

### IMDb non-commercial datasets: later optional enrichment

IMDb datasets may be added later as a local batch-imported rating cache.

Possible fields:

- IMDb average rating
- IMDb vote count
- runtime sanity check
- alternative title sanity check

Rules:

```text
Match only by IMDb tconst / IMDb ID.
Do not create calendar rows from IMDb datasets.
Do not use title matching unless explicitly marked as unverified.
```

This should not be part of the first implementation unless OMDb is insufficient.

### TheTVDB: skip for now

Do not add TheTVDB in the MVP.

Reason:

The app does not need detailed season/episode structure, specials, absolute order, DVD order, episode groups, or media-center metadata.

TheTVDB may be reconsidered only if TMDb data proves consistently incomplete for new TV premieres.

### JustWatch / Watchmode: skip

Do not add JustWatch or Watchmode for this app phase.

Reason:

The user explicitly does not care about availability, local Belgian/NL release, streaming-provider filtering, or where to watch.

These APIs solve a different problem:

```text
Where can I legally watch this?
```

This app solves:

```text
What new English/Dutch premieres exist globally?
```

### Trakt: avoid as a dependency

Do not use Trakt as a primary or secondary calendar source.

Reason:

The app is intentionally replacing the old Trakt/SIMKL calendar-style filtering. Trakt/SIMKL should not be reintroduced as required dependencies.

Trakt may be considered only as a later optional comparison source, and even then only for diagnostics, not for generating calendar rows.

## Required filters

### TV series premieres

Show only new series premieres.

Use TMDb Discover TV with:

```text
first_air_date.gte = selectedWeekStart
first_air_date.lte = selectedWeekEnd
```

Do **not** use `air_date.gte/lte` for the main TV query, because that includes normal weekly episodes.

Required TV language/country logic:

```text
Query A:
with_original_language = en
with_origin_country = US|GB|AU

Query B:
with_original_language = nl
optional with_origin_country = NL|BE
```

Run these as separate queries and merge by TMDb TV ID.

### Movies

Show movie first releases / primary releases.

Use TMDb Discover Movie with:

```text
primary_release_date.gte = selectedWeekStart
primary_release_date.lte = selectedWeekEnd
with_runtime.gte = 40
```

Required movie language/country logic:

```text
Query A:
with_original_language = en
with_origin_country = US|GB|AU

Query B:
with_original_language = nl
optional with_origin_country = NL|BE
```

Run these as separate queries and merge by TMDb Movie ID.

Release type does not matter for the current requirement.

## Data model guidance

Use a normalized local model similar to this:

```csharp
public enum CalendarItemType
{
    SeriesPremiere,
    MovieFirstRelease
}

public sealed class CalendarItem
{
    public required string CanonicalId { get; init; } // tv:123 or movie:456
    public required CalendarItemType Type { get; init; }

    public required int TmdbId { get; init; }
    public string? ImdbId { get; set; }
    public int? TvdbId { get; set; }
    public string? WikidataId { get; set; }

    public required string Title { get; set; }
    public string? OriginalTitle { get; set; }
    public string? Overview { get; set; }

    public required DateOnly PremiereDate { get; set; }
    public string? OriginalLanguage { get; set; }
    public IReadOnlyList<string> OriginCountries { get; set; } = [];

    public int? RuntimeMinutes { get; set; }
    public double? TmdbVoteAverage { get; set; }
    public int? TmdbVoteCount { get; set; }

    public string? PosterUrl { get; set; }
    public string? BackdropUrl { get; set; }
    public string? TrailerUrl { get; set; }
    public string? TmdbUrl { get; set; }

    public RatingEnrichment? Ratings { get; set; }
    public TvSeriesEnrichment? TvSeriesEnrichment { get; set; }

    public DateTimeOffset LastUpdatedUtc { get; set; }
}

public sealed class RatingEnrichment
{
    public double? ImdbRating { get; set; }
    public int? ImdbVoteCount { get; set; }
    public int? RottenTomatoesPercent { get; set; }
    public int? MetacriticScore { get; set; }
    public string Source { get; set; } = "OMDb";
}

public sealed class TvSeriesEnrichment
{
    public string? NetworkName { get; set; }
    public string? WebChannelName { get; set; }
    public int? AverageRuntimeMinutes { get; set; }
    public double? TvmazeRating { get; set; }
    public string? OfficialSiteUrl { get; set; }
    public string? TvmazeUrl { get; set; }
}
```

## Calendar generation pipeline

Implement the pipeline in deterministic stages.

```text
1. User selects week.
2. Build TMDb TV premiere queries.
3. Build TMDb movie first-release queries.
4. Execute TMDb queries.
5. Normalize TMDb results into CalendarItem records.
6. De-duplicate by CanonicalId.
7. Fetch TMDb details/external IDs/videos only for resulting items.
8. Add trailer links from TMDb videos.
9. Add OMDb rating enrichment when IMDb ID exists.
10. Add TVmaze enrichment for TV items when TVDB ID or IMDb ID exists.
11. Cache the normalized results locally.
12. Render week calendar grouped by date.
```

Enrichment must be best-effort.

If OMDb or TVmaze fails, the app should still show the TMDb calendar item.

## De-duplication rules

### Primary de-duplication

```text
Distinct by CanonicalId
```

### Secondary de-duplication

Only use secondary de-duplication for defensive cleanup.

Possible secondary heuristic:

```text
same media type
same normalized title
same premiere year
same original language
same origin country overlap
```

But secondary matches should be logged and reviewed. Do not silently merge uncertain items.

### Title normalization

If needed for diagnostics only:

```text
- lowercase
- trim whitespace
- remove leading articles only for comparison: the, a, an, de, het, een
- remove punctuation
- collapse repeated whitespace
```

Never rely on normalized title as the primary identity.

## UI requirements

The weekly calendar UI should show blocks per day.

Each calendar card should include:

- title
- type: series premiere or movie release
- date
- short description
- language
- origin country/countries
- runtime, if available
- TMDb score
- IMDb score, if available
- Rotten Tomatoes score, if available
- Metacritic score, if available
- trailer link, if available
- TMDb link
- optional TVmaze/network information for series

Required filters in the UI:

- date week selector
- media type: series / movies / both
- minimum TMDb score
- minimum IMDb score, if available
- minimum Rotten Tomatoes score, if available
- minimum Metacritic score, if available
- language: English / Dutch / both
- origin group: US / UK / Australia / Dutch-language / all configured

Do not add watchlist controls unless explicitly requested later.

## Caching strategy

Use a local database or local file cache.

Recommended simple approach:

```text
SQLite + EF Core
```

Cache:

- TMDb normalized calendar items
- TMDb detail responses
- TMDb video responses
- TMDb external IDs
- OMDb responses by IMDb ID
- TVmaze responses by IMDb ID or TVDB ID

Use a refresh button in the UI.

Avoid calling all external APIs on every page load.

## Error handling

The app must degrade gracefully.

Required behavior:

| Failure | Required behavior |
|---|---|
| TMDb discovery fails | Show error banner; keep previous cached results if available |
| TMDb details fail for one item | Show item with basic fields |
| TMDb videos fail | Show item without trailer |
| OMDb fails | Show item without external ratings |
| TVmaze fails | Show item without TVmaze enrichment |
| Cache unavailable | Show clear local storage error |

## Testing requirements

Testing is critical. The app must include unit and integration tests so that future feature changes do not break the core calendar behavior.

### Unit tests

Required unit tests:

- TV query builder uses `first_air_date`, not `air_date`.
- TV English query uses original language `en` and origin countries `US|GB|AU`.
- TV Dutch query uses original language `nl`.
- Movie query uses `primary_release_date` date range.
- Movie query uses runtime greater than or equal to 40 minutes.
- Calendar IDs are generated as `tv:{id}` and `movie:{id}`.
- Duplicate TMDb results are merged by canonical ID.
- OMDb is called only when an IMDb ID exists.
- TVmaze is called only when TVDB ID or IMDb ID exists.
- Title search fallback is not used in MVP enrichment.
- Trailer selector prefers official YouTube trailers when available.
- Score filter handles missing ratings correctly.

### Integration tests

Integration tests must use fake HTTP handlers or canned JSON fixtures.

Do not call live external APIs in normal automated tests.

Required integration tests:

- TMDb TV discovery JSON maps to `SeriesPremiere`.
- TMDb movie discovery JSON maps to `MovieFirstRelease`.
- TMDb details + external IDs enrich the same canonical item.
- OMDb response enriches ratings correctly.
- TVmaze response enriches only TV items.
- OMDb failure does not remove the calendar item.
- TVmaze failure does not remove the calendar item.
- Cached results are used when discovery refresh fails.
- Weekly calendar groups items by date correctly.

### UI/component tests

If using Blazor, add bUnit tests for:

- week calendar renders seven day columns/sections
- empty days render cleanly
- series and movie cards render with correct labels
- score filters hide/show items correctly
- missing trailer does not break card rendering
- failed enrichment shows no broken UI

## Implementation priorities

### Phase 1

Implement:

- TMDb-only weekly calendar
- TV premiere filters
- movie first-release filters
- details, descriptions, posters
- trailer links
- TMDb score filtering
- local cache
- unit tests for query builders and normalization
- integration tests using TMDb fixtures

### Phase 2

Add:

- OMDb rating enrichment by IMDb ID
- IMDb / Rotten Tomatoes / Metacritic UI fields
- rating-range filters
- OMDb fixtures and tests

### Phase 3

Add:

- TVmaze TV-only enrichment by TVDB ID or IMDb ID
- network/web-channel fields
- TVmaze URL
- TVmaze fixtures and tests

### Phase 4

Optional:

- IMDb local dataset import
- Wikidata/Wikipedia links
- extra diagnostics for missing identifiers
- advanced duplicate review screen

## Acceptance criteria

The implementation is acceptable when:

- The calendar shows only new series premieres and movie first releases.
- No regular weekly TV episodes appear.
- The app does not require watched tracking or watchlists.
- TMDb is the only source that creates calendar rows.
- OMDb and TVmaze only enrich existing TMDb rows.
- Duplicate rows are not produced when enrichment sources return data.
- The UI can filter by score ranges.
- Trailer links are shown when TMDb has trailers.
- Missing ratings/trailers do not break the UI.
- Unit and integration tests protect the core behavior.
- The app remains stable when enrichment APIs fail or return partial data.
