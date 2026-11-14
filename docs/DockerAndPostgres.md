# Docker and PostgreSQL

PremiereCalendar is PostgreSQL-only and ships only as a Docker container. SQLite, the Windows service and the signed self-updater were removed; `AppDatabase:Provider=Sqlite` and `AppDatabase:Path` now stop startup with a clear error instead of silently starting against an empty database. The configuration is described in [Configuration](Configuration.md#application-database).

## Local development

Use Ubuntu 24.04 on WSL with Docker Engine and the Compose plugin installed inside the distribution:

```powershell
pwsh -File D:\Projects\PremiereCalendar\eng\wsl-docker.ps1 up
pwsh -File D:\Projects\PremiereCalendar\eng\wsl-docker.ps1 logs
pwsh -File D:\Projects\PremiereCalendar\eng\wsl-docker.ps1 down
```

The script starts Docker in the distribution when needed, and the first `up` creates the ignored development password file `.secrets/dev-postgres-password` (it is never overwritten and never printed). Other actions are `build` (rebuild the development image, which the test script needs), `test` and `reset`.

The development app is available only on `127.0.0.1:5299`; PostgreSQL is exposed only on `127.0.0.1:55432` for debugging and for running the app outside the container (`dotnet run` or the `launchSettings.json` profiles use `appsettings.Development.json`, which points at that port and reads the password from `.secrets/dev-postgres-password`). The development password lives in the ignored `.secrets` directory. `reset` is destructive only to the named PremiereCalendar development volumes.

Run the automated tests with `D:\Projects\PremiereCalendar\eng\Test-WithPostgres.ps1` (or `wsl-docker.ps1 test`), which starts a separate throwaway PostgreSQL server instead of using the development database (see [Testing](Testing.md)). <!-- TEST-COMMANDS -->

## Production container

The household installation is a Portainer stack on the NAS (stack 20, endpoint 2) made of the application, PostgreSQL and a backup service. Builds, tests, vulnerability scans, SBOM generation and container publication run locally; do not use GitHub Actions, including manual workflow dispatches (the CI and Container workflows are disabled). The scripted release process in [Releasing](Releasing.md) builds the runtime image once from the committed source archive, launches that exact image to prove readiness, non-root identity, root HTML and the Blazor boot asset, rejects any vulnerability, secret or misconfiguration finding, retains a CycloneDX SBOM and scan report, exports the scanned image as a Docker archive, loads that archive onto the NAS engine and activates it by pinning the image configuration digest in the stack (pulling and pruning stay disabled). Publishing the same scanned image to a registry is optional and is also done locally; it does not require hosted CI.

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

Production PostgreSQL requires scheduled `pg_dump` output with SHA-256 and completion manifests, retention, an isolated restore test, and off-host backup coverage. The canonical NAS deployment and operational instructions live under `\\NAS\Data\Backups\Infrastructure\NAS\services\premierecalendar`. Every release starts with a fresh backup and an isolated restore check before anything is built or deployed (see [Releasing](Releasing.md)); the restore target is a throwaway, internal-network PostgreSQL container, never the production database.

Roll back by deploying a previously accepted digest-pinned container image while
preserving the PostgreSQL volume. Recover data only from a checksum-verified
PostgreSQL dump that first passes an isolated restore.

## Read-only runtime logging

The runtime image sets `HOME=/tmp`. .NET 11 named mutexes need writable user-shared storage under HOME; leaving it at `/home/app` on the read-only root filesystem silently prevents Serilog's shared file sink from opening. The existing `/tmp` tmpfs supplies this temporary runtime state. Persistent application data and logs remain under `/app/App_Data`. Validate both actual log-file writes and `/health/version` diagnostic counters after an update.
