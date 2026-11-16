#Requires -Version 7.0
# Run the real compatibility script with only external Docker/SQL/Python calls doubled.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$release = Join-Path $root 'eng/release'
$helper = Join-Path $release 'Test-BackupCompatibility.ps1'
$sandbox = Join-Path $root ('artifacts/harness-tests/existing-fixture-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $sandbox -Force | Out-Null
$count = 0
function Assert-FixtureTest([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
    $script:count++
}
function Write-FixtureJson([string] $Path, $Value) {
    [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Value -Depth 20))
}
function Get-FixtureHash([string] $Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

$shim = Join-Path $sandbox 'ReleaseCommon.Double.ps1'
$commonLiteral = (Join-Path $release 'ReleaseCommon.ps1').Replace("'", "''")
[IO.File]::WriteAllText($shim, @"
. '$commonLiteral'
function Assert-LocalDockerAvailable { `$global:FixtureDouble.Calls.Add(@('available')) }
function Invoke-LocalDocker { Invoke-FixtureDockerDouble @args }
function Test-LocalDocker { Test-FixtureDockerDouble @args }
function Invoke-ReleasePython { Invoke-FixtureComparisonDouble @args }
function Wait-LocalCondition {
    param([int]`$Attempts, [string]`$Message, [scriptblock]`$Condition)
    if (`$global:FixtureDouble.Mutation -eq 'startup-failure') { throw 'Fixture startup failed.' }
    if (-not (& `$Condition)) { throw `$Message }
}
`$script:ReleaseWsl = 'Invoke-FixtureSqlDouble'
`$script:ReleaseDockerPrefix = @()
"@)

# The script's actual dot-source is redirected to a shim which loads the real
# common context/file functions, then replaces only process boundaries.
function Join-Path([string] $Path, [string] $ChildPath) {
    if ($ChildPath -eq 'ReleaseCommon.ps1') { return $shim }
    Microsoft.PowerShell.Management\Join-Path -Path $Path -ChildPath $ChildPath
}

function New-FixtureCase([string] $Mutation = '', [switch] $Default) {
    $case = Join-Path $sandbox ([guid]::NewGuid().ToString('N'))
    $old = Join-Path $case 'original'; $new = Join-Path $case 'candidate'
    foreach ($path in @($old, "$old.private", $new, "$new.private")) { New-Item -ItemType Directory -Path $path -Force | Out-Null }
    $oldConfig = @{ schema = 1; version = '1.0.0'; bundleName = 'original'; resourcePrefix = 'original-random'; imageTag = 'old:local'; oldSchemaVersion = 4; databaseSchemaVersion = 5; postgresImage = 'postgres:fixture@sha256:' + ('b' * 64) }
    $newConfig = @{ schema = 1; version = '2.0.0'; bundleName = 'candidate'; resourcePrefix = 'candidate-random'; imageTag = 'candidate:local'; oldSchemaVersion = 5; databaseSchemaVersion = 6; postgresImage = $oldConfig.postgresImage }
    Write-FixtureJson (Join-Path $old 'release-config.json') $oldConfig
    Write-FixtureJson (Join-Path $new 'release-config.json') $newConfig
    $db = if ($Default) { 'candidate-random_restore_db' } else { 'original-random_restore_db' }
    $network = if ($Default) { 'candidate-random_restore_net' } else { 'original-random_restore_net' }
    $owner = if ($Default) { 'restore-candidate' } else { 'restore-original' }
    $backup = @{ DatabaseDumpSha256 = 'f' * 64; DatabaseDumpBytes = 1234; PreparedUtc = '2026-01-01T00:00:00Z'; StackRevision = 10; PreviousImage = 'old-digest'; BackupRoot = '/fake-backup'; DatabaseDumpPath = 'NEVER-READ.dump' }
    $proof = @{ restoreExitCode = 0; inputPipeFailed = $false; timedOut = $false; backupSha256 = $backup.DatabaseDumpSha256; backupBytes = 1234;
        destinationContainer = $db; destinationNetwork = $network; destinationDatabase = 'premierecalendar'; destinationInternalNetwork = $true; destinationPersistentMounts = 0; destinationPublishedPorts = 0 }
    $proofBundle = if ($Default) { $new } else { $old }
    Write-FixtureJson (Join-Path $proofBundle 'backup-restore.json') $proof
    Write-FixtureJson (Join-Path "$proofBundle.private" 'backup-restore.json') $proof
    Write-FixtureJson (Join-Path "$proofBundle.private" 'backup-summary.json') $backup
    $snapshot = "SCHEMA_VERSION|5`nfixture_rows|3`nMIGRATION_HISTORY|4|fixture|checksum`n"
    [IO.File]::WriteAllText((Join-Path $old 'backup-schema-after.txt'), $snapshot)
    Write-FixtureJson (Join-Path $old 'schema-backup-compatibility.json') @{ allChecksPass = $true; checks = @{ preserved = $true }; afterSchemaVersion = 5; backupSha256 = $backup.DatabaseDumpSha256; backupBytes = 1234 }
    $terminal = Join-Path $case 'terminal.json'
    Write-FixtureJson $terminal @{ bundle = $old; source = $root; sourceRevision = '1' * 40; stages = @(@{ name = 'offline-restore'; exitCode = 0 }, @{ name = 'offline-backup-compatibility'; exitCode = 0 });
        offlineBackup = @{ sha256 = $backup.DatabaseDumpSha256; bytes = 1234; createdUtc = '2026-01-01T00:00:00Z'; freshnessChecks = @(@{ checkpoint = 'isolated-restore-and-compatibility-completed'; checkedUtc = '2026-01-01T01:00:00Z'; withinWindow = $true }) } }
    $hashes = [ordered]@{}
    foreach ($entry in @(
        @('terminalReceipt', $terminal), @('config', (Join-Path $old 'release-config.json')),
        @('publicRestoreProof', (Join-Path $proofBundle 'backup-restore.json')), @('privateRestoreProof', (Join-Path "$proofBundle.private" 'backup-restore.json')),
        @('backupSummary', (Join-Path "$proofBundle.private" 'backup-summary.json')), @('originalPostCompatibility', (Join-Path $old 'backup-schema-after.txt')),
        @('originalCompatibility', (Join-Path $old 'schema-backup-compatibility.json')), @('canonicalSql', (Join-Path $release 'schema-state.sql')), @('canonicalParser', (Join-Path $release 'compare_schema_state.py'))
    )) { $hashes[$entry[0]] = Get-FixtureHash $entry[1] }
    $phasePath = Join-Path $case 'phase-one.json'
    Write-FixtureJson $phasePath @{ state = 'passed'; scope = 'historical-existing-restored-fixture-continuity-only'; productionAcceptance = $false; freshBackupEvidence = $false;
        originalHashes = $hashes; anchorUtc = '2026-01-01T01:00:00Z'; originalMaxver = 4; databaseId = 'd' * 64; networkId = 'e' * 64;
        snapshotSha256 = $hashes.originalPostCompatibility; sanitizedStateEqualsOriginalPostCompatibility = $true }
    $descriptor = Join-Path $case 'continuity.json'
    Write-FixtureJson $descriptor @{ schema = 1; descriptorCreatedUtc = [DateTimeOffset]::UtcNow.AddSeconds(-1).ToString('O'); originalBundle = $old;
        terminalReceiptPath = $terminal; phaseOneStatusPath = $phasePath; phaseOneStatusSha256 = Get-FixtureHash $phasePath }
    Write-FixtureJson (Join-Path $new 'build-metadata.json') @{ version = '2.0.0'; sourceRevision = '2' * 40; localImageIdentity = 'sha256:' + ('a' * 64) }
    $global:FixtureDouble = [pscustomobject]@{ Calls = [Collections.Generic.List[object]]::new(); Old = $old; New = $new; Descriptor = $descriptor;
        Mutation = $Mutation; Snapshot = $snapshot; SqlCalls = 0; Attached = $false; Db = $db; Network = $network; Owner = $owner; Default = [bool]$Default }
    $global:FixtureDouble
}

function Test-FixtureDockerDouble {
    $f = $global:FixtureDouble; $f.Calls.Add(@($args))
    if ($args[0] -eq 'container' -and $args[1] -eq 'inspect') { return ($args[2] -eq $f.Db -and $f.Mutation -ne 'missing') }
    if ($args[0] -eq 'rm') { $f.Attached = $false; return $true }
    if ($args[0] -eq 'exec') { return $true }
    throw "Unexpected test Docker call: $($args -join ' ')"
}
function Invoke-FixtureDockerDouble {
    $f = $global:FixtureDouble; $f.Calls.Add(@($args))
    if ($args[0] -eq 'network' -and $args -contains '{{.Internal}}') { return 'true' }
    if ($args[0] -eq 'inspect' -and $args[1] -eq $f.Db) { return (ConvertTo-Json -InputObject @(@{ Config = @{ Labels = @{ 'premierecalendar.validation' = $f.Owner } } }) -Depth 8 -Compress) }
    if ($args[0] -eq 'image' -and $args[1] -eq 'inspect' -and $args[2] -eq ('sha256:' + ('a' * 64))) {
        $revision = if ($f.Mutation -eq 'candidate-image') { '1' * 40 } else { '2' * 40 }
        return (ConvertTo-Json -InputObject @(@{ Id = 'sha256:' + ('a' * 64); Os = 'linux'; Architecture = 'amd64'; Config = @{ Labels = @{ 'org.opencontainers.image.revision' = $revision; 'org.opencontainers.image.version' = '2.0.0' } } }) -Depth 8 -Compress)
    }
    if ($args[0] -in 'container', 'network', 'image' -and $args -contains '--format') {
        $members = @{ ('d' * 64) = @{ Name = $f.Db } }
        if ($f.Attached) { $members[('c' * 64)] = @{ Name = 'candidate-random_compat_app' } }
        $started = if ($f.Mutation -eq 'restart' -or ($f.Mutation -eq 'restart-after-attach' -and $f.Attached)) { '2026-01-01T01:00:00.000000001Z' } else { '2026-01-01T00:30:00.123456789Z' }
        $db = @{
            Id = if ($f.Mutation -eq 'replacement') { '9' * 64 } else { 'd' * 64 }; Name = '/' + $f.Db; Created = '2026-01-01T00:30:00Z'; Image = 'sha256:' + ('b' * 64)
            'Config.Image' = 'postgres:fixture@sha256:' + ('b' * 64); 'Config.Labels' = @{ 'premierecalendar.validation' = $f.Owner }
            'State.Running' = $true; 'State.Paused' = ($f.Mutation -eq 'paused'); 'State.Restarting' = $false; 'State.Dead' = $false; 'State.StartedAt' = $started; RestartCount = 0
            'HostConfig.ReadonlyRootfs' = $true; 'HostConfig.Tmpfs' = @{ '/var/lib/postgresql' = 'rw'; '/tmp' = 'rw'; '/run/postgresql' = 'rw' }; 'HostConfig.PortBindings' = $null
            Mounts = if ($f.Mutation -eq 'persistent') { @(@{ Type = 'volume' }) } else { @(@{ Type = 'tmpfs' }) }
            'NetworkSettings.Ports' = @{ '5432/tcp' = $null }; 'NetworkSettings.Networks' = @{ ($f.Network) = @{ NetworkID = 'e' * 64; Aliases = @('backup-db') } }
        }
        $net = @{ Id = 'e' * 64; Name = $f.Network; Created = if ($f.Mutation -eq 'network-recreated') { '2026-01-01T01:00:01Z' } else { '2026-01-01T00:29:00Z' };
            Driver = 'bridge'; Internal = $true; Labels = @{ 'premierecalendar.validation' = $f.Owner }; Containers = $members }
        $value = if ($args[0] -eq 'container') { $db } elseif ($args[0] -eq 'network') { $net } else { @{ Id = 'sha256:' + ('b' * 64); Os = 'linux'; Architecture = 'amd64' } }
        $template = $args[[Array]::IndexOf($args, '--format') + 1]
        foreach ($match in [regex]::Matches($template, '\{\{println \(json \.([^)]*)\)\}\}')) { ConvertTo-Json -InputObject $value[$match.Groups[1].Value] -Compress -Depth 12 }
        return
    }
    if ($args[0] -eq 'run') { $f.Attached = $true; return ('c' * 64) }
    if ($args[0] -eq 'exec') {
        $revision = if ($f.Mutation -eq 'candidate-health') { '1' * 40 } else { '2' * 40 }
        return (ConvertTo-Json -InputObject @{ version = '2.0.0'; sourceRevision = $revision; databaseSchemaVersion = 6;
            database = @{ currentSchemaVersion = 6; targetSchemaVersion = 6; healthy = $true }; performanceDiagnostics = @{ healthy = $true }; operationalLogging = @{ failureCount = 0 } } -Depth 8 -Compress)
    }
    if ($args[0] -eq 'inspect') { return ('sha256:' + ('a' * 64) + ' 1654:999 true [no-new-privileges:true] [ALL]') }
    throw "Unexpected Docker boundary: $($args -join ' ')"
}
function Invoke-FixtureSqlDouble {
    $f = $global:FixtureDouble; $f.Calls.Add(@($args)); $null = @($input)
    $global:LASTEXITCODE = 0
    $f.SqlCalls++
    if ($f.Mutation -eq 'snapshot') { return 'SCHEMA_VERSION|99' }
    if (-not $f.Default -and $f.SqlCalls -eq 1) { return $f.Snapshot.TrimEnd("`n") }
    if (($f.Default -and $f.SqlCalls -eq 2) -or (-not $f.Default -and $f.SqlCalls -eq 3)) { return 'SCHEMA_VERSION|6' }
    'SCHEMA_VERSION|5'
}
function Invoke-FixtureComparisonDouble {
    $f = $global:FixtureDouble; $f.Calls.Add(@($args))
    $output = $args[[Array]::IndexOf($args, '--output') + 1]
    Write-FixtureJson $output @{ allChecksPass = $true; checks = @{ preserved = $true }; beforeSchemaVersion = 5; afterSchemaVersion = 6; backupSha256 = 'f' * 64; backupBytes = 1234 }
}

try {
    $f = New-FixtureCase
    $plan = & $helper -Bundle $f.New -OriginalRestoreBundle $f.Old -ContinuityProof $f.Descriptor -WhatIf 6>&1
    Assert-FixtureTest ($f.Calls.Count -eq 0 -and -not (Test-Path (Join-Path $f.New 'existing-fixture-compatibility.json'))) 'WhatIf invoked an external boundary or wrote compatibility evidence.'
    & $helper -Bundle $f.New -OriginalRestoreBundle $f.Old -ContinuityProof $f.Descriptor
    $report = Get-Content (Join-Path $f.New 'existing-fixture-compatibility.json') -Raw | ConvertFrom-Json
    Assert-FixtureTest ($report.scope -eq 'historical-existing-restored-fixture-premerge-qualification' -and $report.productionAcceptance -eq $false -and $report.freshBackupEvidence -eq $false) 'Historical result claims production/fresh-backup acceptance.'
    Assert-FixtureTest ($report.sourceRevision -eq ('2' * 40) -and $report.originalSourceRevision -eq ('1' * 40) -and $report.fixture.databaseId -eq ('d' * 64)) 'New candidate and original fixture/source were conflated.'
    Assert-FixtureTest (-not (Test-Path (Join-Path $f.New 'schema-backup-compatibility.json')) -and -not (Test-Path (Join-Path $f.New 'backup-restore.json'))) 'Historical mode produced canonical release/restore evidence.'
    Assert-FixtureTest ($report.continuityProofSha256 -eq (Get-FixtureHash $f.Descriptor) -and $report.originalHashes.originalPostCompatibility -eq (Get-FixtureHash (Join-Path $f.Old 'backup-schema-after.txt')) -and $report.originalBackupCreatedUtc) 'Historical result lost descriptor/original-state hashes or backup time.'
    $releaseRejected = & {
        . (Microsoft.PowerShell.Management\Join-Path -Path $release -ChildPath 'ReleaseCommon.ps1')
        . (Join-Path $release 'ReleaseGates.ps1')
        Write-FixtureJson (Join-Path $f.New 'release-2.0.0-audit.json') @{}
        Write-FixtureJson (Join-Path $f.New 'release-2.0.0-scan-summary.json') @{}
        try { Assert-ReleaseImageGates -Context (Get-ReleaseContext -Bundle $f.New) | Out-Null; $false }
        catch { $_.Exception.Message.Contains('schema-backup-compatibility.json') }
    }
    Assert-FixtureTest $releaseRejected 'Production image gate consumed historical compatibility as canonical acceptance.'
    $run = @($f.Calls | Where-Object { $_[0] -eq 'run' })[0]
    Assert-FixtureTest ($run -contains ('e' * 64) -and $run -contains ('sha256:' + ('a' * 64)) -and $run -contains '--pull=never' -and $run -contains '--read-only' -and $run -contains '1654:999') 'Candidate lost immutable image/network binding or hardening.'
    $sql = @($f.Calls | Where-Object { $_ -contains 'psql' })
    Assert-FixtureTest ($sql.Count -eq 3 -and $sql[0] -contains 'maxver=4' -and $sql[1] -contains 'maxver=5' -and $sql[2] -contains 'maxver=5' -and $sql[0] -contains ('d' * 64)) 'Original snapshot maxver or candidate schema comparison scope changed.'
    $comparison = @($f.Calls | Where-Object { $_ -contains '--restore-proof' })[0]
    Assert-FixtureTest ($comparison -contains (Join-Path "$($f.Old).private" 'backup-restore.json') -and $comparison -contains (Join-Path "$($f.Old).private" 'backup-summary.json') -and $comparison -contains '--old-schema' -and $comparison -contains '--new-schema') 'Canonical schema comparison used candidate restore receipts or lost schema gates.'
    Assert-FixtureTest (-not $f.Attached) 'Historical candidate was not removed.'
    $removals = @($f.Calls | Where-Object { $_[0] -eq 'rm' })
    Assert-FixtureTest ($removals.Count -eq 1 -and $removals[0][2] -eq ('c' * 64)) 'Historical cleanup did not target only its own immutable candidate ID.'

    foreach ($mutation in @('missing', 'replacement', 'restart', 'network-recreated', 'paused', 'persistent', 'snapshot', 'restart-after-attach', 'hash', 'receipt-binding', 'phase-tamper', 'terminal-tamper', 'dump-binding', 'failed-anchor', 'candidate-image', 'candidate-health')) {
        $f = New-FixtureCase $mutation
        if ($mutation -eq 'hash') { [IO.File]::AppendAllText((Join-Path $f.Old 'backup-schema-after.txt'), 'changed') }
        if ($mutation -eq 'receipt-binding') {
            $descriptor = Get-Content $f.Descriptor -Raw | ConvertFrom-Json
            $descriptor.originalBundle = $f.New
            Write-FixtureJson $f.Descriptor $descriptor
        }
        if ($mutation -in 'phase-tamper', 'terminal-tamper', 'dump-binding', 'failed-anchor') {
            $descriptor = Get-Content $f.Descriptor -Raw | ConvertFrom-Json
            $phase = Get-Content $descriptor.phaseOneStatusPath -Raw | ConvertFrom-Json
            if ($mutation -eq 'phase-tamper') { $phase.databaseId = '0' * 64 }
            if ($mutation -eq 'terminal-tamper') { [IO.File]::AppendAllText($descriptor.terminalReceiptPath, ' ') }
            if ($mutation -eq 'dump-binding') {
                $summaryPath = Join-Path "$($f.Old).private" 'backup-summary.json'
                $summary = Get-Content $summaryPath -Raw | ConvertFrom-Json
                $summary.DatabaseDumpSha256 = '0' * 64
                Write-FixtureJson $summaryPath $summary
                # Even a re-bound descriptor must not bypass cross-receipt binding.
                $phase.originalHashes.backupSummary = Get-FixtureHash $summaryPath
            }
            if ($mutation -eq 'failed-anchor') {
                $terminal = Get-Content $descriptor.terminalReceiptPath -Raw | ConvertFrom-Json -DateKind String
                $terminal.stages[0].exitCode = 1
                Write-FixtureJson $descriptor.terminalReceiptPath $terminal
                $phase.originalHashes.terminalReceipt = Get-FixtureHash $descriptor.terminalReceiptPath
            }
            Write-FixtureJson $descriptor.phaseOneStatusPath $phase
            if ($mutation -in 'dump-binding', 'failed-anchor') {
                $descriptor.phaseOneStatusSha256 = Get-FixtureHash $descriptor.phaseOneStatusPath
                Write-FixtureJson $f.Descriptor $descriptor
            }
        }
        $rejected = $false
        try { & $helper -Bundle $f.New -OriginalRestoreBundle $f.Old -ContinuityProof $f.Descriptor } catch { $rejected = $true }
        Assert-FixtureTest $rejected "Unsafe historical fixture accepted: $mutation"
        Assert-FixtureTest (-not (Test-Path (Join-Path $f.New 'existing-fixture-compatibility.json'))) "Rejected fixture wrote acceptance: $mutation"
        if ($mutation -in 'restart-after-attach', 'candidate-health') { Assert-FixtureTest (-not $f.Attached) 'Failed historical attachment left its candidate running.' }
        else { Assert-FixtureTest (@($f.Calls | Where-Object { $_[0] -eq 'run' }).Count -eq 0) "Rejected fixture attached candidate: $mutation" }
    }
    $f = New-FixtureCase 'startup-failure'
    $canonicalStartup = Join-Path "$($f.New).private" 'backup-compat-startup-state.txt'
    [IO.File]::WriteAllText($canonicalStartup, 'preserve canonical diagnostic')
    $canonicalStartupHash = Get-FixtureHash $canonicalStartup
    $rejected = $false
    try { & $helper -Bundle $f.New -OriginalRestoreBundle $f.Old -ContinuityProof $f.Descriptor } catch { $rejected = $true }
    Assert-FixtureTest $rejected 'Historical startup failure was accepted.'
    Assert-FixtureTest ((Test-Path (Join-Path "$($f.New).private" 'existing-fixture-startup-state.txt')) -and (Get-FixtureHash $canonicalStartup) -eq $canonicalStartupHash) 'Historical startup diagnostics collided with canonical release diagnostics.'
    Assert-FixtureTest (-not $f.Attached) 'Failed historical startup left its owned candidate running.'
    $f = New-FixtureCase -Default
    & $helper -Bundle $f.New
    Assert-FixtureTest ((Test-Path (Join-Path $f.New 'schema-backup-compatibility.json')) -and -not (Test-Path (Join-Path $f.New 'existing-fixture-compatibility.json')) -and $f.SqlCalls -eq 2) 'Default canonical compatibility behavior changed.'

    # Exercise the actual production same-backup binding function with a new,
    # different backup; historical comparison success cannot bind it.
    . (Microsoft.PowerShell.Management\Join-Path -Path $release -ChildPath 'ReleaseCommon.ps1')
    . (Join-Path $release 'ReleaseGates.ps1')
    $context = Get-ReleaseContext -Bundle $f.New
    $context.Config | Add-Member expectedStackRevision 10
    $context.Config | Add-Member oldImageDigest 'old-digest'
    $rejected = $false
    try { Assert-RestoreEvidenceBinding -Context $context -Compat ([pscustomobject]@{ backupSha256 = '0' * 64; backupBytes = 1234 }) | Out-Null } catch { $rejected = $true }
    Assert-FixtureTest $rejected 'Production accepted a historical compatibility result for an unmatched fresh dump.'
    # Execute the real Deploy freshness guard AST with the same historical time.
    $tokens = $null; $errors = $null
    $deploy = [Management.Automation.Language.Parser]::ParseFile((Join-Path $release 'Deploy-Release.ps1'), [ref]$tokens, [ref]$errors)
    $guard = $deploy.Find({ param($node) $node -is [Management.Automation.Language.IfStatementAst] -and $node.Extent.Text.Contains('TotalHours -gt $MaximumBackupAgeHours') }, $true)
    $backup = [pscustomobject]@{ PreparedUtc = '2026-01-01T00:00:00Z' }; $MaximumBackupAgeHours = 26
    $rejected = $false
    try { & ([scriptblock]::Create($guard.Extent.Text)) } catch { $rejected = $true }
    Assert-FixtureTest $rejected 'Production freshness guard accepted an expired historical backup.'
    Write-Host "$count existing restored-fixture checks passed; all external processes were doubled."
} finally {
    Remove-Variable FixtureDouble -Scope Global -ErrorAction SilentlyContinue
    $resolved = [IO.Path]::GetFullPath($sandbox)
    $allowed = [IO.Path]::GetFullPath((Microsoft.PowerShell.Management\Join-Path -Path $root -ChildPath 'artifacts/harness-tests')) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase) -or (Get-Item -LiteralPath $resolved).LinkType) { throw 'Refusing fixture test sandbox cleanup outside its owned directory.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
