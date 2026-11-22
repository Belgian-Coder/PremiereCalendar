# Release gates shared by Load-Image, Deploy-Release and Close-Release. Dot-source after ReleaseCommon.ps1.
# Every function throws on the first failed gate and prints nothing secret (hashes, counts and names only).

function Assert-ReleaseSourceEvidence {
    # Source-bound gate: executed test suites, the reviewers' record and the tested bytes all identify the same commit.
    param([Parameter(Mandatory)] $Context, [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedRevision)
    $bundle = $Context.Bundle
    Assert-CleanSourceAtRevision -Revision $ExpectedRevision

    $fingerprints = Join-Path $bundle 'tested-source-fingerprints.json'
    $fingerprintHash = Get-FileSha256 $fingerprints
    $tests = @(Read-Json (Join-Path $bundle 'test-summary.json'))
    foreach ($suite in 'unit', 'component', 'integration') {
        if (@($tests | Where-Object { $_.suite -eq $suite }).Count -ne 1) { throw "Exactly one executed '$suite' test suite is required in test-summary.json." }
    }
    foreach ($entry in $tests) {
        if ($entry.passed -lt 1 -or $entry.failed -ne 0 -or @($entry.missingEvidence).Count -gt 0) { throw "Test suite '$($entry.suite)' failed, ran no cases or has missing evidence." }
        if ($entry.sourceRevision -cne $ExpectedRevision -or $entry.testedSourceFingerprintsSha256 -cne $fingerprintHash) { throw "Test suite '$($entry.suite)' is not bound to this commit and the recorded source fingerprints." }
    }
    Invoke-ReleasePython (Join-Path $PSScriptRoot 'freeze_test_source.py') --repo $Context.Repo --revision $ExpectedRevision --verify $fingerprints

    $review = Read-Json (Join-Path $bundle 'source-review.json')
    if ($review.sourceRevision -cne $ExpectedRevision -or $review.openP0P2 -ne 0) { throw 'The source review identifies another commit or has open (or unknown) P0-P2 findings.' }
    $reviews = @($review.reviews)
    if (@($reviews | Where-Object { $_.status -eq 'completed' }).Count -lt 1) { throw 'At least one completed adversarial source review is required.' }
    foreach ($item in $reviews) {
        if ($item.status -eq 'completed') {
            if ([string]::IsNullOrWhiteSpace([string]$item.reviewer) -or [string]::IsNullOrWhiteSpace([string]$item.invocation)) { throw 'A completed review must record its reviewer and invocation.' }
        }
        elseif ($item.status -eq 'not-performed') {
            if ([string]::IsNullOrWhiteSpace([string]$item.reason)) { throw 'A review recorded as not-performed must state the reason.' }
        }
        else { throw "Unsupported review status '$($item.status)'; use completed or not-performed." }
    }
    Invoke-ReleasePython (Join-Path $PSScriptRoot 'verify_reviewed_source.py') --repo $Context.Repo --bundle $bundle --revision $ExpectedRevision
}

function Get-ArchiveConfigurationDigest {
    param($Audit)
    $digest = $Audit.archiveConfigurationDigest
    if ($digest -notmatch '^sha256:[0-9a-f]{64}$') { throw 'The archive audit has no valid image configuration digest.' }
    $digest
}

function Assert-ReleaseImageGates {
    # Exact-image gate: build metadata, archive identity, scans, SBOM, NuGet audit, smoke and restored-backup compatibility.
    param([Parameter(Mandatory)] $Context)
    $bundle = $Context.Bundle
    $v = $Context.Version
    $cfg = $Context.Config
    $build = Read-Json (Join-Path $bundle 'build-metadata.json')
    $audit = Read-Json (Join-Path $bundle "release-$v-audit.json")
    $scan = Read-Json (Join-Path $bundle "release-$v-scan-summary.json")
    $compat = Read-Json (Join-Path $bundle 'schema-backup-compatibility.json')
    $compatHealth = Read-Json (Join-Path $bundle 'backup-compat-health.json')
    $smoke = Read-Json (Join-Path $bundle 'smoke-health.json')
    if ($build.version -ne $v -or $build.os -ne 'linux' -or $build.architecture -ne 'amd64') { throw 'Build metadata is for another version or platform.' }
    if (-not (Test-AllChecksTrue $audit) -or $audit.sourceRevision -ne $build.sourceRevision -or $audit.archiveSha256 -ne $build.archiveSha256) { throw 'The archive identity audit is incomplete or failed.' }
    $configDigest = Get-ArchiveConfigurationDigest $audit
    if (-not $scan.allChecksPass -or -not (Test-AllChecksTrue $scan)) { throw 'The vulnerability scan, SBOM or NuGet audit gate is not complete.' }
    if (-not $compat.allChecksPass -or -not (Test-AllChecksTrue $compat) -or $compat.afterSchemaVersion -ne $cfg.databaseSchemaVersion) { throw 'The restored-backup compatibility gate is not complete.' }
    if ($compatHealth.sourceRevision -ne $build.sourceRevision -or $compatHealth.databaseSchemaVersion -ne $cfg.databaseSchemaVersion -or -not $compatHealth.database.healthy) { throw 'The candidate was not healthy on the restored backup.' }
    if ($smoke.version -ne $v -or $smoke.sourceRevision -ne $build.sourceRevision -or $smoke.databaseSchemaVersion -ne $cfg.databaseSchemaVersion -or -not $smoke.database.healthy) { throw 'The exact-image smoke identity or schema gate failed.' }
    if (-not $smoke.performanceDiagnostics.healthy -or $smoke.performanceDiagnostics.droppedEvents -ne 0 -or $smoke.performanceDiagnostics.writerFailures -ne 0 -or $smoke.operationalLogging.failureCount -ne 0) { throw 'The exact-image smoke diagnostics gate failed.' }
    foreach ($proof in 'smoke-security.log', 'smoke-nightly-planner.log', 'smoke-downgrade-command.json') {
        if (-not (Test-Path -LiteralPath (Join-Path $bundle $proof) -PathType Leaf)) { throw "Required exact-image smoke evidence is missing: $proof" }
    }
    if (-not (Select-String -LiteralPath (Join-Path $bundle 'smoke-security.log') -Pattern '1654:999 true .*no-new-privileges:true.*ALL' -Quiet) -or
        -not (Select-String -LiteralPath (Join-Path $bundle 'smoke-downgrade-command.json') -Pattern '"Success"\s*:\s*true' -Quiet)) { throw 'The hardened-runtime or checkpoint-command smoke proof failed.' }
    if (-not (Select-String -LiteralPath (Join-Path $bundle 'release-nuget-audit.log') -Pattern 'has no vulnerable packages' -Quiet)) { throw 'The NuGet audit gate failed.' }
    if (Select-String -LiteralPath (Join-Path $bundle 'release-nuget-audit.log') -Pattern '\bNU190[0-5]\b' -Quiet) { throw 'The NuGet audit log reports NU1900-NU1905 (vulnerability data unavailable or a vulnerable package).' }
    if ((Get-FileSha256 $Context.ImageArchive) -ine $build.archiveSha256) { throw 'The image archive SHA-256 differs from the build metadata.' }

    # Re-read the raw scan reports instead of trusting the summary alone.
    $trivy = Read-Json (Join-Path $bundle "release-$v-trivy.json")
    if ($trivy.Metadata.ImageID -ne $configDigest -or $trivy.Metadata.ImageConfig.config.Labels.'org.opencontainers.image.revision' -ne $build.sourceRevision) { throw 'The vulnerability report identifies a different image.' }
    $ignoreUnfixed = [string]$scan.policy -like '*ignore-unfixed*'
    $findings = @($trivy.Results | ForEach-Object { $_.Vulnerabilities } | Where-Object { $null -ne $_ -and (-not $ignoreUnfixed -or $_.FixedVersion) })
    if ($findings.Count -ne 0) { throw 'The vulnerability gate failed.' }
    if (@($trivy.Results | ForEach-Object { $_.Misconfigurations; $_.Secrets } | Where-Object { $null -ne $_ }).Count -ne 0) { throw 'The image secret/misconfiguration gate failed.' }
    $sbom = Read-Json (Join-Path $bundle "release-$v-sbom.cdx.json")
    if ($sbom.bomFormat -ne 'CycloneDX' -or @($sbom.components).Count -lt 1) { throw 'The SBOM gate failed.' }
    [pscustomobject]@{ Build = $build; Audit = $audit; ConfigDigest = $configDigest; Compat = $compat; Scan = $scan; Smoke = $smoke }
}

function Assert-RestoreEvidenceBinding {
    # The fresh backup, its isolated restore and the compatibility run must describe the same dump.
    param([Parameter(Mandatory)] $Context, [Parameter(Mandatory)] $Compat)
    $backup = Read-Json (Join-Path $Context.Private 'backup-summary.json')
    $restore = Read-Json (Join-Path $Context.Bundle 'backup-restore.json')
    if ($restore.restoreExitCode -ne 0 -or $restore.inputPipeFailed -ne $false -or $restore.timedOut -ne $false -or
        $restore.backupSha256 -ine $backup.DatabaseDumpSha256 -or $restore.backupBytes -ne $backup.DatabaseDumpBytes -or
        $Compat.backupSha256 -ine $backup.DatabaseDumpSha256 -or $Compat.backupBytes -ne $backup.DatabaseDumpBytes) {
        throw 'The fresh backup, the isolated restore and the exact-image compatibility run identify different dumps, or the restore failed.'
    }
    if ($backup.StackRevision -ne $Context.Config.expectedStackRevision -or $backup.PreviousImage -ne $Context.Config.oldImageDigest -or -not $backup.BackupRoot) { throw 'The pre-upgrade backup baseline does not match the accepted installed release.' }
    [pscustomobject]@{ Backup = $backup; Restore = $restore }
}
