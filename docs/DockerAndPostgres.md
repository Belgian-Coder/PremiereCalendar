# Docker and PostgreSQL

PremiereCalendar is PostgreSQL-only and ships only as a Docker container. SQLite, the Windows service and the signed self-updater were removed; `AppDatabase:Provider=Sqlite` and `AppDatabase:Path` now stop startup with a clear error instead of silently starting against an empty database. The configuration is described in [Configuration](Configuration.md#application-database).

## Local development

Use the existing Ubuntu 24.04 WSL distribution with Docker Engine and the Compose plugin.
The daemon must already be running; helpers do not install tools, start services,
or keep WSL alive. `-Plan` previews commands without invoking WSL/Docker:

```powershell
.\eng\wsl-docker.ps1 up
.\eng\wsl-docker.ps1 check -Plan
.\eng\wsl-docker.ps1 test
.\eng\wsl-docker.ps1 logs
.\eng\wsl-docker.ps1 down
```

The development app is available only on `127.0.0.1:5299`; PostgreSQL is exposed only on `127.0.0.1:55432` for debugging and native development. The password remains in ignored `.secrets`; worktrees do not copy credentials or databases. Worktrees get a path-derived Compose project name and separate named volumes; the primary checkout keeps `premierecalendar-dev`. Ports are unchanged, so only one stack can own them. Never stop/reset another stack to free a port without authorization. `reset` deletes that stack's named volumes and requires permission for that exact deletion. The existing PostgreSQL and application-data volumes are retained, while build/package bind mounts stay under this project. Obsolete package volumes and sibling output directories are preserved.

Run the automated tests with `eng\Test-WithPostgres.ps1`, which starts a separate throwaway PostgreSQL server instead of using the development database (see [Testing](Testing.md)).

## Production container

Do not use GitHub Actions, including manual workflow dispatches. The CI and
Container workflows are disabled. Build and publish locally: build the runtime
image once, launch that exact image to prove readiness, non-root identity,
root HTML and the Blazor boot asset, reject fixable HIGH or CRITICAL
vulnerability findings, retain a CycloneDX SBOM and scan report, then publish
the scanned image to GitHub Container Registry. Registry hosting does not
require hosted CI.

Keep release work under the releasing checkout's ignored `artifacts/releases/<version>`.
Use normal Docker layer caching: the Dockerfile restores the app project before
copying application source, so ordinary source-only changes can reuse dependency
restore layers. Do not add `--no-cache` or `--pull` to routine builds; refresh
base/dependency pins deliberately and still run current vulnerability checks.
Build the candidate runtime image once with explicit version/source/build metadata,
run every acceptance gate on that immutable image ID, and publish that same image.
Rebuilding after acceptance requires revalidating the replacement. Scoped checks,
cached restore results and a green health endpoint do not replace complete
regression, browser, scan, SBOM, database, boot-asset and hardened-runtime checks.
After every release, complete the [bounded cleanup checkpoint](../AGENTS.md#cleanup-checkpoint)
before reporting completion, as after every commit and again after pushing.
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
before publication. The pinned Alpine 3.24.4 runtime contains `libcrypto3` and
`libssl3` version `3.5.9-r0`, above the fixed `3.5.8-r0` security threshold,
and `zlib` `1.4-r0`, so the image no longer upgrades zlib itself.

## Database commands

The image entrypoint is already `dotnet PremiereCalendar.dll`, so the `database` commands take only their own arguments. Run them on the Docker host, in the folder that contains `compose.yaml`, with the same `PREMIERECALENDAR_IMAGE` and `PREMIERECALENDAR_POSTGRES_PASSWORD_FILE` values as the running stack (the database container must already be running):

```text
docker compose run --rm --no-deps app database verify
docker compose run --rm --no-deps app database prepare-legacy-checkpoints
```

Against a running app container, `docker compose exec app dotnet PremiereCalendar.dll database verify` does the same inside it (`exec` bypasses the entrypoint, so it needs the full command). Never write `docker compose run --rm app dotnet PremiereCalendar.dll ...`: the entrypoint would receive those words as arguments and start a second full application instance. Run `prepare-legacy-checkpoints` only while the app container is stopped.

`verify` checks connectivity, migration history checksums and the schema shape of an initialized database. `prepare-legacy-checkpoints` converts provider-work checkpoints for an offline downgrade to an older release. The earlier SQLite snapshot, restore and `migrate-postgres` commands were removed together with SQLite; installations that still hold a SQLite database must migrate it with v1.2.19, the last release that contained the importer (`dotnet PremiereCalendar.dll database migrate-postgres --source /absolute/source.db`, then `database verify`), before upgrading.

## Backup and recovery boundary

Production PostgreSQL requires scheduled `pg_dump` output with SHA-256 and completion manifests, retention, an isolated restore test, and off-host backup coverage. The canonical NAS deployment and operational instructions live under `\\NAS\Data\Backups\Infrastructure\NAS\services\premierecalendar`.

Roll back by deploying a previously accepted digest-pinned container image while
preserving the PostgreSQL volume. Recover data only from a checksum-verified
PostgreSQL dump that first passes an isolated restore.

## Read-only runtime logging

The runtime image sets `HOME=/tmp`. .NET 11 named mutexes need writable user-shared storage under HOME; leaving it at `/home/app` on the read-only root filesystem silently prevents Serilog's shared file sink from opening. The existing `/tmp` tmpfs supplies this temporary runtime state. Persistent application data and logs remain under `/app/App_Data`. Validate both actual log-file writes and `/health/version` diagnostic counters after an update.
