# PremiereCalendar agent guide

PremiereCalendar is a .NET 11 Blazor application. Its supported runtime is a
digest-pinned Linux container with a dedicated PostgreSQL 18 database on the
NAS. The former Windows service and SQLite installation are retired; do not
treat the legacy Windows packaging scripts as the deployment default.

Before editing, record `git status --short --branch`, `git rev-parse HEAD`, and
the relevant runtime state. Preserve unrelated changes. Never put credentials,
database passwords, private backup contents, or operator state in source or
workflow evidence.

The default local entry point is:

```powershell
.\eng\Invoke-ProjectWorkflow.ps1 -Mode Fast
```

Do not invent lifecycle commands. Build and every supported focused test scope
are modes of that harness; publication, deployment, and rollback are actions of
the confirmation-gated `eng/Invoke-ReleaseLifecycle.ps1` companion. The exact
command map and prerequisites are in `docs/agents/Harness.md`.

Before proposing a release, run the exact source qualification once:

```powershell
.\eng\Invoke-ProjectWorkflow.ps1 -Mode Qualify
```

Do not substitute ad-hoc `dotnet test` or repeated release-script validation
for this gate. Packaging may consume only a passing, current, clean-tree
qualification report. Publication and NAS deployment remain explicit operator
actions and are never implied by a passing local report. Never pass
`-ConfirmExternalAction` unless the user explicitly authorizes that exact
external action in the current task.

Read the routed references that apply to the task:

- [Project structure and task routing](docs/agents/ProjectStructure.md)
- [Engineering and safety rules](docs/agents/EngineeringSafety.md)
- [Local harness modes](docs/agents/Harness.md)
- [Generated assets](docs/agents/GeneratedAssets.md)
- [Configuration and mutable state](docs/agents/ConfigurationState.md)
- [Release, deployment, rollback, and acceptance](docs/agents/ReleaseDeploymentAcceptance.md)

More-specific `AGENTS.md` files override this guide for their subtree. There
are currently no more-specific instruction files.
