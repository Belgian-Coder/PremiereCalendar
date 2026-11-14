#Requires -Version 7.0
<#
.SYNOPSIS
Step 2b: restores the fresh backup once into the isolated PostgreSQL and records a sanitized proof.

.DESCRIPTION
Reads the dump path, SHA-256 and size from <Bundle>.private\backup-summary.json (written by Prepare-Release.ps1),
re-hashes the dump, verifies that the destination is the labelled, internal-network, read-only, empty, mount-less
container from Initialize-RestoreCheck.ps1, then streams the dump through pg_restore. The dump is only read; no
production connection is made, nothing is cleaned up, and no database payload or restore output is persisted.
The public proof (allow-listed fields, no backup locator) is <Bundle>\backup-restore.json; the raw proof with the
dump path stays in <Bundle>.private\backup-restore.json. Existing evidence is never overwritten.

.PARAMETER Bundle
Full path of the release bundle.

.PARAMETER TimeoutSeconds
Deadline for the restore.

.EXAMPLE
pwsh -File D:\Projects\PremiereCalendar\eng\release\Restore-Backup.ps1 -Bundle D:\Projects\PremiereCalendar\artifacts\release\1.2.20
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string] $Bundle,
    [ValidateRange(30, 3600)][int] $TimeoutSeconds = 900
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseCommon.ps1')
$ctx = Get-ReleaseContext -Bundle $Bundle
$cfg = $ctx.Config
$destinationContainer = "$($ctx.Prefix)_restore_db"
$destinationNetwork = "$($ctx.Prefix)_restore_net"
$label = "restore-$($ctx.BundleName)"
$privateProof = Join-Path $ctx.Private 'backup-restore.json'
$publicProof = Join-Path $ctx.Bundle 'backup-restore.json'

$plan = @(
    "Refuse if $publicProof or $privateProof already exist",
    "Read the dump path, SHA-256 and byte count from $(Join-Path $ctx.Private 'backup-summary.json') and re-hash the dump (read-only)",
    "Verify $destinationContainer is read-only, labelled, on internal network $destinationNetwork, has no mounts or published ports and no tables",
    "Stream the dump into pg_restore --exit-on-error inside $destinationContainer (local WSL engine, deadline $TimeoutSeconds s)",
    'Write the allow-listed public proof and the raw private proof')
if (Show-ReleasePlan -Title 'Restore-Backup' -Step $plan) { return }

if ($cfg.requiredComputerName -and $env:COMPUTERNAME -ne $cfg.requiredComputerName) { throw "Run the isolated restore from $($cfg.requiredComputerName)." }
Assert-FilesAbsent @($publicProof, $privateProof)
$summary = Read-Json (Join-Path $ctx.Private 'backup-summary.json')
$backupPath = [string]$summary.DatabaseDumpPath
$expectedSha256 = [string]$summary.DatabaseDumpSha256
$expectedBytes = [long]$summary.DatabaseDumpBytes
if ($expectedSha256 -notmatch '^[A-Fa-f0-9]{64}$') { throw 'The backup summary has no valid SHA-256.' }
if ($expectedBytes -le 0) { throw 'The backup summary has no valid byte count.' }
if (-not (Test-Path -LiteralPath $backupPath -PathType Leaf)) { throw 'The backup file named in the summary does not exist.' }

function New-LocalDockerStartInfo {
    param([string[]] $DockerArguments)
    $info = [Diagnostics.ProcessStartInfo]::new($script:ReleaseWsl)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in (@($script:ReleaseDockerPrefix) + $DockerArguments)) { [void]$info.ArgumentList.Add($argument) }
    $info
}

function Invoke-LocalDockerText {
    param([string[]] $DockerArguments)
    $child = [Diagnostics.Process]::new()
    $child.StartInfo = New-LocalDockerStartInfo $DockerArguments
    try {
        if (-not $child.Start()) { throw 'Could not start the local isolated Docker inspection.' }
        $child.StandardInput.Close()
        $stdout = $child.StandardOutput.ReadToEndAsync()
        $stderr = $child.StandardError.ReadToEndAsync()
        if (-not $child.WaitForExit(30000)) { $child.Kill($true); [void]$child.WaitForExit(5000); throw 'The local isolated Docker inspection exceeded its deadline.' }
        if ($child.ExitCode -ne 0) { throw 'The local isolated Docker inspection failed; no restore was started.' }
        [void]$stderr.GetAwaiter().GetResult()
        $stdout.GetAwaiter().GetResult().Trim()
    }
    finally { $child.Dispose() }
}

$source = [IO.File]::Open($backupPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
try {
    $size = $source.Length
    if ($size -le 0 -or $size -ne $expectedBytes) { throw 'The backup byte count does not match the preparation evidence.' }
    $actualHash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($source))
    if ($actualHash -ne $expectedSha256.ToUpperInvariant()) { throw 'Backup checksum mismatch; the restore was not started.' }
    $source.Position = 0

    $template = '{"name":{{json .Name}},"image":{{json .Config.Image}},"validation":{{json (index .Config.Labels "premierecalendar.validation")}},"readOnly":{{json .HostConfig.ReadonlyRootfs}},"networkMode":{{json .HostConfig.NetworkMode}},"portBindings":{{json .HostConfig.PortBindings}},"binds":{{json .HostConfig.Binds}},"mounts":{{json .Mounts}}}'
    $destination = Invoke-LocalDockerText @('container', 'inspect', '--format', $template, $destinationContainer) | ConvertFrom-Json
    if ($destination.name -ne "/$destinationContainer" -or $destination.image -ne $cfg.postgresImage -or
        $destination.validation -ne $label -or -not $destination.readOnly -or $destination.networkMode -ne $destinationNetwork) {
        throw 'The destination identity/isolation differs from the prepared validation database.'
    }
    if (@($destination.binds | Where-Object { $null -ne $_ }).Count -gt 0 -or
        @($destination.mounts | Where-Object { $_.Type -in @('bind', 'volume') }).Count -gt 0 -or
        ($null -ne $destination.portBindings -and @($destination.portBindings.PSObject.Properties).Count -gt 0)) {
        throw 'The restore destination unexpectedly has persistent mounts or published ports.'
    }
    if ((Invoke-LocalDockerText @('network', 'inspect', '--format', '{{.Internal}}', $destinationNetwork)) -ne 'true') { throw 'The isolated restore network is not internal.' }
    $count = Invoke-LocalDockerText @('exec', $destinationContainer, 'psql', '-X', '-A', '-t', '-q', '-v', 'ON_ERROR_STOP=1',
        '-U', 'postgres', '-d', 'premierecalendar', '-c', "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema='public' AND table_type='BASE TABLE';")
    if ($count -ne '0') { throw 'The isolated restore database is not empty; refusing to overwrite or clean it.' }

    $process = [Diagnostics.Process]::new()
    $process.StartInfo = New-LocalDockerStartInfo @('exec', '-i', $destinationContainer, 'pg_restore', '--exit-on-error', '--no-owner', '--no-privileges', '-U', 'postgres', '-d', 'premierecalendar')
    $deadline = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds($TimeoutSeconds))
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $pipeFailed = $false
    $timedOut = $false
    try {
        if (-not $process.Start()) { throw 'Could not start the isolated restore process.' }
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        try { $source.CopyToAsync($process.StandardInput.BaseStream, 81920, $deadline.Token).GetAwaiter().GetResult() }
        catch { $pipeFailed = $true }
        finally { $process.StandardInput.Close() }
        try { $process.WaitForExitAsync($deadline.Token).GetAwaiter().GetResult() }
        catch [OperationCanceledException] {
            $timedOut = $true
            if (-not $process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(5000) }
        }
        $timer.Stop()
        $stdout = $stdoutTask.GetAwaiter().GetResult()
        $stderr = $stderrTask.GetAwaiter().GetResult()
        $report = [ordered]@{
            version = $cfg.version; backupPath = $backupPath; backupSha256 = $actualHash; backupBytes = $size
            destinationContainer = $destinationContainer; destinationDatabase = 'premierecalendar'; destinationNetwork = $destinationNetwork
            destinationInternalNetwork = $true; destinationPersistentMounts = 0; destinationPublishedPorts = 0
            restoreExitCode = $process.ExitCode; elapsedMilliseconds = $timer.ElapsedMilliseconds
            inputPipeFailed = $pipeFailed; timedOut = $timedOut
            stdoutBytes = [Text.Encoding]::UTF8.GetByteCount($stdout)
            stderrLineCount = @($stderr -split "`r?`n" | Where-Object { $_ }).Count
            dataPolicy = 'Raw dump streamed read-only; no database payload, configuration or restore output persisted.'
        }
        # The raw proof (with the restricted dump locator) stays private; the public proof is an allow-list.
        Write-NewJson -Path $privateProof -Object $report -Depth 4
        $allowed = 'version', 'backupSha256', 'backupBytes', 'destinationContainer', 'destinationDatabase', 'destinationNetwork',
            'destinationInternalNetwork', 'destinationPersistentMounts', 'destinationPublishedPorts', 'restoreExitCode',
            'elapsedMilliseconds', 'inputPipeFailed', 'timedOut', 'stdoutBytes', 'stderrLineCount', 'dataPolicy'
        $public = [ordered]@{}
        foreach ($key in $allowed) { $public[$key] = $report[$key] }
        Write-NewJson -Path $publicProof -Object $public -Depth 4
        $public | ConvertTo-Json -Compress
        if ($process.ExitCode -ne 0 -or $pipeFailed -or $timedOut) { throw 'The isolated pg_restore failed; preserve the source dump and inspect the isolated destination before any retry.' }
    }
    finally { $deadline.Dispose(); $process.Dispose() }
}
finally { $source.Dispose() }
