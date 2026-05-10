# Premiere Calendar

Premiere Calendar is a web page you run yourself. It shows upcoming movie and series premieres by week.

Use it to:

- Browse new movies and series.
- Filter by language, provider, score, runtime, and more.
- See where each card came from.
- Add verified movies to Radarr and verified series to Sonarr, if you use those apps.

TMDb is required for live data. Everything else is optional.

## Screenshots

| Dark mode | Light mode |
| --- | --- |
| ![Series calendar in dark mode](docs/images/readme/series-dark.png) | ![Series calendar in light mode](docs/images/readme/series-light.png) |

| Movies | Filters |
| --- | --- |
| ![Movie calendar in dark mode](docs/images/readme/movies-dark.png) | ![Filter drawer in dark mode](docs/images/readme/filters-dark.png) |

## First Setup

1. Start the app.
2. Open `http://localhost:5298`.
3. Click the cog icon.
4. Paste your TMDb API read access token.
5. Click Save.
6. Go back to All, Series, or Movies.

No TMDb token means the app cannot load real calendar data.

## Run It

For a quick local run:

```powershell
.\Run-PremiereCalendar.ps1
```

To install or update the Windows service:

```powershell
.\Install-PremiereCalendar.ps1
```

The service starts automatically after a reboot.

## Navigation And Shortcuts

| What you want | What to do |
| --- | --- |
| Go to the previous day | Press Left Arrow |
| Go to the next day | Press Right Arrow |
| Pick a day in the week | Click the day button |
| Go to another week | Use Previous, This week, or Next |
| Move from the top of a day to yesterday | Keep scrolling upward until the message appears |
| Move from the bottom of a day to tomorrow | Keep scrolling downward until the message appears |
| See which sources loaded | Click Show sources in Loaded-source filters |
| Hide source details again | Click Hide sources |
| See cache age | Check the cache pills under the toolbar |
| Change filters | Click the filter button |
| Change settings | Click the cog icon |

The arrow keys do not take over while you are typing in a text box.

## Sync Viewing Between Devices

View sync is optional. It lets two or more browsers follow the same calendar URL.

1. Open Settings.
2. Find View sync.
3. Give this browser a name, such as `Office PC`.
4. Create or select a group.
5. Turn on Sync this browser.
6. Save view sync.

Do the same on another computer and choose the same group. When one grouped browser changes day, week, route, or filters, the other grouped browsers follow the latest view. Use Ungroup this device to stop syncing that browser.

All, Series, and Movies keep separate synced views. A browser on Series follows only the latest Series URL, not Movies or All. Only calendar URLs are synced. Settings, About, external links, credentials, and host names are not synced.

## What The Cards Mean

- Normal cards are verified through TMDb.
- Unverified cards are shown below normal cards when an outside source found something that does not match TMDb yet.
- `Source` chips show provider, channel, or streaming source names when known.
- Scores can be `n/a` when the score provider has no data or is not configured.

## Providers

| Provider | Needed? | What it does |
| --- | --- | --- |
| TMDb | Required | Main calendar data, posters, trailers, metadata |
| IMDb datasets | Included | IMDb scores and vote counts, cached locally |
| Trakt | Optional | Extra movie and new-series candidates |
| TVmaze | Optional | Extra series schedule data |
| Watchmode | Optional | Streaming availability fallback |
| OMDb | Optional | Rotten Tomatoes, Metacritic, plot and poster fallback |
| Fanart.tv, TheTVDB, Wikimedia | Optional | Poster fallback when TMDb has no poster |
| SIMKL | Optional | Account/library sync state |
| Sonarr, Radarr | Optional | Add buttons on verified cards |

## Cache

The app keeps calendar data, images, IMDb scores, OMDb responses, view-sync groups, and small provider sync markers on disk. This means cached data survives restarts, crashes, and updates.

Click Refresh when you want the current week checked again. Refresh keeps useful existing data where possible and fills in changes or missing details. TMDb, TVmaze, and SIMKL have change/activity endpoints; the app records those checks so later cache decisions have dates to compare against. IMDb scores come from the daily IMDb dataset, not a per-item change API.

## Common Problems

| Problem | Try this |
| --- | --- |
| No cards load | Add a TMDb token in Settings, then click Refresh |
| IMDb scores are missing | Wait for the IMDb dataset import to finish, then Refresh |
| Rotten Tomatoes or Metacritic is missing | Enable OMDb with a working API key; OMDb free keys can hit daily limits |
| A source looks slow | Click Show sources to see which provider is still loading |
| TVmaze is slow | Use narrower filters or disable TVmaze schedule discovery in Settings |
| Settings look wrong | Change them in the app Settings page, not in `appsettings.json` |
| Posters are missing | Check optional artwork providers, then Refresh |

More help: [Troubleshooting](docs/Troubleshooting.md).

## Test It

```powershell
.\.dotnet\dotnet.exe test .\PremiereCalendar.slnx
```

Tests use fake providers. They must not call live external services.

## More Documentation

Most users only need this README and [Troubleshooting](docs/Troubleshooting.md).

Technical references:

- [Release Installer](docs/ReleaseInstaller.md)
- [Configuration](docs/Configuration.md)
- [How It Works](docs/HowItWorks.md)
- [Architecture](docs/Architecture.md)
- [Performance Notes](docs/PerformanceReview.md)
- [Testing](docs/Testing.md)
