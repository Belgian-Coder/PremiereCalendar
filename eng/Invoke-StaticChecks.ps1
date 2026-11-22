#Requires -Version 7.0
<#
.SYNOPSIS
Runs every portable static check concurrently (Windows 11 and macOS ARM64; no WSL, Docker, SDK or database).

.DESCRIPTION
The checks come from the declared table in eng/StaticChecks.Common.ps1. Each runs as its own child process with
output captured to artifacts/static-checks/<timestamp>/<name>.log. Prints a per-check table (status, seconds), the
total wall time, and the last 30 lines of every failing check; exits 1 when any check fails.
Checks keep their fixtures in GUID-named sandboxes under artifacts/, so they can run side by side.
-ThrottleLimit 1 reproduces the old serial lane. This is feedback, not release acceptance.

.PARAMETER Only
Check names (wildcards allowed) to run. See -Plan for the names.
.PARAMETER Exclude
Check names (wildcards allowed) to skip.
.PARAMETER ThrottleLimit
Maximum concurrent checks; default is the logical CPU count.
.PARAMETER PythonExecutable
Existing Python 3.10+ interpreter; default is the first real python3/python on PATH (Store alias stub skipped).
.PARAMETER Plan
Print the table of what would run and exit without running anything.
.PARAMETER TimeoutSeconds
Per-check bound; the process tree is killed when exceeded.

.EXAMPLE
pwsh -NoProfile -File D:\Projects\PremiereCalendar\eng\Invoke-StaticChecks.ps1
#>
[CmdletBinding()]
param(
    [string[]] $Only,
    [string[]] $Exclude,
    [ValidateRange(1, 256)][int] $ThrottleLimit = [Environment]::ProcessorCount,
    [string] $PythonExecutable,
    [switch] $Plan,
    [ValidateRange(10, 7200)][int] $TimeoutSeconds = 1200
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'StaticChecks.Common.ps1')
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$selected = Select-StaticChecks (Get-StaticCheckTable) $Only $Exclude
if ($selected.Count -eq 0) { throw 'No static checks selected.' }
if ($Plan) {
    $selected | ForEach-Object { [pscustomobject]@{ Name = $_.Name; Kind = $_.Kind; Command = (($_.Path, ($_.Args -join ' ')) -join ' ').Trim(); Python = $_.UsesPython; Weight = $_.Weight; SerialGroup = $_.Group } } | Format-Table -AutoSize | Out-String | Write-Host
    Write-Host "Plan only: nothing was run. Throttle $ThrottleLimit; logs would go to artifacts/static-checks/<timestamp>/."
    return
}
$python = Resolve-StaticPython $PythonExecutable $selected
$commands = @($selected | ForEach-Object { ConvertTo-StaticCheckCommand $_ $root $python })
$logs = Join-Path $root ('artifacts/static-checks/' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + $PID)
Write-Host "Running $($commands.Count) static check(s), up to $ThrottleLimit at once; logs: $logs"
$clock = [Diagnostics.Stopwatch]::StartNew()
$results = Invoke-StaticCheckSet $commands $logs $ThrottleLimit $TimeoutSeconds
$clock.Stop()
Write-StaticCheckSummary $results $clock.Elapsed.TotalSeconds
Write-Host 'Static checks are scoped feedback; they are not release acceptance.'
if (@($results | Where-Object Status -ne 'PASS').Count -gt 0) { exit 1 }
