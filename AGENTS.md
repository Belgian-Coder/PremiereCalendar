# Build and release policy

- Do not use GitHub Actions or hosted GitHub CI/CD, including manual workflow dispatches.
- Run builds, tests, vulnerability scans, SBOM generation and container publication locally.
- Keep the GitHub CI and Container workflows disabled. Do not enable or dispatch them.
- Preserve vulnerability checks and runtime verification when publishing locally.
- Deploy digest-pinned images and preserve PostgreSQL, application data and backups.
- The scripted local release (backup, build, scans, tests, smoke, load, deploy, verify, close) is in `eng/release` and documented in `docs/Releasing.md`.

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
  The supported SDK is supplied by the existing Ubuntu-24.04 WSL Docker image;
  do not install a host SDK or change WSL/Docker configuration as a fallback.
- Start with `eng/wsl-docker.ps1 check -Project App -Plan` or
  `eng/Test-WithPostgres.ps1 -Project <suite> -Filter <expression> -Plan`.
  Plans invoke neither WSL nor Docker. Execution requires an already running
  Docker daemon; helpers never start it implicitly.
- Tests run on xUnit.net 4 over Microsoft.Testing.Platform. `-Project` also accepts `Unit`,
  `Component`, `Integration`, `Browser` or `All` (alias `-Suite`); `-Filter` takes test-name
  substrings and `-QueryFilter` an xUnit.net query filter. Results (TRX, CTRF, log) go to
  this checkout's `artifacts/test-results/<timestamp>`.
- Use the smallest relevant project/filter for feedback. Razor/UI changes need
  component checks; navigation, layout or JS changes also need browser checks.
  This Blazor project has no separate npm frontend build.
- Keep output, reports and temporary files in this checkout's ignored
  `artifacts/`; native Development data belongs in `.local-data/`. Mutable build
  state is per worktree. Linux packages may be shared through the primary
  project's `artifacts/packages/linux`; do not mix Linux/Windows package caches.
- Do not run build/test checks simultaneously in one worktree. The supplied
  helpers lock their shared check outputs; the development watcher has a
  separate build directory. Only one development stack can bind the default
  ports at a time. Worktree Compose names isolate containers/volumes but do not
  grant permission to reset another stack or copy its secrets/database.
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
  supported Linux containers. Windows helpers require Ubuntu-24.04 WSL and an
  already running local engine. They pin the local Docker socket, ignoring an
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
