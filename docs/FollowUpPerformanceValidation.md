# Follow-up speed and reliability validation

This change builds on the installed 1.2.3 source at `15db3c4`. This document records the earlier local candidate before the movie-load investigation. Combined validation and subsequent delivery are recorded in [Movie load diagnostics](MovieLoadDiagnostics.md). No GitHub Actions are used.

## Behavior

- Score and external-ID maintenance merges only its changed fields into the current cache generation. New discoveries, removals, newer conflicting metadata, discovery age, and completeness survive. Remote enrichment runs outside the publication lock. Identity changes prevent old ratings being attached to a different title.
- Calendar cache snapshots return rows and freshness/completeness together. Production file caches deserialize once per read, including expired files; existing item-only adapters remain compatible.
- IMDb imports allow foreground reads of the previous committed dataset. PostgreSQL uses streamed binary COPY into a temporary table, followed by transactional ratings/state publication. Duplicate IDs retain last-value semantics. SQLite keeps WAL and transactional upserts. Bulk PostgreSQL publication has a 600-second command timeout; cancellation remains effective.
- Progressive merging reuses unchanged rows contributed by a single source and computes the immutable stale seed once. Cross-source precedence, attribution, removals, corrections, and reconciliation remain intact. Broader incremental UI indexes are deferred pending profiling.
- After its foreground presentation budget, the page watches the original stream for up to five more minutes. Completion updates the current page without a second load/job. Navigation and disposal cancel that subscription; cached cards survive a refresh failure. Observation expiry explicitly stops UI progress without claiming providers completed.
- Reconnect recovery retries every five seconds for up to twelve attempts while visible and online. Online/visibility events can start a new recovery window. Successful recovery removes timers/listeners; in-flight guards prevent overlapping reconnect calls.
- Scheduler progress persists one canonical checkpoint payload. The existing unused `ProgressJson` column remains for schema compatibility and is cleared on progress writes. Replay still reads `CheckpointJson`.

## Controlled measurements

The existing Windows Debug fixture returns 2,000 movie rows across seven days, with both scheduler workers initially occupied for 300 ms and fake providers. Two baseline runs used an immutable archive of `15db3c4`; two candidate runs used the same SDK and fixture. These are individual runs, not production percentiles or a statistical latency benchmark.

| Scenario | Baseline first result ms | Candidate first result ms | Baseline completion ms | Candidate completion ms | Baseline allocated bytes | Candidate allocated bytes |
|---|---:|---:|---:|---:|---:|---:|
| Fresh | 100–125 | 121–137 | 100–125 | 121–137 | 14.15 million | 14.15–14.16 million |
| Stale | 37–55 | 41–62 | 5,659–5,686 | 3,477–4,138 | 576.58–576.62 million | 419.88–419.89 million |
| Cold | 376–420 | 385–434 | 3,150–3,309 | 2,389–2,537 | 228.12–232.44 million | 202.90–216.67 million |

Stale allocations decreased about 27%; cold allocations decreased about 7–11% in the paired runs. These counters are cumulative process-wide allocations, not retained heap or peak memory. First-result timings do not establish an improvement. Completion was faster in these two candidate runs, but host activity and scheduling affect the results; no production latency claim follows from this fixture.

All runs returned all 2,000 rows. Fresh hits created zero additional provider jobs, made zero discovery calls, and performed zero job/lease/checkpoint row mutations. Stale/cold retained seven discovery calls and 18/16/11 job/lease/checkpoint mutations. Those are SQLite row mutations, not physical PostgreSQL I/O. The file-cache single-read improvement is tested separately; this fixture uses an in-memory cache adapter.

A separate Release experiment streamed 1,700,000 generated IMDb ratings into isolated PostgreSQL 18. Accepted, stored, and published state counts matched. Import took 23.237 seconds while 202 foreground lookup rounds continued against coherent committed snapshots. Single-ID lookup p50/p95/max was 6.066/11.528/121.875 ms; 925-ID batched lookup was 77.756/99.037/110.103 ms. Total process allocation delta was 307,009,768 bytes, including generated records and concurrent lookups. There is no before-import baseline, and other validation workloads were active on the host.

Raw logs and immutable baseline are retained in `D:\Projects\PremiereCalendar.Build.NextValidation`; the generated import benchmark source and `result.json` are in `D:\Projects\PremiereCalendar.Build.NextImdbBenchmark`. Neither directory is part of the repository.

## Acceptance and review

Windows acceptance passed **777 tests across final runs**: 400 unit, 205 integration, 166 component and six browser cases. Integration includes seven tests against real isolated PostgreSQL 18 databases. Browser checks retain the day-switch, card-window, DOM, overflow, accessibility and console budgets, and add actual-script reconnect tests. Component coverage includes late completion, old-route suppression, disposal, refresh failure, cooldown, actual background observation expiry, and final results awaiting slow metadata I/O. The optional `CalendarLoad:BackgroundCompletionBudgetSeconds` defaults to 300 and is clamped to 1–300 seconds.

Final Linux Release acceptance passed **770 tests with one expected Windows-only file-lock skip**: 399 unit passes, 205 integration and 166 component. An earlier run could not locate the repository for the CSS source test because artifacts were outside its ancestry; mounting the isolated source at the expected sibling path fixed the harness without changing the test or application.

The combined delivery run, concurrent with Linux compilation and container export, had two Windows failures: a forced browser load-more click moved outside the viewport, and the existing cross-process lock test's PowerShell helper missed its five-second readiness deadline. These failures remain in `delivery-windows.log`. Without changing assertions or budgets, the complete unit and browser suites then passed separately in `delivery-unit-isolated.log` and `delivery-browser-isolated.log`. The final browser run measured mobile/desktop first usable cards at 1,792/739 ms and day switching at 78/75 ms; documents were preserved. The forced-click/startup timing sensitivity remains a test-harness limitation under host contention.

The local container candidate is `premierecalendar:followup-validation`, image ID `sha256:681652393c622c4414cbfadb425f43f83c9d5d895874a275128cfda095db92f9`. It passed readiness/version, root HTML and Blazor JavaScript checks with PostgreSQL schema 3, UID 1654 and a read-only root filesystem. The final Blazor response was 245,494 bytes. NuGet audit found no known vulnerable dependencies; the final Trivy gate found zero fixable high/critical vulnerabilities. A CycloneDX SBOM is retained with the scan results. No image was published or installed.

Astra high identified two issues during independent review: singleton reuse could change ambiguous source attribution by skipping dictionary mutations, and expired background observations could leave active progress. The fixes preserve baseline mutation order while reusing merged objects and explicitly mark stopped observations without claiming provider completion. Claude's full review found a further status race when final results preceded slow metadata writes; handoff now releases loading state without restarting a completed refresh's status/timer. A defensive dirty-ID guard also prevents stale attribution if future callers batch source replacements. Regressions cover all corrections, including reproducing both status branches before the fix.

Final focused re-reviews from Astra high and fresh read-only Claude CLI `--model claude-opus-5-5 --effort high` sessions found no remaining actionable defects. Claude's recorded model usage confirms `claude-opus-5-5`; its final review inspected the regression logs and correctly required rerunning the two failed Windows suites. Those reruns and the pending Linux unit summary subsequently passed. Reports are `astra-review.md`, `claude-final-review.json` and `claude-delivery-review.json` in the validation directory.

The reviewed delivery diff artifact is `delivery-candidate.diff`, SHA-256 `98e9f3c16f5d7b35039f1d10bcd2b97599e0c0ed9c038305cb83018b1d71ac61`. The immutable delivery source archive is `delivery-source.tar`, SHA-256 `aed8237db451d7c2013e5a081a91b7668dcc6f823f5696f56e0166ebdf806cd7`. Acceptance-report text was completed after that source snapshot; product and test files are unchanged.

Production p50/p95, NAS load, and real provider outage acceptance remain separate from these controlled checks. Existing telemetry already records calendar first/completion timings, scheduler waits, provider behavior, and circuit events; use those instruments rather than duplicate metrics when collecting production evidence. Temporary app/PostgreSQL containers and the test password file are removed after acceptance; source snapshots, candidate image, logs, review reports and SBOM remain available locally.
