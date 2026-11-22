#Requires -Version 7.0
# Regression checks for portable source validation; no SDK, WSL, Docker or database.
[CmdletBinding()]
param([string] $PythonExecutable, [ValidateSet('All', 'Docs', 'Syntax', 'Candidate')][string] $Section = 'All')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $root 'eng/Python.Common.ps1')
if ($Section -eq 'All') {
    # The three groups are independent (each builds its own unique sandbox below artifacts/harness-tests), so they
    # run as concurrent child processes; the parent sums their check counts.
    $self = $PSCommandPath
    $pwshPath = [Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
    $results = @('Docs', 'Syntax', 'Candidate' | ForEach-Object -ThrottleLimit 3 -Parallel {
        $childArguments = @('-NoProfile', '-File', $using:self, '-Section', $_)
        if ($using:PythonExecutable) { $childArguments += @('-PythonExecutable', $using:PythonExecutable) }
        $output = & $using:pwshPath @childArguments 2>&1
        [pscustomobject]@{ Section = $_; ExitCode = $LASTEXITCODE; Output = @($output | ForEach-Object { "$_" }) }
    })
    $total = 0
    foreach ($name in 'Docs', 'Syntax', 'Candidate') {
        $result = @($results | Where-Object Section -eq $name)
        if ($result.Count -ne 1 -or $result[0].ExitCode -ne 0 -or $result[0].Output.Count -eq 0 -or $result[0].Output[-1] -notmatch '^(\d+) validation-tool checks passed') {
            $result | ForEach-Object { $_.Output } | Write-Host
            throw "Validation-tool section failed: $name"
        }
        $total += [int] $Matches[1]
    }
    $result | Out-Null
    Write-Host "$total validation-tool checks passed; fixtures stayed inside this worktree's artifacts."
    return
}
$sandboxRoot = Join-Path $root 'artifacts/harness-tests'
$sandbox = Join-Path $sandboxRoot ('validation-' + $Section.ToLowerInvariant() + '-' + [guid]::NewGuid().ToString('N'))
$count = 0
function Write-Fixture([string] $Path, [string] $Text) {
    $destination = Join-Path $sandbox $Path
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $destination))
    [IO.File]::WriteAllText($destination, $Text.Replace("`r`n", "`n"))
}
function Assert-Rejected([scriptblock] $Action, [string] $Expected) {
    $message = $null
    try { & $Action 6>$null 3>$null } catch { $message = $_.Exception.Message }
    if (-not $message -or $message -notlike $Expected) { throw "Expected '$Expected', received '$message'." }
    $script:count++
}
try {
    [void][IO.Directory]::CreateDirectory((Join-Path $sandbox 'eng'))
    Copy-Item -LiteralPath (Join-Path $root 'eng/Test-Docs.ps1'), (Join-Path $root 'eng/Test-ScriptSyntax.ps1'), (Join-Path $root 'eng/Python.Common.ps1') -Destination (Join-Path $sandbox 'eng')
    & git -C $sandbox init --quiet
    if ($LASTEXITCODE -ne 0) { throw 'Could not initialize the disposable fixture repository.' }
    Write-Fixture 'README.md' '# Fixture'
    Write-Fixture 'AGENTS.md' '# Guidance'
    Write-Fixture 'docs/Present.md' '# Existing heading'
    Write-Fixture 'docs/Evidence-20261115.md' '[missing historical output](../artifacts/missing.json)'
    if ($Section -eq 'Docs') {
    $docs = Join-Path $sandbox 'eng/Test-Docs.ps1'
    Assert-Rejected { & $docs } '*1 documentation problem*'
    & $docs -SourceOnly 3>$null 6>$null
    $count++
    # Source-only must not suppress a missing repository file or anchor.
    Write-Fixture 'docs/Evidence-20261115.md' '[missing source](Missing.md)'
    Assert-Rejected { & $docs -SourceOnly } '*1 documentation problem*'
    Write-Fixture 'docs/Evidence-20261115.md' '[wrong anchor](Present.md#missing)'
    Assert-Rejected { & $docs -SourceOnly } '*1 documentation problem*'
    Write-Fixture 'docs/Evidence-20261115.md' '[partial anchor](Present.md#existing)'
    Assert-Rejected { & $docs -SourceOnly } '*1 documentation problem*'
    Write-Fixture 'docs/Present.md' 'No headings'
    Assert-Rejected { & $docs -SourceOnly } '*1 documentation problem*'
    Write-Fixture 'docs/Present.md' '# Existing heading'
    # Maintained documents cannot describe missing evidence as available.
    Write-Fixture 'docs/Evidence-20261115.md' '[valid source](Present.md#existing-heading)'
    Write-Fixture 'README.md' '[missing evidence](artifacts/missing.json)'
    Assert-Rejected { & $docs -SourceOnly } '*1 documentation problem*'
    Write-Fixture 'README.md' '[valid source](docs/Present.md#existing-heading)'
    & $docs -SourceOnly 6>$null
    $count++
    }

    if ($Section -eq 'Syntax') {
    $syntax = Join-Path $sandbox 'eng/Test-ScriptSyntax.ps1'
    Write-Fixture 'eng/invalid.ps1' 'function Broken {'
    Assert-Rejected { & $syntax } '*script problem*'
    Write-Fixture 'eng/invalid.ps1' '# valid PowerShell'
    Write-Fixture 'eng/one.py' 'value = 1'
    Write-Fixture 'eng/two.py' 'value = 2'
    $python = Resolve-PythonExecutable $PythonExecutable
    if ($python) {
        & $syntax -PythonExecutable $python 6>$null
        $count++
        Write-Fixture 'eng/two.py' 'def broken('
        Assert-Rejected { & $syntax -PythonExecutable $python } '*script problem*'
        Write-Fixture 'eng/two.py' 'value = 2'
        & $syntax -PythonExecutable $python 6>$null
        $count++
        if (Get-ChildItem -LiteralPath $sandbox -Recurse -Filter '*.pyc') { throw 'Python validation wrote bytecode.' }
        $count++
    } else {
        Assert-Rejected { & $syntax } '*1 script problem*'
        Write-Warning 'Python 3 absent: compilation success/failure cases were not run.'
    }
    }

    if ($Section -eq 'Candidate') {
    $python = if ($PythonExecutable) { Get-Command $PythonExecutable -CommandType Application -ErrorAction Stop }
              else { Get-Command python3, python -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1 }
    $candidate = Join-Path $root 'eng/release/Invoke-Candidate.ps1'
    $revision = (& git -C $root rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the candidate revision.' }
    $candidateOutput = Join-Path $root ('artifacts/release/plan-only-' + [guid]::NewGuid().ToString('N'))
    $plan = & $candidate -Target linux-amd64 -Mode plan -Revision $revision -Output $candidateOutput
    if ($plan.ReleaseAccepted -or $plan.UploadSupported -or $plan.Commands.package -notcontains $revision) { throw 'Candidate plan changed its acceptance/source contract.' }
    $count++
    & $candidate -Target linux-amd64 -Mode package -Revision $revision -Output $candidateOutput -WhatIf | Out-Null
    if (Test-Path -LiteralPath $candidateOutput) { throw 'Candidate plan wrote output.' }
    $count++
    Assert-Rejected { & $candidate -Target linux-amd64 -Mode upload -Revision $revision -Output $candidateOutput } '*Candidate upload is unsupported*'
    Assert-Rejected { & $candidate -Target linux-amd64 -Mode package -Revision $revision -Output $root } '*Output must stay*'
    $boundedPlan = & $candidate -Target linux-amd64 -Mode plan -Revision $revision -Output $candidateOutput -Jobs 3
    foreach ($parameter in @('-Cpus', '-MaxThreads', '-BuildMaxCpuCount')) {
        $argv = $boundedPlan.Commands.validate
        if ($argv[[array]::IndexOf($argv, $parameter) + 1] -ne '3') { throw "Candidate budget did not reach $parameter." }
    }
    $count++
    # Execute the real release runner function with an intercepted child executable.
    # This proves the public budgets cross the child-process boundary without a database/build.
    Write-Fixture 'eng/capture-runner.ps1' @'
param([switch] $NoProfile, [string] $File, [int] $Cpus, [int] $MaxThreads, [int] $BuildMaxCpuCount, [string] $BuildVolume, [string] $NugetVolume, [string] $WslDistribution, [int] $TimeoutSeconds)
if (-not $NoProfile -or $File -ne 'fixture-test-runner.ps1' -or $Cpus -ne 3 -or $MaxThreads -ne 5 -or $BuildMaxCpuCount -ne 2 -or $BuildVolume -ne 'fixture-linux-amd64-build' -or $NugetVolume -ne 'fixture-linux-amd64-nuget' -or $WslDistribution -ne 'PC-MARC-Dev' -or $TimeoutSeconds -ne 1800 -or $args.Count) { throw 'Release child argv lost budgets/storage/distribution/deadline or acquired a test filter.' }
$global:LASTEXITCODE = 0
Write-Output 'Total wall time: 0 s. Results: fixture-results'
'@
    $runnerTokens = $null; $runnerErrors = $null
    $releaseAst = [Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'eng/release/Invoke-ReleaseTests.ps1'), [ref]$runnerTokens, [ref]$runnerErrors)
    $runnerFunction = $releaseAst.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-TestRunner' }, $true)
    & {
        $runnerStub = Join-Path $sandbox 'eng/capture-runner.ps1'
        function Get-Command([string] $Name) {
            if ($Name -ne 'pwsh') { throw "Unexpected child executable lookup: $Name" }
            [pscustomobject]@{ Source = $runnerStub }
        }
        . ([scriptblock]::Create($runnerFunction.Extent.Text))
        $Cpus = 3; $MaxThreads = 5; $BuildMaxCpuCount = 2; $TimeoutSeconds = 1800
        $WslDistribution = 'PC-MARC-Dev'
        $BuildVolume = 'fixture-linux-amd64-build'; $NugetVolume = 'fixture-linux-amd64-nuget'
        $testScript = 'fixture-test-runner.ps1'
        $run = Invoke-TestRunner -LogPath (Join-Path $sandbox 'runner.log') 6>$null
        if ($run.ExitCode -ne 0 -or $run.ResultsFolder -ne 'fixture-results') { throw 'Release runner status/result parsing regressed.' }
    }
    $count++
    if ($python) {
        # The vendored contract validates the repository-owned manifest and declared placeholders.
        # Import without bytecode and never invoke dispatch/SSH from this static regression.
        $client = Join-Path $root 'eng/request-release.py'
        $manifest = Join-Path $root 'eng/release-targets.json'
        $contractCheck = @'
import runpy, sys
from pathlib import Path
client = runpy.run_path(sys.argv[1], run_name="contract_test")
manifest = client["load_manifest"](Path(sys.argv[2]).read_bytes())
target = manifest["targets"]["nas-linux-amd64"]
assert set(target["parameters"]) == {"version", "expectedOldVersion", "expectedOldDigest", "expectedStackRevision"}
assert target["uploadPolicy"] == "unsupported" and target["commands"]["upload"] == []
for phase in ("validate", "package"):
    argv = client["interpolate"](target["commands"][phase][0], {"commit": "a" * 40, "output": "fixture-output", "target": "nas-linux-amd64", "jobs": "2"}, {"version": "0.0.2", "expectedOldVersion": "0.0.1", "expectedOldDigest": "sha256:" + "b" * 64, "expectedStackRevision": "1"})
    assert argv[argv.index("-ExpectedOldVersion") + 1] == "0.0.1"
    assert argv[argv.index("-ExpectedStackRevision") + 1] == "1"
'@
        & $python -I -B -c $contractCheck $client $manifest
        if ($LASTEXITCODE -ne 0) { throw 'Release manifest does not satisfy the shared client contract.' }
        $count++
    }
    }
    Write-Host "$count validation-tool checks passed ($Section section); fixtures stayed inside this worktree's artifacts."
} finally {
    $absolute = [IO.Path]::GetFullPath($sandbox)
    $prefix = [IO.Path]::GetFullPath($sandboxRoot).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if ($absolute.StartsWith($prefix, [StringComparison]::Ordinal) -and (Test-Path -LiteralPath $absolute)) {
        Remove-Item -LiteralPath $absolute -Recurse -Force
    }
}
