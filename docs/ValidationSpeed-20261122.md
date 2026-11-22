# Validation, test, build and release speed - 22 November 2026

Measured on WINDOWS11-VM (Windows 11, Ubuntu-24.04 WSL Docker engine with 12 CPUs) while
other projects used the same engine, so single numbers are noisy; comparisons are
alternating runs or duration sums from CTRF where stated. macOS ARM64 runs the same
portable static lane (`eng/Invoke-StaticChecks.ps1`); its non-Windows path passed three
consecutive runs on Linux pwsh 7.7 (73-83 s, see [Testing](Testing.md#portable-static-checks)),
and on the MacBook-Pro (macOS 27, arm64) all 17 checks passed natively in 14 s
(45.5 s serial) with a portable PowerShell 7.7.0; the Mac has no installed `pwsh`,
which the lane requires.

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
| 9 | Builds and tests read source from a content-hash-verified ext4 mirror instead of the virtiofs `/mnt/d` mount (the WSL disk itself lives on D:) | `git status` 10-36 s, reading all files 24.5 s, cold app build 211 s, warm test build 65-240 s | 0.02-0.3 s, 0.13 s, 107 s, 15-44 s; sync 0.6-0.7 s per run (6.7 s first time) |
| 10 | Passing-result cache keyed by all source bytes, image, selection and configuration (feedback runs only: the release test step always runs with `-NoCache`, so its `reusedResults` is false by construction; also keyed by effective `-MaxThreads` and UTC date, never used with `-NoBuild`, `-FreshBuild` or `-SourceMount Windows`) | identical rerun of the three suites: full run again | 11 s, no container |
| 11 | Test impact selection in `Invoke-Validation -Changed` (test classes that reference the changed types, safe fallbacks) | every app change ran all three suites | e.g. one Component change selected ~48 of 275 tests; wiring changes still run whole suites |
| 12 | PostgreSQL CPU quota scales with the runner budget; longer test DB timeouts; two load flakes removed | Npgsql read timeouts under 6 threads; full three-suite run 1,037 s | full three-suite run 267 s, all 2,319 passing |

These rules are enforced, not only documented: `tests/HarnessPolicy.Tests.ps1` (static lane) fails when a script
mounts the Windows checkout as build source, when the mirror, cache, auto budget or impact defaults regress, when the
release receipt stops recording result reuse, or when AGENTS.md stops teaching them.

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

## How the work was split

Seven bounded work packages were handed to Claude Sonnet 5.5 subagents at medium
effort, one `feature/speed-*` branch and worktree each, then merged by the
integrator (merge commits `1d7bbfb` static lane, `d59f081` release orchestrator,
`0fa26da` harness tests, `53990a8` test runner, `629f3d5` service suites,
`a21e40e` release steps, `3443c69` flake hardening). The 22 commits that carry
a `Co-Authored-By: Claude Sonnet 5.5` trailer between `8f55aea` and this record
are theirs; integration, review fixes and the release are the integrator's
(Claude Opus 5.5) commits. Before the release, fresh Claude Opus 5.5 (high)
and Claude Sonnet 5.5 (xhigh) subagents reviewed the speed work and,
separately, the earlier unreleased range `c4d8637..8f55aea`; their record is
`source-review.json` in the NAS release evidence `releases/1.2.20-candidate2`.
