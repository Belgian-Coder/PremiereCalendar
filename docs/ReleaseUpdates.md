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

The first administrator-reviewed installation uses the local release directory:

```powershell
.\deploy\Updates\install-offline.ps1 -ReleaseDirectory .\artifacts\releases\1.1.0
```

That pins the public signing certificate. Later updates can be installed directly
from GitHub with:

```powershell
D:\Apps\PremiereCalendar\updater\install-github-release.ps1
```

The updater downloads the manifest first, selects exactly its declared package,
enforces download and expanded-size limits, verifies the pinned certificate,
RSA signature and SHA-256, rejects unsafe ZIP paths, installs under
`releases/<semver>`, and atomically switches the `current` junction. The Windows
service is repointed to `current\PremiereCalendar.exe`; activation succeeds only
when both liveness and the expected `/health/version` pass. A failed activation
restores the previous junction, service binary, and pre-update SQLite files.

Persistent state is outside immutable releases under
`D:\Apps\PremiereCalendarData`. The first install copies legacy `App_Data`
without deleting it, providing a recovery boundary.
