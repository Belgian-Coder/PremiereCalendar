# Docker and PostgreSQL

PremiereCalendar supports SQLite for the signed Windows service and PostgreSQL for containers. The persistence services keep their historical `Sqlite...` class names for compatibility, but use the configured `AppDatabase:Provider` and provider-neutral ADO.NET commands.

## Local development

Use Ubuntu 24.04 on WSL with Docker Engine and the Compose plugin installed inside the distribution:

```powershell
.\eng\wsl-docker.ps1 up
.\eng\wsl-docker.ps1 test
.\eng\wsl-docker.ps1 logs
.\eng\wsl-docker.ps1 down
```

The development app is available only on `127.0.0.1:5299`; PostgreSQL is exposed only on `127.0.0.1:55432` for migration tests and debugging. The development password lives in the ignored `.secrets` directory. `reset` is destructive only to the named PremiereCalendar development volumes.

## Production container

An operator explicitly starts the manual container workflow for an intended
version. A push, pull request, tag, schedule, or GitHub release does not start
it. The manual workflow builds the runtime image once, launches that exact
image to prove readiness, non-root identity, root HTML and the Blazor boot
asset, rejects fixable HIGH or CRITICAL vulnerability findings, retains a
CycloneDX SBOM and scan report, then publishes the scanned image to GitHub
Container Registry.
Deploy by digest. The runtime:

- runs as the image's non-root application user;
- has a read-only root filesystem, no added Linux capabilities, and `no-new-privileges`;
- uses an internal-only database network;
- reads the PostgreSQL password from a Docker secret file;
- disables the Windows signed updater;
- exposes `/health`, `/health/ready`, and `/health/version`.

The project explicitly pins `Microsoft.AspNetCore.App.Internal.Assets` to the
same .NET 11 preview version as the SDK/runtime. Linux publish otherwise omitted
`_framework/blazor.web.js` with preview 7 even though health remained green.
Upgrade that package together with the pinned SDK/runtime images; the Docker
build assertion and release-image smoke must both pass before publication.

## SQLite migration

First create and verify an online SQLite snapshot with the Windows release. Configure the target PostgreSQL connection and password file, then run:

```text
dotnet PremiereCalendar.dll database migrate-postgres --source /absolute/source.db
dotnet PremiereCalendar.dll database verify
```

The importer requires a schema-current SQLite source and an empty PostgreSQL target. It verifies `PRAGMA quick_check`, the source SHA-256, every copied table count, and commits all application rows in one transaction. It uses PostgreSQL binary COPY and refuses to merge or overwrite existing target rows.

## Backup and recovery boundary

SQLite snapshots are migration inputs, not ongoing PostgreSQL backups. Production PostgreSQL requires scheduled `pg_dump` output with SHA-256 and completion manifests, retention, an isolated restore test, and off-host backup coverage. The canonical NAS deployment and operational instructions live under `\\NAS\Data\Backups\Infrastructure\NAS\services\premierecalendar`.

The Windows installation has been retired. Roll back by deploying a previously
accepted digest-pinned container image while preserving the PostgreSQL volume.
Recover data only from a checksum-verified PostgreSQL dump that first passes an
isolated restore. The final SQLite migration input is retained as restricted
historical evidence on the NAS, not as an executable fallback.
