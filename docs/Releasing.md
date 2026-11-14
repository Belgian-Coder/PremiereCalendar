# Releasing

A release is built, tested, scanned and deployed **locally**. There is no hosted CI: do not use GitHub Actions, including manual workflow dispatches, and keep the CI and Container workflows disabled (see `AGENTS.md`). Production is the Docker stack on the NAS (Portainer stack 20, endpoint 2: the app, PostgreSQL and a backup service) and a release changes exactly one thing there: the app image digest. PostgreSQL, application data and backups are preserved.

The scripts live in `D:\Projects\PremiereCalendar\eng\release`. They are plain PowerShell 7 and Python 3 and replace the former helper folders that sat outside the repository. This page is the runbook for the whole release; each script also has built-in help (`Get-Help <script> -Full`).

## The eleven steps

| # | Step | Script(s) | Touches |
| --- | --- | --- | --- |
| 0 | Create the evidence bundle and record every parameter | `Initialize-Release.ps1` | local files only |
| 1 | Fresh NAS backup and installed baseline | `Prepare-Release.ps1` | NAS (backup, Portainer POSTs) |
| 2 | Isolated restore check | `Initialize-RestoreCheck.ps1`, `Restore-Backup.ps1` | local Docker only (reads the dump) |
| 3 | Build from the exact source archive | `Build-Image.ps1` | local Docker only |
| 4 | NuGet vulnerability audit | `Invoke-NuGetAudit.ps1` | local Docker (restore needs the NuGet feed) |
| 5 | Vulnerability scan and SBOM | `Invoke-Scans.ps1` | local Docker |
| 6 | Tests | `Invoke-ReleaseTests.ps1` | local Docker |
| 7 | Smoke test and restored-backup compatibility | `Invoke-Smoke.ps1`, `Test-BackupCompatibility.ps1` | local Docker only |
| 8 | Load the image into the NAS engine | `Load-Image.ps1` (and optionally `Test-PersistedSetting.ps1`) | NAS (Portainer POST) |
| 9 | Deploy by digest through Portainer | `Deploy-Release.ps1` | NAS (stack update) |
| 10 | Verify `/health/version` on both addresses | `Verify-Release.ps1` | NAS (read-only) and both URLs |
| 11 | Close: release record, release notes, NAS docs | `Close-Release.ps1`, `nas-docs\*` | local, NAS share (copy) |

Steps 3 to 7 never touch the NAS or production. Steps 1, 8, 9 and the optional parts of 10 and 11 do, and refuse to run without an explicit permission switch.

## Prerequisites

- PowerShell 7 (`pwsh`; 7.5 or later preferred), Python 3 and git on the host; run the scripts from the machine that holds the local Docker engine. Run them from any folder: they derive every path from their own location.
- Docker Engine in the `Ubuntu-24.04` WSL distribution (start it with `D:\Projects\PremiereCalendar\eng\wsl-docker.ps1 build`). Every Docker call is pinned to `unix:///var/run/docker.sock` of that distribution, never to an inherited context, because a context can point at the NAS engine.
- The images pinned in `Dockerfile` (SDK, ASP.NET runtime) and `compose.yaml` (PostgreSQL) present locally or pullable by digest; the scripts read the pins from those files. The SDK for the build is the container's: the Windows host has no matching .NET SDK.
- Access to the NAS shares and the Portainer wrapper `\\NAS\Data\Backups\Infrastructure\NUC\windows11-vm\scripts\portainer\Invoke-PortainerApi.ps1`. The wrapper owns the credentials; the release scripts never read, store or print them.
- A committed release commit: the release notes `docs\releases\<version>.md` are part of it, and the tracked tree is clean (`HEAD` is the release commit).

## Rules the scripts enforce

- **Evidence is never overwritten.** Every evidence file is created with `CreateNew`. A retry or a candidate gets a new bundle name and a new resource prefix; nothing is reset or reused.
- **Hash checks at every hand-over.** The source archive is compared byte for byte with the committed Git blobs; the image archive, dump, tested source and review inputs are bound by SHA-256, and later steps re-check them.
- **No secrets are printed.** Restricted material (stack configuration, native Portainer backup, dump locators, exec logs) stays in `<bundle>.private` and the restricted NAS backup tree; the public release JSON is scanned for restricted locators before it is written.
- **Portainer mutation needs `-AllowMutation`.** Any request other than GET is refused otherwise. Portainer exec calls are POSTs too, so steps that use `exec` (including read-only SQL) need the switch although the command they run is read-only.
- **`-WhatIf` is a dry run.** Every script prints its plan and returns without any Docker, NAS, Portainer or network call.
- **Docker networks get an explicit subnet.** The default address pools are exhausted on the release machine, so networks are created with `--subnet 10.13x/14x.y.0/24` (`-SubnetPrefix` and `-SubnetOctet`; roles use N, N+1, N+2).
- **Every `*.cs` file must be LF** before the test source is frozen (`.gitattributes` checks C# out as LF and the evidence compares raw bytes with the committed blob). `git ls-files --eol` shows offenders.
- **Local resources are never reused.** Containers, networks, volumes and temporary folders are named `<prefix>_...` and a name collision stops the run.

## Evidence layout

The bundle is `D:\Projects\PremiereCalendar\artifacts\release\<bundle name>` (git-ignored). Everything in it may be published; it is checksummed and copied to the NAS release share at closure.

- `release-config.json`: every parameter of this release.
- Build and scan evidence: `source-<version>.tar`, `source-archive-verification.json`, `premierecalendar-<version>.tar`, `build-metadata.json`, `release-nuget-audit.log`, `release-<version>-trivy.json`, `release-<version>-sbom.cdx.json`, `release-<version>-audit.json`, `release-<version>-scan-summary.json`.
- Source evidence: `tested-source-fingerprints.json`, `test-run.log`, `test-run.json`, `test-summary.json`, `review-inputs-manifest.json`, `review-source.patch`, `source-review.json`.
- Runtime evidence: `smoke-*`, `backup-restore.json` (sanitized), `backup-schema-before.txt`, `backup-schema-after.txt`, `backup-compat-*`, `schema-backup-compatibility.json`.
- `installed\`: containers before and after, image-load and activation receipts, direct and private health, HTTP checks, `verification.json`, optional setting receipt, `backup-summary-public.json`.
- `<bundle>.private\`: the raw backup summary and restore proof, Portainer exec evidence, image inspect output. Never publish it.

## Before you start

1. Finish and commit the release, including `docs\releases\<version>.md` (the notes are never generated from inferred metrics). The installed-acceptance section is added in the closure commit afterwards; do not edit notes or docs between the release commit and `Close-Release.ps1`, because the gates re-validate the working tree against the commit.
2. Collect the facts about what is installed now: the version, the image configuration digest pinned in the live stack (`sha256:...`), the Portainer stack revision (StackFileVersion), and the source revision of the installed build. The old digest must appear exactly once in the stack file.
3. Choose the version, bundle name (the version, or `<version>-candidate2` for a retry) and a resource prefix that has not been used (the default is derived from the bundle name).

```powershell
$rel = 'D:\Projects\PremiereCalendar\eng\release'
pwsh -File "$rel\Initialize-Release.ps1" -Version 1.2.20 `
    -ExpectedOldVersion 1.2.19 `
    -ExpectedOldDigest sha256:<digest pinned in the live stack> `
    -ExpectedStackRevision <current StackFileVersion> `
    -RequiredSettingKey Sources.Trakt.Enabled -RequiredSettingValue false   # optional gate, see step 8
```

It prints the bundle path; use it as `-Bundle` in every later command. Useful options: `-BundleName`, `-ResourcePrefix`, `-ImageTag` (default `premierecalendar:<bundle name>`), `-SubnetPrefix`/`-SubnetOctet`, `-ExpectedOldSourceRevision`, `-ExpectedOldSchemaVersion` (default: the schema of this source), `-StackId`/`-EndpointId` (20 and 2), the container names, `-DirectUrl`/`-PrivateUrl`, the NAS paths and the scanner image tags. The SDK, runtime and PostgreSQL images and the target schema version are read from `Dockerfile`, `compose.yaml` and `Directory.Build.props`. Add `-WhatIf` to see the plan first.

For the commands below:

```powershell
$rel = 'D:\Projects\PremiereCalendar\eng\release'
$bundle = 'D:\Projects\PremiereCalendar\artifacts\release\1.2.20'
$commit = '<the 40-character release commit>'
```

### Review record (when you use reviewers)

`Load`, `Deploy` and `Close` require a source review bound to the commit. After the release commit exists, freeze what is reviewed:

```powershell
pwsh -File "$rel\Freeze-ReviewInputs.ps1" -Bundle $bundle -BaseCommit <previous release source commit> -ExpectedRevision $commit
```

Then write `$bundle\source-review.json`:

```json
{
  "sourceRevision": "<release commit>", "baseCommit": "<previous release source commit>",
  "inputManifestSha256": "<sha256 of review-inputs-manifest.json>",
  "reviewedDiffSha256": "<sha256 of review-source.patch>",
  "openP0P2": 0,
  "reviews": [
    { "reviewer": "...", "model": "...", "effort": "...", "status": "completed", "invocation": "..." },
    { "reviewer": "...", "status": "not-performed", "reason": "why it was not done" }
  ]
}
```

At least one review must be `completed` and every `not-performed` entry needs a reason; open P0-P2 findings block the release.

## Step 1: fresh NAS backup

```powershell
pwsh -File "$rel\Prepare-Release.ps1" -Bundle $bundle -AllowMutation
```

Checks the endpoint is the NAS, the stack is at the expected revision with the old digest pinned exactly once, all three stack containers are running and healthy, and the live `/health/version` matches the expected version and schema. It then keeps the raw stack configuration and a native Portainer backup only in the restricted backup tree, runs the stack's own backup, strict verification and freshness check in the backup container into a unique `pre-upgrade-<bundle>-<UTC>` folder, confirms exactly one dump whose SHA-256 equals its sidecar and records the container ids and start times (`installed\containers-before.json`).

## Step 2: isolated restore check

```powershell
pwsh -File "$rel\Initialize-RestoreCheck.ps1" -Bundle $bundle
pwsh -File "$rel\Restore-Backup.ps1" -Bundle $bundle
```

Creates a read-only, tmpfs-only PostgreSQL container on a new internal network (no mounts, no published ports) in the local WSL engine, then streams the fresh dump into it once with `pg_restore --exit-on-error` after re-hashing it. The destination must be empty and correctly labelled; nothing is cleaned up for you. The sanitized proof is `backup-restore.json`; the raw proof with the dump locator stays private.

## Step 3: build

```powershell
pwsh -File "$rel\Build-Image.ps1" -Bundle $bundle -ExpectedRevision $commit
```

`HEAD` must be the commit with a clean tracked tree and no untracked source files. The commit is archived with `git archive` (`core.autocrlf=false`), every archived C# and SQL file is compared with its committed blob, and linux/amd64 is built from that archive only (it is the build context on stdin) with `VERSION`, `SOURCE_REVISION`, `BUILD_ID` and `BUILD_TIME_UTC` build arguments. The image is exported with `docker save`; `build-metadata.json` records the archive hash, image id and labels.

## Step 4: NuGet audit

```powershell
pwsh -File "$rel\Invoke-NuGetAudit.ps1" -Bundle $bundle
```

Restores the archived source in the Dockerfile's SDK image and runs `dotnet list package --vulnerable --include-transitive`. The gate is the sentence "has no vulnerable packages" in `release-nuget-audit.log`.

## Step 5: vulnerability scan and SBOM

```powershell
pwsh -File "$rel\Invoke-Scans.ps1" -Bundle $bundle
```

Scans the exported archive (the exact bytes that will be deployed) with Trivy (vulnerabilities, misconfigurations, secrets) and writes a CycloneDX SBOM with Syft. `audit_image_archive.py` binds archive, labels, platform and source proof to the local image; `summarize_scan.py` fails unless there are no vulnerabilities, misconfigurations or secrets, the SBOM is non-empty and the NuGet audit passed. `-IgnoreUnfixed` counts only vulnerabilities with a fix (the policy of the former hosted workflow) and is recorded in the summary. The scanner image tags are release parameters (`-TrivyImage`, `-SyftImage` of `Initialize-Release.ps1`); review them when you start a release.

## Step 6: tests

```powershell
pwsh -File "$rel\Invoke-ReleaseTests.ps1" -Bundle $bundle -ExpectedRevision $commit
```

Requires the same clean commit, freezes fingerprints of the tracked source (it fails when a `*.cs` file contains CR characters), runs `D:\Projects\PremiereCalendar\eng\Test-WithPostgres.ps1` with its defaults (xUnit.net 4 on Microsoft.Testing.Platform: the unit, component and integration suites against a throwaway PostgreSQL), verifies the fingerprints again and writes `test-summary.json` per suite. The counts come from the CTRF JSON files the script writes to `<repo>\TestResults\<timestamp>\` (the TRX files are the fallback, `-SummaryJson` an override); the console log, the CTRF and TRX files are copied into the bundle as `test-run.log` and `tests\*`. A focused or partial run cannot satisfy the gates. Tests may run any time before step 8, also before the build.

## Step 7: smoke test and restored-backup compatibility

```powershell
pwsh -File "$rel\Invoke-Smoke.ps1" -Bundle $bundle
pwsh -File "$rel\Test-BackupCompatibility.ps1" -Bundle $bundle
```

`Invoke-Smoke.ps1` runs the exact image by id with production hardening (read-only root, user 1654:999, all capabilities dropped, `no-new-privileges`) against an empty internal PostgreSQL and checks readiness, the `/health/version` identity (version, source revision, schema, healthy database, zero dropped diagnostic events, writer failures and logging failures), the root HTML and `_framework/blazor.web.js`, the Brussels time zone (including a probe built from `eng\release\timezone-probe`), the nightly planner log lines, real log-file writes, and `database verify` plus `database prepare-legacy-checkpoints`.

`Test-BackupCompatibility.ps1` starts the same image against the restored production backup with background services disabled and compares sanitized states (row counts, migration history and one-way fingerprints of the settings, filter, view-sync, SIMKL and item-enrichment tables) before and after. It fails unless the installed schema's migration history is unchanged, the candidate reaches its own schema and every preserved table is identical.

## Step 8: load the image

```powershell
pwsh -File "$rel\Test-PersistedSetting.ps1" -Bundle $bundle -AllowMutation   # only when a required setting was configured
pwsh -File "$rel\Load-Image.ps1" -Bundle $bundle -AllowMutation
```

`Load-Image.ps1` re-validates every gate (source-bound tests and review, exact image, scans, SBOM, NuGet audit, smoke, backup binding), refuses to load when the tag already exists on the NAS engine, uploads the archive (no registry, no pull) and checks the loaded image's configuration digest and labels. `Test-PersistedSetting.ps1` is the optional read-only proof that a persisted setting (for example `Sources.Trakt.Enabled` = `false`) already has the value the release must not change; when the release configured one, `Deploy-Release.ps1` requires its receipt.

## Step 9: deploy by digest

```powershell
pwsh -File "$rel\Deploy-Release.ps1" -Bundle $bundle -AllowMutation
```

After re-validating all gates it checks the live state: stack revision, the old digest pinned exactly once, the three containers unchanged since step 1, the loaded image, a backup younger than 26 hours that passes the stack's own freshness check, the live migration history equal to the one proven against the restored backup, and no running foreground work. The candidate stack file is the current one with only the app digest replaced (the script proves that nothing else differs and saves it in the restricted backup folder); it is applied with `PullImage=false` and `Prune=false`. There is no production restore and no database or backup restart. An existing activation receipt blocks a second activation; after a failed attempt keep the evidence and rerun with a new `-AttemptTagPrefix` only when the stack did not change.

## Step 10: verify

```powershell
pwsh -File "$rel\Verify-Release.ps1" -Bundle $bundle -AllowMutation
```

Waits for the app container to be healthy on the audited digest, proves the database and backup containers were not restarted, checks the hardening of the running container and requests `/health/version`, `/health/ready`, `/` and `/_framework/blazor.web.js` on `http://192.168.68.20:18084` and `https://premiere.home.belgiancoder.be` (the release configuration's `-DirectUrl` and `-PrivateUrl`). `/health/version` must show the new version and source revision, the schema, a healthy database, and zero dropped diagnostic events, writer failures and operational logging failures. It also checks that the persisted stack revision is one higher and that the Compose file on the NAS disk pins the new digest. A request right after activation can time out; each request is retried and evidence is only written when everything passed, so a failed pass can simply be repeated. `-AllowMutation` only enables the read-only time-zone file check inside the container.

Also complete the installed browser acceptance (calendars, filter links, posters, Settings) yourself before closing; the closure record does not claim what was not observed.

## Step 11: close

```powershell
pwsh -File "$rel\Close-Release.ps1" -Bundle $bundle -AllowNasWrite
```

Re-validates every gate and the installed evidence, requires the committed release notes, writes the sanitized public backup summary, preserves a copy of `eng\release` as `operations\`, writes `docs\releases\<version>.json`, `release.json` and `SHA256SUMS`, and with `-AllowNasWrite` copies the bundle to `\\NAS\Docker\PremiereCalendar\releases\<bundle name>` and reads every file back to compare hashes. Then:

1. Add the installed-acceptance section to `docs\releases\<version>.md`, commit it with the new `docs\releases\<version>.json` as the closure commit, and tag the **build** commit (not the closure commit).
2. Update the canonical NAS infrastructure documents. This is a separate, explicitly approved step; read the `AGENTS.md` files in `\\NAS\Data\Backups\Infrastructure` first (no deletions, no secrets, no evidence bundles there). Copy `D:\Projects\PremiereCalendar\eng\release\nas-docs\values.example.json` into `<bundle>.private\nas-docs\values.json`, fill it from the real evidence (pending values keep `readyToApply` false), then:

```powershell
$stage = "$bundle.private\nas-docs"
python -I "$rel\nas-docs\render_nas_docs.py" --values "$stage\values.json" --output "$stage\candidate"
pwsh -File "$rel\nas-docs\Apply-NasDocs.ps1" -ManifestPath "$stage\candidate\manifest.json"          # preview
pwsh -File "$rel\nas-docs\Apply-NasDocs.ps1" -ManifestPath "$stage\candidate\manifest.json" -Apply   # publish
```

The renderer only reads the five canonical documents and writes a managed block (`premierecalendar-<version>-managed`) into staged copies, preserving older blocks, the BOM and the line endings; the apply script verifies every staged and preimage hash, holds the files exclusively, writes in place (keeping ACLs), saves the preimages and never deletes anything.

3. Remove the local validation resources when you no longer need them: `docker ps -a`, `docker network ls` and `docker volume ls` in the Ubuntu-24.04 distribution list everything named `<prefix>_*` (the restore database and its network are the usual leftovers).

## Roll back

There is no scripted rollback, because it must be a deliberate decision. An app-only rollback is the reverse of step 9: update the stack so the app pins the previous image configuration digest (kept on the NAS engine) with `PullImage=false` and `Prune=false`, leave the PostgreSQL volume, application data and backups alone, and repeat the checks of step 10. It is only safe when the release did not change the database schema (`rollback.appOnlyRollbackSupported` in `release.json`); otherwise an older image may not read migrated data. Never restore the database automatically: recovery uses a checksum-verified dump that first passes an isolated restore and preserves newer user data (see [Docker and PostgreSQL](DockerAndPostgres.md#backup-and-recovery-boundary)).

## Migrating an old SQLite installation

SQLite and the Windows service, installers and self-updater no longer exist. An installation that still holds a SQLite database must first be upgraded to v1.2.19, the last release that can import it with `database migrate-postgres`, and verified with `database verify`; only then can it move to a current container image (see [Docker and PostgreSQL](DockerAndPostgres.md#database-commands)).

## When something fails

- A script stops on the first failed gate and leaves its evidence in place. Read the message, fix the cause and start the failed step again only if it wrote no evidence; otherwise create a new bundle (`-BundleName <version>-candidate2`) and a new `-ResourcePrefix`.
- `Docker network create` failures about overlapping or exhausted address pools: pick another `-SubnetPrefix` or `-SubnetOctet` in `Initialize-Release.ps1` for the new bundle.
- Python helper failures that mention CR characters: a `*.cs` file is CRLF in the working tree; re-checkout it (`.gitattributes` forces LF) and commit nothing unrelated.
- Integration tests are timing sensitive under host load; the machine is shared, so a full run may have to be repeated, with the earlier attempt kept as evidence of its own.
