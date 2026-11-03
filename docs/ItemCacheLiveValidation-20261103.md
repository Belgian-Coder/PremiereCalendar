# Three-month database cache — installed validation, 2026-11-03

Release 1.2.13, source `53ebe855254f849811b1a954b0913f53a788bce7`, is installed on NAS Portainer stack 20 revision 12. App `5208cac18220d423908b4970fb2b301ded459c9fcbbc78722427857363d186ec` started at 14:45:43.192881471 UTC. Its configuration digest is `sha256:18d88fa50ec03ccc57c84a2a36169bc6b45952351f94911484285564be7b2e0f`. PostgreSQL and backup container identities/start times, application data, networks and backup policy remained unchanged.

The installed configuration enables database item caching with three calendar months of retention since last use, seven-day metadata fallback refresh and 24-hour external-score refresh. Change feeds invalidate matching metadata earlier. Failure cooldowns preserve good payloads and success timestamps. The full-week file lifetime remains 30 hours; image bytes remain on disk. These are distinct lifetimes.

## Database and migration evidence

Startup scanned 44 matching recent/default week files containing 2,511 rows and imported 2,472 distinct media/ID records; the import operation took 741 ms. The initial database count was 1,721 movies and 751 series. Later normal background work raised the count to 2,500 records: 1,721 movies and 779 series. These are retained records, not the number of premieres in one week.

The latest pre-upgrade dump, `premierecalendar-20261103T142434Z-30106.dump`, is 86,658,409 bytes, SHA256 `10aad74516c3c3bba00746373738fe19769689cd95d7aeb3a27f6aced025c29b`. It passed backup checks, was streamed privately into isolated PostgreSQL, and was migrated by the final image. All six preservation checks passed: schema 3 before/schema 4 after, migration history 1–3 retained, existing table counts retained, preference counts/fingerprints retained, and the new item table empty before backfill. No raw database payload was copied into the release artifacts. The restricted stack/source/native Portainer backup has 244 entries under `20261103T142447Z-before-1.2.13`.

## Browser and server observations

The saved English/Dutch selections and movie runtime 45–360 minutes remained unchanged. Series still uses first-debut mode. No filters were saved or modified during acceptance; date navigation alone selected the next week. Browser warnings/errors after the post-install reload: 0.

| Visit | Browser observation | Server page completion | Rows | Refresh job |
| --- | ---: | ---: | ---: | --- |
| Series first | 448 ms to rows | 31.5 ms | 31 | none |
| Series repeat | 442 ms | 28.8 ms | 31 | none |
| Movies first | 333 ms to cached rows | 17.756 s including enrichment | 64 | 17.703 s active pipeline |
| Movies next week | 290 ms | 57.1 ms | 86 | none |
| Movies repeat | 433 ms | 44.9 ms | 64 | none |

Browser measurements include automation overhead. The first movie update marker was observed absent by 35.499 seconds; that is an upper bound containing observer delay, not a second server measurement. Cached content was visible while enrichment continued. The repeat/direct loads attached no refresh job; unrelated background work continued in the process.

The first movie job recorded **93 status-coded HTTP responses**: OMDb 31×200; Rotten Tomatoes 55×200 and 2×404; Wikimedia 4×200; Watchmode 1×429. No TMDb, image, Fanart or TheTVDB responses were attributed to that job. Another 106 Watchmode failed attempts had no observed response headers/status; the logs do not establish whether those attempts reached the network. They must not be counted as confirmed downloads or added to the response count.

The full captured process instance contains 28,000 parseable performance records and 1,361 status-coded responses, including background work. Latest health reported 0 dropped events, 0 writer failures and 0 operational logging failures. Detailed prepare/read/write/reconcile, provider, source and job timings are recorded automatically; overlapping stage durations must not be summed as wall time.

## Nightly cache and comparison limits

The existing November/December cohort retained 20/20 complete week/profile files, 20 receipts and 657 rows including boundary weeks. The actual scheduled run on the former version completed at 02:15 local time, 15.956 seconds after enqueue, using retained caches and no charged retries.

Immediately after activation all 20 membership digests matched. A subsequent normal background refresh republished the October 26–November 1 Series week with 68 rows; its membership/date digest changed while its row count stayed 68. Nineteen other digests still matched at the saved cutoff. The refreshed job completed in 25.130 seconds. The digest does not establish which identities changed or why, so preservation is claimed for database/user data and observed row/receipt totals, not an immutable upstream catalogue.

These observations use retained caches. They do not establish cold-provider performance, production percentiles or a controlled version-to-version speedup. Watchmode rate limits and the observed Trakt 403 responses remain external-service limitations.

## Release checks

Local validation passed **1,229 tests**: 650 unit, 191 component and 388 integration, including actual PostgreSQL, plus one expected Linux platform skip. Independent Astra High review has no open P0–P2 findings. Trivy 0.74.0 reported zero vulnerabilities, secrets and misconfigurations; NuGet audit reported no vulnerable packages. Syft 1.51.0 generated a 488-component CycloneDX 1.7 SBOM. Exact-image hardened runtime, schema/source/readiness/assets, Brussels timezone/DST/aliases, nightly planner scheduling and checkpoint CLI checks passed. Hosted workflows remained disabled.

The earlier unactivated `f917595` candidate is preserved separately. The activation API succeeded at stack revision 12; an outer shell check then reported a stale native exit code. Independent installed checks confirmed the accepted source/image/schema and preserved containers. No second activation or rollback was performed.

The immutable release bundle includes the sanitized live JSON/report, browser evidence, test results, image identity/security reports, migration preservation report and checksums. Operational configuration and recovery are documented in the NAS-owned service runbook.
