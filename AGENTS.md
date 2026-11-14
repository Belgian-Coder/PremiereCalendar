# Build and release policy

- Do not use GitHub Actions or hosted GitHub CI/CD, including manual workflow dispatches.
- Run builds, tests, vulnerability scans, SBOM generation and container publication locally.
- Keep the GitHub CI and Container workflows disabled. Do not enable or dispatch them.
- Preserve vulnerability checks and runtime verification when publishing locally.
- Deploy digest-pinned images and preserve PostgreSQL, application data and backups.

# Local agent workflow

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
- Use `Workspace-Maintenance.ps1 -Mode Check` for hygiene. Cleaning requires
  explicit permission for the exact data being deleted; inspect `-WhatIf` first.
  Never clean another worktree or sibling directory, or remove credentials.
