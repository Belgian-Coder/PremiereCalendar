#Requires -Version 7.0
<#
.SYNOPSIS
Step 6: runs the full test suites against a throwaway PostgreSQL and records source-bound test evidence.

.DESCRIPTION
Requires HEAD to be -ExpectedRevision with a clean tracked tree. Before anything runs it fingerprints the tracked
source (freeze_test_source.py: every *.cs file must be LF, because the evidence compares raw bytes with the committed
Git blobs), then runs eng\Test-WithPostgres.ps1 with all unit, component and integration projects in the
development image with PostgreSQL on tmpfs in a child PowerShell and keeps the console output as test-run.log. Afterwards
the fingerprints are verified again, so the evidence proves that exactly the committed bytes were tested.
The runner is always started with -NoCache and -Lane Wsl: a release test step never reuses an earlier passing run (a
"REUSED passing results" line in the log fails the step) and never uses the native feedback lane.
test-summary.json (per suite: passed, failed, skipped, source revision, fingerprint hash) is what the Load, Deploy and
Close gates read; a focused or partial run can never satisfy them.

The runner is eng\Test-WithPostgres.ps1 (xUnit.net 4 on Microsoft.Testing.Platform). It writes <Suite>.ctrf.json (CTRF),
<Suite>.trx and <Suite>.log into <repo>\artifacts\test-results\<timestamp>\ (this checkout's ignored artifacts folder) and
prints that folder in a "Results:" line; the counts
come from the CTRF summary (tests, passed, failed, skipped, pending, other). If a CTRF file is missing, the xUnit.net
"Total: n, Errors: n, Failed: n, Skipped: n, Not Run: n" line of the suite's log is used instead. The CTRF, TRX and log
files are copied into the bundle under tests\ as evidence (not the local-data folder of the test hosts' runtime data).

.PARAMETER Bundle
Full path of the release bundle.

.PARAMETER ExpectedRevision
The exact 40-character release commit.

.PARAMETER SummaryJson
Optional path of a JSON array [{suite, passed, failed, skipped}] produced from the real test run; it replaces the
parsing of the result files. Use it if the runner's result format changes.

.PARAMETER Cpus
CPU quota for the SDK/test container: auto (default; half of the engine's CPUs, at least 2) or 1-64. The disposable
PostgreSQL gets half of the resolved runner budget (at least 2); the resolved numbers are recorded from the runner's
PC_TEST_BUDGET line.

.PARAMETER MaxThreads
Maximum xUnit test threads: auto (default, same rule as -Cpus) or 1-64. This changes scheduling, never the selected
test roster.

.PARAMETER BuildMaxCpuCount
Maximum MSBuild workers: auto (default, same rule as -Cpus) or 1-64. pass smaller numbers to throttle further.

.PARAMETER BuildVolume
Optional existing helper override for an exclusively owned per-worktree Linux/amd64 build volume on ext4.

.PARAMETER NugetVolume
Optional Linux-only NuGet volume override. Keep it separate from native-host caches.

.PARAMETER WslDistribution
Explicit local test distribution (default Ubuntu-24.04). Does not change other release or NAS transports.

.PARAMETER TimeoutSeconds
Total test helper budget including guest cleanup and output drain (default 0: the runner's suite-scaled
default, 3300 seconds for the three release suites).

.EXAMPLE
pwsh -File D:\Projects\PremiereCalendar\eng\release\Invoke-ReleaseTests.ps1 -Bundle D:\Projects\PremiereCalendar\artifacts\release\1.2.20 -ExpectedRevision 0123456789abcdef0123456789abcdef01234567
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string] $Bundle,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedRevision,
    [string] $SummaryJson,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')][string] $WslDistribution = 'Ubuntu-24.04',
    # auto = half of the engine's CPUs (at least 2), resolved by Test-WithPostgres.ps1 and recorded in the receipt.
    [ValidatePattern('^(auto|[1-9]|[1-5][0-9]|6[0-4])$')][string] $Cpus = 'auto',
    [ValidatePattern('^(auto|[1-9]|[1-5][0-9]|6[0-4])$')][string] $MaxThreads = 'auto',
    [ValidatePattern('^(auto|[1-9]|[1-5][0-9]|6[0-4])$')][string] $BuildMaxCpuCount = 'auto',
    # 0 = the runner's suite-scaled default (600 s + 900 s per service suite, 3300 s for the release roster).
    [ValidateRange(0, 86400)][int] $TimeoutSeconds = 0,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')][string] $BuildVolume,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')][string] $NugetVolume
)

$ErrorActionPreference = 'Stop'
# Test-WithPostgres.ps1 refuses 1-59; refuse here, before anything is frozen (a late refusal would burn the bundle).
if ($TimeoutSeconds -ne 0 -and $TimeoutSeconds -lt 60) { throw '-TimeoutSeconds must be 0 (the runner default) or at least 60.' }
. (Join-Path $PSScriptRoot 'ReleaseCommon.ps1')
$ctx = Get-ReleaseContext -Bundle $Bundle
$fingerprints = Join-Path $ctx.Bundle 'tested-source-fingerprints.json'
$log = Join-Path $ctx.Bundle 'test-run.log'
$receipt = Join-Path $ctx.Bundle 'test-run.json'
$summaryPath = Join-Path $ctx.Bundle 'test-summary.json'
$testScript = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Test-WithPostgres.ps1'))
$localParameters = @{ WslDistribution = $WslDistribution; Cpus = $Cpus; MaxThreads = $MaxThreads; BuildMaxCpuCount = $BuildMaxCpuCount; TimeoutSeconds = $TimeoutSeconds; Lane = 'Wsl'; NoCache = $true }
if ($BuildVolume) { $localParameters.BuildVolume = $BuildVolume }
if ($NugetVolume) { $localParameters.NugetVolume = $NugetVolume }
# Resolve the test profile through its pure plan; do not change ReleaseCommon's
# compatibility transport or any NAS activation path.
$localTestPlan = & $testScript @localParameters -Plan
$requiredSuites = @('unit', 'component', 'integration')
$resultsEvidence = Join-Path $ctx.Bundle 'tests'

$plan = @(
    "Require HEAD = $ExpectedRevision, a clean tracked tree; refuse if $fingerprints, $log, $receipt, $summaryPath or the folder $resultsEvidence exist",
    "freeze_test_source.py: fail if any *.cs file has CR characters; write $fingerprints",
    "Run $testScript (all unit, component, integration) with WslDistribution=$WslDistribution, Cpus=$Cpus, MaxThreads=$MaxThreads, BuildMaxCpuCount=$BuildMaxCpuCount, TimeoutSeconds=$TimeoutSeconds, BuildVolume=$($localTestPlan.BuildStorage), NugetVolume=$($localTestPlan.PackageStorage) in a child pwsh with -NoCache -Lane Wsl (fail if the log shows REUSED results); keep the console output as $log",
    'Verify the fingerprints again (the tested bytes are the committed bytes)',
    "Write $summaryPath (suites: $($requiredSuites -join ', ')) and $receipt")
if (Show-ReleasePlan -Title "Invoke-ReleaseTests $($ctx.BundleName)" -Step $plan) { return }

Assert-CleanSourceAtRevision -Revision $ExpectedRevision
Assert-FilesAbsent @($fingerprints, $log, $receipt, $summaryPath, $resultsEvidence)
if ($SummaryJson -and -not (Test-Path -LiteralPath $SummaryJson -PathType Leaf)) { throw "-SummaryJson does not exist: $SummaryJson" }
Invoke-ReleasePython (Join-Path $PSScriptRoot 'freeze_test_source.py') --repo $ctx.Repo --revision $ExpectedRevision --output $fingerprints

function Invoke-TestRunner {
    # The one place that knows how tests are started: the default (all unit, component and integration suites) run of
    # eng\Test-WithPostgres.ps1 in a child pwsh. The console output is kept in $LogPath. Returns the exit code and the
    # results folder named by the script's "Results: <folder>" line.
    param([string] $LogPath)
    $pwsh = (Get-Command pwsh).Source
    $storage = @{ WslDistribution = $WslDistribution; TimeoutSeconds = $TimeoutSeconds }
    if ($BuildVolume) { $storage.BuildVolume = $BuildVolume }
    if ($NugetVolume) { $storage.NugetVolume = $NugetVolume }
    & $pwsh -NoProfile -File $testScript -NoCache -Lane Wsl -Cpus $Cpus -MaxThreads $MaxThreads -BuildMaxCpuCount $BuildMaxCpuCount @storage 2>&1 |
        ForEach-Object { "$_" } | Tee-Object -FilePath $LogPath | Out-Host
    $code = [int]$LASTEXITCODE
    $line = @(Get-Content -LiteralPath $LogPath | Where-Object { $_ -match '^Total wall time: .* Results: (.+)$' }) | Select-Object -Last 1
    $folder = if ($line -and $line -match 'Results: (.+)$') { $Matches[1].Trim() } else { $null }
    [pscustomobject]@{ ExitCode = $code; ResultsFolder = $folder }
}

function Get-SuiteSummary {
    # Reads <Suite>.ctrf.json (preferred) or, failing that, the xUnit.net summary line of <Suite>.log from the results
    # folder. Pending and other (errors, not run) outcomes count as failures.
    param([string] $Folder, [string] $Suite)
    $name = (Get-Culture).TextInfo.ToTitleCase($Suite)
    $ctrf = Join-Path $Folder "$name.ctrf.json"
    if (Test-Path -LiteralPath $ctrf) {
        $s = (Get-Content -LiteralPath $ctrf -Raw | ConvertFrom-Json).results.summary
        $bad = [int]$s.failed + [int]$s.other + [int]$s.pending
        return [pscustomobject]@{ suite = $Suite; passed = [int]$s.passed; failed = $bad; skipped = [int]$s.skipped; source = 'ctrf' }
    }
    $logFile = Join-Path $Folder "$name.log"
    if (Test-Path -LiteralPath $logFile) {
        $text = [regex]::Replace((Get-Content -LiteralPath $logFile -Raw), "\x1B\[[0-9;?]*[A-Za-z]", '')
        $m = [regex]::Matches($text, 'Total:\s*(\d+),\s*Errors:\s*(\d+),\s*Failed:\s*(\d+),\s*Skipped:\s*(\d+),\s*Not Run:\s*(\d+)')
        if ($m.Count -gt 0) {
            $g = $m[$m.Count - 1].Groups
            $bad = [int]$g[2].Value + [int]$g[3].Value + [int]$g[5].Value
            return [pscustomobject]@{ suite = $Suite; passed = [int]$g[1].Value - $bad - [int]$g[4].Value; failed = $bad; skipped = [int]$g[4].Value; source = 'log' }
        }
    }
}

$started = [DateTimeOffset]::UtcNow
$run = Invoke-TestRunner -LogPath $log
$exit = $run.ExitCode
$finished = [DateTimeOffset]::UtcNow
# By construction a release run never reuses earlier results (-NoCache); a REUSED line would mean the gate tested nothing.
if (Select-String -LiteralPath $log -Pattern '^REUSED passing results from ' -Quiet) { throw "The test runner reported REUSED results although -NoCache was passed; the release gate did not run its own tests. See $log." }
if ($run.ResultsFolder -and (Test-Path -LiteralPath $run.ResultsFolder)) {
    # The reports and logs are the evidence. local-data holds the test hosts' runtime data and logs (written as root by the
    # containers); it stays in the checkout's artifacts and is never copied into the bundle.
    New-Item -ItemType Directory -Path $resultsEvidence -Force | Out-Null
    Get-ChildItem -LiteralPath $run.ResultsFolder -Force | Where-Object { $_.Name -cne 'local-data' } |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $resultsEvidence -Recurse }
}
if ($exit -ne 0) { throw "Tests did not pass (exit code $exit); the log is kept as $log. A new bundle is needed for the next attempt." }
Invoke-ReleasePython (Join-Path $PSScriptRoot 'freeze_test_source.py') --repo $ctx.Repo --revision $ExpectedRevision --verify $fingerprints
Assert-CleanSourceAtRevision -Revision $ExpectedRevision

$entries = if ($SummaryJson) { @(Read-Json $SummaryJson) }
elseif ($run.ResultsFolder) { @($requiredSuites | ForEach-Object { Get-SuiteSummary -Folder $run.ResultsFolder -Suite $_ }) }
else { @() }
$missing = @($requiredSuites | Where-Object { $_ -notin @($entries | ForEach-Object { $_.suite }) })
if ($entries.Count -eq 0 -or $missing.Count -gt 0) {
    throw "Could not find the passed/failed/skipped counts of: $($missing -join ', '). Read $log and the files in $resultsEvidence, then re-run with -SummaryJson (a JSON array of {suite, passed, failed, skipped}) or adapt Get-SuiteSummary."
}
$fingerprintHash = Get-FileSha256 $fingerprints
$budgetLine = (@(Get-Content -LiteralPath $log | Where-Object { $_ -match '^PC_TEST_BUDGET ' }) | Select-Object -First 1)
if (-not $budgetLine -or $budgetLine -notmatch '\bpostgresCpus=(\d+)\b') { throw "The runner log has no PC_TEST_BUDGET line with postgresCpus=N, so the resolved resources cannot be recorded. See $log." }
$postgresCpus = [int] $Matches[1]
$summary = @($entries | ForEach-Object {
        [ordered]@{
            suite = $_.suite; passed = [int]$_.passed; failed = [int]$_.failed; skipped = [int]$_.skipped
            sourceRevision = $ExpectedRevision; testedSourceFingerprintsSha256 = $fingerprintHash
            evidence = 'test-run.log; tests/'; missingEvidence = @()
        } })
Write-NewJson -Path $summaryPath -Object $summary
Write-NewJson -Path $receipt -Object ([ordered]@{
        sourceRevision = $ExpectedRevision; startedUtc = $started.ToString('O'); finishedUtc = $finished.ToString('O')
        runner = 'eng\Test-WithPostgres.ps1 (all unit/component/integration)'; exitCode = $exit; logSha256 = Get-FileSha256 $log
        resources = [ordered]@{ cpus = $Cpus; maxThreads = $MaxThreads; buildMaxCpuCount = $BuildMaxCpuCount; postgresCpus = $postgresCpus; timeoutSeconds = $TimeoutSeconds
            resolvedBudget = $budgetLine }
        storage = [ordered]@{ buildVolume = $localTestPlan.BuildStorage; nugetVolume = $localTestPlan.PackageStorage; wslDistribution = $WslDistribution; cacheFlavor = $localTestPlan.CacheFlavor }
        testedSourceFingerprintsSha256 = $fingerprintHash; summarySource = $(if ($SummaryJson) { 'supplied -SummaryJson' } else { 'CTRF/log files of the runner' })
        # False by construction: the runner runs with -NoCache and a REUSED line in the log fails the step above.
        reusedResults = $false })
$summary | ForEach-Object { Write-Host ("{0}: {1} passed, {2} failed, {3} skipped" -f $_.suite, $_.passed, $_.failed, $_.skipped) }
if (@($summary | Where-Object { $_.failed -ne 0 -or $_.passed -lt 1 }).Count -gt 0) { throw 'A suite has failures or no passed cases; see test-summary.json.' }
