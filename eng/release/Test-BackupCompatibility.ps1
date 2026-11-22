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

.PARAMETER OriginalRestoreBundle
Optional original bundle holding an already-running historical restored fixture. Requires -ContinuityProof.
This mode writes existing-fixture-compatibility.json, never release acceptance evidence.

.PARAMETER ContinuityProof
Path to a new descriptor of already-validated read-only continuity evidence:
{ "schema": 1, "descriptorCreatedUtc": "<now>", "originalBundle": "<original bundle>",
  "terminalReceiptPath": "<original terminal status.json>",
  "phaseOneStatusPath": "<read-only phase-one status.json>", "phaseOneStatusSha256": "<sha256>" }
The phase-one status must record state=passed, scope=historical-existing-restored-fixture-continuity-only,
productionAcceptance=false, freshBackupEvidence=false, databaseId, networkId, anchorUtc, originalMaxver,
sanitizedStateEqualsOriginalPostCompatibility=true, snapshotSha256, and originalHashes for terminalReceipt,
config, publicRestoreProof, privateRestoreProof, backupSummary, originalPostCompatibility,
originalCompatibility, canonicalSql and canonicalParser. IDs were observed during phase one, not at restore time.
Original evidence is rehashed, never copied or relabelled. Continuity assumes trusted Docker metadata/clock.

.EXAMPLE
pwsh -File D:\Projects\PremiereCalendar\eng\release\Test-BackupCompatibility.ps1 -Bundle D:\Projects\PremiereCalendar\artifacts\release\1.2.20
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string] $Bundle,
    [switch] $KeepResources,
    [string] $OriginalRestoreBundle,
    [string] $ContinuityProof
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseCommon.ps1')
$ctx = Get-ReleaseContext -Bundle $Bundle
$cfg = $ctx.Config
$historical = -not [string]::IsNullOrWhiteSpace($OriginalRestoreBundle)
if ($historical -ne (-not [string]::IsNullOrWhiteSpace($ContinuityProof))) { throw '-OriginalRestoreBundle and -ContinuityProof must be supplied together.' }
$restoreCtx = if ($historical) { Get-ReleaseContext -Bundle $OriginalRestoreBundle } else { $ctx }
if ($historical -and ($restoreCtx.Bundle -eq $ctx.Bundle -or $restoreCtx.Prefix -eq $ctx.Prefix)) { throw 'Historical compatibility requires a distinct candidate bundle and resource prefix.' }
$network = "$($restoreCtx.Prefix)_restore_net"
$db = "$($restoreCtx.Prefix)_restore_db"
$app = "$($ctx.Prefix)_compat_app"
$label = "restore-$($restoreCtx.BundleName)"
$before = Join-Path $ctx.Bundle 'backup-schema-before.txt'
$after = Join-Path $ctx.Bundle 'backup-schema-after.txt'
$healthFile = Join-Path $ctx.Bundle 'backup-compat-health.json'
$imageFile = Join-Path $ctx.Bundle 'backup-compat-image.json'
$report = Join-Path $ctx.Bundle 'schema-backup-compatibility.json'
$securityFile = Join-Path $ctx.Bundle 'backup-compat-security.txt'
if ($historical) {
    $before = Join-Path $ctx.Bundle 'existing-fixture-schema-before.txt'
    $after = Join-Path $ctx.Bundle 'existing-fixture-schema-after.txt'
    $healthFile = Join-Path $ctx.Bundle 'existing-fixture-health.json'
    $imageFile = Join-Path $ctx.Bundle 'existing-fixture-image.json'
    $report = Join-Path $ctx.Bundle 'existing-fixture-compatibility.json'
    $securityFile = Join-Path $ctx.Bundle 'existing-fixture-security.txt'
    $continuitySnapshot = Join-Path $ctx.Bundle 'existing-fixture-continuity-state.txt'
    $comparisonReport = Join-Path $ctx.Bundle 'existing-fixture-schema-comparison.json'
}
$outputs = @($before, $after, $healthFile, $imageFile, $report, $securityFile)
if ($historical) { $outputs += @($continuitySnapshot, $comparisonReport) }

function ConvertTo-FixtureInstant([string] $Value) {
    # Docker uses nanoseconds. DateTime's 100ns precision must not round a later
    # start down across the historical completion boundary.
    if ($Value -notmatch '^(?<whole>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(?<fraction>\d{1,9}))?(?<zone>Z|[+-]\d{2}:\d{2})$') { throw 'Invalid fixture evidence timestamp.' }
    $whole = [DateTimeOffset]::Parse($Matches.whole + $Matches.zone, [Globalization.CultureInfo]::InvariantCulture)
    [decimal]$whole.ToUnixTimeSeconds() * 1000000000 + [decimal](([string]$Matches['fraction']).PadRight(9, '0'))
}

function Assert-FixtureInputHashes {
    foreach ($key in $fixturePaths.Keys) {
        $expected = [string]$fixturePhase.originalHashes.$key
        if ($expected -notmatch '^[0-9a-fA-F]{64}$' -or (Get-FileSha256 $fixturePaths[$key]) -ine $expected) { throw "Original fixture evidence hash mismatch: $key" }
    }
    if ((Get-FileSha256 $ContinuityProof) -ine $descriptorHash -or (Get-FileSha256 $fixtureDescriptor.phaseOneStatusPath) -ine $fixtureDescriptor.phaseOneStatusSha256) { throw 'The continuity descriptor or phase-one receipt changed.' }
}

function Read-FixtureDockerFields([string] $Kind, [string] $Name, [string[]] $Fields) {
    # Select fields only: never read/persist environment variables or health logs.
    $template = ($Fields | ForEach-Object { '{{println (json .' + $_ + ')}}' }) -join ''
    $rows = @(Invoke-LocalDocker $Kind inspect --format $template $Name | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    if ($rows.Count -ne $Fields.Count) { throw 'Incomplete fixture Docker inspection.' }
    $selected = [ordered]@{}
    for ($i = 0; $i -lt $Fields.Count; $i++) {
        if ($Fields[$i] -in 'Created', 'State.StartedAt') {
            $json = [Text.Json.JsonDocument]::Parse([string]$rows[$i])
            try { $selected[$Fields[$i]] = $json.RootElement.GetString() } finally { $json.Dispose() }
        } else { $selected[$Fields[$i]] = ConvertFrom-Json -InputObject $rows[$i] -NoEnumerate }
    }
    [pscustomobject]$selected
}

function Assert-ExistingFixture([string] $CandidateId) {
    Assert-FixtureInputHashes
    $d = Read-FixtureDockerFields container $db @('Id', 'Name', 'Created', 'Image', 'Config.Image', 'Config.Labels',
        'State.Running', 'State.Paused', 'State.Restarting', 'State.Dead', 'State.StartedAt', 'RestartCount',
        'HostConfig.ReadonlyRootfs', 'HostConfig.Tmpfs', 'HostConfig.PortBindings', 'Mounts', 'NetworkSettings.Ports', 'NetworkSettings.Networks')
    $n = Read-FixtureDockerFields network $network @('Id', 'Name', 'Created', 'Driver', 'Internal', 'Labels', 'Containers')
    if ($d.Id -cne $fixturePhase.databaseId -or $n.Id -cne $fixturePhase.networkId -or $d.Name -cne "/$db" -or $n.Name -cne $network -or
        $d.Image -cne $postgresIdentity.Id -or $d.'Config.Image' -cne $restoreCtx.Config.postgresImage -or
        $d.'Config.Labels'.'premierecalendar.validation' -cne $label -or $n.Labels.'premierecalendar.validation' -cne $label) { throw 'Historical fixture identity/ownership changed.' }
    if ($d.'State.Running' -ne $true -or $d.'State.Paused' -ne $false -or $d.'State.Restarting' -ne $false -or $d.'State.Dead' -ne $false) { throw 'The historical fixture is not continuously running.' }
    foreach ($stamp in @($d.Created, $d.'State.StartedAt', $n.Created)) {
        $instant = ConvertTo-FixtureInstant $stamp
        if ($instant -le 0 -or $instant -gt $fixtureAnchor) { throw 'Historical fixture was created/restarted after the completion anchor.' }
    }
    if ($d.'HostConfig.ReadonlyRootfs' -ne $true -or
        (@($d.'HostConfig.Tmpfs'.PSObject.Properties.Name | Sort-Object) -join '|') -cne '/run/postgresql|/tmp|/var/lib/postgresql' -or
        @($d.Mounts | Where-Object { $_.Type -ne 'tmpfs' }).Count -ne 0 -or
        ($null -ne $d.'HostConfig.PortBindings' -and @($d.'HostConfig.PortBindings'.PSObject.Properties).Count -ne 0) -or
        ($null -ne $d.'NetworkSettings.Ports' -and @($d.'NetworkSettings.Ports'.PSObject.Properties | Where-Object { $null -ne $_.Value -and @($_.Value).Count -gt 0 }).Count -ne 0)) { throw 'Historical fixture storage/port isolation changed.' }
    $attachments = @($d.'NetworkSettings.Networks'.PSObject.Properties)
    if ($attachments.Count -ne 1 -or $attachments[0].Name -cne $network -or $attachments[0].Value.NetworkID -cne $n.Id -or
        $attachments[0].Value.Aliases -notcontains 'backup-db' -or $n.Internal -ne $true -or $n.Driver -cne 'bridge') { throw 'Historical fixture network isolation/alias changed.' }
    $members = @($n.Containers.PSObject.Properties)
    $expectedMembers = @($d.Id)
    if ($CandidateId) { $expectedMembers += $CandidateId }
    if ((($members.Name | Sort-Object) -join '|') -cne (($expectedMembers | Sort-Object) -join '|') -or $n.Containers.($d.Id).Name -cne $db -or
        ($CandidateId -and $n.Containers.$CandidateId.Name -cne $app)) { throw 'Unexpected historical fixture network member.' }
    $state = [ordered]@{ databaseId = $d.Id; networkId = $n.Id; postgresImageId = $d.Image;
        databaseCreatedUtc = $d.Created; databaseStartedUtc = $d.'State.StartedAt'; networkCreatedUtc = $n.Created; restartCount = $d.RestartCount }
    if ($fixtureObservation -and ($state | ConvertTo-Json -Compress) -cne ($fixtureObservation | ConvertTo-Json -Compress)) { throw 'Historical fixture continuity changed during qualification.' }
    $state
}

$plan = @(
    "Refuse if compatibility evidence or the container $app exist; require the restored database $db on the internal network $network",
    "Record the sanitized state of the restored database (schema $($cfg.oldSchemaVersion) expected) with schema-state.sql",
    "Start the exact candidate image from build-metadata.json against it, hardened, background services disabled",
    "Require readiness, version/revision, schema $($cfg.databaseSchemaVersion) and a healthy database from /health/version",
    'Record the state again; compare_schema_state.py must pass every check',
    "Write $report (and the other backup-compat-* files); remove the candidate container")
if ($historical) { $plan += 'Historical premerge qualification only: revalidate the explicit continuity proof, original hashes and post-state; never create canonical release compatibility evidence.' }
if (Show-ReleasePlan -Title 'Test-BackupCompatibility' -Step $plan) { return }

Assert-FilesAbsent $outputs
if ($historical) {
    $descriptorHash = Get-FileSha256 $ContinuityProof
    $fixtureDescriptor = Read-Json $ContinuityProof
    if ($fixtureDescriptor.schema -ne 1 -or $fixtureDescriptor.originalBundle -ne $restoreCtx.Bundle -or
        -not [IO.Path]::IsPathRooted($fixtureDescriptor.terminalReceiptPath) -or -not [IO.Path]::IsPathRooted($fixtureDescriptor.phaseOneStatusPath) -or
        $fixtureDescriptor.phaseOneStatusSha256 -notmatch '^[0-9a-fA-F]{64}$' -or
        (Get-FileSha256 $fixtureDescriptor.phaseOneStatusPath) -ine $fixtureDescriptor.phaseOneStatusSha256) { throw 'Invalid or mismatched historical continuity descriptor.' }
    $fixturePhase = Read-Json $fixtureDescriptor.phaseOneStatusPath
    if ($fixturePhase.state -cne 'passed' -or $fixturePhase.scope -cne 'historical-existing-restored-fixture-continuity-only' -or
        $fixturePhase.productionAcceptance -ne $false -or $fixturePhase.freshBackupEvidence -ne $false -or
        $fixturePhase.sanitizedStateEqualsOriginalPostCompatibility -ne $true -or
        $fixturePhase.databaseId -notmatch '^[0-9a-f]{64}$' -or $fixturePhase.networkId -notmatch '^[0-9a-f]{64}$') { throw 'Phase-one historical fixture continuity did not pass.' }
    if ((Get-FileSha256 $fixtureDescriptor.terminalReceiptPath) -ine $fixturePhase.originalHashes.terminalReceipt) { throw 'Historical terminal receipt hash mismatch.' }
    $fixtureTerminal = Read-Json $fixtureDescriptor.terminalReceiptPath
    if ($fixtureTerminal.bundle -ne $restoreCtx.Bundle -or $fixtureTerminal.sourceRevision -notmatch '^[0-9a-f]{40}$') { throw 'The terminal receipt identifies another original bundle/source.' }
    $anchors = @($fixtureTerminal.offlineBackup.freshnessChecks | Where-Object { $_.checkpoint -ceq 'isolated-restore-and-compatibility-completed' })
    if ($anchors.Count -ne 1 -or $anchors[0].withinWindow -ne $true) { throw 'Missing unique historical restore/compatibility completion anchor.' }
    $fixtureAnchor = ConvertTo-FixtureInstant $anchors[0].checkedUtc
    $backupCreated = ConvertTo-FixtureInstant $fixtureTerminal.offlineBackup.createdUtc
    $descriptorInstant = ConvertTo-FixtureInstant $fixtureDescriptor.descriptorCreatedUtc
    if ($backupCreated -le 0 -or $backupCreated -gt $fixtureAnchor -or $fixtureAnchor -ne (ConvertTo-FixtureInstant $fixturePhase.anchorUtc) -or $descriptorInstant -lt $fixtureAnchor -or
        $descriptorInstant -gt (ConvertTo-FixtureInstant ([DateTimeOffset]::UtcNow.ToString('O')))) { throw 'Historical anchor/descriptor timestamps are inconsistent.' }
    foreach ($stage in 'offline-restore', 'offline-backup-compatibility') {
        $completed = @($fixtureTerminal.stages | Where-Object { $_.name -ceq $stage })
        if ($completed.Count -ne 1 -or $completed[0].exitCode -ne 0) { throw 'Historical restore/compatibility did not complete successfully.' }
    }
    $fixturePaths = [ordered]@{
        terminalReceipt = $fixtureDescriptor.terminalReceiptPath
        config = Join-Path $restoreCtx.Bundle 'release-config.json'
        publicRestoreProof = Join-Path $restoreCtx.Bundle 'backup-restore.json'
        privateRestoreProof = Join-Path $restoreCtx.Private 'backup-restore.json'
        backupSummary = Join-Path $restoreCtx.Private 'backup-summary.json'
        originalPostCompatibility = Join-Path $restoreCtx.Bundle 'backup-schema-after.txt'
        originalCompatibility = Join-Path $restoreCtx.Bundle 'schema-backup-compatibility.json'
        canonicalSql = Join-Path $fixtureTerminal.source 'eng/release/schema-state.sql'
        canonicalParser = Join-Path $fixtureTerminal.source 'eng/release/compare_schema_state.py'
    }
    Assert-FixtureInputHashes
    if ((Get-FileSha256 (Join-Path $PSScriptRoot 'schema-state.sql')) -ine $fixturePhase.originalHashes.canonicalSql -or
        (Get-FileSha256 (Join-Path $PSScriptRoot 'compare_schema_state.py')) -ine $fixturePhase.originalHashes.canonicalParser) { throw 'Canonical schema tools differ from the historical comparison; review is required.' }
    $originalComparison = Read-Json $fixturePaths.originalCompatibility
    $fixtureSummary = Read-Json $fixturePaths.backupSummary
    $fixtureRestore = Read-Json $fixturePaths.privateRestoreProof
    if ($originalComparison.allChecksPass -ne $true -or -not (Test-AllChecksTrue $originalComparison) -or
        $originalComparison.afterSchemaVersion -ne $restoreCtx.Config.databaseSchemaVersion -or
        $cfg.oldSchemaVersion -ne $originalComparison.afterSchemaVersion -or
        $fixturePhase.originalMaxver -ne $restoreCtx.Config.oldSchemaVersion -or
        $fixturePhase.snapshotSha256 -ine $fixturePhase.originalHashes.originalPostCompatibility) { throw 'Historical post-state or candidate old-schema contract is inconsistent.' }
    if ($fixtureSummary.DatabaseDumpSha256 -ine $fixtureTerminal.offlineBackup.sha256 -or $fixtureSummary.DatabaseDumpBytes -ne $fixtureTerminal.offlineBackup.bytes -or
        $originalComparison.backupSha256 -ine $fixtureSummary.DatabaseDumpSha256 -or $originalComparison.backupBytes -ne $fixtureSummary.DatabaseDumpBytes) { throw 'Historical backup/compatibility receipts identify different dumps.' }
    $fixtureObservation = $null
}
Assert-LocalDockerAvailable
Assert-DockerNamesFree -Container $app
if (-not (Test-LocalDocker container inspect $db)) { throw "Missing the restored database container: $db (run Initialize-RestoreCheck.ps1 and Restore-Backup.ps1)." }
if ((Invoke-LocalDocker network inspect $network --format '{{.Internal}}' | Select-Object -First 1) -ne 'true') { throw 'The validation network is not internal.' }
# Labels are read from the JSON (a --format template with embedded quotes would not survive the wsl.exe boundary).
if ((Invoke-LocalDockerJson inspect $db)[0].Config.Labels.'premierecalendar.validation' -ne $label) { throw 'Unexpected isolated database ownership label.' }
$restorePublic = Read-Json (Join-Path $restoreCtx.Bundle 'backup-restore.json')
if ($restorePublic.restoreExitCode -ne 0 -or $restorePublic.inputPipeFailed -or $restorePublic.timedOut -or $restorePublic.destinationContainer -ne $db -or $restorePublic.destinationNetwork -ne $network) { throw 'The restore proof does not describe this isolated database.' }
if (-not $restorePublic.destinationInternalNetwork -or $restorePublic.destinationPersistentMounts -ne 0 -or $restorePublic.destinationPublishedPorts -ne 0) { throw 'The restore destination was not isolated.' }
if ($historical) {
    foreach ($proof in @($restorePublic, $fixtureRestore)) {
        if ($proof.restoreExitCode -ne 0 -or $proof.inputPipeFailed -ne $false -or $proof.timedOut -ne $false -or
            $proof.destinationContainer -cne $db -or $proof.destinationNetwork -cne $network -or $proof.destinationDatabase -cne 'premierecalendar' -or
            $proof.destinationInternalNetwork -ne $true -or $proof.destinationPersistentMounts -ne 0 -or $proof.destinationPublishedPorts -ne 0 -or
            $proof.backupSha256 -ine $fixtureSummary.DatabaseDumpSha256 -or $proof.backupBytes -ne $fixtureSummary.DatabaseDumpBytes) { throw 'Historical restore proof/backup binding failed.' }
    }
    $postgresIdentity = Read-FixtureDockerFields image $restoreCtx.Config.postgresImage @('Id', 'Os', 'Architecture')
    if ($postgresIdentity.Os -cne 'linux' -or $postgresIdentity.Architecture -cne 'amd64' -or $postgresIdentity.Id -notmatch '^sha256:[0-9a-f]{64}$') { throw 'Historical PostgreSQL image identity/platform is invalid.' }
    $fixtureObservation = Assert-ExistingFixture
}

$metadata = Read-Json (Join-Path $ctx.Bundle 'build-metadata.json')
$image = (Invoke-LocalDockerJson image inspect $metadata.localImageIdentity)[0]
if ($metadata.version -ne $cfg.version -or $image.Id -ne $metadata.localImageIdentity -or $image.Os -ne 'linux' -or $image.Architecture -ne 'amd64') { throw 'The candidate image differs from the build metadata.' }
if ($image.Config.Labels.'org.opencontainers.image.revision' -ne $metadata.sourceRevision -or $image.Config.Labels.'org.opencontainers.image.version' -ne $cfg.version) { throw 'The candidate image labels differ from the build metadata.' }
Write-NewJson -Path $imageFile -Object ([ordered]@{ version = $cfg.version; sourceRevision = $metadata.sourceRevision; imageId = $image.Id; platform = 'linux/amd64' })

function Get-SchemaState {
    param([string] $Path, [int] $MaxVersion = $cfg.oldSchemaVersion)
    $sql = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'schema-state.sql') -Raw
    $readOnly = @(if ($historical) { '-e'; 'PGOPTIONS=-c default_transaction_read_only=on' })
    $schemaDb = if ($historical) { $fixtureObservation.databaseId } else { $db }
    $text = Invoke-LocalDockerInput -InputText ($sql + "`n") -DockerArguments (@('exec', '-i') + @($readOnly) + @($schemaDb, 'psql', '-X', '-A', '-t', '-F', '|', '-q', '-v', 'ON_ERROR_STOP=1', '-v', "maxver=$MaxVersion", '-U', 'postgres', '-d', 'premierecalendar'))
    Write-NewText -Path $Path -Text ((@($text) -join "`n") + "`n")
}

if ($historical) {
    Get-SchemaState -Path $continuitySnapshot -MaxVersion $fixturePhase.originalMaxver
    if ((Get-FileSha256 $continuitySnapshot) -ine $fixturePhase.originalHashes.originalPostCompatibility) { throw 'The current sanitized fixture state differs from the original post-compatibility snapshot.' }
    $null = Assert-ExistingFixture
}
Get-SchemaState -Path $before
if ($historical) { $null = Assert-ExistingFixture }
$connection = 'Host=backup-db;Database=premierecalendar;Username=postgres;GSS Encryption Mode=Disable'
$networkTarget = if ($historical) { $fixtureObservation.networkId } else { $network }
# @(if ...) keeps an empty array: a typed assignment of an empty if-branch is $null, and splatting $null passes one
# empty argument that docker run reads as the image name ("invalid reference format").
$pullPolicy = @(if ($historical) { '--pull=never' })
$candidateStarted = $false
try {
    $candidateOutput = @(Invoke-LocalDocker run @pullPolicy -d --name $app --label "premierecalendar.validation=compat-$($ctx.BundleName)" --network $networkTarget `
        --read-only --user 1654:999 --cap-drop ALL --security-opt no-new-privileges:true --tmpfs '/tmp:rw,noexec,nosuid,size=64m' --tmpfs '/app/App_Data:rw,uid=1654,gid=999,mode=0770,size=128m' `
        -e AppDatabase__Provider=PostgreSql -e "AppDatabase__ConnectionString=$connection" `
        -e ProviderScheduler__Enabled=false -e ProviderDeltaSync__Enabled=false -e CalendarWarmup__Enabled=false -e CalendarCache__Enabled=false `
        -e CalendarCache__AdjacentWeekPrefetchEnabled=false -e ItemEnrichmentCache__Enabled=false -e ImdbDataset__Enabled=false `
        $image.Id)
    $candidateStarted = $true
    if ($historical) {
        $candidateId = ($candidateOutput -join '').Trim()
        if ($candidateId -notmatch '^[0-9a-f]{64}$') { throw 'The candidate did not return an immutable container identity.' }
        $null = Assert-ExistingFixture -CandidateId $candidateId
    }
    try {
        Wait-LocalCondition -Attempts 90 -Message 'The candidate app did not become ready against the restored backup.' -Condition { Test-LocalDocker exec $app wget -qO- http://127.0.0.1:8080/health/ready }
    }
    catch {
        $state = (@(Invoke-LocalDocker inspect $app --format '{{.State.Status}} exit={{.State.ExitCode}} oom={{.State.OOMKilled}}') -join ' ')
        $startupStateName = if ($historical) { 'existing-fixture-startup-state.txt' } else { 'backup-compat-startup-state.txt' }
        Write-NewText -Path (Join-Path $ctx.Private $startupStateName) -Text ($state + "`n")
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
    if ($historical) { $null = Assert-ExistingFixture -CandidateId $candidateId }
    $schemaReport = if ($historical) { $comparisonReport } else { $report }
    Invoke-ReleasePython (Join-Path $PSScriptRoot 'compare_schema_state.py') --before $before --after $after --output $schemaReport `
        --restore-proof (Join-Path $restoreCtx.Private 'backup-restore.json') --backup-summary (Join-Path $restoreCtx.Private 'backup-summary.json') `
        --old-schema $cfg.oldSchemaVersion --new-schema $newSchema
    Write-NewText -Path $securityFile -Text ((@(Invoke-LocalDocker inspect $app --format '{{.Image}} {{.Config.User}} {{.HostConfig.ReadonlyRootfs}} {{.HostConfig.SecurityOpt}} {{.HostConfig.CapDrop}}') -join "`n") + "`n")
    if ($historical) {
        $finalObservation = Assert-ExistingFixture -CandidateId $candidateId
        Write-NewJson -Path $report -Object ([ordered]@{
            schema = 1; scope = 'historical-existing-restored-fixture-premerge-qualification'
            productionAcceptance = $false; freshBackupEvidence = $false
            version = $cfg.version; sourceRevision = $metadata.sourceRevision; imageId = $image.Id; candidateContainerId = $candidateId
            descriptorCreatedUtc = $fixtureDescriptor.descriptorCreatedUtc; continuityProofSha256 = $descriptorHash
            phaseOneStatusSha256 = $fixtureDescriptor.phaseOneStatusSha256
            originalBundleName = $restoreCtx.BundleName; originalSourceRevision = $fixtureTerminal.sourceRevision
            originalHashes = $fixturePhase.originalHashes; completionAnchorUtc = $fixturePhase.anchorUtc
            originalBackupCreatedUtc = $fixtureTerminal.offlineBackup.createdUtc
            backupSha256 = $fixtureSummary.DatabaseDumpSha256; backupBytes = $fixtureSummary.DatabaseDumpBytes
            fixture = $finalObservation; schemaComparisonSha256 = Get-FileSha256 $comparisonReport
            comparison = Read-Json $comparisonReport
            continuityAssumptions = 'Trusted Docker metadata/clock; monitored canonical counts/migrations/fingerprints, not proof of all row bytes.'
        })
    } elseif (-not $KeepResources) { $null = Test-LocalDocker rm -f $app }
    Write-Host 'The candidate image preserved the restored backup (all compatibility checks passed).'
} finally {
    if ($historical -and $candidateStarted -and -not $KeepResources -and $candidateId -match '^[0-9a-f]{64}$') {
        if (-not (Test-LocalDocker rm -f $candidateId)) { throw 'Could not remove the owned historical-qualification candidate container; preserve the fixture and inspect that candidate ID.' }
    }
}
