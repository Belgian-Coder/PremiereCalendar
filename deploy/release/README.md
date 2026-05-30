# Install Premiere Calendar

This folder contains everything needed for Windows. You do not need to install .NET.

## Install

1. Double-click `Install-PremiereCalendar.cmd`.
2. Click Yes when Windows asks for administrator permission.
3. When the installer finishes, open `http://localhost:5298`.
4. Add the TMDb API Read Access Token in Settings when the app redirects you there.

Premiere Calendar starts automatically after the computer reboots.

## What You Should See

| Check | Good result |
| --- | --- |
| App | `http://localhost:5298` opens |
| Health | `http://localhost:5298/health` says Healthy |
| Cards | All, Series, or Movies shows cards after the TMDb token is saved |

## TMDb Token

TMDb is required for live calendar data. You need the API Read Access Token, not the short API key.

After install, you can change it in the app:

1. Open `http://localhost:5298`.
2. Click the cog icon, or follow the automatic Settings redirect.
3. Paste the token in Source APIs.
4. Click Save.

## Open From Another Computer

1. Keep the computer running Premiere Calendar turned on.
2. Find its local network IP address.
3. On another computer, open `http://IP-ADDRESS:5298`.

If it does not open, check that both computers are on the same network and that Windows allowed the Premiere Calendar firewall prompt.

For a friendly LAN name, add a DNS record on your router or local DNS server that points to the host computer's LAN IP, then open `http://NAME:5298`.

The app has no built-in user login. Keep it on a trusted LAN or VPN and do not expose TCP `5298` directly to the public internet.

## Update

Extract the new release zip and double-click `Install-PremiereCalendar.cmd` again. Existing app-database settings, cache, and local data are kept. Older Windows Service environment credential variables are removed during update; re-enter any missing API credentials in the Settings page.

## Uninstall

Double-click `Uninstall-PremiereCalendar.cmd`.

The uninstall keeps local data by default. To remove everything, open PowerShell as Administrator and run:

```powershell
.\Uninstall-PremiereCalendar.ps1 -RemoveData
```

## Advanced

Use PowerShell only when you need a custom port, install folder, or data folder.

```powershell
.\Install-PremiereCalendar.ps1 -Port 8080 -InstallDirectory 'D:\Apps\PremiereCalendar' -DataDirectory 'D:\Data\PremiereCalendar'
```
