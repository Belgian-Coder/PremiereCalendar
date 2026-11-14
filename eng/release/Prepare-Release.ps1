#Requires -Version 7.0
<#
.SYNOPSIS
Step 1: takes a fresh, verified NAS backup and captures the installed baseline before anything is built.

.DESCRIPTION
Checks that Portainer endpoint and stack still match the release configuration (stack revision, the old image
pinned exactly once, all three stack containers running and healthy, the live /health/version identity), then
  * stores the raw stack configuration and a native Portainer backup only in the restricted backup tree,
  * runs the stack's own backup, strict verification and freshness check inside the backup container into a unique
    pre-upgrade folder,
  * confirms exactly one fresh dump whose SHA-256 matches its sidecar,
  * records container ids and start times (installed\containers-before.json) so that later steps can prove the
    database and backup containers were never restarted.
The restricted summary (with private locators) goes to <Bundle>.private\backup-summary.json; nothing secret is
printed. Existing evidence is never overwritten. Every Portainer POST needs -AllowMutation.

.PARAMETER Bundle
Full path of the release bundle created by Initialize-Release.ps1.

.PARAMETER AllowMutation
Permits the Portainer POSTs this step needs (native backup and the container exec calls).

.EXAMPLE
pwsh -File D:\Projects\PremiereCalendar\eng\release\Prepare-Release.ps1 -Bundle D:\Projects\PremiereCalendar\artifacts\release\1.2.20 -AllowMutation
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string] $Bundle,
    [switch] $AllowMutation,
    [ValidateRange(1, 168)][int] $MaximumBackupAgeHours = 26
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
. (Join-Path $PSScriptRoot 'ReleaseCommon.ps1')
$ctx = Get-ReleaseContext -Bundle $Bundle
$cfg = $ctx.Config
Start-ReleaseSession -Context $ctx -AllowMutation:$AllowMutation

$tagPrefix = "$($ctx.Prefix)-prep"
$tags = @("$tagPrefix-pre-upgrade-backup", "$tagPrefix-backup-strict", "$tagPrefix-backup-health")
$outputs = @(
    (Join-Path $ctx.Installed 'containers-before.json'), (Join-Path $ctx.Private 'backup-summary.json')
) + @($tags | ForEach-Object { foreach ($suffix in '-exec-id.txt', '-exec.bin', '.log') { Join-Path $ctx.Private "$_$suffix" } })

$plan = @(
    'Refuse if any backup evidence for this bundle already exists',
    "Read-only Portainer checks: endpoint $($cfg.endpointId) is the NAS, stack $($cfg.stackId) is at revision $($cfg.expectedStackRevision) and pins $($cfg.oldImageDigest) exactly once, the app, database and backup containers are running and healthy",
    "Read-only HTTP check of $($cfg.directUrl)/health/version: version $($cfg.oldVersion), schema $($cfg.oldSchemaVersion), database healthy",
    "Restricted copy of the stack configuration and native Portainer backup under $($cfg.privateBackupRoot) (POST, needs -AllowMutation)",
    "Exec in $($cfg.backupContainer): backup-postgres.sh, verify-backups.sh, verify-backups.sh --health into BACKUP_ROOT=/backups/pre-upgrade-$($ctx.BundleName)-<UTC stamp> (POST)",
    "Exactly one fresh .dump under $($cfg.nasBackupShare) whose SHA-256 matches its sidecar; stack revision unchanged during the backup",
    "Write $($ctx.Installed)\containers-before.json and $($ctx.Private)\backup-summary.json")
if (Show-ReleasePlan -Title "Prepare-Release $($ctx.BundleName)" -Step $plan) { return }
if (-not $AllowMutation) { throw 'This step creates backups through Portainer exec calls; run it with -AllowMutation.' }
Assert-FilesAbsent $outputs

$endpoint = "/api/endpoints/$($cfg.endpointId)/docker"
$engine = Invoke-ReleaseApi -Path "$endpoint/info"
if ($engine.Name -ne 'NAS') { throw "Endpoint $($cfg.endpointId) is not the NAS." }
$stack = Invoke-ReleaseApi -Path "/api/stacks/$($cfg.stackId)"
$source = Invoke-ReleaseApi -Path "/api/stacks/$($cfg.stackId)/file"
if ($stack.Id -ne $cfg.stackId -or $stack.StackFileVersion -ne $cfg.expectedStackRevision) { throw "Expected PremiereCalendar stack $($cfg.stackId) revision $($cfg.expectedStackRevision), found revision $($stack.StackFileVersion)." }
if ([regex]::Matches($source.StackFileContent, [regex]::Escape($cfg.oldImageDigest)).Count -ne 1) { throw 'The accepted old app pin is not unique in the current stack source.' }

$before = @(Get-NasContainerBaseline -Context $ctx -Name @($cfg.appContainer, $cfg.databaseContainer, $cfg.backupContainer))
foreach ($container in $before) {
    if ($container.Status -ne 'running' -or $container.Health -ne 'healthy') { throw "Required container is not running and healthy: $($container.Name)" }
    if ($container.Name -eq "/$($cfg.appContainer)" -and $container.Image -ne $cfg.oldImageDigest) { throw 'The current app image differs from the accepted old release.' }
}
$health = Invoke-RestMethod "$($cfg.directUrl)/health/version" -TimeoutSec 20
if ($health.version -ne $cfg.oldVersion -or $health.databaseSchemaVersion -ne $cfg.oldSchemaVersion -or -not $health.database.healthy) { throw 'The installed identity or schema differs from the expected baseline.' }
if ($cfg.oldSourceRevision -and $health.sourceRevision -cne $cfg.oldSourceRevision) { throw 'The installed source revision differs from the expected baseline.' }

$stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssZ')
$backupFolder = "pre-upgrade-$($ctx.BundleName)-$stamp"
$restricted = Join-Path $cfg.privateBackupRoot "$stamp-before-$($ctx.BundleName)"
if (Test-Path -LiteralPath $restricted) { throw 'The restricted backup destination already exists; refusing to overwrite.' }
New-Item -ItemType Directory -Path $ctx.Installed, $ctx.Private -Force | Out-Null
New-Item -ItemType Directory -Path $restricted | Out-Null
# Raw stack environment and configuration live only in the restricted tree.
Write-NewText -Path "$restricted\stack.json" -Text ((ConvertTo-Json -InputObject $stack -Depth 60) + "`n")
Write-NewText -Path "$restricted\source.json" -Text ((ConvertTo-Json -InputObject $source -Depth 60) + "`n")
Write-NewText -Path "$restricted\docker-compose.yml" -Text ($source.StackFileContent + "`n")
Write-NewJson -Path (Join-Path $ctx.Installed 'containers-before.json') -Object $before -Depth 8
$native = "$restricted\portainer.tar.gz"
$null = Invoke-ReleaseApi -Path '/api/backup' -Method POST -Body @{ Password = '' } -OutFile $native
$entries = @(& tar -tzf $native)
if ($LASTEXITCODE -ne 0 -or $entries.Count -lt 1) { throw 'The native Portainer backup archive is invalid.' }

$backupRoot = "BACKUP_ROOT=/backups/$backupFolder"
$null = Invoke-ReleaseExec -Container $cfg.backupContainer -Command @('env', $backupRoot, '/opt/backup/backup-postgres.sh') -Tag $tags[0] -Directory $ctx.Private
$null = Invoke-ReleaseExec -Container $cfg.backupContainer -Command @('env', $backupRoot, '/opt/backup/verify-backups.sh') -Tag $tags[1] -Directory $ctx.Private
$null = Invoke-ReleaseExec -Container $cfg.backupContainer -Command @('env', $backupRoot, "BACKUP_MAX_AGE_HOURS=$MaximumBackupAgeHours", '/opt/backup/verify-backups.sh', '--health') -Tag $tags[2] -Directory $ctx.Private

$backupDirectory = Join-Path $cfg.nasBackupShare "$backupFolder\daily"
$dumps = @(Get-ChildItem -LiteralPath $backupDirectory -Filter '*.dump' -File)
if ($dumps.Count -ne 1) { throw 'Expected exactly one fresh dump in the unique pre-upgrade directory.' }
$dumpHash = Get-FileSha256 $dumps[0].FullName
$sidecars = @(Get-ChildItem -LiteralPath $backupDirectory -Filter '*.sha256' -File)
if ($sidecars.Count -ne 1 -or (Get-Content -LiteralPath $sidecars[0].FullName -Raw) -notmatch "(?im)^$dumpHash\s") { throw 'The fresh dump SHA-256 does not match its sidecar.' }
$currentStack = Invoke-ReleaseApi -Path "/api/stacks/$($cfg.stackId)"
if ($currentStack.StackFileVersion -ne $cfg.expectedStackRevision) { throw 'The stack changed during the backup; do not activate from this baseline.' }

$summary = [ordered]@{
    Version = $cfg.version; PreparedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    PrivateBackup = $restricted; NativeEntries = $entries.Count; NativeSha256 = Get-FileSha256 $native
    StackId = $stack.Id; StackRevision = $stack.StackFileVersion; PreviousImage = $cfg.oldImageDigest
    BackupRoot = "/backups/$backupFolder"; DatabaseDumpPath = $dumps[0].FullName
    DatabaseDumpBytes = $dumps[0].Length; DatabaseDumpSha256 = $dumpHash
    DatabaseDump = @(Get-Content -LiteralPath $sidecars[0].FullName)
}
Write-NewJson -Path (Join-Path $ctx.Private 'backup-summary.json') -Object $summary -Depth 6
Write-Host "Fresh backup verified: $($dumps[0].Name), $($dumps[0].Length) bytes, SHA-256 $dumpHash"
Write-Host "Restricted summary: $(Join-Path $ctx.Private 'backup-summary.json')"
