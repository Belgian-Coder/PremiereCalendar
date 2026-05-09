# Premiere Calendar Release Package

This package contains a self-contained Windows x64 build. It does not require the .NET runtime to be installed on the target machine.

## Install

1. Extract the zip to a temporary folder.
2. Open PowerShell as Administrator in the extracted folder.
3. Run:

```powershell
.\Install-PremiereCalendar.ps1 -TmdbBearerToken 'YOUR_TMDB_V4_READ_ACCESS_TOKEN'
```

You can also double-click `Install-PremiereCalendar.cmd`; it opens an elevated PowerShell window and prompts for the TMDb token. Source API keys can also be entered later from the app's Settings page; values saved there live in the SQLite settings database and override installer-provided environment fallback values.

The installer:

- copies the app to `C:\Program Files\PremiereCalendar`;
- stores calendar and image cache files under `C:\ProgramData\PremiereCalendar`;
- creates or updates the `PremiereCalendar` Windows Service;
- configures the service to restart after failures;
- removes old user Startup-folder shortcuts for Premiere Calendar so duplicate manual instances are not launched;
- opens inbound TCP `5298` for `LocalSubnet`;
- health-checks `http://localhost:5298/health`.

Optional free-source keys can be installed at the same time:

```powershell
.\Install-PremiereCalendar.ps1 `
  -TmdbBearerToken 'YOUR_TMDB_V4_READ_ACCESS_TOKEN' `
  -TraktClientId 'YOUR_TRAKT_CLIENT_ID' `
  -FanartApiKey 'YOUR_FANART_TV_KEY' `
  -OmdbApiKey 'YOUR_OMDB_KEY' `
  -TheTvdbApiKey 'YOUR_THETVDB_KEY'
```

## Update

Extract the new release zip and run the same install command again from an elevated PowerShell session:

```powershell
.\Install-PremiereCalendar.ps1
```

Existing service secrets are preserved when the matching parameter is omitted. Existing cache/data files are preserved because they live in `C:\ProgramData\PremiereCalendar`.

## Uninstall

Run from an elevated PowerShell session:

```powershell
.\Uninstall-PremiereCalendar.ps1
```

The uninstall script removes the service, firewall rule, and installed binaries. It preserves `C:\ProgramData\PremiereCalendar` by default. Add `-RemoveData` when you also want to delete caches and local data.

You can also double-click `Uninstall-PremiereCalendar.cmd`.

## Custom Port Or Install Path

```powershell
.\Install-PremiereCalendar.ps1 -Port 8080 -InstallDirectory 'D:\Apps\PremiereCalendar' -DataDirectory 'D:\Data\PremiereCalendar'
```

If you change the port, the installer updates the Windows Service environment and firewall rule.
