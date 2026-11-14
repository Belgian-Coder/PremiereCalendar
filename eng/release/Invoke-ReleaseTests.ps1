#Requires -Version 7.0
<#
.SYNOPSIS
Step 6: runs the full test suites against a throwaway PostgreSQL and records source-bound test evidence.

.DESCRIPTION
Requires HEAD to be -ExpectedRevision with a clean tracked tree. Before anything runs it fingerprints the tracked
source (freeze_test_source.py: every *.cs file must be LF, because the evidence compares raw bytes with the committed
Git blobs), then runs eng\Test-WithPostgres.ps1 with its defaults (unit, component and integration projects in the
development image, PostgreSQL on tmpfs) in a child PowerShell and keeps the console output as test-run.log. Afterwards
the fingerprints are verified again, so the evidence proves that exactly the committed bytes were tested.
test-summary.json (per suite: passed, failed, skipped, source revision, fingerprint hash) is what the Load, Deploy and
Close gates read; a focused or partial run can never satisfy them.

<!-- TEST-COMMANDS -->
The runner invocation and the parsing of its console summary below are written for the current
eng\Test-WithPostgres.ps1 (one "=== dotnet test <project>" header per project, then the runner's summary line; the
VSTest "Passed! - Failed: 0, Passed: n, Skipped: n, Total: n" line and the Microsoft.Testing.Platform "total/failed/
succeeded/skipped" block are both understood). When the test commands change, update Invoke-TestRunner and
Convert-TestLogToSummary here, or supply the per-suite counts yourself with -SummaryJson.

.PARAMETER Bundle
Full path of the release bundle.

.PARAMETER ExpectedRevision
The exact 40-character release commit.

.PARAMETER SummaryJson
Optional path of a JSON array [{suite, passed, failed, skipped}] produced from the real test run; it replaces the
parsing of the console log (the log is still kept as evidence). Use it when the runner output format changes.

.EXAMPLE
pwsh -File D:\Projects\PremiereCalendar\eng\release\Invoke-ReleaseTests.ps1 -Bundle D:\Projects\PremiereCalendar\artifacts\release\1.2.20 -ExpectedRevision 0123456789abcdef0123456789abcdef01234567
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string] $Bundle,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedRevision,
    [string] $SummaryJson
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseCommon.ps1')
$ctx = Get-ReleaseContext -Bundle $Bundle
$fingerprints = Join-Path $ctx.Bundle 'tested-source-fingerprints.json'
$log = Join-Path $ctx.Bundle 'test-run.log'
$receipt = Join-Path $ctx.Bundle 'test-run.json'
$summaryPath = Join-Path $ctx.Bundle 'test-summary.json'
$testScript = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Test-WithPostgres.ps1'))
$requiredSuites = @('unit', 'component', 'integration')

$plan = @(
    "Require HEAD = $ExpectedRevision, a clean tracked tree; refuse if $fingerprints, $log, $receipt or $summaryPath exist",
    "freeze_test_source.py: fail if any *.cs file has CR characters; write $fingerprints",
    "Run $testScript (full default set: unit, component, integration) in a child pwsh; keep the console output as $log   <!-- TEST-COMMANDS -->",
    'Verify the fingerprints again (the tested bytes are the committed bytes)',
    "Write $summaryPath (suites: $($requiredSuites -join ', ')) and $receipt")
if (Show-ReleasePlan -Title "Invoke-ReleaseTests $($ctx.BundleName)" -Step $plan) { return }

Assert-CleanSourceAtRevision -Revision $ExpectedRevision
Assert-FilesAbsent @($fingerprints, $log, $receipt, $summaryPath)
if ($SummaryJson -and -not (Test-Path -LiteralPath $SummaryJson -PathType Leaf)) { throw "-SummaryJson does not exist: $SummaryJson" }
Invoke-ReleasePython (Join-Path $PSScriptRoot 'freeze_test_source.py') --repo $ctx.Repo --revision $ExpectedRevision --output $fingerprints

function Invoke-TestRunner {
    # TEST-COMMANDS: the one place that knows how tests are started. Returns the exit code; output goes to $log.
    param([string] $LogPath)
    $pwsh = (Get-Command pwsh).Source
    & $pwsh -NoProfile -File $testScript 2>&1 | ForEach-Object { "$_" } | Tee-Object -FilePath $LogPath | Out-Host
    [int]$LASTEXITCODE
}

function Convert-TestLogToSummary {
    # TEST-COMMANDS: reads the runner's console output; supports the VSTest and Microsoft.Testing.Platform summaries.
    param([string] $Text)
    $clean = [regex]::Replace($Text, "\x1B\[[0-9;?]*[A-Za-z]", '')
    $blocks = [regex]::Split($clean, '(?m)^=== dotnet test ') | Select-Object -Skip 1
    foreach ($block in $blocks) {
        $first = ($block -split "`r?`n")[0]
        if ($first -notmatch 'PremiereCalendar\.([A-Za-z]+?)Tests') { continue }
        $suite = $Matches[1].ToLowerInvariant()
        $passed = $null; $failed = $null; $skipped = $null
        $vstest = [regex]::Matches($block, '(?:Passed|Failed)!\s+-\s+Failed:\s+(\d+),\s+Passed:\s+(\d+),\s+Skipped:\s+(\d+),\s+Total:\s+(\d+)')
        if ($vstest.Count -gt 0) {
            $last = $vstest[$vstest.Count - 1]
            $failed = [int]$last.Groups[1].Value; $passed = [int]$last.Groups[2].Value; $skipped = [int]$last.Groups[3].Value
        }
        else {
            $mtp = [regex]::Match($block, '(?ms)Test run summary:.*?total:\s*(\d+).*?failed:\s*(\d+).*?succeeded:\s*(\d+).*?skipped:\s*(\d+)')
            if ($mtp.Success) { $failed = [int]$mtp.Groups[2].Value; $passed = [int]$mtp.Groups[3].Value; $skipped = [int]$mtp.Groups[4].Value }
        }
        if ($null -eq $passed) { continue }
        [pscustomobject]@{ suite = $suite; passed = $passed; failed = $failed; skipped = $skipped }
    }
}

$started = [DateTimeOffset]::UtcNow
$exit = Invoke-TestRunner -LogPath $log
$finished = [DateTimeOffset]::UtcNow
if ($exit -ne 0) { throw "Tests did not pass (exit code $exit); the log is kept as $log. A new bundle is needed for the next attempt." }
Invoke-ReleasePython (Join-Path $PSScriptRoot 'freeze_test_source.py') --repo $ctx.Repo --revision $ExpectedRevision --verify $fingerprints
Assert-CleanSourceAtRevision -Revision $ExpectedRevision

$entries = if ($SummaryJson) { @(Read-Json $SummaryJson) } else { @(Convert-TestLogToSummary -Text (Get-Content -LiteralPath $log -Raw)) }
$missing = @($requiredSuites | Where-Object { $_ -notin @($entries | ForEach-Object { $_.suite }) })
if ($entries.Count -eq 0 -or $missing.Count -gt 0) {
    throw "Could not find the passed/failed/skipped counts of: $($missing -join ', '). Read $log and re-run with -SummaryJson (a JSON array of {suite, passed, failed, skipped}) or adapt Convert-TestLogToSummary."
}
$fingerprintHash = Get-FileSha256 $fingerprints
$summary = @($entries | ForEach-Object {
        [ordered]@{
            suite = $_.suite; passed = [int]$_.passed; failed = [int]$_.failed; skipped = [int]$_.skipped
            sourceRevision = $ExpectedRevision; testedSourceFingerprintsSha256 = $fingerprintHash
            evidence = 'test-run.log'; missingEvidence = @()
        } })
Write-NewJson -Path $summaryPath -Object $summary
Write-NewJson -Path $receipt -Object ([ordered]@{
        sourceRevision = $ExpectedRevision; startedUtc = $started.ToString('O'); finishedUtc = $finished.ToString('O')
        runner = 'eng\Test-WithPostgres.ps1 (defaults)'; exitCode = $exit; logSha256 = Get-FileSha256 $log
        testedSourceFingerprintsSha256 = $fingerprintHash; summarySource = $(if ($SummaryJson) { 'supplied -SummaryJson' } else { 'parsed from the console log' }) })
$summary | ForEach-Object { Write-Host ("{0}: {1} passed, {2} failed, {3} skipped" -f $_.suite, $_.passed, $_.failed, $_.skipped) }
if (@($summary | Where-Object { $_.failed -ne 0 -or $_.passed -lt 1 }).Count -gt 0) { throw 'A suite has failures or no passed cases; see test-summary.json.' }
