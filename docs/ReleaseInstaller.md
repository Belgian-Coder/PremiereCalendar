# Release Installer

Premiere Calendar ships as a self-contained Windows x64 release zip. The target machine does not need a separate .NET runtime.

The installer sets up a Windows Service, so the app starts again after reboot.

## Simple Install

Use these steps on the computer that should host Premiere Calendar.

1. Copy the release zip to the computer.
2. Right-click the zip and choose Extract All.
3. Open the extracted folder.
4. Double-click `Install-PremiereCalendar.cmd`.
5. Click Yes when Windows asks for administrator permission.
6. Paste the TMDb API Read Access Token when asked. Press Enter to skip this and add it later in Settings.
7. Open `http://localhost:5298`.

To use the app from another computer on the same network, open `http://HOST-IP:5298`.

Good signs after install:

- `http://localhost:5298` opens.
- `http://localhost:5298/health` says Healthy.
- Cards load after the TMDb token is saved.

## For Maintainers: Build A Release Package

From the repository root:

```powershell
.\Build-ReleasePackage.ps1
```

The root wrapper calls `deploy\Build-ReleasePackage.ps1`; use the deploy script directly only when you need its lower-level parameters.

The build script:

1. runs `dotnet test --no-restore`;
2. publishes `PremiereCalendar` in Release mode for `win-x64`;
3. clears secrets from the packaged `appsettings.json`;
4. copies installer, uninstaller, and documentation files into the package;
5. writes `VERSION.txt`;
6. creates `artifacts\release\PremiereCalendar-<version>-win-x64.zip`;
7. writes a matching `.sha256` checksum file.

Use `-Version 1.2.3` to pin a release version:

```powershell
.\Build-ReleasePackage.ps1 -Version 1.2.3
```

Use `-SkipTests` only when tests have already been run for the exact commit/package being released.

## Advanced Install Command

The double-click installer is the recommended path. Use PowerShell only when you need to pass values up front.

```powershell
.\Install-PremiereCalendar.ps1 -TmdbBearerToken 'YOUR_TMDB_V4_READ_ACCESS_TOKEN'
```

You can install without source keys and enter them later on the app's Settings page. Installer-provided keys are first-run fallback values. Values saved in the Settings page take precedence after they are saved there.

By default this installs:

- binaries: `C:\Program Files\PremiereCalendar`
- cache and local data: `C:\ProgramData\PremiereCalendar`
- settings database: `C:\ProgramData\PremiereCalendar\data\premiere-calendar.db`
- service: `PremiereCalendar`
- URL: `http://0.0.0.0:5298`
- firewall: inbound TCP `5298` from `LocalSubnet`

The installer verifies `http://localhost:5298/health` before it finishes.

The installer also removes old user Startup-folder shortcuts for Premiere Calendar. The Windows Service is the only supported automatic-start mechanism, because it starts after reboot even before the user logs in and it has restart-on-failure recovery.

## Optional Source Keys

Optional free-source keys can be passed during install:

```powershell
.\Install-PremiereCalendar.ps1 `
  -TmdbBearerToken 'YOUR_TMDB_V4_READ_ACCESS_TOKEN' `
  -TraktClientId 'YOUR_TRAKT_CLIENT_ID' `
  -FanartApiKey 'YOUR_FANART_TV_KEY' `
  -OmdbApiKey 'YOUR_OMDB_KEY' `
  -TheTvdbApiKey 'YOUR_THETVDB_KEY'
```

These values are stored in the Windows Service `Environment` registry value as ASP.NET Core environment variables. They are not written into packaged `appsettings.json`. After installation, the Settings page can edit the same source API values into the SQLite settings database, which overrides the service-environment fallback.

## Update

Extract the new zip and double-click `Install-PremiereCalendar.cmd` again.

PowerShell alternative:

```powershell
.\Install-PremiereCalendar.ps1
```

The installer stops the service, replaces the installed binaries, preserves the existing service secrets when matching parameters are omitted, preserves `C:\ProgramData\PremiereCalendar`, restarts the service, and health-checks the app.

If an older install has an in-place `App_Data` folder under the binary directory, the installer excludes that folder from the mirror copy so upgrades do not delete it.

Pass a secret parameter again when you want to rotate it:

```powershell
.\Install-PremiereCalendar.ps1 -TmdbBearerToken 'NEW_TMDB_TOKEN'
```

## Custom Port Or Paths

```powershell
.\Install-PremiereCalendar.ps1 `
  -Port 8080 `
  -InstallDirectory '<install-directory>' `
  -DataDirectory '<data-directory>'
```

When the port changes, the installer updates `ASPNETCORE_URLS`, the app `Urls` setting, and the Windows firewall rule.

Use `-SkipFirewall` when another firewall or reverse proxy manages access.

## Uninstall

From an elevated PowerShell session:

```powershell
.\Uninstall-PremiereCalendar.ps1
```

You can also double-click `Uninstall-PremiereCalendar.cmd`.

The uninstall script removes the service, firewall rule, and installed binaries. It preserves cache/data by default.

To remove everything:

```powershell
.\Uninstall-PremiereCalendar.ps1 -RemoveData
```

## Operational Checks

Useful commands on the target machine:

```powershell
Get-Service PremiereCalendar
Invoke-WebRequest -UseBasicParsing http://localhost:5298/health
Get-NetFirewallRule -DisplayName 'Premiere Calendar 5298'
```

The service runs as `LocalSystem` by default through `sc.exe`. If you need a lower-privileged account later, configure that service account with read access to the install directory and read/write access to the data directory.
