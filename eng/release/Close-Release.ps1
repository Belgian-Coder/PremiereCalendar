#Requires -Version 7.0
<#
.SYNOPSIS
Step 11: closes the release: sanitized release record, checksummed evidence bundle and (optionally) the NAS copy.

.DESCRIPTION
Re-validates every gate and the installed evidence (activation receipt, containers before/after, direct and private
health, HTTP checks, verification summary, the optional persisted-setting receipt), then
  * requires the prepared release notes docs\releases\<version>.md to exist and be committed (the notes are never
    invented from metrics here) and copies them into the bundle,
  * writes the sanitized public backup summary (allow-listed fields; restricted locators are rejected),
  * preserves the operational scripts that produced this release (a copy of eng\release) in <Bundle>\operations,
  * writes docs\releases\<version>.json (commit it afterwards, together with the installed section of the notes),
    <Bundle>\release.json and <Bundle>\SHA256SUMS,
  * with -AllowNasWrite: copies the bundle to <nasReleaseRoot>\<bundle name> and reads every file back to compare
    SHA-256 (an existing different file is never overwritten).
Existing closure artifacts are never overwritten. Raw backup, configuration and restore payloads stay private.

The canonical NAS infrastructure documents are a separate, explicitly approved step: see docs\Releasing.md and
eng\release\nas-docs.

.PARAMETER Bundle
Full path of the release bundle.

.PARAMETER AllowNasWrite
Permit writing the bundle copy to the NAS release share.

.EXAMPLE
pwsh -File D:\Projects\PremiereCalendar\eng\release\Close-Release.ps1 -Bundle D:\Projects\PremiereCalendar\artifacts\release\1.2.20 -AllowNasWrite
#>
[CmdletBinding(SupportsShouldProcess)]
param([Parameter(Mandatory)][string] $Bundle, [switch] $AllowNasWrite)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
. (Join-Path $PSScriptRoot 'ReleaseCommon.ps1')
. (Join-Path $PSScriptRoot 'ReleaseGates.ps1')
$ctx = Get-ReleaseContext -Bundle $Bundle
$cfg = $ctx.Config
$v = $cfg.version
$notes = Join-Path $ctx.Repo "docs\releases\$v.md"
$recordInRepo = Join-Path $ctx.Repo "docs\releases\$v.json"
$releaseJson = Join-Path $ctx.Bundle 'release.json'
$sums = Join-Path $ctx.Bundle 'SHA256SUMS'
$bundleNotes = Join-Path $ctx.Bundle 'release-notes.md'
$publicBackupFile = Join-Path $ctx.Installed 'backup-summary-public.json'
$nasTarget = Join-Path $cfg.nasReleaseRoot $ctx.BundleName

$plan = @(
    "Refuse if $releaseJson, $sums, $recordInRepo, $bundleNotes or $publicBackupFile already exist",
    'Re-validate all release gates and the installed evidence (activation, containers, health, HTTP checks, verification summary)',
    "Require the committed release notes $notes; copy them to the bundle",
    'Write the allow-listed public backup summary; reject any restricted backup locator in public JSON',
    "Copy eng\release to $($ctx.Bundle)\operations (an existing different copy stops the run)",
    "Write $recordInRepo, $releaseJson and $sums (SHA-256 of every bundle file)",
    $(if ($AllowNasWrite) { "Copy the bundle to $nasTarget with SHA-256 readback of every file" } else { 'No NAS copy (needs -AllowNasWrite)' }))
if (Show-ReleasePlan -Title "Close-Release $($ctx.BundleName)" -Step $plan) { return }

Assert-FilesAbsent @($releaseJson, $sums, $recordInRepo, $bundleNotes, $publicBackupFile)
$build = Read-Json (Join-Path $ctx.Bundle 'build-metadata.json')
Assert-ReleaseSourceEvidence -Context $ctx -ExpectedRevision $build.sourceRevision
$gates = Assert-ReleaseImageGates -Context $ctx
$binding = Assert-RestoreEvidenceBinding -Context $ctx -Compat $gates.Compat
$backup = $binding.Backup
$restore = $binding.Restore
$imageDigest = $gates.ConfigDigest
$review = Read-Json (Join-Path $ctx.Bundle 'source-review.json')
$tests = @(Read-Json (Join-Path $ctx.Bundle 'test-summary.json'))

$activation = Read-Json (Join-Path $ctx.Installed 'activation.json')
$before = @(Read-Json (Join-Path $ctx.Installed 'containers-before.json'))
$after = @(Read-Json (Join-Path $ctx.Installed 'containers-after.json'))
$healthPrivate = Read-Json (Join-Path $ctx.Installed 'health-private.json')
$healthDirect = Read-Json (Join-Path $ctx.Installed 'health-direct.json')
$http = @(Read-Json (Join-Path $ctx.Installed 'http-checks.json'))
$verification = Read-Json (Join-Path $ctx.Installed 'verification.json')
$expectedRevisionAfter = [int]$cfg.expectedStackRevision + 1
if ($activation.SourceRevision -ne $build.sourceRevision -or $activation.ImageConfigurationDigest -ne $imageDigest -or $activation.StackFileVersion -ne $expectedRevisionAfter) { throw 'The activation record differs from the accepted image/source/stack revision.' }
if ($verification.StackRevision -ne $expectedRevisionAfter -or -not $verification.DatabaseAndBackupUnchanged -or -not $verification.RuntimeHardeningPassed) { throw 'The installed verification summary is incomplete.' }
$beforeApp = $before | Where-Object Name -eq "/$($cfg.appContainer)"
if ($beforeApp.Image -ne $cfg.oldImageDigest) { throw 'The preserved pre-upgrade app is not the accepted previous image.' }
foreach ($health in @($healthPrivate, $healthDirect)) {
    if ($health.version -ne $v -or $health.sourceRevision -ne $build.sourceRevision -or $health.databaseSchemaVersion -ne $cfg.databaseSchemaVersion -or -not $health.database.healthy -or -not $health.performanceDiagnostics.healthy -or $health.performanceDiagnostics.droppedEvents -ne 0 -or $health.performanceDiagnostics.writerFailures -ne 0 -or $health.operationalLogging.failureCount -ne 0) { throw 'The installed direct/private health evidence does not match the accepted release.' }
}
if ($http.Count -lt 8 -or @($http | Where-Object Status -ne 200).Count -gt 0) { throw 'The installed HTTP checks are incomplete or failed.' }
foreach ($prior in @($before | Where-Object Name -ne "/$($cfg.appContainer)")) {
    $current = $after | Where-Object Name -eq $prior.Name
    if ($null -eq $current -or $current.Id -ne $prior.Id -or $current.StartedAt -ne $prior.StartedAt) { throw "The installation did not preserve $($prior.Name) (id/start time)." }
}
$app = $after | Where-Object Name -eq "/$($cfg.appContainer)"
if ($app.Image -ne $imageDigest -or $app.Health -ne 'healthy') { throw 'The installed app is not healthy on the exact accepted image.' }
$settingReceipt = $null
if ($cfg.requiredSettingKey) {
    $settingReceipt = Read-Json (Join-Path $ctx.Installed 'setting-verified.json')
    if ($settingReceipt.key -ne $cfg.requiredSettingKey -or $settingReceipt.value -cne $cfg.requiredSettingValue -or -not $settingReceipt.readOnly) { throw 'The persisted-setting receipt is invalid.' }
}

& git -C $ctx.Repo ls-files --error-unmatch -- "docs/releases/$v.md" *> $null
if ($LASTEXITCODE -ne 0) { throw "The release notes docs\releases\$v.md must exist and be committed before closure (they are never generated from inferred metrics)." }

if ($backup.NativeSha256 -notmatch '^[0-9a-fA-F]{64}$' -or $backup.DatabaseDumpBytes -le 0) { throw 'The public backup fields must be validated scalar counts and hashes.' }
$publicBackup = [ordered]@{
    PreparedUtc = $backup.PreparedUtc; StackId = $backup.StackId; StackRevision = $backup.StackRevision; PreviousImage = $backup.PreviousImage
    DatabaseDumpFileName = ([string]$backup.DatabaseDumpPath -split '[/\\]')[-1]; DatabaseDumpBytes = $backup.DatabaseDumpBytes; DatabaseDumpSha256 = $backup.DatabaseDumpSha256
    NativeEntries = $backup.NativeEntries; NativeSha256 = $backup.NativeSha256 }
Write-NewJson -Path $publicBackupFile -Object $publicBackup
Copy-Item -LiteralPath $notes -Destination $bundleNotes

# Preserve the scripts that produced this release next to its evidence.
$operations = Join-Path $ctx.Bundle 'operations'
foreach ($file in Get-ChildItem -LiteralPath $PSScriptRoot -File -Recurse) {
    $relative = [IO.Path]::GetRelativePath($PSScriptRoot, $file.FullName)
    $target = Join-Path $operations $relative
    if (Test-Path -LiteralPath $target) {
        if ((Get-FileSha256 $target) -ne (Get-FileSha256 $file.FullName)) { throw "An existing operations copy differs; preserve it and review before closure: $relative" }
    }
    else { New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null; Copy-Item -LiteralPath $file.FullName -Destination $target }
}

$assets = @(Get-ChildItem -LiteralPath $ctx.Bundle -File -Recurse | Where-Object { $_.Name -notin @('release.json', 'SHA256SUMS') } | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{ name = [IO.Path]::GetRelativePath($ctx.Bundle, $_.FullName).Replace('\', '/'); bytes = $_.Length; sha256 = Get-FileSha256 $_.FullName } })
$schemaChanged = $cfg.databaseSchemaVersion -ne $cfg.oldSchemaVersion
$record = [ordered]@{
    schemaVersion = 1; version = $v; tag = "v$v"; sourceRevision = $build.sourceRevision; createdUtc = $build.buildTimeUtc
    distribution = 'docker-archive'; platform = 'linux/amd64'; imageConfigurationDigest = $imageDigest; ociIndexDigest = $build.localImageIdentity
    archive = $build.archive; archiveBytes = $build.bytes; archiveSha256 = $build.archiveSha256; databaseSchemaVersion = $cfg.databaseSchemaVersion; status = 'installed'
    acceptance = [ordered]@{
        tests = $tests; sourceReview = $review; scanSummary = $gates.Scan; backupCompatibility = $gates.Compat
        backupRestore = (Read-Json (Join-Path $ctx.Bundle 'backup-restore.json')); persistedSetting = $settingReceipt
        installedHealth = @{ private = 'passed'; direct = 'passed' }; httpChecks = $http.Count }
    installation = [ordered]@{
        installedUtc = $app.StartedAt; stackId = $cfg.stackId; stackFileVersion = $activation.StackFileVersion; appContainer = $app.Id
        previousAppContainer = $beforeApp.Id; databaseContainer = ($before | Where-Object Name -eq "/$($cfg.databaseContainer)").Id; backupContainer = ($before | Where-Object Name -eq "/$($cfg.backupContainer)").Id
        diagnosticDroppedEvents = $healthPrivate.performanceDiagnostics.droppedEvents; diagnosticWriterFailures = $healthPrivate.performanceDiagnostics.writerFailures
        operationalLoggingFailures = $healthPrivate.operationalLogging.failureCount }
    backup = $publicBackup
    rollback = [ordered]@{
        previousVersion = $cfg.oldVersion; previousImageConfigurationDigest = $cfg.oldImageDigest; previousSchemaVersion = $cfg.oldSchemaVersion
        schemaChanged = $schemaChanged; appOnlyRollbackSupported = (-not $schemaChanged); automaticDatabaseRestorePerformed = $false }
    assets = $assets
}
$json = ConvertTo-Json -InputObject $record -Depth 28
foreach ($restricted in @($backup.PrivateBackup, $backup.BackupRoot, $backup.DatabaseDumpPath)) {
    if ([string]::IsNullOrWhiteSpace([string]$restricted)) { continue }
    $escaped = ([string]$restricted | ConvertTo-Json -Compress).Trim([char]34)
    if ($json.Contains([string]$restricted, [StringComparison]::OrdinalIgnoreCase) -or $json.Contains($escaped, [StringComparison]::OrdinalIgnoreCase)) { throw 'A restricted backup locator was found in the public release JSON.' }
}
Write-NewText -Path $recordInRepo -Text ($json + "`n")
Write-NewText -Path $releaseJson -Text ($json + "`n")
$files = @(Get-ChildItem -LiteralPath $ctx.Bundle -File -Recurse | Where-Object Name -ne 'SHA256SUMS' | Sort-Object FullName)
Write-NewText -Path $sums -Text ((@($files | ForEach-Object { (Get-FileSha256 $_.FullName) + '  ' + [IO.Path]::GetRelativePath($ctx.Bundle, $_.FullName).Replace('\', '/') }) -join "`n") + "`n")

$copied = 0
if ($AllowNasWrite) {
    foreach ($file in (@($files) + (Get-Item -LiteralPath $sums))) {
        $relative = [IO.Path]::GetRelativePath($ctx.Bundle, $file.FullName)
        $target = Join-Path $nasTarget $relative
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        $hash = Get-FileSha256 $file.FullName
        if (Test-Path -LiteralPath $target) { if ((Get-FileSha256 $target) -ne $hash) { throw "An existing NAS release artifact differs; refusing to overwrite: $relative" } }
        else { Copy-Item -LiteralPath $file.FullName -Destination $target }
        if ((Get-FileSha256 $target) -ne $hash) { throw "The NAS readback SHA-256 differs: $relative" }
        $copied++
    }
}
[pscustomobject]@{
    Version = $v; SourceRevision = $build.sourceRevision; ImageConfigurationDigest = $imageDigest
    TestsPassed = ($tests | Measure-Object passed -Sum).Sum; TestsSkipped = ($tests | Measure-Object skipped -Sum).Sum
    Assets = $assets.Count; NasFilesVerified = $copied; ReleaseRecord = $recordInRepo
} | ConvertTo-Json -Compress
Write-Host "Commit $recordInRepo (and the installed section of the release notes) as the closure commit. Tag the build commit, not the closure commit."
