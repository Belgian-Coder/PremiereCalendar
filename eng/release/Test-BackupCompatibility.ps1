#Requires -Version 7.0
<#
.SYNOPSIS
Step 7b: starts the exact candidate image on the restored production backup and proves nothing was lost.

.DESCRIPTION
Uses the isolated database from Initialize-RestoreCheck.ps1/Restore-Backup.ps1 (it must still hold the restored
dump). It records a sanitized state of that database (schema-state.sql: table row counts, migration history and
one-way row fingerprints of the settings, filter, view-sync, SIMKL and item-enrichment tables; no row payload), starts
the candidate image against it with every background service disabled, waits for readiness, records the state again
and compares both with compare_schema_state.py. The run passes only when the migration history of the installed
schema is unchanged, the candidate reached its own schema version, the database is healthy and every preserved
table and fingerprint is identical. Exists to catch migration, checksum and line-ending regressions on real data
before anything touches the NAS.

.PARAMETER Bundle
Full path of the release bundle.

.EXAMPLE
pwsh -File D:\Projects\PremiereCalendar\eng\release\Test-BackupCompatibility.ps1 -Bundle D:\Projects\PremiereCalendar\artifacts\release\1.2.20
#>
[CmdletBinding(SupportsShouldProcess)]
param([Parameter(Mandatory)][string] $Bundle, [switch] $KeepResources)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseCommon.ps1')
$ctx = Get-ReleaseContext -Bundle $Bundle
$cfg = $ctx.Config
$network = "$($ctx.Prefix)_restore_net"
$db = "$($ctx.Prefix)_restore_db"
$app = "$($ctx.Prefix)_compat_app"
$label = "restore-$($ctx.BundleName)"
$before = Join-Path $ctx.Bundle 'backup-schema-before.txt'
$after = Join-Path $ctx.Bundle 'backup-schema-after.txt'
$healthFile = Join-Path $ctx.Bundle 'backup-compat-health.json'
$imageFile = Join-Path $ctx.Bundle 'backup-compat-image.json'
$report = Join-Path $ctx.Bundle 'schema-backup-compatibility.json'
$securityFile = Join-Path $ctx.Bundle 'backup-compat-security.txt'
$outputs = @($before, $after, $healthFile, $imageFile, $report, $securityFile)

$plan = @(
    "Refuse if compatibility evidence or the container $app exist; require the restored database $db on the internal network $network",
    "Record the sanitized state of the restored database (schema $($cfg.oldSchemaVersion) expected) with schema-state.sql",
    "Start the exact candidate image from build-metadata.json against it, hardened, background services disabled",
    "Require readiness, version/revision, schema $($cfg.databaseSchemaVersion) and a healthy database from /health/version",
    'Record the state again; compare_schema_state.py must pass every check',
    "Write $report (and the other backup-compat-* files); remove the candidate container")
if (Show-ReleasePlan -Title 'Test-BackupCompatibility' -Step $plan) { return }

Assert-FilesAbsent $outputs
Assert-LocalDockerAvailable
Assert-DockerNamesFree -Container $app
if (-not (Test-LocalDocker container inspect $db)) { throw "Missing the restored database container: $db (run Initialize-RestoreCheck.ps1 and Restore-Backup.ps1)." }
if ((Invoke-LocalDocker network inspect $network --format '{{.Internal}}' | Select-Object -First 1) -ne 'true') { throw 'The validation network is not internal.' }
# Labels are read from the JSON (a --format template with embedded quotes would not survive the wsl.exe boundary).
if ((Invoke-LocalDockerJson inspect $db)[0].Config.Labels.'premierecalendar.validation' -ne $label) { throw 'Unexpected isolated database ownership label.' }
$restorePublic = Read-Json (Join-Path $ctx.Bundle 'backup-restore.json')
if ($restorePublic.restoreExitCode -ne 0 -or $restorePublic.inputPipeFailed -or $restorePublic.timedOut -or $restorePublic.destinationContainer -ne $db -or $restorePublic.destinationNetwork -ne $network) { throw 'The restore proof does not describe this isolated database.' }
if (-not $restorePublic.destinationInternalNetwork -or $restorePublic.destinationPersistentMounts -ne 0 -or $restorePublic.destinationPublishedPorts -ne 0) { throw 'The restore destination was not isolated.' }

$metadata = Read-Json (Join-Path $ctx.Bundle 'build-metadata.json')
$image = (Invoke-LocalDockerJson image inspect $metadata.localImageIdentity)[0]
if ($metadata.version -ne $cfg.version -or $image.Id -ne $metadata.localImageIdentity -or $image.Os -ne 'linux' -or $image.Architecture -ne 'amd64') { throw 'The candidate image differs from the build metadata.' }
if ($image.Config.Labels.'org.opencontainers.image.revision' -ne $metadata.sourceRevision -or $image.Config.Labels.'org.opencontainers.image.version' -ne $cfg.version) { throw 'The candidate image labels differ from the build metadata.' }
Write-NewJson -Path $imageFile -Object ([ordered]@{ version = $cfg.version; sourceRevision = $metadata.sourceRevision; imageId = $image.Id; platform = 'linux/amd64' })

function Get-SchemaState {
    param([string] $Path)
    $sql = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'schema-state.sql') -Raw
    $wsl = $script:ReleaseWsl; $prefix = $script:ReleaseDockerPrefix
    $text = $sql | & $wsl @prefix exec -i $db psql -X -A -t -F '|' -q -v ON_ERROR_STOP=1 -v "maxver=$($cfg.oldSchemaVersion)" -U postgres -d premierecalendar
    if ($LASTEXITCODE -ne 0) { throw 'Could not read the sanitized database state.' }
    Write-NewText -Path $Path -Text ((@($text) -join "`n") + "`n")
}

Get-SchemaState -Path $before
$connection = 'Host=backup-db;Database=premierecalendar;Username=postgres;GSS Encryption Mode=Disable'
$null = Invoke-LocalDocker run -d --name $app --label "premierecalendar.validation=compat-$($ctx.BundleName)" --network $network `
    --read-only --user 1654:999 --cap-drop ALL --security-opt no-new-privileges:true --tmpfs '/tmp:rw,noexec,nosuid,size=64m' --tmpfs '/app/App_Data:rw,uid=1654,gid=999,mode=0770,size=128m' `
    -e AppDatabase__Provider=PostgreSql -e "AppDatabase__ConnectionString=$connection" `
    -e ProviderScheduler__Enabled=false -e ProviderDeltaSync__Enabled=false -e CalendarWarmup__Enabled=false -e CalendarCache__Enabled=false `
    -e CalendarCache__AdjacentWeekPrefetchEnabled=false -e ItemEnrichmentCache__Enabled=false -e ImdbDataset__Enabled=false `
    $image.Id
try {
    Wait-LocalCondition -Attempts 90 -Message 'The candidate app did not become ready against the restored backup.' -Condition { Test-LocalDocker exec $app wget -qO- http://127.0.0.1:8080/health/ready }
}
catch {
    $state = (@(Invoke-LocalDocker inspect $app --format '{{.State.Status}} exit={{.State.ExitCode}} oom={{.State.OOMKilled}}') -join ' ')
    Write-NewText -Path (Join-Path $ctx.Private 'backup-compat-startup-state.txt') -Text ($state + "`n")
    throw
}
$healthText = (@(Invoke-LocalDocker exec $app wget -qO- http://127.0.0.1:8080/health/version) -join "`n")
$health = $healthText | ConvertFrom-Json
$newSchema = $cfg.databaseSchemaVersion
if ($health.version -ne $cfg.version -or $health.sourceRevision -ne $metadata.sourceRevision) { throw 'The running identity differs from the build metadata.' }
if ($health.databaseSchemaVersion -ne $newSchema -or $health.database.currentSchemaVersion -ne $newSchema -or $health.database.targetSchemaVersion -ne $newSchema -or -not $health.database.healthy) { throw 'The candidate did not reach a healthy target schema on the restored backup.' }
if (-not $health.performanceDiagnostics.healthy -or $health.operationalLogging.failureCount -ne 0) { throw 'Diagnostics or operational logging are unhealthy.' }
Write-NewText -Path $healthFile -Text ($healthText + "`n")
Get-SchemaState -Path $after
Invoke-ReleasePython (Join-Path $PSScriptRoot 'compare_schema_state.py') --before $before --after $after --output $report `
    --restore-proof (Join-Path $ctx.Private 'backup-restore.json') --backup-summary (Join-Path $ctx.Private 'backup-summary.json') `
    --old-schema $cfg.oldSchemaVersion --new-schema $newSchema
Write-NewText -Path $securityFile -Text ((@(Invoke-LocalDocker inspect $app --format '{{.Image}} {{.Config.User}} {{.HostConfig.ReadonlyRootfs}} {{.HostConfig.SecurityOpt}} {{.HostConfig.CapDrop}}') -join "`n") + "`n")
if (-not $KeepResources) { $null = Test-LocalDocker rm -f $app }
Write-Host 'The candidate image preserved the restored backup (all compatibility checks passed).'
