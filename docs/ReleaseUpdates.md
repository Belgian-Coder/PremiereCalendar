# Releases and updates

PremiereCalendar uses signed, immutable GitHub releases. Initialize the
non-exportable operator signing key once:

```powershell
.\eng\Initialize-ReleaseSigner.ps1
```

Commit and push a reviewed clean `main`, then publish:

```powershell
.\eng\Publish-GitHubRelease.ps1 -Version 1.1.0 -ReleaseNotes 'Speed and stability release.' -SigningCertificateThumbprint <thumbprint>
```

The publisher requires `main == origin/main`, a clean worktree, an authenticated
GitHub CLI, and a version newer than every published release. It restores, builds,
tests, makes a self-contained Windows x64 package, emits deterministic build
metadata, signs `stable.manifest.json`, writes `SHA256SUMS.txt`, creates a draft,
uploads all assets, and only then publishes it.

Publishing is always an explicit operator action; no GitHub Actions workflow creates
releases. CI runs for pull requests targeting `main` and can also be started manually
with `workflow_dispatch`. A push to `main` does not start CI.

The first administrator-reviewed installation uses the local release directory:

```powershell
.\deploy\Updates\install-offline.ps1 -ReleaseDirectory .\artifacts\releases\1.1.0
```

That pins the public signing certificate. Later updates can be installed directly
from GitHub with:

```powershell
D:\Apps\PremiereCalendar\updater\install-github-release.ps1
```

The normal acceptance and user path is Settings > Local status >
`Signed GitHub release update` > `Update`. The Settings action runs the same installed
updater as a detached Windows PowerShell process and stores its transcript under
`D:\Apps\PremiereCalendarData\logs\application-updates`. During activation the page
may briefly show the reconnect UI while the service restarts.
Service shutdown is bounded to 15 seconds so a provider call that is slow to observe cancellation
cannot hold a signed update in `StopPending`; cancellation from the Windows service lifetime is
treated as a normal shutdown rather than an application crash.

The updater downloads the manifest first, selects exactly its declared package,
enforces download and expanded-size limits, verifies the pinned certificate,
RSA signature and SHA-256, rejects unsafe ZIP paths, installs under
`releases/<semver>`, and atomically switches the `current` junction. The Windows
service is repointed to `current\PremiereCalendar.exe`; activation succeeds only
when both liveness and the expected `/health/version` pass. A failed activation
restores the previous junction, service binary, and pre-update SQLite files.
Asset downloads retry transient transport failures up to three times with bounded
exponential backoff. Each failed attempt removes its partial file; exhausting the
retry budget leaves the active release untouched and records the failure in the log.
After the new application version passes its health/version checks, its signed
`updater-payload` refreshes the installed updater scripts transactionally. Existing
scripts are backed up until both replacements succeed, and are restored if activation
later rolls back. This lets future updater fixes travel inside the same verified package.

Persistent state is outside immutable releases under
`D:\Apps\PremiereCalendarData`. The first install copies legacy `App_Data`
without deleting it, providing a recovery boundary.

For release acceptance, start from an older installed version, click the Settings
update button, wait for the page to reconnect, and verify all of the following:

- Settings and `/health/version` report the new version.
- `current` targets `releases/<new-version>` and the Windows Service is running.
- The transcript ends with `installed and healthy`.
- Public HTTPS, readiness, HSTS, CSP and `nosniff` still pass.
- Clicking Update again reports `already the latest stable release` without restarting the service.

### Upgrade note for 1.1.4 and older

The pre-1.1.5 package layout did not carry updater scripts. Upgrade those installations
once with the 1.1.5-or-newer installer bundle, or have an administrator replace
`updater/install-github-release.ps1` and `updater/update-helper.ps1` from the reviewed
release bundle. After that one-time bootstrap, Settings updates refresh the updater
payload automatically.
