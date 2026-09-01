# Configuration and mutable state

Non-secret defaults live in `PremiereCalendar/appsettings.json`. Infrastructure
overrides use ASP.NET Core environment variables and Docker secrets. Operator
settings, provider credentials, view-sync state, job history, caches, and other
mutable application data live in the configured SQLite/PostgreSQL persistence
store or `App_Data`; they do not belong in Git.

Container production uses PostgreSQL and a password file mounted as a Docker
secret. Never commit `.secrets`, `.env`, database files, dumps, manifests that
contain private topology, logs, caches, or exported Settings backups. The
existing `.gitignore` and `.dockerignore` enforce these boundaries.

The application already provides its authenticated-by-network Settings UI and
application persistence API boundary for operator values. Do not give an
external workflow direct production database write access. Any future mutable
setting migration must add validation, revision/optimistic concurrency,
idempotency, and audit history at the application boundary; preserve and
verify a rollback snapshot before removing an old source.

`appsettings.json` still contains retired Windows updater defaults for
backward-compatible Windows packages. Container Compose explicitly disables
the updater and overrides persistence with PostgreSQL. Do not interpret those
defaults as a supported Windows runtime or as current mutable operator state.
