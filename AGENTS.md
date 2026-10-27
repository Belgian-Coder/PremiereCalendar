# Build and release policy

- Do not use GitHub Actions or hosted GitHub CI/CD, including manual workflow dispatches.
- Run builds, tests, vulnerability scans, SBOM generation and container publication locally.
- Keep the GitHub CI and Container workflows disabled. Do not enable or dispatch them.
- Preserve vulnerability checks and runtime verification when publishing locally.
- Deploy digest-pinned images and preserve PostgreSQL, application data and backups.
