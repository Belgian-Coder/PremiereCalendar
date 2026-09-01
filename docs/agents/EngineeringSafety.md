# Engineering and safety rules

- Inspect first and keep user-owned worktree changes intact. Commit only files
  explicitly owned by the task.
- Tests use fake external providers. Do not use live TMDb, Sonarr, Radarr,
  SIMKL, GitHub release, or other business APIs as a test substitute.
- Read-only acceptance may GET health, root, and framework assets and load the
  UI. Do not click Add, Update, Refresh, repair, backfill, import, save, or sync
  controls without separate authorization.
- The NAS PostgreSQL database is production state. Source qualification uses
  only the dedicated local development database. Never point tests at NAS.
- Keep the app/database password in the existing Docker secret file. Reports
  may record the secret file path and hash of non-secret configuration, never
  secret contents or a connection string containing a password.
- Parallelize only independent test assemblies after one successful build.
  Do not run concurrent builds against shared `bin`/`obj` directories.
- A health response is necessary but not sufficient. Container and installed
  acceptance must also prove the root page references the Blazor boot asset,
  the asset loads, runtime identity is coherent, and browser rendering works.
- A published tag/image is not an installation. Report source qualification,
  publication, deployment, runtime health, and browser acceptance separately.
- Do not enable automatic GitHub triggers. Builds, publications, and deploys
  remain deliberate operator actions.
