# Local Observability And Productivity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add cache inspection, background-job history, visible-week freshness, refresh modes, filter presets, Actions palette, mobile day navigation polish, release checks, settings backup/restore, and subtle changed-since-last-visit indicators.

**Architecture:** Add focused backend services for local app state, diagnostics, presets, release checks, and backups, backed by the existing SQLite `AppParameters` table where persistence is needed. Keep Calendar changes compact by reusing the existing command bar, cache freshness, query progress, filter state, and selected-day model.

**Tech Stack:** ASP.NET Core Blazor Server on .NET 11 preview, SQLite via `Microsoft.Data.Sqlite`, existing file caches, existing bUnit component tests, xUnit unit tests, and Playwright visual validation.

---

### Task 1: Shared Local App State Store

**Files:**
- Create: `PremiereCalendar/Services/IAppStateStore.cs`
- Create: `PremiereCalendar/Services/SqliteAppStateStore.cs`
- Modify: `PremiereCalendar/Program.cs`
- Test: `tests/PremiereCalendar.UnitTests/AppStateStoreTests.cs`

- [ ] Write a failing round-trip test that saves two namespaced JSON values, reloads them from a new store, and verifies both values survive independently.
- [ ] Implement `IAppStateStore.GetValueAsync`, `SetValueAsync`, `DeleteValueAsync`, and `GetValuesByPrefixAsync` using the existing `AppParameters` schema.
- [ ] Register the store as a singleton in DI.

### Task 2: Diagnostics Services

**Files:**
- Create: `PremiereCalendar/Services/AppDiagnosticsModels.cs`
- Create: `PremiereCalendar/Services/CacheInspectorService.cs`
- Create: `PremiereCalendar/Services/BackgroundJobTimelineService.cs`
- Modify: `PremiereCalendar/Services/CurrentWeekCalendarWarmupService.cs`
- Modify: `PremiereCalendar/Services/ImdbDatasetRefreshService.cs`
- Modify: `PremiereCalendar/Services/ProviderDeltaSyncService.cs`
- Modify: `PremiereCalendar/Services/AdjacentWeekPrefetcher.cs`
- Test: `tests/PremiereCalendar.UnitTests/AppDiagnosticsServiceTests.cs`

- [ ] Write failing tests for cache summaries and background timeline retention.
- [ ] Implement cache summaries from configured calendar and image cache directories.
- [ ] Implement background job event recording with bounded history.
- [ ] Record warmup, cache maintenance, IMDb import, provider delta sync, and adjacent prefetch lifecycle events.

### Task 3: Calendar Productivity State

**Files:**
- Create: `PremiereCalendar/Services/CalendarProductivityModels.cs`
- Create: `PremiereCalendar/Services/CalendarPresetService.cs`
- Create: `PremiereCalendar/Services/CalendarVisitChangeService.cs`
- Test: `tests/PremiereCalendar.UnitTests/CalendarProductivityServiceTests.cs`

- [ ] Write failing tests for saving/applying named filter presets.
- [ ] Write failing tests for visit delta detection using canonical item IDs.
- [ ] Store presets per route mode without week pinning.
- [ ] Store last seen IDs per route/week/filter key and return subtle new/removed counts.

### Task 4: Backup And Release Services

**Files:**
- Create: `PremiereCalendar/Services/SettingsBackupService.cs`
- Create: `PremiereCalendar/Services/ReleaseUpdateService.cs`
- Modify: `PremiereCalendar/Program.cs`
- Test: `tests/PremiereCalendar.UnitTests/BackupAndReleaseServiceTests.cs`

- [ ] Write failing tests for export/import of settings plus local app state.
- [ ] Write failing tests for release version comparison.
- [ ] Implement JSON backup export/import through service methods.
- [ ] Implement guarded GitHub latest-release checking; expose update availability without silent self-mutation.

### Task 5: Calendar UI

**Files:**
- Modify: `PremiereCalendar/Components/Pages/Calendar.razor`
- Modify: `PremiereCalendar/Components/Shared/CalendarWeek.razor`
- Modify: `PremiereCalendar/wwwroot/app.css`
- Test: `tests/PremiereCalendar.ComponentTests/CalendarPageTests.cs`
- Test: `tests/PremiereCalendar.ComponentTests/CalendarWeekTests.cs`

- [ ] Add a compact data freshness card that uses existing cache metadata plus refresh mode state.
- [ ] Replace the single refresh action with `Update` and `Refresh sources` refresh options.
- [ ] Add save/apply preset UI to the Actions palette without moving the calendar down materially.
- [ ] Add Actions palette overlay with keyboard shortcut and route/refresh/filter/preset actions.
- [ ] Add subtle changed-since-last-visit badges.
- [ ] Add mobile day jump polish: shorter day labels, selected-day affordance, and horizontal snap behavior.

### Task 6: Settings UI

**Files:**
- Modify: `PremiereCalendar/Components/Pages/Settings.razor`
- Modify: `PremiereCalendar/wwwroot/app.css`
- Test: `tests/PremiereCalendar.ComponentTests/SettingsPageTests.cs`

- [ ] Add a Local status section with cache inspector, background job timeline, release checker, and backup/restore controls.
- [ ] Add actions for check update, export backup, and import backup.
- [ ] Keep destructive actions explicit and avoid automatic update installation.

### Task 7: Verification, Playwright, Deploy

**Files:**
- Modify docs if UI or install behavior changes.

- [ ] Run targeted unit/component tests while implementing each task.
- [ ] Run `dotnet test .\PremiereCalendar.slnx --no-restore`.
- [ ] Run the app and validate desktop/mobile with Playwright screenshots.
- [ ] Deploy with `.\Install-PremiereCalendar.ps1 -NoElevate`.
- [ ] Verify service is running and `/health` returns `200 Healthy`.
- [ ] Commit and push the finished branch.
