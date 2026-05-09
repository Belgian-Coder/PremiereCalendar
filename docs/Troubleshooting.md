# Troubleshooting

Use this page when something looks wrong. Start at the top.

## Quick Checks

1. Open the app.
2. Click the cog icon.
3. Check that TMDb is configured.
4. Click Save.
5. Go back to the calendar.
6. Click Refresh.

If the app is installed as a service, also open:

```text
http://localhost:5298/health
```

You should see a healthy response.

## Shortcuts

| Action | Shortcut |
| --- | --- |
| Previous day | Left Arrow |
| Next day | Right Arrow |
| Yesterday from top edge | Keep scrolling upward |
| Tomorrow from bottom edge | Keep scrolling downward |
| Show source details | Show sources |
| Hide source details | Hide sources |
| Open filters | Filter button |
| Open settings | Cog icon |

Arrow keys are ignored while you type in an input field.

## Common Problems

| Problem | What to do |
| --- | --- |
| No cards load | Add a TMDb token in Settings, save, then click Refresh |
| Refresh seems slow | Wait for the 30-second budget, then click Show sources |
| TVmaze is always slow | Use new-series mode, choose fewer languages, or disable TVmaze schedule discovery |
| Watchmode shows no cards | That is expected; Watchmode is used as availability fallback, not broad discovery |
| IMDb scores are missing | Wait for the IMDb dataset import to finish, then Refresh |
| Rotten Tomatoes or Metacritic is missing | Enable OMDb with a working API key; free OMDb keys can hit daily limits |
| Posters are missing | Check artwork provider settings, then Refresh |
| Settings do not match `appsettings.json` | The Settings page wins; change values in the app |
| Sonarr add fails | The series needs a TVDB ID and valid Sonarr settings |
| Radarr add fails | Check Radarr URL, API key, root folder, and quality profile |

## What The Source Panel Means

The Loaded-source filters box is collapsed by default.

- The number is the total cards found.
- Click Show sources to see each provider.
- A provider with `0 cards` did not add matching cards for the current filters.
- A timed-out provider was skipped so the page could finish.
- Clicking a source chip filters the visible cards to that source only.

## Cache

Calendar, image, IMDb rating, OMDb response, and provider-sync cache data are stored on disk. They survive app restarts, crashes, and updates.

Refresh does not delete everything. It checks the week again and reuses useful existing details when it can.

If the cache looks wrong, delete only the affected week under:

```text
App_Data/cache/calendar
```

Installed service data is usually under:

```text
C:\ProgramData\PremiereCalendar
```

## Settings

The app stores Settings-page values in a local database, not in the browser.

```mermaid
flowchart LR
    Settings["Settings page"] --> Db["Local settings database"]
    Fallback["First-run fallback settings"] --> App["Running app"]
    Db --> App
```

This is why changing `appsettings.json` may not change the running app after you already saved settings in the UI.

## Providers

| Provider | Use it for |
| --- | --- |
| TMDb | Main calendar data. Required. |
| IMDb datasets | IMDb score and vote count. Included. |
| Trakt | Extra candidates. Optional. |
| TVmaze | Extra series schedule data. Optional. |
| Watchmode | Streaming availability fallback. Optional. |
| OMDb | Rotten Tomatoes, Metacritic, plot, poster fallback. Optional. |
| Fanart.tv, TheTVDB, Wikimedia | Poster fallback. Optional. |
| SIMKL | Account/library sync state. Optional. |
| Sonarr, Radarr | Add buttons. Optional. |

## When You Still Need Help

Send:

- the page URL,
- which day is selected,
- which filters are enabled,
- what the Loaded-source filters panel says after clicking Show sources.
