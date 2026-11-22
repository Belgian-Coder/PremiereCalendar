#Requires -Version 7.0
<#
.SYNOPSIS
Steps 2-7 of the local release in one command: runs the unchanged canonical gate scripts with the maximum safe concurrency.

.DESCRIPTION
Replaces running Initialize-RestoreCheck + Restore-Backup, Build-Image, Invoke-NuGetAudit, Invoke-Scans,
Invoke-ReleaseTests, Invoke-Smoke and Test-BackupCompatibility by hand. Every step is the canonical script, started as
its own child `pwsh -NoProfile -File` process with its exit code checked and its console output kept in a new log
`<Bundle>\gates\<step>.log` (never overwritten). Nothing about a gate is changed, skipped or retried.

Dependency graph (each arrow is a real input of the later script):
  tests (CPU heavy)                                        freezes tested-source-fingerprints.json early
  restore-init -> restore                                  needs <Bundle>.private\backup-summary.json (Prepare-Release)
  build -> nuget-audit -> scans                            build needs the fingerprint file (see below)
  smoke, backup-compat                                     need the built image; backup-compat also needs restore

Concurrency policy (default): the CPU-heavy test lane overlaps only I/O and network bound steps (restore, build and
export, NuGet audit, scans). smoke and backup-compat contain readiness timing, so they never overlap the test lane and
by default not each other; they start when the tests are done. -Serial runs the same graph one step at a time (escape
hatch for a loaded host). -ParallelRuntimeChecks lets smoke and backup-compat overlap each other.

Fingerprint ordering: Build-Image binds tested-source-fingerprints.json into the source verification when the file
exists, and Invoke-ReleaseTests creates it (it refuses when it already exists). To make the binding deterministic the
orchestrator starts the tests first and starts Build-Image only when the fingerprint file exists and parses (or the tests
already finished), so every build is bound to the tested bytes regardless of timing. -Serial does the same by running
tests first.

Failure handling: on the first failed step no new step is launched, running steps finish (a Docker build or restore is
never killed mid-way), the evidence that exists is listed and the run exits 1. There is no automatic retry. A failed step
that wrote evidence needs a new bundle (docs/Releasing.md, "When something fails").

Resume: before launching anything every step is classified from the exact evidence files its script writes. Complete
evidence is skipped. Partial or failed evidence stops the run (exit 2) without launching any step. A step with no
evidence is run; one that failed earlier without evidence may be run again (a new log number is used).

gates\stage-timings.json (then stage-timings.2.json, ...) records per-step start, end, duration, exit code and the total
wall time. -WhatIf prints the graph, the evidence status and every command line without running anything (no Docker,
WSL or network); -ChildWhatIf additionally runs every child script with its own -WhatIf to prove the command lines.

.PARAMETER Bundle
Full path of the release bundle created by Initialize-Release.ps1 (Prepare-Release.ps1 must have run for the restore).

.PARAMETER ExpectedRevision
The exact 40-character release commit (HEAD, clean tracked tree).

.PARAMETER Serial
Run the graph one step at a time (tests, restore, build, audit, scans, smoke, compatibility).

.PARAMETER MaxParallel
Maximum number of concurrently running steps (default 3; the restore lane is short).

.PARAMETER ParallelRuntimeChecks
Allow smoke and backup-compat to run at the same time.

.PARAMETER IgnoreUnfixed
Forwarded to Invoke-Scans.ps1.

.PARAMETER Cpus
.PARAMETER MaxThreads
.PARAMETER BuildMaxCpuCount
.PARAMETER TimeoutSeconds
.PARAMETER WslDistribution
.PARAMETER BuildVolume
.PARAMETER NugetVolume
.PARAMETER SummaryJson
Forwarded to Invoke-ReleaseTests.ps1 unchanged when supplied (its own defaults otherwise).

.PARAMETER RestoreTimeoutSeconds
Forwarded to Restore-Backup.ps1 as -TimeoutSeconds when supplied.

.PARAMETER FingerprintWaitSeconds
How long Build-Image may wait for the test run to freeze the fingerprint file (default 300).

.PARAMETER ChildWhatIf
With -WhatIf: also run each child script with -WhatIf (Windows builder; local files only).

.EXAMPLE
pwsh -File D:\Projects\PremiereCalendar\eng\release\Invoke-LocalGates.ps1 -Bundle D:\Projects\PremiereCalendar\artifacts\release\1.2.20 -ExpectedRevision 0123456789abcdef0123456789abcdef01234567 -Cpus 4 -MaxThreads 4 -BuildMaxCpuCount 4
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string] $Bundle,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedRevision,
    [switch] $Serial,
    [ValidateRange(1, 8)][int] $MaxParallel = 3,
    [switch] $ParallelRuntimeChecks,
    [switch] $IgnoreUnfixed,
    [ValidateRange(1, 64)][int] $Cpus,
    [ValidateRange(1, 64)][int] $MaxThreads,
    [ValidateRange(1, 64)][int] $BuildMaxCpuCount,
    [ValidateRange(30, 86400)][int] $TimeoutSeconds,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')][string] $WslDistribution,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')][string] $BuildVolume,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')][string] $NugetVolume,
    [string] $SummaryJson,
    [ValidateRange(30, 3600)][int] $RestoreTimeoutSeconds,
    [ValidateRange(1, 3600)][int] $FingerprintWaitSeconds = 300,
    [switch] $ChildWhatIf,
    # Test hook: replaces the child-process launcher. Called with a step object (Id, Script, Arguments, LogPath);
    # must return an object with IsDone and ExitCode properties. Used only by tests/ReleaseOrchestrator.Tests.ps1.
    [Parameter(DontShow)][scriptblock] $StepRunner,
    [Parameter(DontShow)][ValidateRange(1, 5000)][int] $PollMilliseconds = 500,
    # Test hook: skips the git and Windows preflight (the stub runner never touches the repository or Docker).
    [Parameter(DontShow)][switch] $SkipHostPreflight
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$bound = $PSBoundParameters
$releaseRoot = $PSScriptRoot
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..' '..'))

if (-not [IO.Path]::IsPathRooted($Bundle)) { throw '-Bundle must be a full path, for example D:\Projects\PremiereCalendar\artifacts\release\1.2.20.' }
$bundlePath = [IO.Path]::GetFullPath($Bundle).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
$privatePath = "$bundlePath.private"
$configPath = Join-Path $bundlePath 'release-config.json'
if (-not (Test-Path -LiteralPath $configPath -PathType Leaf)) { throw "release-config.json is missing; run Initialize-Release.ps1 first: $configPath" }
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
if ($config.schema -ne 1) { throw "Unsupported release-config.json schema: $($config.schema)" }
$version = [string]$config.version
$gatesDir = Join-Path $bundlePath 'gates'
$fingerprintFile = Join-Path $bundlePath 'tested-source-fingerprints.json'

# ----------------------------------------------------------------------------------------------- step definitions

function New-Step {
    param([string] $Id, [string] $Title, [string] $Script, [string[]] $Arguments, [string[]] $After, [string] $Class,
        [string[]] $Required = @(), [scriptblock] $Validate)
    [pscustomobject]@{
        Id = $Id; Title = $Title; Script = (Join-Path $releaseRoot $Script); ScriptName = $Script
        Arguments = @($Arguments); After = @($After); Class = $Class; Required = @($Required); Validate = $Validate
        Status = 'pending'; Marker = $null; Evidence = $null; Handle = $null; LogPath = $null; StartUtc = $null; EndUtc = $null
        Seconds = $null; ExitCode = $null; Note = $null; Clock = $null
    }
}

function Get-JsonOrNull([string] $Path) {
    try { Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json } catch { $null }
}

function Get-GateSteps {
    $b = @('-Bundle', $bundlePath)
    $testArgs = $b + @('-ExpectedRevision', $ExpectedRevision)
    foreach ($name in 'Cpus', 'MaxThreads', 'BuildMaxCpuCount', 'TimeoutSeconds', 'WslDistribution', 'BuildVolume', 'NugetVolume', 'SummaryJson') {
        if ($bound.ContainsKey($name)) { $testArgs += @("-$name", [string]$bound[$name]) }
    }
    $restoreArgs = $b + $(if ($bound.ContainsKey('RestoreTimeoutSeconds')) { @('-TimeoutSeconds', [string]$RestoreTimeoutSeconds) } else { @() })
    $scanArgs = $b + $(if ($IgnoreUnfixed) { @('-IgnoreUnfixed') } else { @() })
    $v = $version
    @(
        New-Step 'tests' 'Invoke-ReleaseTests (unit, component, integration)' 'Invoke-ReleaseTests.ps1' $testArgs @() 'cpu' @(
            $fingerprintFile, (Join-Path $bundlePath 'test-run.log'), (Join-Path $bundlePath 'test-run.json'),
            (Join-Path $bundlePath 'test-summary.json'), (Join-Path $bundlePath 'tests')) -Validate {
            $rows = @(Get-JsonOrNull (Join-Path $bundlePath 'test-summary.json'))
            $receipt = Get-JsonOrNull (Join-Path $bundlePath 'test-run.json')
            if ($rows.Count -lt 1 -or @($rows | Where-Object { $_.failed -ne 0 -or $_.passed -lt 1 }).Count -gt 0) { 'test-summary.json records failures or no passed cases' }
            elseif (-not $receipt -or $receipt.sourceRevision -cne $ExpectedRevision -or @($rows | Where-Object { $_.sourceRevision -cne $ExpectedRevision }).Count -gt 0) { 'the test evidence belongs to another source revision' }
        }
        New-Step 'restore-init' 'Initialize-RestoreCheck (isolated PostgreSQL)' 'Initialize-RestoreCheck.ps1' $b @() 'io'
        New-Step 'restore' 'Restore-Backup (fresh dump into the isolated database)' 'Restore-Backup.ps1' $restoreArgs @('restore-init') 'io' @(
            (Join-Path $bundlePath 'backup-restore.json'), (Join-Path $privatePath 'backup-restore.json')) -Validate {
            $p = Get-JsonOrNull (Join-Path $bundlePath 'backup-restore.json')
            if (-not $p -or $p.restoreExitCode -ne 0 -or $p.timedOut -or $p.inputPipeFailed) { 'backup-restore.json records a failed restore' }
        }
        New-Step 'build' 'Build-Image (exact source archive, docker save)' 'Build-Image.ps1' $testArgs[0..3] @() 'io' @(
            (Join-Path $bundlePath "source-$v.tar"), (Join-Path $bundlePath 'source-archive-verification.json'),
            (Join-Path $bundlePath "premierecalendar-$v.tar"), (Join-Path $bundlePath 'build.log'), (Join-Path $bundlePath 'build-metadata.json')) -Validate {
            # A build made outside this graph is only reused when it is bound to the same revision and tested bytes.
            $m = Get-JsonOrNull (Join-Path $bundlePath 'build-metadata.json')
            if (-not $m -or $m.sourceRevision -cne $ExpectedRevision) { 'build-metadata.json belongs to another source revision' }
            elseif (-not (Test-Path -LiteralPath $fingerprintFile) -or $m.testedSourceFingerprintsSha256 -cne (Get-FileHash -LiteralPath $fingerprintFile -Algorithm SHA256).Hash.ToLowerInvariant()) { 'the build is not bound to the tested source fingerprints' }
        }
        New-Step 'nuget-audit' 'Invoke-NuGetAudit' 'Invoke-NuGetAudit.ps1' $b @('build') 'io' @((Join-Path $bundlePath 'release-nuget-audit.log')) -Validate {
            if (-not (Select-String -LiteralPath (Join-Path $bundlePath 'release-nuget-audit.log') -Pattern 'has no vulnerable packages' -Quiet)) { 'release-nuget-audit.log does not report "has no vulnerable packages"' }
        }
        New-Step 'scans' 'Invoke-Scans (Trivy, Syft SBOM, archive audit)' 'Invoke-Scans.ps1' $scanArgs @('nuget-audit') 'io' @(
            (Join-Path $bundlePath "release-$v-trivy.json"), (Join-Path $bundlePath "release-$v-sbom.cdx.json"), (Join-Path $bundlePath "release-$v-audit.json"),
            (Join-Path $bundlePath "release-$v-scan-summary.json"), (Join-Path $bundlePath "release-$v-scanners.json"),
            (Join-Path $bundlePath 'release-trivy.log'), (Join-Path $bundlePath 'release-syft.log'), (Join-Path $bundlePath "release-$v-trivy-db-metadata.json"),
            (Join-Path $privatePath 'image-inspect.json')) -Validate {
            $s = Get-JsonOrNull (Join-Path $bundlePath "release-$version-scan-summary.json")
            if (-not $s -or $s.allChecksPass -ne $true) { "release-$version-scan-summary.json does not record allChecksPass" }
        }
        New-Step 'smoke' 'Invoke-Smoke (exact image, hardened runtime)' 'Invoke-Smoke.ps1' $b @('build') 'timing' (
            @('smoke-health.json', 'smoke-assets.json', 'smoke-zoneinfo.log', 'smoke-timezone-probe.log', 'smoke-runtime.log', 'smoke-nightly-planner.log',
                'smoke-security.log', 'smoke-logfiles.log', 'smoke-downgrade-command.json', 'smoke-database-verify.json', 'smoke-timezone-probe') | ForEach-Object { Join-Path $bundlePath $_ })
        New-Step 'backup-compat' 'Test-BackupCompatibility (candidate on the restored backup)' 'Test-BackupCompatibility.ps1' $b @('build', 'restore') 'timing' (
            @('backup-schema-before.txt', 'backup-schema-after.txt', 'backup-compat-health.json', 'backup-compat-image.json', 'schema-backup-compatibility.json',
                'backup-compat-security.txt') | ForEach-Object { Join-Path $bundlePath $_ }) -Validate {
            $r = Get-JsonOrNull (Join-Path $bundlePath 'schema-backup-compatibility.json')
            if ($r -and $r.PSObject.Properties['allChecksPass'] -and $r.allChecksPass -ne $true) { 'schema-backup-compatibility.json records failed checks' }
        }
    )
}

# ----------------------------------------------------------------------------------------------- evidence / resume

function Get-ResultMarkers([string] $Id) {
    if (-not (Test-Path -LiteralPath $gatesDir)) { return @() }
    @(Get-ChildItem -LiteralPath $gatesDir -Filter "$Id*.result.json" -File | Where-Object { $_.Name -match "^$([regex]::Escape($Id))(\.\d+)?\.result\.json$" })
}

function Get-StepEvidence($Step) {
    $present = @($Step.Required | Where-Object { Test-Path -LiteralPath $_ })
    $missing = @($Step.Required | Where-Object { -not (Test-Path -LiteralPath $_) })
    if ($Step.Required.Count -eq 0) {
        # Initialize-RestoreCheck writes no file: only our own successful result marker proves it ran.
        $ok = @(Get-ResultMarkers $Step.Id | Where-Object { (Get-JsonOrNull $_.FullName).exitCode -eq 0 })
        return [pscustomobject]@{ State = $(if ($ok.Count -gt 0) { 'complete' } else { 'none' }); Present = @($ok | ForEach-Object { $_.FullName }); Missing = @(); Problem = $null }
    }
    if ($present.Count -eq 0) { return [pscustomobject]@{ State = 'none'; Present = @(); Missing = $missing; Problem = $null } }
    if ($missing.Count -gt 0) { return [pscustomobject]@{ State = 'partial'; Present = $present; Missing = $missing; Problem = 'some evidence files are missing' } }
    $problem = if ($Step.Validate) { & $Step.Validate } else { $null }
    if ($problem) { return [pscustomobject]@{ State = 'invalid'; Present = $present; Missing = @(); Problem = [string]$problem } }
    [pscustomobject]@{ State = 'complete'; Present = $present; Missing = @(); Problem = $null }
}

function Get-UniqueLogName([string] $Id) {
    $n = 1
    while ($true) {
        $suffix = if ($n -eq 1) { '' } else { ".$n" }
        $log = Join-Path $gatesDir "$Id$suffix.log"
        $marker = Join-Path $gatesDir "$Id$suffix.result.json"
        if (-not (Test-Path -LiteralPath $log) -and -not (Test-Path -LiteralPath $marker)) { return [pscustomobject]@{ Log = $log; Marker = $marker } }
        $n++
    }
}

function Write-NewFile([string] $Path, [string] $Text) {
    $directory = Split-Path -Parent $Path
    if ($directory) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
    try { $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None) }
    catch [IO.IOException] { throw "Existing evidence must be preserved; refusing to overwrite: $Path" }
    try { $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Text); $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
}

# ----------------------------------------------------------------------------------------------- default child launcher

function Initialize-GateProcessType {
    if ('GateProcess' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
public sealed class GateProcess : IDisposable {
    readonly Process process; readonly StreamWriter log; readonly object gate = new object(); readonly Thread[] pumps = new Thread[2];
    public GateProcess(ProcessStartInfo info, string logPath) {
        // CreateNew: a log is never overwritten.
        log = new StreamWriter(new FileStream(logPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
        try {
            process = Process.Start(info);
            process.StandardInput.Close();
            pumps[0] = Pump(process.StandardOutput); pumps[1] = Pump(process.StandardError);
        } catch { log.Dispose(); throw; }
    }
    Thread Pump(StreamReader reader) {
        var t = new Thread(() => {
            string line;
            while ((line = reader.ReadLine()) != null) { lock (gate) { log.WriteLine(line); } }
        }) { IsBackground = true };
        t.Start();
        return t;
    }
    public bool IsDone { get { return process.HasExited && !pumps[0].IsAlive && !pumps[1].IsAlive; } }
    public int ExitCode { get { return process.ExitCode; } }
    public void Dispose() { lock (gate) { log.Dispose(); } process.Dispose(); }
}
'@
}

function Start-GateProcess($Step) {
    Initialize-GateProcessType
    $info = [Diagnostics.ProcessStartInfo]::new([Environment]::ProcessPath)
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    $info.RedirectStandardInput = $true; $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    $info.StandardOutputEncoding = [Text.Encoding]::UTF8; $info.StandardErrorEncoding = [Text.Encoding]::UTF8
    foreach ($a in (@('-NoProfile', '-NonInteractive', '-File', $Step.Script) + $Step.Arguments)) { [void]$info.ArgumentList.Add($a) }
    [GateProcess]::new($info, $Step.LogPath)
}

function Format-CommandLine($Step, [switch] $WithWhatIf) {
    $parts = @('pwsh', '-NoProfile', '-File', $Step.Script) + $Step.Arguments + $(if ($WithWhatIf) { @('-WhatIf') } else { @() })
    ($parts | ForEach-Object { if ($_ -match '\s') { "'$_'" } else { $_ } }) -join ' '
}

# ----------------------------------------------------------------------------------------------- plan

$steps = @(Get-GateSteps)
$byId = @{}
foreach ($s in $steps) { $byId[$s.Id] = $s }
# Plan order is also the start priority and the serial order: tests first (fingerprints exist before the build).
$mode = if ($Serial) { 'serial' } else { 'parallel' }
$limit = if ($Serial) { 1 } else { $MaxParallel }

foreach ($s in $steps) {
    $s.Evidence = Get-StepEvidence $s
    switch ($s.Evidence.State) {
        'complete' { $s.Status = 'skipped'; $s.Note = 'complete evidence already exists' }
        'none' { $s.Status = 'pending' }
        default { $s.Status = 'refused' }
    }
}
$refused = @($steps | Where-Object { $_.Status -eq 'refused' })
$backupSummary = Join-Path $privatePath 'backup-summary.json'
$restoreNeeded = @($steps | Where-Object { $_.Id -in 'restore-init', 'restore' -and $_.Status -eq 'pending' }).Count -gt 0
$preflightProblems = [Collections.Generic.List[string]]::new()
if ($restoreNeeded -and -not (Test-Path -LiteralPath $backupSummary)) { $preflightProblems.Add("Prepare-Release.ps1 has not produced the backup summary the restore needs: $backupSummary") }

function Show-Plan {
    Write-Host "Local release gates for $($config.bundleName) (version $version, revision $ExpectedRevision)"
    Write-Host "Mode: $mode (max $limit concurrent step$(if ($limit -ne 1) { 's' }))$(if ($ParallelRuntimeChecks -and -not $Serial) { '; smoke and backup-compat may overlap' })"
    Write-Host 'Graph:'
    Write-Host '  tests (CPU heavy) ............ may overlap restore-init, restore, build, nuget-audit, scans; never smoke or backup-compat'
    Write-Host '  restore-init -> restore'
    Write-Host '  build -> nuget-audit -> scans   (build waits for tested-source-fingerprints.json so it is always bound to the tested bytes)'
    Write-Host '  smoke, backup-compat ......... after the build (backup-compat also after restore) and only while no test step runs'
    Write-Host 'Steps in start-priority order:'
    $n = 0
    foreach ($s in $steps) {
        $n++
        $after = if ($s.After.Count) { 'after ' + ($s.After -join ', ') } else { 'no dependency' }
        $state = switch ($s.Evidence.State) {
            'complete' { 'SKIP (complete evidence)' } 'none' { 'run' }
            default { "REFUSE ($($s.Evidence.State): $(@($s.Evidence.Missing | ForEach-Object { Split-Path -Leaf $_ }) -join ', ')$($s.Evidence.Problem))" }
        }
        Write-Host ("  {0}. {1,-14} [{2}] {3}; {4}" -f $n, $s.Id, $s.Class, $after, $state)
        Write-Host "       $(Format-CommandLine $s)"
    }
    Write-Host "Logs: $gatesDir\<step>.log; timings: $gatesDir\stage-timings.json"
    foreach ($p in $preflightProblems) { Write-Host "WARNING: $p" }
}

if ($WhatIfPreference -or $ChildWhatIf) {
    Show-Plan
    if ($ChildWhatIf) {
        foreach ($s in $steps) {
            Write-Host "---- child -WhatIf: $($s.Id)"
            & ([Environment]::ProcessPath) -NoProfile -File $s.Script @($s.Arguments) -WhatIf
            if ($LASTEXITCODE -ne 0) { throw "The child -WhatIf of $($s.ScriptName) failed with exit code $LASTEXITCODE; the command line is not valid." }
        }
        Write-Host 'All child -WhatIf runs succeeded.'
    }
    Write-Host 'WHAT IF: nothing was executed.'
    return
}

# ----------------------------------------------------------------------------------------------- preflight (real run)

if (-not $SkipHostPreflight) {
    if (-not $IsWindows) { throw 'Executing the local gates requires the Windows builder with the Ubuntu-24.04 WSL Docker engine; use -WhatIf on this host.' }
    $head = (& git -C $repoRoot rev-parse HEAD).Trim()
    if ($head -cne $ExpectedRevision) { throw "HEAD ($head) differs from the expected release commit ($ExpectedRevision); nothing was started." }
    if (@(& git -C $repoRoot status --porcelain --untracked-files=no).Count -gt 0) { throw 'Tracked files must be clean before the gates run; nothing was started.' }
}
foreach ($s in $refused) {
    $preflightProblems.Add("$($s.Id): $($s.Evidence.State) evidence ($(if ($s.Evidence.Missing.Count) { 'missing: ' + (@($s.Evidence.Missing | ForEach-Object { Split-Path -Leaf $_ }) -join ', ') } else { $s.Evidence.Problem })). A step that wrote partial or failed evidence is never re-run; create a new bundle (-BundleName <version>-candidate2) and a new -ResourcePrefix.")
}
if ($preflightProblems.Count -gt 0) {
    $preflightProblems | ForEach-Object { Write-Host "REFUSED: $_" }
    Write-Host "The gates were not started ($($preflightProblems.Count) problem(s) above); no step was launched."
    exit 2
}

# ----------------------------------------------------------------------------------------------- scheduler

New-Item -ItemType Directory -Path $gatesDir -Force | Out-Null
$runner = if ($StepRunner) { $StepRunner } else { { param($step) Start-GateProcess $step } }
$wall = [Diagnostics.Stopwatch]::StartNew()
$runStartedUtc = [DateTimeOffset]::UtcNow
$failed = $null
$fingerprintWait = $null
$fatal = $null

function Test-FingerprintsReady {
    $t = $byId['tests']
    if ($t.Status -in 'passed', 'skipped') { return $true }
    if (-not (Test-Path -LiteralPath $fingerprintFile)) { return $false }
    $null -ne (Get-JsonOrNull $fingerprintFile)
}

function Complete-Step($Step) {
    $h = $Step.Handle
    $Step.Clock.Stop()
    $Step.EndUtc = [DateTimeOffset]::UtcNow
    $Step.Seconds = [math]::Round($Step.Clock.Elapsed.TotalSeconds, 3)
    $Step.ExitCode = [int]$h.ExitCode
    if ($h -is [IDisposable]) { $h.Dispose() }
    $Step.Status = if ($Step.ExitCode -eq 0) { 'passed' } else { 'failed' }
    $record = [ordered]@{ step = $Step.Id; exitCode = $Step.ExitCode; startUtc = $Step.StartUtc.ToString('O'); endUtc = $Step.EndUtc.ToString('O'); seconds = $Step.Seconds; log = (Split-Path -Leaf $Step.LogPath) }
    Write-NewFile $Step.Marker ((ConvertTo-Json -InputObject $record) + "`n")
    Write-Host ("[{0}] {1} {2} in {3}s (exit {4})" -f ([DateTime]::UtcNow.ToString('HH:mm:ss')), $Step.Id, $Step.Status.ToUpperInvariant(), $Step.Seconds, $Step.ExitCode)
}

function Test-CanStart($Step, [object[]] $Running) {
    if ($Running.Count -ge $limit) { return $false }
    foreach ($d in $Step.After) { if ($byId[$d].Status -notin 'passed', 'skipped') { return $false } }
    if ($Step.Id -eq 'build' -and -not (Test-FingerprintsReady)) { return $false }
    $classes = @($Running | ForEach-Object { $_.Class })
    switch ($Step.Class) {
        'cpu' { if ($classes -contains 'timing') { return $false } }
        'timing' {
            if ($classes -contains 'cpu') { return $false }
            if (-not $ParallelRuntimeChecks -and $classes -contains 'timing') { return $false }
            # Never start a timing-sensitive step while the CPU-heavy lane is still waiting to start.
            if (@($steps | Where-Object { $_.Class -eq 'cpu' -and $_.Status -eq 'pending' }).Count -gt 0) { return $false }
        }
    }
    $true
}

try {
    while ($true) {
        $running = @($steps | Where-Object { $_.Status -eq 'running' })
        foreach ($s in $running) {
            if ($s.Handle.IsDone) { Complete-Step $s; if ($s.Status -eq 'failed' -and -not $failed) { $failed = $s } }
        }
        $running = @($steps | Where-Object { $_.Status -eq 'running' })
        if (-not $failed -and -not $fatal) {
            foreach ($s in @($steps | Where-Object { $_.Status -eq 'pending' })) {
                if (-not (Test-CanStart $s $running)) { continue }
                $names = Get-UniqueLogName $s.Id
                $s.LogPath = $names.Log
                $s.Marker = $names.Marker
                $s.StartUtc = [DateTimeOffset]::UtcNow
                $s.Clock = [Diagnostics.Stopwatch]::StartNew()
                $s.Handle = & $runner ([pscustomobject]@{ Id = $s.Id; Script = $s.Script; Arguments = $s.Arguments; LogPath = $s.LogPath })
                $s.Status = 'running'
                $running = @($running + $s)
                Write-Host "[$([DateTime]::UtcNow.ToString('HH:mm:ss'))] $($s.Id) started: $(Format-CommandLine $s)"
                if ($running.Count -ge $limit) { break }
            }
            # Build-Image is held back until the test run froze the fingerprints; bound the wait deterministically.
            $b = $byId['build']
            if ($b.Status -eq 'pending' -and $byId['tests'].Status -eq 'running' -and -not (Test-FingerprintsReady)) {
                if (-not $fingerprintWait) { $fingerprintWait = [Diagnostics.Stopwatch]::StartNew() }
                elseif ($fingerprintWait.Elapsed.TotalSeconds -gt $FingerprintWaitSeconds) {
                    $fatal = "tested-source-fingerprints.json did not appear within $FingerprintWaitSeconds s; Build-Image was not started (it must bind the tested bytes)."
                }
            }
        }
        if ($running.Count -eq 0) {
            if (@($steps | Where-Object { $_.Status -eq 'pending' }).Count -eq 0 -or $failed -or $fatal) { break }
            if (-not (@($steps | Where-Object { $_.Status -eq 'pending' } | Where-Object { Test-CanStart $_ @() }))) {
                $fatal = 'No pending step can start (unsatisfied dependency); this is a scheduler error: ' + (@($steps | Where-Object { $_.Status -eq 'pending' } | ForEach-Object { $_.Id }) -join ', ')
                break
            }
        }
        Start-Sleep -Milliseconds $PollMilliseconds
    }
}
finally {
    $wall.Stop()
    $finishedUtc = [DateTimeOffset]::UtcNow
    # Running children are never killed here (a Docker build or restore must finish); an abort leaves them to finish.
    foreach ($s in @($steps | Where-Object { $_.Status -eq 'running' })) { $s.Status = 'abandoned-running' }
    foreach ($s in @($steps | Where-Object { $_.Status -eq 'pending' })) { $s.Status = 'not-started' }

    # Critical path: longest chain of actual durations through the dependency graph (the lower bound for this run).
    $memo = @{}
    function Get-PathSeconds($Step) {
        if ($memo.ContainsKey($Step.Id)) { return $memo[$Step.Id] }
        $before = 0.0
        foreach ($d in $Step.After) { $before = [math]::Max($before, (Get-PathSeconds $byId[$d])) }
        $own = if ($Step.Seconds) { [double]$Step.Seconds } else { 0.0 }
        $memo[$Step.Id] = $before + $own
        $memo[$Step.Id]
    }
    $critical = [math]::Round((($steps | ForEach-Object { Get-PathSeconds $_ } | Measure-Object -Maximum).Maximum), 3)
    $sum = [math]::Round((($steps | ForEach-Object { if ($_.Seconds) { [double]$_.Seconds } else { 0.0 } } | Measure-Object -Sum).Sum), 3)
    $timings = [ordered]@{
        schema = 1; bundle = $config.bundleName; version = $version; expectedRevision = $ExpectedRevision; mode = $mode; maxParallel = $limit
        parallelRuntimeChecks = [bool]$ParallelRuntimeChecks
        startedUtc = $runStartedUtc.ToString('O'); finishedUtc = $finishedUtc.ToString('O')
        wallSeconds = [math]::Round($wall.Elapsed.TotalSeconds, 3); sumOfStepSeconds = $sum; criticalPathSeconds = $critical
        concurrencySavingSeconds = [math]::Round($sum - $wall.Elapsed.TotalSeconds, 3)
        succeeded = ($null -eq $failed -and $null -eq $fatal -and @($steps | Where-Object { $_.Status -notin 'passed', 'skipped' }).Count -eq 0)
        failedStep = $(if ($failed) { $failed.Id } else { $null }); error = $fatal
        steps = @($steps | ForEach-Object {
                [ordered]@{
                    id = $_.Id; script = $_.ScriptName; class = $_.Class; status = $_.Status; note = $_.Note
                    startUtc = $(if ($_.StartUtc) { $_.StartUtc.ToString('O') } else { $null }); endUtc = $(if ($_.EndUtc) { $_.EndUtc.ToString('O') } else { $null })
                    seconds = $_.Seconds; exitCode = $_.ExitCode; log = $(if ($_.LogPath) { Split-Path -Leaf $_.LogPath } else { $null })
                } })
    }
    $n = 1
    do { $timingPath = Join-Path $gatesDir $(if ($n -eq 1) { 'stage-timings.json' } else { "stage-timings.$n.json" }); $n++ } while (Test-Path -LiteralPath $timingPath)
    Write-NewFile $timingPath ((ConvertTo-Json -InputObject $timings -Depth 6) + "`n")

    Write-Host ''
    Write-Host ('{0,-14} {1,-18} {2,9}  {3,-9} {4}' -f 'Step', 'Status', 'Seconds', 'Exit', 'Log')
    foreach ($s in $steps) {
        Write-Host ('{0,-14} {1,-18} {2,9}  {3,-9} {4}' -f $s.Id, $s.Status, $(if ($null -ne $s.Seconds) { $s.Seconds } else { '-' }), $(if ($null -ne $s.ExitCode) { $s.ExitCode } else { '-' }), $(if ($s.LogPath) { Split-Path -Leaf $s.LogPath } else { '' }))
    }
    Write-Host ("Wall time {0}s; sum of steps {1}s; critical path {2}s; saved by concurrency {3}s. Timings: {4}" -f $timings.wallSeconds, $sum, $critical, $timings.concurrencySavingSeconds, $timingPath)
    if ($failed -or $fatal) {
        Write-Host 'Evidence present after the stop (nothing was retried; a failed step that wrote evidence needs a new bundle):'
        foreach ($s in $steps) {
            $e = Get-StepEvidence $s
            Write-Host ("  {0,-14} {1}{2}" -f $s.Id, $e.State, $(if ($e.Present.Count) { ' (' + (@($e.Present | ForEach-Object { Split-Path -Leaf $_ }) -join ', ') + ')' } else { '' }))
        }
    }
}
if ($failed) { Write-Host "ERROR: gate step '$($failed.Id)' failed with exit code $($failed.ExitCode); see $($failed.LogPath)."; exit 1 }
if ($fatal) { Write-Host "ERROR: $fatal"; exit 1 }
Write-Host 'All local gates passed. Next: Load-Image, Deploy-Release, Verify-Release, Close-Release (docs/Releasing.md).'
