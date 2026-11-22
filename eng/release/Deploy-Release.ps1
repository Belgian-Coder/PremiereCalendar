#Requires -Version 7.0
<#
.SYNOPSIS
Step 9: activates the loaded image by changing exactly one value in the Portainer stack: the app image digest.

.DESCRIPTION
Re-validates every gate (source-bound tests and review, exact image, scans, SBOM, NuGet audit, smoke, restored-backup
compatibility, backup/restore/compatibility binding to one dump) and the live state, then updates the stack through
the Portainer API with PullImage=false and Prune=false. The candidate stack file is the current one with the old
image configuration digest replaced by the audited new one; the script proves that nothing else differs and saves
the candidate in the restricted backup folder first. Before activating it requires:
  * the stack at the expected revision and the old digest pinned exactly once,
  * the app, database and backup containers unchanged since the baseline (same ids and start times) and healthy,
  * the loaded image on the NAS engine equal to the audited archive,
  * a pre-upgrade backup younger than -MaximumBackupAgeHours that passes the stack's own freshness check,
  * the live migration history equal to the one the candidate was proven against,
  * no foreground provider work running,
  * when the release configured a required persisted setting: the read-only receipt from Test-PersistedSetting.ps1.
No restore, no database or backup restart and no data change is performed. An existing activation receipt blocks a
second activation; after a failed attempt preserve the evidence and use a new -AttemptTagPrefix. Needs -AllowMutation.

.PARAMETER Bundle
Full path of the release bundle.

.PARAMETER AllowMutation
Permits the Portainer PUT (stack update) and the exec POSTs of the pre-activation checks.

.PARAMETER AttemptTagPrefix
Prefix of the exec evidence files of this attempt. Defaults to a unique UTC-plus-GUID value.

.EXAMPLE
pwsh -File D:\Projects\PremiereCalendar\eng\release\Deploy-Release.ps1 -Bundle D:\Projects\PremiereCalendar\artifacts\release\1.2.20 -AllowMutation
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string] $Bundle,
    [switch] $AllowMutation,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]{5,120}$')][string] $AttemptTagPrefix,
    [ValidateRange(1, 168)][int] $MaximumBackupAgeHours = 26
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
. (Join-Path $PSScriptRoot 'ReleaseCommon.ps1')
. (Join-Path $PSScriptRoot 'ReleaseGates.ps1')
$ctx = Get-ReleaseContext -Bundle $Bundle
$cfg = $ctx.Config
Start-ReleaseSession -Context $ctx -AllowMutation:$AllowMutation
if ([string]::IsNullOrWhiteSpace($AttemptTagPrefix)) { $AttemptTagPrefix = "$($ctx.Prefix)-deploy-" + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '-' + [Guid]::NewGuid().ToString('N') }
$suffixes = @('pre-activation-backup-health', 'pre-activation-setting', 'pre-activation-migrations', 'active-foreground-before')
$activationReceipt = Join-Path $ctx.Installed 'activation.json'

$plan = @(
    'Re-validate all release gates and the bundle evidence (nothing is trusted from an earlier step)',
    "Refuse if $activationReceipt exists or the exec evidence $AttemptTagPrefix-* exists",
    "Read-only Portainer checks: NAS endpoint; stack $($cfg.stackId) at revision $($cfg.expectedStackRevision); old digest $($cfg.oldImageDigest) pinned exactly once; stack containers unchanged since installed\containers-before.json; image $($cfg.imageTag) loaded with the audited digest; refuse when the stack file, stack Env or running app container sets AppDatabase Path or a non-PostgreSql provider (1.2.20+ stops at startup)",
    "Backup freshness (younger than $MaximumBackupAgeHours h) through exec in $($cfg.backupContainer); live migration history equals the proven one; no running foreground jobs (execs are Portainer POSTs)",
    $(if ($cfg.requiredSettingKey) { "Require the read-only receipt for '$($cfg.requiredSettingKey)' = '$($cfg.requiredSettingValue)'" } else { 'No required persisted setting is configured' }),
    "Save the candidate stack (only the app pin replaced) in the restricted backup folder, then PUT /api/stacks/$($cfg.stackId)?endpointId=$($cfg.endpointId) with PullImage=false, Prune=false (needs -AllowMutation)",
    "Write $activationReceipt")
if (Show-ReleasePlan -Title "Deploy-Release $($ctx.BundleName)" -Step $plan) { return }
if (-not $AllowMutation) { throw 'Deployment changes the Portainer stack; run this step with -AllowMutation.' }

foreach ($suffix in $suffixes) { Assert-ReleaseExecEvidenceAvailable -Directory $ctx.Private -Tag "$AttemptTagPrefix-$suffix" }
if (Test-Path -LiteralPath $activationReceipt) { throw 'Preserve the existing activation evidence; refusing a second activation.' }
$build = Read-Json (Join-Path $ctx.Bundle 'build-metadata.json')
$revision = $build.sourceRevision
$endpoint = "/api/endpoints/$($cfg.endpointId)/docker"
$info = Invoke-ReleaseApi -Path "$endpoint/info"
if ($info.Name -ne 'NAS') { throw 'The deployment endpoint is not the NAS.' }

Assert-ReleaseSourceEvidence -Context $ctx -ExpectedRevision $revision
$gates = Assert-ReleaseImageGates -Context $ctx
$binding = Assert-RestoreEvidenceBinding -Context $ctx -Compat $gates.Compat
$new = $gates.ConfigDigest
$old = $cfg.oldImageDigest
$backup = $binding.Backup
$loadReceipt = Read-Json (Join-Path $ctx.Installed 'image-load.json')
if ($loadReceipt.ImageConfigurationDigest -ne $new -or $loadReceipt.SourceRevision -ne $revision) { throw 'The image-load receipt does not describe the audited image.' }

if ($cfg.requiredSettingKey) {
    $settingReceipt = Read-Json (Join-Path $ctx.Installed 'setting-verified.json')
    if ($settingReceipt.version -ne $cfg.version -or $settingReceipt.sourceRevision -ne $revision -or $settingReceipt.key -ne $cfg.requiredSettingKey -or $settingReceipt.value -cne $cfg.requiredSettingValue -or -not $settingReceipt.readOnly -or $settingReceipt.oldAppRestarted -ne $false) { throw 'The read-only persisted-setting receipt is missing or invalid.' }
}

$before = @(Read-Json (Join-Path $ctx.Installed 'containers-before.json'))
foreach ($prior in $before) {
    $now = Invoke-ReleaseApi -Path "$endpoint/containers/$($prior.Name.TrimStart('/'))/json"
    if ($now.Id -ne $prior.Id -or $now.State.StartedAt -ne $prior.StartedAt) { throw 'The live containers changed since the pre-upgrade baseline capture.' }
    if ($now.State.Status -ne 'running' -or $now.State.Health.Status -ne 'healthy') { throw "A stack container is not running and healthy: $($prior.Name)" }
}
$stack = Invoke-ReleaseApi -Path "/api/stacks/$($cfg.stackId)"
$current = Invoke-ReleaseApi -Path "/api/stacks/$($cfg.stackId)/file"
if ($stack.StackFileVersion -ne $cfg.expectedStackRevision) { throw 'Unexpected current stack revision.' }
if (([regex]::Matches($current.StackFileContent, [regex]::Escape($old))).Count -ne 1) { throw 'The old app pin is not unique in the stack file.' }
# Since 1.2.20 the app refuses to start with a SQLite path or a non-PostgreSql provider: refuse before activation
# instead of crash-looping the new container (stack file, stack Env and the running app container are all checked).
$appDatabaseSettings = @($current.StackFileContent -split "`r?`n" | Where-Object { $_ -match 'AppDatabase(__|:)(Path|Provider)\b' }) +
    @($stack.Env | Where-Object { $_.name -match '^AppDatabase(__|:)(Path|Provider)$' } | ForEach-Object { "$($_.name)=$($_.value)" })
$appContainer = @($before | Where-Object { $_.Name.TrimStart('/') -eq $cfg.appContainer }) | Select-Object -First 1
if ($appContainer) {
    $appDatabaseSettings += @((Invoke-ReleaseApi -Path "$endpoint/containers/$($appContainer.Id)/json").Config.Env | Where-Object { $_ -match '^AppDatabase__(Path|Provider)=' })
}
$blockers = @(Get-AppDatabaseStartupBlockers $appDatabaseSettings)
if ($blockers.Count -gt 0) { throw ($blockers -join ' ') }
$candidateSource = $current.StackFileContent.Replace($old, $new)
if ($candidateSource.Replace($new, $old) -cne $current.StackFileContent) { throw 'The candidate changes more than the single app pin.' }
$image = Invoke-ReleaseApi -Path "$endpoint/images/$($cfg.imageTag)/json"
if ($image.Id -ne $new -or $image.Config.Labels.'org.opencontainers.image.revision' -ne $revision) { throw 'The loaded image identity gate failed.' }
$candidate = @{ StackFileContent = $candidateSource; Env = $stack.Env; Prune = $false; PullImage = $false }
if (([DateTimeOffset]::UtcNow - [DateTimeOffset]::Parse($backup.PreparedUtc, [Globalization.CultureInfo]::InvariantCulture)).TotalHours -gt $MaximumBackupAgeHours) { throw 'The pre-upgrade backup is older than the accepted freshness window.' }

$null = Invoke-ReleaseExec -Container $cfg.backupContainer -Directory $ctx.Private -Tag "$AttemptTagPrefix-pre-activation-backup-health" `
    -Command @('env', "BACKUP_ROOT=$($backup.BackupRoot)", "BACKUP_MAX_AGE_HOURS=$MaximumBackupAgeHours", '/opt/backup/verify-backups.sh', '--health')
if ($cfg.requiredSettingKey) {
    $sql = "SELECT Value FROM AppParameters WHERE Key='$($cfg.requiredSettingKey)';"
    $value = Invoke-ReleaseExec -Container $cfg.databaseContainer -Directory $ctx.Private -Tag "$AttemptTagPrefix-pre-activation-setting" -Command @('psql', '-X', '-U', 'premierecalendar', '-d', 'premierecalendar', '-At', '-c', $sql)
    if (($value -join '').Trim() -cne $cfg.requiredSettingValue) { throw "The persisted setting '$($cfg.requiredSettingKey)' must be '$($cfg.requiredSettingValue)' before activation; investigate without changing settings automatically." }
}
$liveHistory = Invoke-ReleaseExec -Container $cfg.databaseContainer -Directory $ctx.Private -Tag "$AttemptTagPrefix-pre-activation-migrations" `
    -Command @('psql', '-X', '-U', 'premierecalendar', '-d', 'premierecalendar', '-At', '-F', '|', '-c', 'SELECT Version,Name,Checksum FROM SchemaMigrations ORDER BY Version;')
$historyLines = @(($liveHistory -join [Environment]::NewLine) -split '\r?\n' | Where-Object { $_.Length -gt 0 })
if ($historyLines.Count -ne $cfg.oldSchemaVersion) { throw 'The live schema migration history differs from the history proven against the restored backup.' }
foreach ($line in $historyLines) {
    $parts = $line -split '\|', 3
    $expected = $gates.Compat.migrationHistory.($parts[0])
    if ($parts.Count -ne 3 -or $null -eq $expected -or $parts[1] -cne $expected.name -or $parts[2] -ine $expected.checksum) { throw 'A live migration checksum differs from the exact-image restored-backup acceptance.' }
}
$active = Invoke-ReleaseExec -Container $cfg.databaseContainer -Directory $ctx.Private -Tag "$AttemptTagPrefix-active-foreground-before" `
    -Command @('psql', '-X', '-U', 'premierecalendar', '-d', 'premierecalendar', '-At', '-c', "SELECT count(*) FROM ProviderWorkJobs WHERE State='Running' AND Priority<=100;")
if ([int](($active -join '').Trim()) -gt 0) { throw 'Foreground work is active; retry the activation after it completes (use a new -AttemptTagPrefix).' }

$candidatePath = Join-Path $backup.PrivateBackup ("candidate-$($ctx.BundleName)-" + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ') + '.json')
Write-NewText -Path $candidatePath -Text ((ConvertTo-Json -InputObject $candidate -Depth 60) + "`n")
$requested = [DateTimeOffset]::UtcNow; $timer = [Diagnostics.Stopwatch]::StartNew()
$result = Invoke-ReleaseApi -Path "/api/stacks/$($cfg.stackId)?endpointId=$($cfg.endpointId)" -Method PUT -Body $candidate
$public = [ordered]@{
    attemptTag = $AttemptTagPrefix; StackId = $result.Id; StackFileVersion = $result.StackFileVersion; Status = $result.Status; UpdateDate = $result.UpdateDate
    SourceRevision = $revision; ImageConfigurationDigest = $new; PreviousImageConfigurationDigest = $old
    ActivationRequestedUtc = $requested.ToString('O'); ActivationDurationMilliseconds = $timer.Elapsed.TotalMilliseconds }
Write-NewJson -Path $activationReceipt -Object $public
$public | ConvertTo-Json
