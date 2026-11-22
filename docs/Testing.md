# Testing

## Local-only execution

Validation, tests, builds and CI run locally on PC-MARC, WINDOWS11-VM or Mac ARM
where the lane is supported. Never execute or orchestrate them with GitHub Actions,
including self-hosted runners or manual dispatches. The former workflow
entrypoints are removed; source pushes do not request remote validation.

From a PowerShell 7 session in the native checkout, run source checks with:

```powershell
pwsh -NoProfile -File ./eng/Test-ScriptSyntax.ps1
pwsh -NoProfile -File ./eng/Test-Docs.ps1
pwsh -NoProfile -File ./tests/Harness.Tests.ps1
```

Pass `-PythonExecutable` to the syntax checker when the existing Python is not on
PATH. The portable-static lane below applies to Mac ARM; application acceptance
still requires the supported local Windows/WSL Linux/amd64 builder. Select the
already running distribution (`PC-MARC-Dev` on PC-MARC, `Ubuntu-24.04` on
WINDOWS11-VM). Preview first, then run the service suites and all browser cases
sequentially when execution is allocated:

```powershell
$distro = 'PC-MARC-Dev'
& ./eng/wsl-docker.ps1 check -Project App -WslDistribution $distro -Plan
& ./eng/Test-WithPostgres.ps1 -Project Unit,Component,Integration -WslDistribution $distro
# Set $browserImage to the approved, already prepared local Linux/amd64 browser image.
& ./eng/Test-WithPostgres.ps1 -Project Browser -WslDistribution $distro -DevImage $browserImage -CacheFlavor glibc -TimeoutSeconds 2400
```

Both test commands retain restore/build validation, isolated PostgreSQL, TRX,
CTRF, logs and browser failure evidence under this checkout's artifacts. They do
not install a host SDK or Chromium. The prepared browser image supplies the
browser tooling. Filters, old-source results and static checks never substitute
for the complete source-bound release gates in [Releasing](Releasing.md).

Normal automated tests must not call live external providers. TMDb, OMDb, TVmaze, Fanart.tv, Trakt, Watchmode, SIMKL, TheTVDB, Wikimedia, Sonarr, and Radarr behavior must be covered with fake HTTP handlers, test doubles, and JSON fixtures.

Supported application acceptance uses the SDK pinned by `global.json` in the Linux development image inside Ubuntu 24.04 on WSL, through the script described under [Run](#run). An installed native SDK does not replace that container qualification. The runner is xUnit.net 4.0.3 (package `xunit.v3`) on Microsoft.Testing.Platform v2; the test projects are executables and `Microsoft.NET.Test.Sdk` and `coverlet.collector` are no longer used (Playwright uses `Microsoft.Playwright.Xunit.v3`). For acceptance that includes browser timing budgets, run the suites one at a time; running the memory/CPU-intensive service fixtures concurrently with Playwright can distort those timings. Keep all existing assertions unchanged. See [speed and reliability validation](SpeedReliabilityValidation.md) for the measured workload, PostgreSQL and container evidence, and review results.

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
- Image cache host validation, disk reuse, forced refresh behavior, and separately keyed JPEG/WebP width variants.
- Response compression negotiation for zstd, Brotli, and gzip HTML responses.
- App host smoke checks.

Component tests cover Blazor rendering:

- Seven day buttons, one mounted selected day, empty-day states, selected-day switching, and the measured two-way window that keeps at most 40 cards mounted while restoring earlier batches.
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
- Settings source health, score backfill, and missing-ID repair rendering.
- Mobile filter drawer review text without reintroducing noisy header filter chips.

## Run

The selected WSL distribution and its local Linux/amd64 Docker Engine must already be running, and the development image must exist. Select `-WslDistribution PC-MARC-Dev` on PC-MARC; the compatibility default is `Ubuntu-24.04`. Helpers never start the distribution or daemon, install tools, pull images or build them implicitly (build the image once with the explicit `wsl-docker.ps1 build` action). All commands work from any folder; each script works on its containing checkout, so use the worktree's own script. Add `-Plan` to print the selected projects, arguments, storage profile and output paths without invoking WSL or Docker.

`-TimeoutSeconds` sets the total helper budget, including WSL execution, guest
cleanup and output drain. By default it scales with the selected suites: 600
seconds plus 900 per Unit/Component/Integration suite and 1800 per Browser suite,
at most 7200 (3300 seconds for the default three suites); an explicit value wins.
The earlier fixed 1800-second default could not fit a full run on a loaded host. Browser qualification
explicitly passes `-TimeoutSeconds 2400` to retain its 2400-second bound.
`wsl-docker.ps1` and `Invoke-ReleaseTests.ps1` forward this budget. The guest
deadline reserves 15 seconds for termination/cleanup (120 seconds for the test
runner, whose six bounded cleanup Docker calls get 18 seconds each because a
loaded shared engine was measured at 19 seconds per `docker inspect`) and five
seconds for output drain within the total. Input stays on stdin; both output streams drain concurrently.
Cancellation stops only the owned Windows client, never a distro or shared WSL
host. It does not prove that Linux processes or Docker resources stopped.

Disposable test containers and their network carry a unique
`premierecalendar.test` label. Cleanup verifies immutable IDs and that label,
bounds each Docker call, and prints `PC_TEST_CLEANUP` dispositions. A cleanup
failure preserves an earlier test failure; otherwise it makes the run fail.
If cleanup is unresolved, preserve the log and inspect the exact recorded
resources before another run. Do not infer cleanup from Windows process exit.

Development and release text calls share `eng/Wsl.Transport.ps1`. Release Docker
arguments use `ArgumentList` and the explicit local socket; SQL text travels on
stdin. JSON reads stdout only, while stderr stays separate. Combined UTF-8 logs
are opened with `CreateNew` and drain continuously; existing evidence is never
overwritten. Each pipe retains incomplete Unicode pairs until the next chunk,
so interleaved output cannot corrupt supplementary characters. A wrapper error
or transport/input/drain deadline throws even during an existence
probe, so uncertainty cannot be interpreted as an available resource name.

Release WSL calls share a monotonic step budget: 120 seconds for candidate
initialization, 900 for compatibility/smoke, and 1800 for other release entry
points. Poll loops also cap each child call within their attempts/delay budget;
existence probes have a 30-second cap. Guest termination and output drain are
reserved inside each call. Outer stage watchdogs still bound non-WSL work and
total completion, and must report retained clients/resources on failure.
Binary `Restore-Backup.ps1` keeps its separate 900-second streaming contract;
it is not converted to text or claimed to use this cancellation/drain path.

```powershell
# Preview only (no WSL/Docker): what would run and where the output goes
pwsh -File D:\Projects\PremiereCalendar\eng\Test-WithPostgres.ps1 -Project Unit -Filter ViewSync -Plan

# Build the development image once (it is also used by the test script)
pwsh -File D:\Projects\PremiereCalendar\eng\wsl-docker.ps1 build

# Unit + Component + Integration
pwsh -File D:\Projects\PremiereCalendar\eng\Test-WithPostgres.ps1

# One suite (Unit | Component | Integration | Browser | All, or UnitTests ...; a comma list is allowed; -Suite is an alias of -Project)
pwsh -File D:\Projects\PremiereCalendar\eng\Test-WithPostgres.ps1 -Project Unit

# A subset: substrings of the full test name, combined with OR (FullyQualifiedName~X is still accepted)
pwsh -File D:\Projects\PremiereCalendar\eng\Test-WithPostgres.ps1 -Project Unit,Component -Filter ViewSync,Wikimedia

# An xUnit.net query filter
pwsh -File D:\Projects\PremiereCalendar\eng\Test-WithPostgres.ps1 -Project Integration -QueryFilter '/*/*/PostgresResponseCacheTests/*'

# Compile only (no PostgreSQL): the app, or one test project
pwsh -File D:\Projects\PremiereCalendar\eng\wsl-docker.ps1 check -Project App
```

Every test runs against PostgreSQL (SQLite no longer exists). In one WSL shell the script takes this checkout's check lock, starts a throwaway `postgres` container (the digest-pinned image from `compose.yaml`, data on tmpfs, `max_connections=300`) on a private Docker network with an explicit `10.141.x.0/24` subnet, builds the requested projects inside `premierecalendar-dev-app:latest` with ONE MSBuild invocation (a solution filter generated in the container's `/tmp` lists the selected test projects and the application, so MSBuild start-up, restore and the application's up-to-date check happen once; if that combined build fails, each project is rebuilt separately so the failure is attributed per suite), runs each test executable directly (faster than `dotnet test`) and removes its own server, runner and network on exit, however it exits. PostgreSQL starts first and overlaps the build: the runner container is started right after it, the host polls the server every 0.2 s (bounded to 180 s) and signals readiness through a marker file the runner waits for before its first suite. `-Cpus`, `-MaxThreads` and `-BuildMaxCpuCount` default to `auto`: half of the engine's CPUs (`nproc` in the WSL guest, at least 2, at most 64), resolved before any container starts and printed as a `PC_TEST_BUDGET` line; explicit numbers win. `-TimeoutSeconds` defaults to 600 s plus 900 s per Unit/Component/Integration suite (1800 s per Browser suite), at most 7200 s, and an explicit value wins. Other switches: `-NoBuild`, `-NoRestore`, `-FreshBuild`, `-NoRuntimeAsync`, `-Cpus`, `-Parallel`, `-MaxThreads` and `-RunnerArguments` (see the script's built-in help). `wsl-docker.ps1 test` forwards `-Project` and `-Filter` to the script; it does not build the image. The release process runs the same script and records the outcome (see [Releasing](Releasing.md)).

Results go to this checkout's ignored `artifacts\test-results\<yyyyMMdd-HHmmss>\<Suite>.trx`, `<Suite>.ctrf.json` and `<Suite>.log`, and the run ends with a `Results:` line naming that folder. All requested suites run even when an earlier one fails; the script prints a per-suite summary and exits non-zero if any failed or if a filter matched no test at all. Test credentials are generated per run and passed over stdin and inherited container environment; the helper writes no secret-bearing files to WSL `/tmp`, and no development or production database is used.

Plain `dotnet test` also works, because `global.json` selects the Microsoft.Testing.Platform runner, for example inside the development container: `dotnet test tests/PremiereCalendar.UnitTests/PremiereCalendar.UnitTests.csproj -c Release --filter-class '*ViewSync*'`. It needs `PREMIERECALENDAR_TEST_POSTGRES` (below); the script sets everything up for you.

To run against your own server instead, set `PREMIERECALENDAR_TEST_POSTGRES` to an Npgsql connection string (its database is only used for `CREATE DATABASE`/`DROP DATABASE`) and, when the server needs a password, `PREMIERECALENDAR_TEST_POSTGRES_PASSWORD_FILE` to a file containing it. The development Compose app container already sets both variables for the development database. Use a dedicated test server with `max_connections` of at least 300: tests run in parallel and every test database is cloned from a migrated template database that each test process creates once (about 67 ms per test instead of about 795 ms); `tests/Shared/PostgresTestDatabase.cs` creates the clone and drops it with `DROP DATABASE ... WITH (FORCE)`.

Every test case runs in parallel (`ParallelMode.All`, see `tests/*/AssemblyConfiguration.cs`). `[Collection]` alone does not serialize anything in this mode: a test opts out with `[Fact/Theory(DisableParallelization = true)]` and a group with `[CollectionDefinition(..., DisableParallelization = true)]`, and those tests then run one after the other in a non-parallel phase after the parallel tests. The existing opt-outs cover ImageMagick (collection), telemetry listeners, the 2,000-item load benchmark and the thread-blocking Wikimedia cancellation-race test. The Wikimedia and IMDb batcher tests no longer need serial execution: their batches are formed by occupied slots and gates, never by the 10 ms coalescing window.

Hosting tests (`WebApplicationFactory<Program>`, each with a migrated database) are the most expensive fixtures, so a class starts one host through an `IClassFixture` for all of its read-only cases instead of one per test or theory row (`WebApplicationFactoryTests`, `CachedImageEndpointTests`, `ProviderHttpTransportConfigurationTests`). `CachedImageEndpointTests` registers a stub image cache per source URL on the shared host, so cases cannot see each other's stubs. A test that needs its own configuration or stops its host (`StoppingTheHostDisposesItsDatabaseDataSources`) builds a private host; keep that pattern for new hosting tests.

## Scoped feedback and output ownership

### Portable static checks

Run these from the native checkout on WINDOWS11-VM, PC-MARC or MacBook-Pro
ARM64 with PowerShell 7 and Git. Python 3.10 or newer is required for the Python
checks; Bash (Git Bash on Windows) enables the intercepted shell lifecycle checks.
No SDK, WSL, Docker daemon, credentials or database is required. The scripts
work by absolute path from any folder and have no Windows-only dependency.

#### One command

```powershell
# Everything the portable lane covers, concurrently, one summary (see the measurement below)
pwsh -NoProfile -File ./eng/Invoke-StaticChecks.ps1
# Only what your change can affect (docs, eng/tests scripts, app code), see below
pwsh -NoProfile -File ./eng/Invoke-Validation.ps1 -Changed
```

`eng/Invoke-StaticChecks.ps1` runs the checks in the table below as separate child
processes with up to `-ThrottleLimit` at a time (default: the logical CPU count; `1`
runs them one at a time, heaviest first). Each check's output goes to
`artifacts/static-checks/<timestamp>-<pid>/<name>.log`. It prints a per-check table
(status, seconds), the total wall time, the last 30 lines of every failing check,
and exits 1 when any check fails or exceeds `-TimeoutSeconds`. `-Only <names>` and
`-Exclude <names>` take check names (wildcards allowed), `-PythonExecutable <path>`
overrides discovery and `-Plan` prints the table without running anything.
Fixtures live in GUID-named sandboxes below `artifacts/`, so the checks do not
collide; the three checks with sub-second child-process deadlines share the
`timing` group and run one after another so they do not starve each other.

Python is chosen once by `eng/Python.Common.ps1`: the explicit
`-PythonExecutable`, otherwise the first real Python 3.10 or newer among
`python3`, then `python` on PATH. The Windows Store alias stub
(`WindowsApps\python.exe`) and older interpreters are skipped; the result is
always a single path. The same helper serves `Test-ScriptSyntax.ps1` and the
Python-using tests, so none of them needs the parameter on a normal host.

`eng/Invoke-Validation.ps1 -Changed [-Base <ref>]` classifies the paths changed
since the merge-base with `-Base` (default `origin/main`) plus staged, unstaged
and untracked files, following the table in "Scoped application checks":

| Changed paths | Static checks | Builder follow-up (Windows/WSL) |
| --- | --- | --- |
| Documentation only (`*.md`, `docs/**`) | `Docs` and `GitDiffCheck` | none |
| `eng/**`, `tests/*.ps1`, `tests/*.py`, `tests/fixtures/**` | the checks whose table row covers those files (for example `eng/run-with-sdk-cache.sh` runs `SdkCache` and `ScriptSyntax`); all checks for a file no row covers | none |
| `PremiereCalendar/**` C#/project files | applicable rows plus `GitDiffCheck` | `eng/wsl-docker.ps1 check -Project App`, then `eng/Test-WithPostgres.ps1 -Project Unit,Component,Integration` |
| `*.razor`, `*.css`, `*.js` | same | App check, then `-Project Component`; Browser checks are required before release |
| `tests/PremiereCalendar.<X>Tests/**` | same | `-Project <X>` (Browser tests are only flagged: they need the prepared browser image) |
| `Dockerfile`, `compose*.yaml`, `global.json`, `Directory.Build.props`, `PremiereCalendar.slnx`, `tests/Shared/**` | same | App check and all three service suites |

On Windows, `-Run` executes the builder commands after the static checks passed,
one after another (never concurrently in one worktree); without `-Run`, and on
macOS, they are printed as the required builder follow-up. `-All` runs the full
static lane, and `-Plan` prints the classification and commands only. The result
is scoped feedback and never release acceptance: complete suites, Browser,
vulnerability, SBOM and exact-image gates stay as documented below.

What the lane runs (names for `-Only`/`-Exclude`; the declared table is
`Get-StaticCheckTable` in `eng/StaticChecks.Common.ps1`, and
`tests/StaticChecks.Tests.ps1` fails when a `tests/*.Tests.ps1` or `test_*.py`
file is missing from it):

| Name | Command | Guards |
| --- | --- | --- |
| `Harness` | `tests/Harness.Tests.ps1` | WSL/Docker/PostgreSQL helper plans, argument and cache-volume contracts |
| `HarnessProcess` | `tests/HarnessProcess.Tests.ps1` | native child-process pipes, deadlines and cleanup |
| `ReleaseTransport` | `tests/ReleaseTransport.Tests.ps1` | release transport I/O, timeouts, no false "absent" results |
| `HarnessCacheRoot` | `tests/HarnessCacheRoot.Tests.ps1` | canonical primary/clone cache-root binding with real Git fixtures |
| `ExistingRestoreFixture` | `tests/ExistingRestoreFixture.Tests.ps1` | restore-compatibility fixture handling in the release scripts |
| `ReleaseDockerArguments` | `tests/ReleaseDockerArguments.Tests.ps1` | release Docker argv across the native process boundary |
| `ValidationTools` | `tests/ValidationTools.Tests.ps1` | `Test-Docs.ps1`, `Test-ScriptSyntax.ps1` and candidate-plan contracts |
| `SdkCache` | `tests/SdkCache.Tests.ps1` | SDK cache launcher ownership markers and refusals |
| `PerformanceLogAudit` | `tests/PerformanceLogAudit.Tests.ps1` | `eng/Read-CalendarPerformance.ps1` log summaries |
| `ReleaseOrchestrator` | `tests/ReleaseOrchestrator.Tests.ps1` | `Invoke-LocalGates.ps1` ordering, concurrency limits, stop-on-failure, bound resume |
| `ReleaseSpeed` | `tests/ReleaseSpeed.Tests.ps1` | release cache volumes, Trivy DB age gates, concurrent Trivy/Syft, Dockerfile cache mounts |
| `StaticChecks` | `tests/StaticChecks.Tests.ps1` | Python discovery, parallel runner, change classifier |
| `ScriptSyntax` | `eng/Test-ScriptSyntax.ps1` | PowerShell/Python parse, LF-only `*.py/*.sh/*.sql` |
| `Docs` | `eng/Test-Docs.ps1 -SourceOnly` | links, anchors, referenced scripts, stale statements |
| `SourceFingerprints` | `tests/test_source_fingerprints.py` | exact source/blob fingerprint verification |
| `ReviewInputs` | `tests/test_review_inputs.py` | review-input inventory and binding |
| `GitDiffCheck` | `git diff --check HEAD` (with `-Changed`: against the merge-base) | whitespace errors in staged and unstaged (and, with `-Changed`, committed) changes |

Measured on WINDOWS11-VM (12 vCPU, shared with other validation runs, so absolute times are noisy): three alternating
runs of the same 15 checks took 118.7, 125.0 and 88.5 seconds wall with the default throttle versus 370.4, 273.0 and
214.0 seconds with `-ThrottleLimit 1` (medians 118.7 versus 273.0 seconds; the first serial run matches the 362-second
baseline of the former one-command-per-check list). All six runs passed every check. The parallel floor is the slowest check
(`Harness` or `HarnessCacheRoot`, 85-110 seconds alone). A documentation-only change through
`Invoke-Validation.ps1 -Changed` took 4.1 seconds (`Docs` plus `GitDiffCheck`) and one new `eng` script 4.1 seconds
(`ScriptSyntax`, `Docs`, `GitDiffCheck`). This measures static feedback only, not application or release speed.

Each script can still be run alone with `pwsh -NoProfile -File <path>`
(`python -B <path>` for the two Python tests); pass `-PythonExecutable <path>` to
the Python-using scripts only when the default choice is not the one you want.
`tests/StaticChecks.Tests.ps1` runs no Docker or WSL.
The syntax checker compiles all Python files in one isolated interpreter process
without writing bytecode. Missing Python fails the syntax check; it never falls back to
another SDK or installs anything.

At source `dbf17a67306f44283025a87e2ea5c6af3b1f480b` on PC-MARC with Python
3.12.11, the same nine Python files compiled in 0.214855 seconds with separate
interpreters versus 0.060113 seconds in one batch (72.02% less compiler-phase
time; medians of five alternating warm samples, no cache clearing). Source
hashes stayed unchanged and malformed-file/no-bytecode regression checks passed.
This measures static compilation startup only, not application or release speed.

The source fingerprint gate also batches exact committed C# reads through one
Git process. On the same PC-MARC source `9210b3283e5aeb24c982853cdf4a6e8337cf582a`,
three alternating warm verification samples over 544 fingerprints and 401 C#
blobs had medians of 10.441452 seconds before and 0.265914 seconds after (97.45%
less source-check time). Output matched byte-for-byte; all native SHA-256,
committed blob, inventory, clean-source and LF guards remain. The batch reader
checks frame sizes, separators and object identity and rejects duplicate paths.
Its 28 regressions cover valid and malformed source/frames. This is a measured
source-verification improvement, not a 97% end-to-end build claim.

The default documentation check remains strict. `-SourceOnly` permits unavailable
machine-local evidence links only in historical records, reports their count,
and continues checking source files, anchors, maintained documents and script
references. It does not establish that the historical tests ran. Use the strict
check on the evidence-owning host when checking those links is required.

The non-Windows path of the portable lane was exercised on 22 November 2026 on
Linux (PowerShell 7.7 installed as a .NET global tool, Python 3.14, BusyBox
userland) as the closest available stand-in for macOS: three consecutive runs
of all 17 checks passed in 73-83 seconds. That run found and fixed three
portability bugs (child `pwsh` resolution for tool installs, hidden dotfiles
outside Windows, a load-sensitive pipe budget). Without GNU coreutils
`timeout` (BusyBox, stock macOS) the three real-timeout cleanup cases of
`Harness.Tests.ps1` are skipped with a warning. On the MacBook-Pro itself
(macOS 27.0.1, arm64, reached through the build bridge) the complete lane then
passed natively on the same day: all 17 checks, twice, in 14.1-14.2 seconds
with the default throttle versus 45.5 seconds with `-ThrottleLimit 1`, using
the official portable PowerShell 7.7.0 osx-arm64 archive (SHA-256 checked
against the release's `hashes.sha256`, run from a temporary folder and removed
afterwards; the Mac has no installed `pwsh`), Python 3.10.9, Git and the stock
Bash 3.2. Only the three GNU-`timeout` cases were skipped.

macOS ARM64 execution has not been qualified. Static checks are suitable for
that host, but the WSL build/test helpers and release scripts require a Windows
builder. The accepted release platform is linux/amd64; a native Mac build or an
emulated container is not independently established release acceptance. Keep
editable source native and retain writable caches/results on their owning
host and worktree, separated by OS/architecture. The existing container caches
and old WSL prototype remain in place.

### Scoped application checks

```powershell
# Check the harness itself without SDK, PostgreSQL, Docker or WSL.
pwsh -NoProfile -File D:\Projects\PremiereCalendar\tests\Harness.Tests.ps1
# Verify marked CLI/plugin cache reuse and refusal of unknown state without an SDK.
pwsh -NoProfile -File D:\Projects\PremiereCalendar\tests\SdkCache.Tests.ps1
# Run only the behavior under development, then its complete affected suite.
pwsh -File D:\Projects\PremiereCalendar\eng\Test-WithPostgres.ps1 -Project Unit -Filter ViewSync
pwsh -File D:\Projects\PremiereCalendar\eng\Test-WithPostgres.ps1 -Project Unit
```

| Change | Feedback | Acceptance before release |
| --- | --- | --- |
| Instructions/documentation only | Text/reference review and `git diff --check`; no WSL, Docker or unrelated tests | No application qualification needed for prose alone |
| Executable harness changes | Affected `Harness.Tests.ps1` cases and relevant command `-Plan` | Exercise changed execution paths in a prepared local environment; disclose anything unrun |
| Pure logic/provider/cache | Relevant unit/integration project plus a focused filter | Complete unit, component and integration suites |
| Razor/CSS/JavaScript/navigation | App build and relevant component tests; affected browser cases | Complete suites and serial browser performance/accessibility checks |
| Database, dependency, container or cross-cutting change | Affected integration/project build first | Complete regression, database and exact-image release gates |

`eng/Invoke-Validation.ps1 -Changed` applies the feedback column to the current changes (see [Portable static checks](#portable-static-checks)); the acceptance column is never implied by it.

Each checkout/worktree owns named ext4 Docker build volumes, separated by Linux/amd64 ABI (`musl` or `glibc`), runtime-async mode and check/watcher role. A project-scoped Linux/amd64 NuGet volume is shared across worktrees. Source and timestamped `artifacts/test-results/<run>` reports stay in the native checkout. Existing native output remains under `artifacts/build/host` and native packages under `artifacts/packages/host`; old Linux bind caches are preserved. Helpers retain the worktree's `artifacts/check.lock` to reject concurrent check/test writers. Do not bypass that lock with concurrent raw `dotnet` commands. Sharing packages does not share `obj`/`bin`, test databases, results or application data. See [Docker and PostgreSQL](DockerAndPostgres.md#local-development) for the profile naming and watcher mapping.

The frozen request client supplies `BUILD_RELEASE_PROJECT_ROOT` for independent
execution clones. A validated protocol owner, exact manifest-derived clone path,
native primary root and matching credential-free origin bind this hint before
any cache use. Invalid hints fail without fallback. The canonical primary selects
the shared package/HTTP/SCRATCH namespace; mutable build state remains per clone.
`HarnessCacheRoot.Tests.ps1` exercises real independent and linked Git fixtures,
refuses foreign/linked/malformed bindings, and checks primary WIP preservation.

SDK CLI state and NuGet plugin metadata persist at
`/build/.sdk-cache/dotnet` and `/build/.sdk-cache/nuget-plugins` inside that same
isolated build storage. `SdkCacheStorage` reports its `BuildStorage` and
`.sdk-cache` subdirectory; it is a view of the build volume, not another volume
or a fallback. `-FreshBuild` keeps all three paths container-local. The shared
launcher requires its exact build-storage owner marker and refuses unknown,
unmarked or linked existing SDK cache state without changing it. Preserve that
state for review; do not copy credentials, log in to plugins, or adopt a legacy
`premierecalendar-linux-*-sdk-cache` volume. Public restores stay noninteractive,
with automatic HTTPS certificate generation disabled. Ordinary temporary state
stays in each owned container, outside the shared package mount.

The earlier main layout remains historical evidence; its
`premierecalendar-linux-*` build/package/SDK volumes stay untouched. They are
never automatically adopted by the profiles above.

Storage verification on 15 November 2026: Compose configuration validated; an App check passed with zero build warnings/errors in 102.43 seconds wall time (88.66 seconds SDK build). The primary checkout used `premierecalendar-linux-0f087d370a-{build,nuget,sdk-cache}` volumes, whose mountpoints all resolved to `/var/lib/docker` on ext4. These populated volumes retained their identities across the WSL virtual-disk move. Afterwards, two identical normal `-Project Unit -Filter ViewSync` runs passed all 17 selected tests: first 77.54 seconds wall/49.14 seconds build, repeat 44.41 seconds wall/18.72 seconds build. Both kept restore validation and incremental compilation enabled; reports are in `artifacts/test-results/20261115-160712` and `artifacts/test-results/20261115-160837`. This demonstrates cache persistence and warm reuse, not a controlled comparison against the previous Windows-mounted layout. The current VirtioFS source mount emitted `mmap(/src/global.json) ... No such device` before each successful build; the existing SDK image still built and ran the checks, but that source-mount diagnostic remains unresolved. After adding shared NuGet scratch locks, 53 host-only harness checks and documentation/reference checks passed; the app/test runs were not repeated for that lock-path configuration change. Full suites, browser checks and development-stack activation were not run for this storage change.

`-NoRestore` and `-NoBuild` are explicit rerun options only after a successful matching restore/build of the same worktree, configuration, image ABI and runtime-async mode. Normal runs keep restore validation and incremental compilation enabled; `-FreshBuild` builds from scratch in a throwaway container location. `-BuildVolume`/`-NugetVolume` preserve explicit Docker cache overrides; a build-volume override must belong exclusively to that worktree/ABI/mode. A custom prepared `-DevImage` requires explicit `-CacheFlavor musl|glibc`, and the browser lane uses `glibc`. Execution checks the local image's Linux/amd64 identity and runs its immutable ID with pulls disabled. A filtered run is feedback, never proof that the complete regression suite passed.

Worker budgets default to `auto` everywhere (`Test-WithPostgres.ps1`,
`wsl-docker.ps1 check|test` and the release test step): half of the engine's CPUs
(`nproc` in the WSL guest), at least 2 and at most 64, which leaves the other half
to other tenants of the shared engine. `-Cpus`, `-MaxThreads` and
`-BuildMaxCpuCount` bound the runner container, xUnit scheduling and MSBuild
workers respectively; pass explicit numbers (for example `-Cpus 2 -MaxThreads 2
-BuildMaxCpuCount 2`) to throttle further on a busy host. PostgreSQL has its
separate two-CPU quota; suites still run serially and every selected test
executes. The release test entry point records the requested values and the
resolved `PC_TEST_BUDGET` line in its receipt, and always runs the complete unit/component/
integration roster. The request adapter maps `-Jobs` to those same budgets.
For a native Windows source checkout, the canonical release test runner also
forwards `-WslDistribution`, `-BuildVolume` and `-NugetVolume`; its receipt records
the resolved ext4 volumes and distro. Defaults isolate musl service output from
the explicitly selected glibc browser profile. Other release/NAS transports retain
their compatibility configuration.

Every supplied container consumer of a shared Linux package cache (test runner,
scoped check and Compose watcher) sets `NUGET_SCRATCH` to
`/root/.nuget/packages/.scratch` inside that same cache mount. Thus the package
downloads and extraction locks share one namespace, including a `-NugetVolume`
override. Ordinary build outputs and test/application temporary files remain
isolated. All concurrent consumers of a package cache must use this mapping.
`NUGET_HTTP_CACHE_PATH=/root/.nuget/packages/.http-cache` also retains HTTP
metadata in the same mount with NuGet's normal expiration and revalidation.
Package auditing and freshness checks remain enabled.
Do not start an older watcher with isolated NuGet locks beside a new restore.
Do not restart someone else's stack to adopt it. This follows
[NuGet's shared-cache coordination requirement](https://learn.microsoft.com/en-us/nuget/consume-packages/managing-the-global-packages-and-cache-folders).

The default helper covers unit, component and integration projects. Browser tests are opt-in (`-Project Browser -DevImage <prepared-image> -CacheFlavor glibc`): the Alpine SDK development image does not include compatible Playwright browsers. Use the established prepared browser environment, run after service tests (not alongside them), and retain all timing/assertion budgets. Never report default tests as complete browser acceptance. There is no npm toolchain in this Blazor project.

The browser performance contract requires three forward window transitions and
one Previous transition, checking each completed range and every ordered canonical
ID with at most 40 mounted cards. Its unchanged 3,000 ms wall budget includes all
four actions and completion waits. This measures committed DOM state, not
completed paint or end-to-end scroll timing. Both viewports separately exercise
real downward/upward scrolling through the existing IntersectionObserver
autoload triggers, checking exact ordered progression/restoration and bounded
cards. Dispatch/DOM-completion timestamps provide separate diagnostics.
This contract retains action/network/source tracing and failure
HTML/full-page PNG, with continuous trace screenshots/DOM snapshots disabled;
console, CLS and long-task observers remain enabled. Record the capture profile
alongside results when comparing timings.

There are no skipped PostgreSQL tests: when `PREMIERECALENDAR_TEST_POSTGRES` is missing, every database-backed test fails with a message that names the variable. No test is skipped: the former Windows file-lock test was removed because the product ships only as a Linux container.

`ProviderWorkSchedulerDurabilityTests` covers checkpoint accumulation, failure exclusion, deferred work, independent lease renewal, stale-owner fencing, watcher priority, and bounded progress replay. `CalendarLoadPerformanceTests` uses the real scheduler with both workers occupied for 300 ms and 2,000 deterministic rows split across seven days. It covers cold, fresh, and stale caches. Its `PERFORMANCE` output is retained in the suite's `.log` and `.trx` files under `artifacts\test-results\<timestamp>\`.

The browser fixture deliberately replaces live providers and disables background scheduling. Its desktop/mobile rendering checks complement scheduler-enabled service tests; they do not establish PostgreSQL or live-provider acceptance. See [speed and reliability validation](SpeedReliabilityValidation.md).

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
- Release acceptance deploys the new digest-pinned image and checks `/health/ready` and `/health/version` (container, database schema and version) on both the direct URL and the private HTTPS URL; see [Releasing](Releasing.md).

## Browser Performance Checks

Dense-week UI checks use `/series?week=2026-04-20` because that week has hundreds of series rows and exercises repeated forward/backward movement of the measured card window.

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
- Unit tests cover ICS all-day dates and escaping, CSV quoting and spreadsheet-formula neutralization, JSON canonical identity, trusted-proxy defaults, and startup option validation.
- Settings checks verified the Local status center rendered and provider delta failures stay visible above routine job entries.
- Layout checks reported no horizontal overflow. The deployed first-viewport measurements were command bar `82.75px` and calendar top `209.125px` on desktop, and command bar `150.8125px` and calendar top `342.359375px` on mobile with source diagnostics visible.

Additional deployed Playwright validation on 2026-05-19 after filter URL and view-sync hardening:

- Desktop `/series?week=2026-05-11&day=2026-05-17&seriesScope=new&seriesLang=en,nl` rendered badge `2`, no horizontal overflow, no console errors, and preserved the two-card desktop grid.
- Desktop `/movies?week=2026-05-11&movieScope=new&movieLang=en` rendered badge `1`, proving unsupported `movieScope` no longer inflates the count.
- Mobile `390x844` `/series?week=2026-05-11&day=2026-05-17&seriesScope=new&seriesLang=en,nl` rendered badge `2`, no horizontal overflow, and no console errors.
- Post-deploy health check returned `200 Healthy`; the then-current Windows service deployment was `Running` with `Automatic` start type (that deployment has since been removed).
- Regression tests now cover view-sync overview failures so optional sync storage issues do not break Calendar or Settings rendering.

Historical automated validation on 2026-05-31 after data-trust diagnostics (run directly on the Windows host, which is no longer possible; see [Run](#run) for the current way):

- A full solution test run passed 518 tests.
- New tests cover date semantics, source merge attribution, anomaly detection, score backfill, missing external-ID repair, source health, update evidence, rollback script behavior, provenance rendering, Settings diagnostics, and mobile filter review.
- Deployed Playwright validation covered desktop `1440x1000` All, Series, Movies, card provenance, Settings diagnostics, command palette, and filter drawer, plus iPhone `390x844` Series, Movies, card provenance, Settings diagnostics, source health, and filter drawer.
- Browser validation reported no console warnings or errors, no horizontal overflow, no visible command clipping, filter panes within the viewport, and two-column desktop card grids.


## Cold discovery and OMDb retry validation â€” 7 November 2026

1,905 tests pass: 1,118 unit, 195 component and 592 integration, with one expected
Linux file-lock skip. PostgreSQL coverage executes in isolated databases.
An earlier integration-container bootstrap directory required a writable tmpfs;
its 581-case replay used unchanged compiled DLLs. The final 592-case suite includes
post-review regressions and passes with that container correction. Earlier runs
and hashes remain separate; only final accepted cases contribute to this total.

Held-provider checks cover complete pagination before optional work finishes,
source checkpoint ownership, cancellation, canonical invalidation, accepted
projection preservation and queued admission deadlines. A gated concurrent-week
case reproduces and protects the calculation-to-persistence sharing interval.
OMDb cases cover persisted per-ID deadlines, exact expiry, stale-value retention,
credential/endpoint isolation and forced-success races.

Matched results and their limits are recorded in
[Cold discovery and OMDb recovery](ColdDiscoveryAndOmdb-20261107.md).

## v1.2.18 final validation - 12 November 2026

2,002 tests pass on the exact release source: 1,185 unit, 225 component and 592
integration, with one expected Linux file-lock skip. The suites ran once, in
order, in isolated containers with an internal tmpfs-only PostgreSQL server.

## v1.2.19 final validation - 12 November 2026

2,183 tests pass on the exact release source: 1,283 unit, 271 component and 629
integration, with one expected Linux file-lock skip, run once in isolated
containers with an internal tmpfs-only PostgreSQL server. Wall-clock-sensitive
tests must not assume an idle host: start timers before the first measured
event and give storage budgets an explicit generous value.
