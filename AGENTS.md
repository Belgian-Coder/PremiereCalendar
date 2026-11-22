# Build and release policy

- Run validation, tests, builds, CI, vulnerability scans, SBOM generation and
  release packaging locally on PC-MARC, WINDOWS11-VM or Mac ARM where the lane is
  supported. Never use GitHub Actions, including self-hosted runners or manual
  dispatches, to execute or orchestrate these jobs.
- Build release artifacts locally and upload them from the local builder only
  after the existing acceptance gates and separate publication authorization.
  GitHub may host source and artifacts; it must not execute the work.
- The former CI and Container workflow entrypoints are removed. Do not recreate
  build, test, validation or release workflows, including dispatch-only wrappers.
- Preserve vulnerability checks and runtime verification when publishing locally.
- Deploy digest-pinned images and preserve PostgreSQL, application data and backups.
- The scripted local release (backup, build, scans, tests, smoke, load, deploy, verify, close) is in `eng/release` and documented in `docs/Releasing.md`.

Normal flow: `pwsh -NoProfile -File ./eng/Invoke-Validation.ps1 -Changed` runs the
static checks that cover what changed and lists (or, with `-Run` on the Windows
builder, runs) the scoped app commands; `./eng/Invoke-StaticChecks.ps1` runs the
whole portable static lane in parallel (Windows and macOS). Releases run steps 2-7
with `eng/release/Invoke-LocalGates.ps1`. Preview application work with
`pwsh -NoProfile -File ./eng/wsl-docker.ps1 check -Project App -Plan`; the full
service/browser commands are in [Testing](docs/Testing.md#local-only-execution).

# Local agent workflow

- `AGENTS.md` is the sole project instruction format for Codex and Claude Code.
  Claude Code 2.1.277 or newer loads it natively when this project and its
  ancestors contain neither `CLAUDE.md` nor `CLAUDE.local.md`; do not recreate
  those files or redundant hooks that print `AGENTS.md`. A global user
  `~/.claude/CLAUDE.md` does not disable native loading. Follow applicable nested
  instructions without changing persistent model routing.
- Keep worktrees under `.wt/` in this project. Preserve unrelated changes;
  use a dedicated branch/worktree when another task owns the primary checkout.
- Read `docs/Testing.md` and `docs/DockerAndPostgres.md` before running checks.
  The supported SDK is supplied by a prepared Linux/amd64 Docker image in WSL;
  select `-WslDistribution PC-MARC-Dev` on PC-MARC (default `Ubuntu-24.04`).
  do not install a host SDK or change WSL/Docker configuration as a fallback.
- Start with `eng/Invoke-Validation.ps1 -Changed -Plan`,
  `eng/wsl-docker.ps1 check -Project App -Plan` or
  `eng/Test-WithPostgres.ps1 -Project <suite> -Filter <expression> -Plan`.
  Plans invoke neither WSL nor Docker. Execution requires an already running
  selected WSL distribution and Docker daemon; helpers never start them implicitly.
- Tests run on xUnit.net 4 over Microsoft.Testing.Platform. `-Project` also accepts `Unit`,
  `Component`, `Integration`, `Browser` or `All` (alias `-Suite`); `-Filter` takes test-name
  substrings and `-QueryFilter` an xUnit.net query filter. Results (TRX, CTRF, log) go to
  this checkout's `artifacts/test-results/<timestamp>`.
- Use the smallest relevant project/filter for feedback. Razor/UI changes need
  component checks; navigation, layout or JS changes also need browser checks.
  This Blazor project has no separate npm frontend build.
- Keep output, reports and temporary files in this checkout's ignored
  `artifacts/`; native Development data belongs in `.local-data/`. Mutable build
  state defaults to named ext4 Docker volumes per worktree, ABI and runtime mode;
  the watcher uses separate build state. Linux packages share a project-scoped
  Linux/amd64 volume; do not mix Linux/Windows package caches. Preserve old bind caches.
  Shared package consumers also share `NUGET_SCRATCH` within that package mount;
  keep ordinary temporary/build state isolated and preserve warm caches.
  Persistent SDK CLI/plugin state is a marked `.sdk-cache` subdirectory inside
  that same isolated build storage, never another shared volume. Preserve and
  report an unmarked/foreign existing SDK cache instead of adopting it.
- Do not run build/test checks simultaneously in one worktree. The supplied
  helpers lock their shared check outputs; the development watcher has a
  separate build directory. Only one development stack can bind the default
  ports at a time. Worktree Compose names isolate containers/volumes but do not
  grant permission to reset another stack or copy its secrets/database.
- Worker budgets default to `auto` (half of the shared engine's CPUs, at least
  two) for the runner container, xUnit threads and MSBuild workers, leaving the
  other half to other tenants; PostgreSQL has its own two-CPU quota. Pass
  `-Cpus 2 -MaxThreads 2 -BuildMaxCpuCount 2` to throttle on a busy host. The
  release runner records the requested and resolved budgets; benchmark
  scheduling changes without removing tests or gates.
- Scoped feedback is not release acceptance. Preserve the complete regression,
  browser timing, vulnerability, SBOM and exact-image runtime gates documented
  in `docs/Testing.md` and `docs/DockerAndPostgres.md`. Never publish or deploy
  merely to validate a harness change. Record unrun checks and blockers plainly.
- Use `Workspace-Maintenance.ps1 -Mode Check` for hygiene. Its separate `Clean`
  mode resets build/test output and is not the routine cleanup checkpoint:
  it requires explicit permission for those exact deletions; inspect `-WhatIf`
  first. Never clean a sibling directory or remove credentials.

## Cross-host checks

- Keep editable source in native host folders. Use native PowerShell 7, Git,
  Python 3 and Bash for the static checks in `docs/Testing.md#portable-static-checks`.
  These checks require neither a .NET SDK nor a running service. On a fresh
  host, `eng/Test-Docs.ps1 -SourceOnly` explicitly reports unavailable historical
  machine-local evidence; the default documentation check remains strict.
- Application builds, PostgreSQL tests and release acceptance still use the
  supported Linux containers. Windows helpers require an explicitly selected,
  already running WSL distribution and local Linux/amd64 engine. They pin the local Docker socket, ignoring an
  inherited remote context. Never replace container acceptance with native SDK
  compilation, even when a host has the exact SDK.
- macOS ARM64 currently supports the static lane only. The WSL helpers and NAS
  release scripts are not a Mac execution backend; do not invent ARM64 release,
  signing or browser acceptance. Use an approved Windows builder for the
  existing linux/amd64 release artifact.
- Share only committed source across hosts. Keep mutable outputs, test results,
  databases, credentials and package caches on their owning host/worktree;
  keep OS/architecture caches separate. Do not copy another host's `artifacts`,
  `.secrets`, `.local-data` or ignored prototype worktrees.

## Development workflow

Use `.wt/<task>` on a `feature/` branch for concurrent writers. Name each
writer's owned paths and choose one integrator; read-only agents may share a
checkout. Preserve unrelated index entries, working changes and local commits.
Stage explicit owned paths; never use broad add, reset, stash or clean.

Choose focused, valuable checks at meaningful checkpoints. Reuse successful
results while relevant inputs, dependencies, configuration and environment match.
Only the integrator runs combined validation once after integration; broaden
checks for unresolved risk or release requirements. Add regression tests for
valuable behavior, not trivial changes or tests that mirror implementation.
Build incrementally once; do not retry with clean builds or repeat restore,
packaging or full CI without a concrete reason. Documentation-only changes need
text/reference review and `git diff --check`, not app launches, screenshots,
builds or routine independent reviews.

Do not install automatic development validation, formatting, startup, polling,
cost-reporting or Stop hooks. Format only explicitly requested owned files while
no other writer can race the formatter. There are no fixed story quotas, model
lanes, review counts, time/cost reports or mandatory cleanup JSON receipts.
Full CI is not an automatic per-commit or per-push local requirement.
Operational approval, data compatibility, signing, safety and release gates
remain required. On PC-MARC, machine-managed elevation automatically runs shell
commands as Administrator in both Codex and Claude Code, independent of project
location. Do not add project elevation hooks or change sandbox/permission settings
as a workaround. Automatic elevation does not sandbox Administrator commands.

Preserve warm dependencies and OS/cache isolation. Remove only proven task-owned
disposable files and clean, pushed worktrees after reviewing ignored contents
and checking resolved paths stay in the project; retain unknown data, caches,
evidence and recovery branches. Never force worktree removal or sweep/prune.
