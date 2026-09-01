# Project structure and task routing

| Area | Purpose | Primary checks |
| --- | --- | --- |
| `PremiereCalendar/` | Blazor web app, HTTP endpoints, persistence, provider integrations | unit, component, integration |
| `PremiereCalendar/wwwroot/` | Shipped CSS, JavaScript, images, and vendored Bootstrap assets | build, UI regression, container smoke |
| `tests/PremiereCalendar.UnitTests/` | Pure service, security, update, and contract tests | fast and complete modes |
| `tests/PremiereCalendar.ComponentTests/` | bUnit rendering and interaction tests | fast and complete modes |
| `tests/PremiereCalendar.IntegrationTests/` | Fake-provider HTTP, SQLite, app host, and PostgreSQL tests | complete mode |
| `tests/PremiereCalendar.BrowserTests/` | Playwright browser, accessibility, and layout regression tests | complete mode |
| `eng/` | Canonical workflow, Docker/WSL helpers, and explicit release tooling | script syntax plus complete mode |
| `deploy/` | Retired Windows installer/updater compatibility and signed-release code | test when touched; not NAS deployment |
| `compose.yaml`, `Dockerfile` | Supported production image/runtime contract | complete mode container build and smoke |
| `compose.dev.yaml` | Local WSL Docker/PostgreSQL development | local development and PostgreSQL gate |
| `.github/workflows/` | Manual-only hosted qualification and container publication | never add automatic triggers |

The app uses the SDK pinned by `global.json`, NuGet lock files beside each
project, xUnit, bUnit, Playwright, Docker Compose inside Ubuntu 24.04 on WSL,
and PostgreSQL 18. There is no Node package manager or generated web-asset
pipeline.

Route UI work through component and browser tests, persistence work through
unit/integration plus the PostgreSQL gate, container work through the image
build/smoke, and release code through its security-focused unit tests and the
complete qualification.
