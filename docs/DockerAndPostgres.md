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

Release tags build the runtime image once, reject fixable HIGH or CRITICAL vulnerability findings, retain a CycloneDX SBOM and scan report, then publish the scanned image to GitHub Container Registry. Deploy by digest. The runtime:

- runs as the image's non-root application user;
- has a read-only root filesystem, no added Linux capabilities, and `no-new-privileges`;
- uses an internal-only database network;
- reads the PostgreSQL password from a Docker secret file;
- disables the Windows signed updater;
- exposes `/health`, `/health/ready`, and `/health/version`.

## SQLite migration

First create and verify an online SQLite snapshot with the Windows release. Configure the target PostgreSQL connection and password file, then run:

```text
dotnet PremiereCalendar.dll database migrate-postgres --source /absolute/source.db
dotnet PremiereCalendar.dll database verify
```

The importer requires a schema-current SQLite source and an empty PostgreSQL target. It verifies `PRAGMA quick_check`, the source SHA-256, every copied table count, and commits all application rows in one transaction. It uses PostgreSQL binary COPY and refuses to merge or overwrite existing target rows.

## Backup and recovery boundary

SQLite snapshots are migration inputs, not ongoing PostgreSQL backups. Production PostgreSQL requires scheduled `pg_dump` output with SHA-256 and completion manifests, retention, an isolated restore test, and off-host backup coverage. The canonical NAS deployment and operational instructions live under `\\NAS\Data\Backups\Infrastructure\NAS\services\premierecalendar`.

The retained Windows release and its untouched SQLite data are the rollback boundary. Do not restore a PostgreSQL dump over SQLite or point the Windows service at the PostgreSQL container.
