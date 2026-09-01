# Release, deployment, rollback, and acceptance

The supported artifact is the Linux container image. The local workflow first
qualifies source and builds one exact local image. `-Mode Package` can export
that same image to an OCI-compatible Docker archive with a SHA-256 manifest;
it never rebuilds or publishes.

Publication remains the manual `.github/workflows/container.yml` process. Use
`eng/Invoke-ReleaseLifecycle.ps1 -Action Publish`; it requires a passing clean
qualification for the current pushed commit, dispatches that manual workflow,
waits for build/smoke/Trivy/SBOM gates, and records the immutable GHCR digest.
A tag or successful workflow is publication proof, not deployment proof.

The protected NAS deployment source is
`\\NAS\Data\Backups\Infrastructure\NAS\services\premierecalendar`. Deploy the
accepted digest through that runbook/Portainer stack only after explicit
authorization, using `eng/Invoke-ReleaseLifecycle.ps1 -Action Deploy`. It
requires publication evidence and a fresh operator-validated native Portainer
backup, exports the active revision into the restricted recovery tree, and uses
the guarded authenticated API. Preserve PostgreSQL and backup volumes. Verify the running
image digest, container health/security state, PostgreSQL schema/connectivity,
fresh backup evidence, and both direct/private routes.

Rollback uses `eng/Invoke-ReleaseLifecycle.ps1 -Action Rollback` with the
previously accepted digest-pinned app image and source revision while preserving
the PostgreSQL volume. Data recovery uses only a checksum/manifest-valid
`pg_dump` restored and verified in an isolated database before promotion. The
historical SQLite archive is not a live fallback.

Read-only acceptance command:

```powershell
.\eng\Invoke-ProjectWorkflow.ps1 -Mode Installed \
  -InstalledBaseUri https://premiere.home.belgiancoder.be
```

It records readiness, version/build/source identity, database schema and
connectivity, root HTML, Blazor boot asset, security headers, and response
timings. Also perform a browser render check of the normal private URL: confirm
the Loading state clears, cards or the expected configured empty/setup state
renders, WebSocket/Blazor boot succeeds, and there are no application errors.
Do not invoke business actions during acceptance.

Keep four claims separate in reports: qualified source; published immutable
artifact; deployed digest/runtime; browser acceptance. None implies the next.
