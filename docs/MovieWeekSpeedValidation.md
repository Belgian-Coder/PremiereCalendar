# Movie-week refresh speed investigation

## Reproducer and baseline

The reported route is movies for 31 August–6 September 2026, English/Dutch, minimum runtime 45 minutes. The 77.35-second Simkl source in v1.2.6 processed 1,020 candidates; its feeds downloaded in roughly 0.25 seconds. The job made 900 TMDb and 204 Rotten Tomatoes requests. Enrichment, rather than calendar download or scheduler queueing, determined completion. The last observed request was a 4.50-second Fanart lookup.

A new isolated live baseline used the same saved 150 rows as expired seeds and a PostgreSQL database restored from the pre-upgrade dump. It completed in 58.12 seconds. The initial candidate completed in 45.91 seconds with the same 150 results and the same provider call counts. Network timings differed, so that difference is **not evidence of a code-induced cold-load improvement**.

The saved Simkl payloads do not contain original language or runtime. The baseline mapped 1,015 distinct TMDb IDs and fetched 878 distinct detail responses to obtain authoritative filtering metadata. Removing those lookups without a valid cache would change coverage or filtering. At the unchanged 20-request/second TMDb limit, hundreds of genuinely uncached candidates necessarily take substantial time.

## Changes

- Independent movie ratings, Rotten Tomatoes and availability lookups run concurrently. Artwork begins when ratings supply its fallback poster, overlapping Rotten Tomatoes. External-candidate ratings and Rotten Tomatoes also overlap. Every started branch is observed on failure and cancellation.
- Successful TMDb movie/TV detail responses persist for their existing 12-hour lifetime. Rotten Tomatoes responses retain their configured lifetime, including verified successful no-match results. Restart hydration preserves the original absolute expiry. A failed Wikimedia identifier lookup cannot create a durable negative score result.
- Normal and forced flights remain separate. Shared publication gates fence stale normal work against forced refreshes. An oversized successful forced response replaces an older saved response with an explicit cache miss. Database failure remains best effort; unavailable storage cannot guarantee durable invalidation.
- Response entries use isolated, versioned, hashed keys in the existing database table. Individual envelopes are capped at 256 KiB. Existing scheduled maintenance removes owned response entries older than eight days; warmup and maintenance must be enabled for that schedule to run. Other provider metadata is preserved.
- Cache storage uses a 500 ms cooperative budget and separate five-second failure backoffs for TMDb, Rotten Tomatoes and cleanup. PostgreSQL response operations use four bounded slots with independent initialization instead of blocking behind a single store gate; SQLite retains serialized access and additionally uses a one-second busy retry limit. This is not a hard 500 ms wall-clock promise for native SQLite calls. Remote provider limits, pagination, discovery providers and final filters remain unchanged.

Fine-grained `providerresponsecache` events retain read/write/cleanup outcomes, hashed keys, durations and fixed reason codes. They omit response payloads, titles, URLs and credentials.

## Measurement method

The private local probe source, immutable baseline, restored isolated databases, captured provider responses and validation logs are retained under `D:\Projects\PremiereCalendar.Build.SpeedInvestigation`. The source baseline is main `f8dd0ad` (v1.2.6 runtime). The probe calls the real calendar pipeline and clients with unchanged provider limits. It substitutes an expired 150-row seed and suppresses week-cache writes so a complete week-cache hit cannot hide refresh work. Hosted warmup/dataset jobs are not started. Production data is never written by the probe.

Replay uses the same captured response bytes, status codes, network failures and delays for both versions. Request URLs and authorization headers are not saved; capture filenames are hashes. A replay with a missing record is rejected. The first exploratory replay omitted four Watchmode failure records and is retained as rejected evidence; final comparisons require zero missing records.

Output comparisons remove only `LastUpdatedUtc` and order the saved projection by canonical ID/date. The initial replay produced byte-identical JSON for all 150 complete rows, including scores, artwork, source attribution and other metadata (SHA-256 `4fad30dab6fedfdbdf3ab1e3c8d2822266a19aa377d6cb9cac26636dd9c21e31`). Final evidence and installation status are recorded below after acceptance.

## Windows/WSL observations

The zero-miss replay before the final storage-contention correction measured 45.366 seconds before and 45.376 seconds after, with identical provider counts: 894 TMDb, 202 Rotten Tomatoes, 31 Wikimedia, seven Fanart, four Watchmode failures, three Simkl, two Trakt and one OMDb. This establishes no meaningful cold-load improvement.

The first restart refresh took 24.270 seconds; later restarts took 9.576 and 9.525 seconds. All retained 150 complete rows. Later restarts made 16 TMDb and four Rotten Tomatoes calls, with other discovery/artwork providers still participating. The first fill encountered 21 cache-budget failures and 1,012 backoff skips. A single store gate serialized cache operations and a global backoff suppressed both providers. Those measurements triggered bounded PostgreSQL cache parallelism and provider-specific backoff rather than being presented as successful first-fill acceptance.

These are server/pipeline timings and process allocation deltas, not browser paint timings or production percentiles. A truly cold provider refresh remains separate from a complete week-cache read and from a refresh whose detail/score cache survives restart.

## Final Linux container measurements

The probe ran in the pinned .NET 11 RC3 Linux SDK container against the actual isolated PostgreSQL 18 instance, using the same captured responses/delays and expired seed. Each restart measurement starts a new process with empty in-memory caches. No test suites or container builds ran during these final measurements.

| Run | Completion | First nonempty server batch | TMDb / RT sends | Allocated bytes |
| --- | ---: | ---: | ---: | ---: |
| v1.2.6 baseline, cold detail/score caches | 47.364 s | 2.136 s | 894 / 202 | 342,283,976 |
| Final candidate, cold detail/score caches | 45.379 s | 0.927 s | 894 / 202 | 402,343,696 |
| Final candidate, first restart refresh | 8.888 s | 1.177 s | 16 / 4 | 217,442,704 |
| Final candidate, second restart refresh | 6.517 s | 0.786 s | 16 / 4 | 217,632,872 |
| Final candidate, third restart refresh | 5.888 s | 0.797 s | 16 / 4 | 217,422,984 |

All five runs returned 150 complete rows with zero missing replay records. Their normalized full projections are byte-identical: SHA-256 `3623cea9566e92d15aa6f793f52cf141fc423a96b3f856e315a5e454dad59584`. This comparison includes scores, artwork and all source contributions. Total sends decreased from 1,144 to 36 on each restart refresh. Fanart, Watchmode, Simkl and Trakt counts stayed at seven, four, three and two; previously cached OMDb/Wikimedia data required no additional sends. Provider coverage is retained, with valid responses reused.

The measured benefit is repeat enrichment after restart: roughly 81–88% shorter completion and 97% fewer sends. It is **not** a claim that completely cold providers finish in a few seconds. The cold candidate's allocation delta is about 18% higher because it serializes durable responses; restart allocation is about 36% below the cold baseline. Cold latency varied little relative to the unchanged provider work. This is a controlled workload, not an installed NAS percentile or an end-to-end browser measurement.

Final raw evidence: `linux-before-cold`, `linux-accepted-cold`, `linux-accepted-restart-{1,2,3}`, `linux-accepted-cache-outcomes.json`, and the probe source/response recordings in the private validation directory. Earlier Windows and rejected exploratory results remain available rather than being replaced by favorable runs.
