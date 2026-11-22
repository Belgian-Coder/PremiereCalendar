#Requires -Version 7.0
# Enforces the harness speed and test-minimisation policy so a new session cannot silently fall back to slow or
# redundant paths: source mounts go through the verified ext4 mirror, identical passing runs are reused, change-aware
# validation narrows tests, release steps 2-7 run through Invoke-LocalGates, and AGENTS.md keeps teaching all of it.
# No WSL, Docker, SDK or network is used; plans are evaluated with -Plan only.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$count = 0
function Assert-Policy([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw "Harness policy violated: $Message" }
    $script:count++
}
function Read-Repo([string] $Relative) { [IO.File]::ReadAllText((Join-Path $root $Relative)) }

# 1. No script may bind-mount the Windows checkout (/mnt/<drive> or the converted checkout path) as build/test source.
#    Allowed /src sources: the mirror variable ($pc_src / __SRC__) and WSL-native staging folders. The Compose watcher is
#    the documented exception (dotnet watch needs live edits).
$sourceMount = [regex] '-v\s+(?<source>[^\s]+?):/src(?=[\s:"''`]|$)'
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $root 'eng') -Recurse -File -Include '*.ps1', '*.sh') {
    $relative = [IO.Path]::GetRelativePath($root, $file.FullName).Replace('\', '/')
    foreach ($match in $sourceMount.Matches([IO.File]::ReadAllText($file.FullName))) {
        $source = $match.Groups['source'].Value
        $allowed = $source -match 'pc_src|__SRC__|\$\(ConvertTo-ShellQuoted \$staging\)'
        Assert-Policy $allowed "$relative mounts '$source' as /src; build and test sources must come from the verified ext4 mirror (Sync in eng/Harness.Common.ps1)."
    }
    Assert-Policy (-not ([IO.File]::ReadAllText($file.FullName) -match '/mnt/[a-z]/[^\s"'']*:/src')) "$relative bind-mounts a /mnt/<drive> path as /src."
}
Assert-Policy ((Read-Repo 'compose.dev.yaml') -match '\./:/src') 'The Compose watcher exception moved; review this policy.'

$env:PC_HARNESS_LANE = 'Wsl'   # the WSL helpers' plans are checked on every host (the native lane is checked in 2b)
# 2. Defaults of the WSL helpers: mirror source and the passing-result cache (plans only; no WSL is invoked).
$testPlan = & (Join-Path $root 'eng/Test-WithPostgres.ps1') -Project Unit -Plan
Assert-Policy ($testPlan.SourceMount -eq 'Mirror') 'Test-WithPostgres no longer defaults to the ext4 source mirror.'
Assert-Policy ([string]$testPlan.SourceStorage -like 'wsl:*mirror*') 'Test-WithPostgres plan does not report the mirror storage.'
$testText = Read-Repo 'eng/Test-WithPostgres.ps1'
Assert-Policy ($testText -match '\[switch\]\s*\$NoCache' -and $testText -match 'REUSED passing results') 'Test-WithPostgres lost the passing-result cache (or its -NoCache opt-out).'
$rosterPlan = & (Join-Path $root 'eng/Test-WithPostgres.ps1') -Plan
Assert-Policy ($testPlan.Cpus -eq 'auto' -and $testPlan.BuildMaxCpuCount -eq 'auto' -and $testPlan.TimeoutSeconds -eq 1500 -and $rosterPlan.TimeoutSeconds -eq 3300) 'Test-WithPostgres lost the auto budget or the suite-scaled deadline.'
$checkPlan = & (Join-Path $root 'eng/wsl-docker.ps1') check -Project App -Plan
Assert-Policy ([string]$checkPlan.Command -match '__SRC__|pc_src' -and [string]$checkPlan.Command -match 'maxcpucount:\$pc_budget') 'wsl-docker check no longer builds from the mirror with the auto budget.'

$env:PC_HARNESS_LANE = $null
# 2b. The macOS/Linux lane is NATIVE feedback only: never WSL/Docker, never part of a release, never claimed as acceptance.
. (Join-Path $root 'eng/Harness.Common.ps1')
Assert-Policy ((Resolve-HarnessHostLane 'Auto' $false) -eq 'Native' -and (Resolve-HarnessHostLane 'Auto' $true) -eq 'Wsl') 'The host lane decision changed: Windows must use WSL, other hosts the native lane.'
$nativePlan = & (Join-Path $root 'eng/Test-WithPostgres.ps1') -Lane Native -Project Unit -Plan
Assert-Policy ($nativePlan.Lane -eq 'native' -and $nativePlan.Acceptance -match 'feedback only' -and $nativePlan.Acceptance -match 'container builder') 'The native lane no longer states that it is feedback only.'
Assert-Policy ($null -eq $nativePlan.PSObject.Properties['WslDistribution'] -and $null -eq $nativePlan.PSObject.Properties['DevImage']) 'The native lane plan reports WSL/Docker resources.'
$nativeStart = $testText.IndexOf('if ($hostLane -eq ''Native'')'); $nativeEnd = $testText.IndexOf("`n" + 'if ($Plan) {', $nativeStart)
$nativeBlock = if ($nativeStart -ge 0 -and $nativeEnd -gt $nativeStart) { $testText.Substring($nativeStart, $nativeEnd - $nativeStart) } else { '' }
Assert-Policy ($nativeBlock.Length -gt 500 -and $nativeBlock -notmatch 'Invoke-HarnessMirroredWsl|ConvertTo-WslPath|Invoke-HarnessWsl|Assert-HarnessWslRunning|wsl\.exe|docker\s+(run|exec|create|network|info|image)') 'The native lane block uses WSL or Docker.'
Assert-Policy ($nativeBlock -match "Browser" -and $nativeBlock -match 'Invoke-HarnessWithNativePostgres') 'The native lane lost the Browser refusal or the throwaway PostgreSQL wrapper.'
foreach ($releaseScript in Get-ChildItem -LiteralPath (Join-Path $root 'eng/release') -Recurse -File -Include '*.ps1', '*.py', '*.sh') {
    Assert-Policy (([IO.File]::ReadAllText($releaseScript.FullName)) -notmatch 'Lane\s+Native|Resolve-HarnessHostLane|Invoke-HarnessWithNativePostgres|Get-HarnessNativePlatform|-PostgresBin') "$($releaseScript.Name) uses the native feedback lane; release acceptance stays on the Windows/WSL container builder."
}
# The release and wsl-docker parameter sets (WSL distribution, dev image, volumes) are refused by the native lane.
$refused = $false
try { & (Join-Path $root 'eng/Test-WithPostgres.ps1') -Lane Native -WslDistribution Ubuntu-24.04 -Plan | Out-Null } catch { $refused = $true }
Assert-Policy $refused 'A release-style (WSL) parameter set was accepted by the native lane.'

# 3. Change-aware validation narrows tests and never adds a redundant app build before a test run.
. (Join-Path $root 'eng/StaticChecks.Common.ps1')
Assert-Policy ([bool](Get-Command Get-TestImpact -ErrorAction SilentlyContinue)) 'Invoke-Validation lost test impact selection.'
$validationText = Read-Repo 'eng/Invoke-Validation.ps1'
Assert-Policy ($validationText -match 'FullSuites' -and $validationText -match 'Get-TestImpact|TestImpact') 'Invoke-Validation no longer narrows tests by impact by default.'
$appAndSuites = @(Get-AppFollowUpCommands ([pscustomobject]@{ BuildApp = $true; Suites = @('Unit') }) $root)
Assert-Policy (@($appAndSuites | Where-Object { ($_.Arguments -join ' ') -match '^check -Project App$' }).Count -eq 0) 'A separate App build runs before a test run that already compiles the app.'

# 4. Release: steps 2-7 through the orchestrator, release tests record result reuse, candidates default to auto.
Assert-Policy (Test-Path -LiteralPath (Join-Path $root 'eng/release/Invoke-LocalGates.ps1')) 'Invoke-LocalGates.ps1 is missing.'
Assert-Policy ((Read-Repo 'eng/release/Invoke-ReleaseTests.ps1') -match 'reusedResults') 'The release test receipt does not record result reuse.'
Assert-Policy ((Read-Repo 'eng/release/Invoke-Candidate.ps1') -match "\`$Jobs = 'auto'") 'Invoke-Candidate no longer defaults to the auto budget.'

# 5. A new session learns the policy: AGENTS.md (loaded natively by Codex and Claude Code) must keep these rules.
$agents = (Read-Repo 'AGENTS.md') -replace '\s+', ' '
foreach ($phrase in @('Invoke-Validation.ps1 -Changed', 'Invoke-StaticChecks.ps1', 'Invoke-LocalGates', 'ext4 source mirror', 'NoCache', 'impact', 'once per release candidate', 'auto')) {
    Assert-Policy ($agents.Contains($phrase)) "AGENTS.md no longer mentions '$phrase'."
}

Write-Host "$count harness policy checks passed; no WSL, Docker or SDK was used."
