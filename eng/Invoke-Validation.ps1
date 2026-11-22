#Requires -Version 7.0
<#
.SYNOPSIS
Change-aware validation entry: picks the static checks and the application follow-up commands for what changed.

.DESCRIPTION
-Changed (default): paths changed since the merge-base with -Base (default origin/main) plus staged, unstaged and
untracked files are classified following docs/Testing.md "Scoped application checks":
  docs only                       -> Test-Docs and git diff --check
  eng/**, tests/*.ps1|*.py        -> the static checks that cover those files (all of them when unsure)
  C#/Razor/csproj/Dockerfile/...  -> applicable static checks plus the builder commands
                                     (eng/wsl-docker.ps1 check -Project App, eng/Test-WithPostgres.ps1 -Project <suites>)
Test impact selection (eng/StaticChecks.Common.ps1 Get-TestImpact) narrows the service suites to the test classes that reference
the changed types (plus one hop of consumers) and falls back to whole suites with a printed reason; -FullSuites disables it.
-All runs the complete static lane. -Plan prints the classification and commands and runs nothing.
Builder commands run only on Windows with -Run, after the static checks passed, one after another (never
concurrently in one worktree). Without -Run, or on macOS, they are printed as the required builder follow-up.
This is scoped feedback. It never claims release acceptance; Browser checks and the complete suites stay required.

.EXAMPLE
pwsh -NoProfile -File D:\Projects\PremiereCalendar\eng\Invoke-Validation.ps1 -Changed -Plan
#>
[CmdletBinding()]
param(
    [switch] $Changed,
    [string] $Base = 'origin/main',
    [switch] $All,
    [switch] $Run,
    [switch] $FullSuites,
    [switch] $Plan,
    [ValidateRange(1, 256)][int] $ThrottleLimit = [Environment]::ProcessorCount,
    [string] $PythonExecutable,
    [ValidateRange(10, 7200)][int] $TimeoutSeconds = 1200
)
$ErrorActionPreference = 'Stop'
if ($All -and $Changed) { throw 'Use either -All or -Changed.' }
. (Join-Path $PSScriptRoot 'StaticChecks.Common.ps1')
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$table = Get-StaticCheckTable
$classification = $null; $followUp = @()
if ($All) {
    $selected = @($table)
} else {
    $classification = Get-ChangeClassification (Get-ChangedPaths $root $Base) $table
    $selected = if ($classification.Paths.Count -eq 0) { @($table | Where-Object Name -eq 'GitDiffCheck') }
                elseif ($classification.RunAll) { @($table) }
                else { @($table | Where-Object { $classification.StaticNames -contains $_.Name }) }
    $followUp = @(Get-AppFollowUpCommands $classification $root)
    if ($classification.Suites.Count -gt 0 -and -not $FullSuites) {
        $clock = [Diagnostics.Stopwatch]::StartNew()
        $impact = Get-TestImpact $root $classification.Paths (Get-TestCountIndex (Get-TestResultRoots $root))
        $clock.Stop()
        Write-Host ("Test impact selection ({0:N1} s):" -f $clock.Elapsed.TotalSeconds)
        Write-TestImpactReport $impact
        $narrowed = @(Get-TestImpactCommands $impact $root)
        if ($narrowed.Count -gt 0) { $followUp = $narrowed }
        Write-Host 'Narrowed runs are feedback only: the complete suites run once per release candidate (release step 6). Add -FullSuites to run whole suites now.'
    } elseif ($FullSuites -and $classification.Suites.Count -gt 0) { Write-Host '-FullSuites: whole suites, no test impact selection.' }
    Write-Host "Changed paths: $($classification.Paths.Count) (base $Base)."
    $classification.Reasons | Select-Object -First 25 | ForEach-Object { Write-Host "  $_" }
    if ($classification.Reasons.Count -gt 25) { Write-Host "  ... $($classification.Reasons.Count - 25) more" }
    if ($classification.DocsOnly) { Write-Host 'Documentation only: Test-Docs and git diff --check.' }
}
Write-Host ("Static checks: {0}" -f (($selected.Name) -join ', '))
foreach ($c in $followUp) { Write-Host ("Builder step: pwsh -NoProfile -File {0} {1}" -f $c.File, (($c.Arguments | ForEach-Object { if ($_ -match '[,\s]') { "'$_'" } else { $_ } }) -join ' ')) }
if ($classification -and $classification.BrowserNeeded) {
    Write-Host 'Browser checks (prepared glibc browser image, run after the service suites) are required before release for these UI/browser changes.'
}
if ($Plan) { Write-Host 'Plan only: nothing was run.'; return }

$python = Resolve-StaticPython $PythonExecutable $selected
$commands = @($selected | ForEach-Object { ConvertTo-StaticCheckCommand $_ $root $python })
if (-not $All) {
    # With -Changed, check whitespace of everything since the merge-base (committed and uncommitted), not only HEAD.
    $mergeBase = (& git -C $root merge-base HEAD $Base).Trim()
    foreach ($c in $commands) { if ($c.Name -eq 'GitDiffCheck') { $c.Arguments = [string[]]@('diff', '--check', $mergeBase) } }
}
$logs = Join-Path $root ('artifacts/static-checks/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $PID)
$clock = [Diagnostics.Stopwatch]::StartNew()
$results = Invoke-StaticCheckSet $commands $logs $ThrottleLimit $TimeoutSeconds
$clock.Stop()
Write-StaticCheckSummary $results $clock.Elapsed.TotalSeconds
$failed = @($results | Where-Object Status -ne 'PASS').Count -gt 0
if ($failed) { Write-Host 'Static checks failed; builder commands were not started.' -ForegroundColor Red; exit 1 }

if ($followUp.Count -gt 0) {
    if ($Run -and $IsWindows) {
        foreach ($c in $followUp) {
            Write-Host "== $($c.Label)"
            & (Get-PwshExecutable) -NoProfile -File $c.File @($c.Arguments)
            if ($LASTEXITCODE -ne 0) { Write-Host "$($c.Label) failed (exit $LASTEXITCODE)." -ForegroundColor Red; exit 1 }
        }
        Write-Host 'Builder steps passed. Scoped feedback only: not release acceptance (complete suites, Browser, vulnerability, SBOM and exact-image gates remain).'
    } else {
        Write-Host 'Static checks passed. Required builder follow-up (Windows/WSL builder; add -Run on Windows to execute it) is listed above. Not release acceptance.'
    }
} else {
    Write-Host 'Static checks passed. Scoped feedback only: not release acceptance.'
}
