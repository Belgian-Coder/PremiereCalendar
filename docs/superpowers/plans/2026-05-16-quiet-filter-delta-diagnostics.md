# Quiet Filter Header And Delta Diagnostics Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the calendar header less noisy by replacing inline filter chips with a filter-count badge, and make provider delta failures visible and actionable.

**Architecture:** Keep calendar filter state in `Calendar.razor`, but expose it through a compact count instead of a chip strip. Keep provider sync isolation in `ProviderDeltaSyncService`, but return provider-specific failure details to the timeline. Keep settings diagnostics in `Settings.razor`, but prioritize failures over routine success noise.

**Tech Stack:** Blazor Interactive Server, bUnit component tests, xUnit service tests, Playwright visual validation, Windows Service publish.

---

### Task 1: Calendar Header Filter Count

**Files:**
- Modify: `PremiereCalendar/Components/Pages/Calendar.razor`
- Modify: `PremiereCalendar/wwwroot/app.css`
- Test: `tests/PremiereCalendar.ComponentTests/CalendarPageTests.cs`

- [x] Write failing component tests proving active filter chips and header clear controls are gone, while a `Filters` button shows a count badge when filters are active.
- [x] Run the targeted component tests and confirm they fail because the current header still renders `active-filter-strip`.
- [x] Replace `ActiveFilterLabels()` header rendering with `ActiveFilterCount()` and render one compact filter button.
- [x] Move preset save/apply controls into the Actions palette and rename header actions to `Update`, `Refresh sources`, and `Actions`.
- [x] Run the targeted component tests and confirm they pass.

### Task 2: Provider Delta Diagnostics

**Files:**
- Modify: `PremiereCalendar/Services/ProviderDeltaSyncService.cs`
- Modify: `PremiereCalendar/Services/BackgroundJobTimelineService.cs`
- Modify: `PremiereCalendar/Components/Pages/Settings.razor`
- Test: `tests/PremiereCalendar.UnitTests/ProviderDeltaSyncServiceTests.cs`
- Test: `tests/PremiereCalendar.ComponentTests/SettingsPageTests.cs`

- [x] Write failing unit tests proving TMDb timeouts produce a provider-specific timeline reason and TMDb lookback uses a 14-day inclusive range.
- [x] Write a failing settings component test proving a provider delta failure stays visible above routine prefetch entries.
- [x] Run the targeted tests and confirm they fail for the expected old behavior.
- [x] Return provider failure records from the provider sync helpers, record detailed failure messages, and use a 14-day inclusive TMDb range.
- [x] Increase timeline retention and render failures before routine entries in the settings timeline.
- [x] Run the targeted tests and confirm they pass.

### Task 3: Validation, Publish, And Git

**Files:**
- Modify docs screenshots only if Playwright shows the README images are stale.

- [x] Run the full test suite with `dotnet test`.
- [x] Run Playwright against the local app and inspect desktop/mobile screenshots.
- [x] Publish locally with `.\Install-PremiereCalendar.ps1 -NoElevate`.
- [x] Verify `/health` returns healthy from the deployed service.
- [x] Commit and push the branch.
