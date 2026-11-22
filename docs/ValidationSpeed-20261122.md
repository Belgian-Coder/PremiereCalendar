# Validation, test, build and release speed - 22 November 2026

Measured on WINDOWS11-VM (Windows 11, Ubuntu-24.04 WSL Docker engine with 12 CPUs) while
other projects used the same engine, so single numbers are noisy; comparisons are
alternating runs or duration sums from CTRF where stated. macOS ARM64 runs the same
portable static lane (`eng/Invoke-StaticChecks.ps1`) and was not measured here.

| # | Iteration | Before | After |
| --- | --- | --- | --- |
| 1 | Portable static lane as one parallel command (`eng/Invoke-StaticChecks.ps1`), shared Python discovery (three checks failed on hosts with several Pythons on PATH) | 362 s serial, 3 failing checks | 42-120 s for 17 checks, all passing |
| 2 | Change-aware normal flow (`eng/Invoke-Validation.ps1 -Changed`) | full lane for every change | about 4 s for a docs-only or single-script change |
| 3 | Faster harness tests (shared Git fixtures, concurrent independent groups, worker threads) | Harness 60 s, HarnessCacheRoot 57 s, fingerprints 20 s | 23 s, 38 s, 5 s (medians) |
| 4 | Test runner: one MSBuild pass for all suites, PostgreSQL start overlapping the build, `auto` worker budgets, suite-scaled deadline, cleanup tolerant of a loaded engine | no-op builds 296 s; the default run did not finish in its 1800 s budget and leaked containers under load | no-op builds 128 s; full run 709-1037 s with clean cleanup |
| 5 | Service suites: one web host per integration class, smaller fixtures and synthetic delays, 5 obsolete/redundant tests removed | Integration duration sum 744 s, Unit 583 s | 373 s and 505 s |
| 6 | Deterministic batch-window tests; `[Collection]` replaced by effective `DisableParallelization` where state is shared | intermittent Wikimedia/IMDb/Calendar failures under load | repeated runs green, including `-MaxThreads 12` |
| 7 | Release steps: DB-only Trivy cache with currency gates, NuGet package cache, BuildKit restore cache, Trivy and Syft concurrently | scan 370 s, NuGet restore 20 s, restore after a csproj change 88 s | scan 71-86 s in the release, restore 4 s, 25-45 s |
| 8 | Release steps 2-7 in one command (`eng/release/Invoke-LocalGates.ps1`) with safe concurrency and `gates/stage-timings.json` | steps run one by one | 1.2.20: 944 s wall for 1,702 s of step time |

Necessity review: removed the Windows-only file-lock test (product is Linux-container
only), a duplicated 4,000-item checkpoint row, a JavaScript source-text grep, two
framework-equivalence checks, duplicated harness text assertions and two fixture
self-exit assertions. No vulnerability, SBOM, exact-image, backup-compatibility or
source-binding gate was removed; several were tightened (Trivy DB download age,
NU1900-NU1905 in every gate, bound resume evidence, deploy refusal of SQLite settings).

Follow-ups not done: calendar autoload re-pump after a pending window move, a
per-step deadline in `Invoke-LocalGates.ps1`, a broader null-splat lint, recording an
`-IgnoreUnfixed` policy in `release-config.json`, a scram/password-file smoke variant
and a resumable NAS copy in `Close-Release.ps1`.
