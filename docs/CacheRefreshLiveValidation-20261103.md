# Selective cache refresh — installed validation, 3 November 2026

v1.2.14 is installed on NAS Portainer stack 20 revision 13. Image source is `1781c3eb045513f182e4aa16da8dca37f19919d9`; accepted image configuration identity is `sha256:1bbaa00d773228eab29286d2328e81792cc7c0dedfbcda215730deb3a14b86bb`. The app started at **20:38:24 UTC / 21:38:24 Brussels**. Only the app was replaced. PostgreSQL and backup container IDs and start times were preserved, together with schema 4, data, environment and runtime hardening.

The originally reported **19–25 October movie week now shows 134 titles and seven Monday movies**. English/Dutch and movie runtime 45–360 minutes were verified in the filter dialog; Series retains New only. Navigation used normal loads with saved defaults.

These are retained-cache observations from one installed session, not cold-cache timings, percentiles or a controlled before/after code-speedup experiment. Browser measurements include navigation, interactive startup and observation of populated rows. Server timings come from correlated performance events and measure different boundaries.

## Visible page timings

| Profile/week | First cached rows, server | Final result, server | Rows | Observed HTTP responses | Browser populated-row observation |
|---|---:|---:|---:|---:|---:|
| Series, 2 November, first | 76.49ms | 5,148.13ms, partial | 31 →33 | 103 | 3,114ms including initial tab/bootstrap |
| Series, 2 November, repeat | 29.41ms | 36.08ms, partial | 33 | 0 | First snapshot still loading; excluded from populated-row timing |
| Series, 2 November, confirmed repeat | 32.91ms | 36.99ms, partial | 33 | 0 | 405ms |
| Movies, 12 October, first | 112.17ms | 13,104.90ms, complete | 181 | 36 | 698ms to cached rows |
| Movies, 12 October, repeat | 128.17ms | 135.51ms, complete | 181 | 0 | 665ms |
| Movies, 19 October, next week | 86.22ms | 94.19ms, complete | 134 | 0 | 568ms |

The first Series and first October12 Movies loads each attached one foreground job. Repeat visits and October19 attached none. About rendered successfully; browser warning/error capture was empty. An auxiliary All-profile load cancelled during About navigation after 24.23ms, with no job or HTTP response; it is excluded from the page-performance comparison.

Series correctly shows **“Trakt temporarily unavailable. Results may be incomplete”** with its next-check deadline. Its otherwise validated33-row partial snapshot is reused before that deadline. A partial result is not reported as complete discovery.

## Service totals and detailed attribution

The fixed log snapshot covers process startup through **20:41:17.064 UTC**, captured at 20:41:41.380 UTC. It contains 34,428 events, with zero omitted/malformed/incomplete/timestamp/loss records. Its SHA256 is `038d1d75d1b01df2d3b44cf4050f9fa1045ffb090c418b70e9ccb09cd4d414da`.

There were **872 logical provider requests, 875 attempts, 841 status-coded HTTP responses and 34 local no-send circuit deferrals**. All 34 status-less completed attempts were Watchmode local-circuit deferrals. The three additional attempts were retries. Wait/admission/cache/parser/item events are not additional HTTP requests.

| Attribution | HTTP responses |
|---|---:|
| Visible Series foreground job | 103 |
| Visible Movies foreground job | 36 |
| Background jobs | 694 |
| TMDb responses without load/job attribution | 8 |
| **Total** | **841** |

The103 Series responses were57 TMDb200,28 TVmaze200,10 TVmaze404, seven Rotten Tomatoes200 and one SIMKL200. The36 Movies responses were33 Rotten Tomatoes200, two Rotten Tomatoes404 and one Wikimedia200. Repeat loads contributed zero responses and no jobs.

| Provider | HTTP responses by status |
|---|---|
| TMDb | 332 ×200 |
| Rotten Tomatoes | 385 ×200;8 ×404 |
| TVmaze | 58 ×200;10 ×404 |
| Wikimedia | 33 ×200 |
| SIMKL | 5 ×200;4 ×404 |
| IMDb dataset | 1 ×200 |
| Trakt | 2 ×403 |
| Watchmode | 3 ×429 |
| OMDb | 0; persisted access/quota backoff remained active |

Background work overlaps foreground work; its durations must not be added to page elapsed time. Startup warmup took 3.098s/51 responses, delta sync13.538s/258, and the IMDb dataset refresh10.642s/one response. Nine adjacent-week executions used384 responses in total:24/8.070s,110/29.775s,0/0.066s,0/0.077s,36/11.962s,19/5.137s,22/7.388s,21/5.858s and 152/37.371s. This remaining background work is included in the totals, rather than hidden behind the fast visible repeats.

## Persistence and replacement evidence

Before/after projections contain **2,531 records, with zero additions or removals**. Twenty-two core metadata clocks advanced;21 core-value fingerprints changed. Artwork/availability changed on 14/20 records. First publication of explicit component clocks for legacy records is distinct from a new provider fetch and must not be counted as a redownload.

The accepted-result events include47 movie `cleared`,183 movie `refreshed`,719 movie `reused`, and 52 Series `reused` observations covering 50 distinct Series IDs. These are logged accepted-write observations; they are not a sum of unique titles or confirmed new HTTP requests. Provider payloads and their own success clocks are the authority for replacement.

All 357 IDs with changed rating fingerprints were compared with the restored backup. IMDb changed on 212 items:83 score and 211 vote-count changes, including nine previously absent values; every changed identity/value matches the current local dataset, with no IMDb clears. All 130 Metacritic hash changes were omitted-property → explicit-null serialization changes, with zero numeric changes or clears. Across 226 existing stable-identity records, OMDb retained every external numeric field and its exact previous success clock despite276 locally deferred failure observations and zero OMDb HTTP responses.

Rotten Tomatoes changed on 56 items:13 critic and 48 audience changes, including 47 field clears (one critic,46 audience). Every clear retained its stable identity and old OMDb clock, and correlated with successful RT processing plus an accepted matching new RT clock. For example, Movie 1214013 has verified-empty event28557 and accepted-write event31121, independently of OMDb failure28298. The sanitized per-ID evidence is in `installed-cache/live-score-preservation.json`. Raw RT HTML was not retained, so the saved evidence proves parsed provider payloads and accepted persistence, rather than independently reparsing the remote response.

All 20 prior November/December nightly windows and receipts remain present. The current Series file correctly changed from 31 complete rows to 33 **partial** rows after Trakt denial, leaving19 presently complete windows and 659 cached rows across 20 files. The20 stored completed jobs are historical, rather than proof that this release rebuilt20 complete windows. The next scheduled nightly run has not yet occurred. Exact-image planner/timezone and degraded-coverage receipt regressions passed.

## Release and logging gates

- **1,510 tests passed:**818 unit,191 component,501 integration including real PostgreSQL; zero failures and one expected platform skip. Eleven standalone diagnostic-audit checks passed.
- Requested **Astra High** and **Claude Opus 5.5 XHigh through CLI** reviews completed. Retained raw findings and final fix/adjudication artifacts are bound to the image source; no open P0–P2 findings remain.
- Local Trivy/NuGet checks found zero vulnerabilities; Trivy also found zero secrets/misconfigurations. Syft generated a 488-component CycloneDX 1.7 SBOM. Hosted workflows remained disabled.
- The actual86,648,057-byte pre-upgrade dump was restored into isolated PostgreSQL. The exact image passed seven schema 4 compatibility checks: all 16 public table counts, migration history1–4, preference fingerprints and the2,531 item-record fingerprint were unchanged.
- Hardened image smoke passed readiness/source/assets, Brussels timezone/DST/aliases, nightly planner and checkpoint CLI checks. Installed direct/private HTTP checks all returned200; accepted source/image/schema and runtime hardening matched.
- Post-exercise health recorded **34,428 written events, zero dropped events, zero writer/flush/stall failures and zero operational logging failures**. The bounded logger was not saturated.

Remaining external restrictions are Trakt403, Watchmode429 and OMDb's persisted retry deadline. The app preserves useful data and reports partial coverage/deferred enrichment; these tests do not establish that those provider accounts have recovered.

The185,451,008-byte archive has SHA256 `1de8b3e7d981573c23f6503e53c90f02eec712de63acce0d9b997616d5896c73`. Retained artifacts are under `\\NAS\Docker\PremiereCalendar\releases\1.2.14`; canonical activation/reapply/rollback instructions are under `\\NAS\Data\Backups\Infrastructure\NAS\services\premierecalendar\README.md`. Raw configuration and credentials remain outside the evidence bundle in Infrastructure-Secrets.

For subsequent sessions use [the automatic cache diagnostics and audit commands](CacheFreshnessDiagnostics.md). Keep an explicit UTC window, process identity and logging-health snapshot with every saved result. The release includes the exact per-load/job/service attribution, snapshot provenance, before/after clocks, review dispositions and local gate timings for later investigation.
