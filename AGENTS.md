# Build and release policy

- Do not use GitHub Actions or hosted GitHub CI/CD, including manual workflow dispatches.
- Run builds, tests, vulnerability scans, SBOM generation and container publication locally.
- Keep the GitHub CI and Container workflows disabled. Do not enable or dispatch them.
- Preserve vulnerability checks and runtime verification when publishing locally.
- Deploy digest-pinned images and preserve PostgreSQL, application data and backups.

# Local agent workflow

- `AGENTS.md` is the sole project instruction format for Codex and Claude Code.
  Claude Code 2.1.277 or newer loads it natively when this project and its
  ancestors contain neither `CLAUDE.md` nor `CLAUDE.local.md`; do not recreate
  those files or redundant hooks that print `AGENTS.md`. A global user
  `~/.claude/CLAUDE.md` does not disable native loading. Follow applicable nested
  instructions without changing persistent model routing.
- Keep worktrees under `.worktrees/` in this project. Preserve unrelated changes;
  use a dedicated branch/worktree when another task owns the primary checkout.
- Read `docs/Testing.md` and `docs/DockerAndPostgres.md` before running checks.
  The supported SDK is supplied by the existing Ubuntu-24.04 WSL Docker image;
  do not install a host SDK or change WSL/Docker configuration as a fallback.
- Start with `eng/wsl-docker.ps1 check -Project App -Plan` or
  `eng/Test-WithPostgres.ps1 -Project <suite> -Filter <expression> -Plan`.
  Plans invoke neither WSL nor Docker. Execution requires an already running
  Docker daemon; helpers never start it implicitly.
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

# Cleanup checkpoint

After **every commit and every release**, complete a bounded cleanup checkpoint
before continuing or reporting completion; repeat it after pushing. The owner
authorizes removal only of proven task-owned disposable temporary files/fixtures
and finished, clean, pushed worktrees inside this project.

- Identify exact owned paths and their provenance. Inspect tracked, untracked
  and ignored contents; resolve every removal target inside this project and
  reject links or paths that can escape it. If ownership is unclear, retain the
  material and report the exact cleanup limit.
- Preserve user WIP, unknown/untracked user data, authentication material,
  audit/release/rollback evidence and warm reusable dependency/incremental caches.
  Age, an ignored path or an apparently successful release does not prove that
  data is disposable. Never run broad `git clean`, `git worktree prune`, recursive
  sweeps or unconditional `bin`/`obj`/Docker/BuildKit cache pruning.
- Keep active or unpushed worktrees. Remove a completed worktree from the primary
  checkout only after verifying its HEAD is included in fetched remote `main`,
  its index/working tree are clean, and it contains no retained data or caches.
  Use `git worktree remove <verified-path>` without force. If protected material
  remains, retain the worktree and report why.
- A checkpoint can correctly remove nothing. Report the exact removed and
  retained paths; cleanup never authorizes deployment, service administration,
  production data deletion or a change to the release gates above.
