# Local workflow harness

`eng/Invoke-ProjectWorkflow.ps1` is the canonical entry point. It writes
machine-readable, secret-free evidence under ignored `artifacts/workflow/`.

## Modes

```powershell
# Normal edit loop: locked restore, one build, unit and component tests.
.\eng\Invoke-ProjectWorkflow.ps1 -Mode Fast

# Deterministic restore/build only.
.\eng\Invoke-ProjectWorkflow.ps1 -Mode Build

# Focused scopes: locked restore, one Release build of only the required graph,
# then the selected tests.
.\eng\Invoke-ProjectWorkflow.ps1 -Mode TestUnit
.\eng\Invoke-ProjectWorkflow.ps1 -Mode TestComponent
.\eng\Invoke-ProjectWorkflow.ps1 -Mode TestIntegration
.\eng\Invoke-ProjectWorkflow.ps1 -Mode TestBrowser
.\eng\Invoke-ProjectWorkflow.ps1 -Mode TestPostgreSql

# Release-grade local source gate: exact identity, locked restore, one build,
# all fake-provider tests, Playwright, PostgreSQL, image build and image smoke.
.\eng\Invoke-ProjectWorkflow.ps1 -Mode Qualify

# Re-run after interruption; passing stages with the same identity resume.
.\eng\Invoke-ProjectWorkflow.ps1 -Mode Qualify

# Read-only validation of an installed/deployed instance.
.\eng\Invoke-ProjectWorkflow.ps1 -Mode Installed \
  -InstalledBaseUri https://premiere.home.belgiancoder.be

# Create an offline OCI archive from a passing clean-tree qualification.
.\eng\Invoke-ProjectWorkflow.ps1 -Mode Package -Version 1.2.3
```

Do not call `dotnet restore`, `dotnet build`, `dotnet test`, Playwright, Docker
Compose, Docker build, Trivy, or installed HTTP probes ad hoc. The modes above
are the reproducible local AI/operator interface for those actions.

External lifecycle actions use the companion harness and always require an
explicit confirmation switch in addition to PowerShell confirmation:

```powershell
# Publish the clean, pushed, locally qualified commit and retain digest evidence.
.\eng\Invoke-ReleaseLifecycle.ps1 -Action Publish -Version 1.2.3 `
  -ConfirmExternalAction

# Deploy only the published digest through guarded Portainer.
.\eng\Invoke-ReleaseLifecycle.ps1 -Action Deploy `
  -PublicationEvidence .\artifacts\workflow\publication-1.2.3.json `
  -ValidatedPortainerBackup <restricted-backup-path> `
  -InstalledBaseUri https://premiere.home.belgiancoder.be `
  -ConfirmExternalAction

# Restore the retained prior app digest without replacing PostgreSQL.
.\eng\Invoke-ReleaseLifecycle.ps1 -Action Rollback `
  -RollbackImageDigest <previous-ghcr-digest> `
  -RollbackSourceRevision <previous-40-character-commit> `
  -ValidatedPortainerBackup <restricted-backup-path> `
  -InstalledBaseUri https://premiere.home.belgiancoder.be `
  -ConfirmExternalAction
```

Use `-WhatIf` to inspect an external action without dispatching or mutating it.
Never add `-ConfirmExternalAction` on an agent's own initiative. Publish checks
clean qualification and upstream identity. Deploy rechecks that chain, accepts
only an immutable project digest, uses stack ID 20 through the DPAPI-backed
guarded Portainer client, retains recovery exports outside Git, and verifies
the installed source revision. Rollback requires the previously accepted
digest and source revision explicitly; it does not touch database volumes.

Fast mode is deliberately not release evidence. Qualify records the commit,
branch, upstream, dirty state, source fingerprint, every NuGet lock-file hash,
tool versions, container inputs, relevant shipped asset hashes, attempts,
timestamps, durations, results, logs, and the five slowest stages.

Resume is fail-closed: the run directory is keyed by the complete source and
dependency fingerprint. Any source, lock-file, generated-asset, tool, or
harness change creates a different identity. `-NoResume` repeats the same
identity from scratch. Packaging rejects a failed, stale, incomplete, or dirty
qualification and verifies the qualified local image ID before export.

The complete mode builds shared .NET outputs once, then runs test assemblies
without restore/build duplication. Fast and qualification use reduced solution
graphs so test hosts with no selected tests are never launched. Endpoint
integration tests reuse deterministic app hosts, and pure timeline policy tests
use an in-memory store while dedicated tests retain SQLite durability coverage.
Browser and PostgreSQL checks are kept sequential because they use
process/database runtime resources. Container
packaging creates one explicit minimal tar context and packages the already
compiled, tested portable publish output into the Linux runtime image. This
avoids a second SDK compilation while retaining the same runtime-base upgrade,
smoke, and scan gates. Hosted/public Docker builds still use the default
source-build target. Packaging exports that exact qualified image and does not
rebuild it. The local compiler server remains enabled because deterministic
SDK outputs do not require paying compiler-process startup on every build.

`artifacts/workflow/latest-qualification.json` points to the latest report.
Stage logs and checkpoints remain in the identity-specific directory. These
files are evidence, not source, and must not be committed.
