# Docker and PostgreSQL

PremiereCalendar is PostgreSQL-only and ships only as a Docker container. SQLite, the Windows service and the signed self-updater were removed; `AppDatabase:Provider=Sqlite` and `AppDatabase:Path` now stop startup with a clear error instead of silently starting against an empty database. The configuration is described in [Configuration](Configuration.md#application-database).

## Local development

Use an explicitly selected Ubuntu 24.04 WSL distribution with Docker Engine and the Compose plugin.
The local helpers and canonical release test wrapper accept `-WslDistribution`
(default `Ubuntu-24.04`); PC-MARC uses `-WslDistribution PC-MARC-Dev`.
The distribution and daemon must already be running; helpers first query the
running distribution list and refuse a stopped selection. This observes a running
precondition; the listing and subsequent transport are not an atomic WSL lock.
They do not install
tools, start services or keep WSL alive. `-Plan` previews commands without invoking WSL/Docker:

```powershell
pwsh -File D:\Projects\PremiereCalendar\eng\wsl-docker.ps1 up
pwsh -File D:\Projects\PremiereCalendar\eng\wsl-docker.ps1 check -Project App -Plan
pwsh -File D:\Projects\PremiereCalendar\eng\wsl-docker.ps1 test -Project Unit -Plan
pwsh -File D:\Projects\PremiereCalendar\eng\wsl-docker.ps1 logs
pwsh -File D:\Projects\PremiereCalendar\eng\wsl-docker.ps1 down
```

The first `up` creates the ignored development password file `.secrets/dev-postgres-password` (it is never overwritten and never printed). Image-only `build` creates no password. Other actions are `build` (rebuild the development image, which the test script needs), `check` (compile one project without PostgreSQL), `test` (forwards project/filter, distro, profile and scheduling options to `Test-WithPostgres.ps1`; it does not build the image) and `reset`.

The development app is available only on `127.0.0.1:5299`; PostgreSQL is exposed only on `127.0.0.1:55432` for debugging and for running the app outside the container (`dotnet run` or the `launchSettings.json` profiles use `appsettings.Development.json`, which points at that port and reads the password from `.secrets/dev-postgres-password`). The password remains in the ignored `.secrets` directory; worktrees do not copy credentials or databases. Worktrees get a path-derived Compose project name and separate application/database volumes; the primary checkout keeps `premierecalendar-dev`. Ports are unchanged, so only one stack can own them. Never stop/reset another stack to free a port without authorization. `reset` deletes that stack's application/database volumes and requires permission for that exact deletion. External build/package caches survive reset. Existing data volumes, old bind caches and obsolete output locations are never migrated or removed by the helpers.

Source and reports stay in the native checkout. Writable Linux intermediates and
packages default to deterministic named Docker volumes on the selected engine's
ext4 storage. The canonical primary Git root defines the project namespace;
the exact checkout defines the worktree namespace. Test/check outputs share the
worktree's `linux-amd64-musl-async-check` profile; `-NoRuntimeAsync`, `-CacheFlavor
glibc`, and the Compose watcher each select separate build state. Packages share
one `linux-amd64-nuget` project volume, with every consumer using
`NUGET_SCRATCH=/root/.nuget/packages/.scratch` inside that same mount.
`NUGET_HTTP_CACHE_PATH=/root/.nuget/packages/.http-cache` retains HTTP metadata
with normal NuGet expiration and revalidation; auditing remains enabled. The
worktree `artifacts/check.lock` still excludes concurrent check/test writers.

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

Independent request-release clones may select that same canonical primary cache
through `BUILD_RELEASE_PROJECT_ROOT`. The helper validates the existing native
primary, exact protocol owner marker/project/target/root, manifest-derived clone
path and matching credential-free origin before using the hint. Invalid or linked
paths are refused without fallback or writes. Only dependency caches share the
primary namespace; build state still uses the exact execution checkout. Normal
primary and linked worktrees keep their existing Git-common-root behavior.
Git metadata subprocesses ignore inherited `GIT_*` overrides and disable optional
locks/prompts; the caller environment and primary work in progress stay intact.

`-BuildVolume` and `-NugetVolume` preserve explicit cache overrides. A custom
prepared `-DevImage` for check/test requires explicit `-CacheFlavor musl|glibc`;
choose its actual ABI so browser and service objects never mix. Test/check
execution checks the local engine and each required local image for Linux/amd64,
then runs the inspected image ID with pulls disabled. An inherited remote Docker
context cannot redirect these operations: helpers pin the local Unix socket.
Images and the daemon must already exist; `build`/`up` remain explicit image-build
actions. This does not add a native SDK or macOS application execution backend.

Test/check default to `-Cpus auto -BuildMaxCpuCount auto` and tests to
`-MaxThreads auto` (half of the engine's CPUs, at least 2); explicit numbers win.
PostgreSQL has its separate two-CPU quota. `wsl-docker.ps1 test` forwards those
budgets, distribution, image/ABI, runtime-async mode and volume overrides without
changing the selected suites. Plans report the resolved storage/profile and
budgets without querying WSL/Docker or creating files or volumes. For example:

```powershell
pwsh -File ./eng/wsl-docker.ps1 check -WslDistribution PC-MARC-Dev -Plan
pwsh -File ./eng/Test-WithPostgres.ps1 -Project Browser -WslDistribution PC-MARC-Dev -DevImage prepared-browser:local -CacheFlavor glibc -Plan
```

Run the automated tests with `D:\Projects\PremiereCalendar\eng\Test-WithPostgres.ps1` (or `wsl-docker.ps1 test`; add `-Plan` to preview), which starts a separate throwaway PostgreSQL server instead of using the development database (see [Testing](Testing.md)).

## Production container

The household installation is a Portainer stack on the NAS (stack 20, endpoint 2) made of the application, PostgreSQL and a backup service. Never use GitHub Actions, including self-hosted runners or manual dispatches, to execute or orchestrate validation, builds, tests or CI. The former workflow entrypoints are removed. Release artifacts are built, tested, scanned and packaged locally and uploaded from the local builder. The scripted release process in [Releasing](Releasing.md) builds the runtime image once from the committed source archive, launches that exact image to prove readiness, non-root identity, root HTML and the Blazor boot asset, rejects any vulnerability, secret or misconfiguration finding, retains a CycloneDX SBOM and scan report, exports the scanned image as a Docker archive, loads that archive onto the NAS engine and activates it by pinning the image configuration digest in the stack (pulling and pruning stay disabled). Publishing the same scanned image to a registry is optional and is also done locally; registry hosting does not require hosted CI.

Keep release work under the releasing checkout's ignored `artifacts/release/<bundle>` (see [Releasing](Releasing.md)).
Use normal Docker layer caching: the Dockerfile restores the app project before
copying application source, so ordinary source-only changes can reuse dependency
restore layers; the restore and publish steps also mount a BuildKit cache (`id=premierecalendar-nuget`) so a changed project file downloads only new packages. Release scans and the NuGet audit keep their public data (only the Trivy vulnerability DB directory, downloaded NuGet packages) in the labelled `premierecalendar-release-*` volumes, which are created when missing and never deleted by a release. Do not add `--no-cache` or `--pull` to routine builds; refresh
base/dependency pins deliberately and still run current vulnerability checks.
Build the candidate runtime image once with explicit version/source/build metadata,
run every acceptance gate on that immutable image ID, and activate that same image.
Rebuilding after acceptance requires revalidating the replacement. Scoped checks,
cached restore results and a green health endpoint do not replace complete
regression, browser, scan, SBOM, database, boot-asset and hardened-runtime checks.
After every release, complete the [development cleanup review](../AGENTS.md#development-workflow)
before reporting completion; ordinary commits need no separate cleanup receipt.
Retain release/rollback/audit evidence and warm build/dependency caches; remove
only verified task-owned disposable files and eligible completed worktrees.
`Workspace-Maintenance.ps1 -Mode Clean` is a separate, explicitly authorized
reset, not an automatic release step. No release script currently calls it.
Deploy by digest. The runtime:

- runs as the image's non-root application user;
- has a read-only root filesystem, no added Linux capabilities, and `no-new-privileges`;
- uses an internal-only database network;
- reads the PostgreSQL password from a Docker secret file;
- exposes `/health`, `/health/ready`, and `/health/version`.

The project explicitly pins `Microsoft.AspNetCore.App.Internal.Assets` to the
same exact .NET 11 version as the SDK/runtime (`11.0.0`). An
earlier preview omitted `_framework/blazor.web.js` even though health remained
green, so the Docker build assertion and release-image smoke must both pass
before publication. The pinned Alpine 3.24.4 runtime base still ships `libcrypto3` and
`libssl3` `3.5.9-r0`, for which the current Trivy database lists fixable
findings (fixed in `3.5.9-r1`); no rebuilt `11.0.0-alpine3.24` base exists yet,
so the runtime stage upgrades exactly those two packages with a `>=3.5.9-r1`
floor that fails the build if the repository cannot supply them. Remove that
line once the pinned base contains the fix. `zlib` `1.4-r0` needs no upgrade.

## Database commands

The image entrypoint is already `dotnet PremiereCalendar.dll`, so the `database` commands take only their own arguments. Run them on the Docker host, in the folder that contains `compose.yaml`, with the same `PREMIERECALENDAR_IMAGE` and `PREMIERECALENDAR_POSTGRES_PASSWORD_FILE` values as the running stack (the database container must already be running):

```text
docker compose run --rm --no-deps app database verify
docker compose run --rm --no-deps app database prepare-legacy-checkpoints
```

Against a running app container, `docker compose exec app dotnet PremiereCalendar.dll database verify` does the same inside it (`exec` bypasses the entrypoint, so it needs the full command). Never write `docker compose run --rm app dotnet PremiereCalendar.dll ...`: the entrypoint would receive those words as arguments and start a second full application instance. Run `prepare-legacy-checkpoints` only while the app container is stopped.

`verify` checks connectivity, migration history checksums and the schema shape of an initialized database. `prepare-legacy-checkpoints` converts provider-work checkpoints for an offline downgrade to an older release. The earlier SQLite snapshot, restore and `migrate-postgres` commands were removed together with SQLite; installations that still hold a SQLite database must migrate it with v1.2.19, the last release that contained the importer (`dotnet PremiereCalendar.dll database migrate-postgres --source /absolute/source.db`, then `database verify`), before upgrading.

## Backup and recovery boundary

Production PostgreSQL requires scheduled `pg_dump` output with SHA-256 and completion manifests, retention, an isolated restore test, and off-host backup coverage. The canonical NAS deployment and operational instructions live under `\\NAS\Data\Backups\Infrastructure\NAS\services\premierecalendar`. Every release starts with a fresh backup and an isolated restore check before anything is built or deployed (see [Releasing](Releasing.md)); the restore target is a throwaway, internal-network PostgreSQL container, never the production database.

Roll back by deploying a previously accepted digest-pinned container image while
preserving the PostgreSQL volume. Recover data only from a checksum-verified
PostgreSQL dump that first passes an isolated restore.

## Read-only runtime logging

The runtime image sets `HOME=/tmp`. .NET 11 named mutexes need writable user-shared storage under HOME; leaving it at `/home/app` on the read-only root filesystem silently prevents Serilog's shared file sink from opening. The existing `/tmp` tmpfs supplies this temporary runtime state. Persistent application data and logs remain under `/app/App_Data`. Validate both actual log-file writes and `/health/version` diagnostic counters after an update.
