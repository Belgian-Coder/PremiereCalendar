#Requires -Version 7.0
# Standalone harness checks: no SDK, Docker, WSL, credentials or test database required.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $root 'eng/Harness.Common.ps1')
$count = 0
function Assert-Harness([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
    $script:count++
}
foreach ($script in Get-ChildItem (Join-Path $root 'eng') -Filter '*.ps1') {
    $tokens = $null; $parseErrors = $null
    [void][Management.Automation.Language.Parser]::ParseFile($script.FullName, [ref]$tokens, [ref]$parseErrors)
    Assert-Harness ($parseErrors.Count -eq 0) "PowerShell parse failure: $($script.Name)"
}
$context = Get-HarnessContext $root
Assert-Harness ($context.BuildRoot.StartsWith($root + [IO.Path]::DirectorySeparatorChar)) 'Build state escaped the current checkout.'
$common = & git -C $root rev-parse --path-format=absolute --git-common-dir
$primary = Split-Path -Parent $common
Assert-Harness ($context.PackageRoot -eq (Join-Path $primary 'artifacts/packages/linux')) 'Package cache is not shared through the primary project.'
$primaryContext = Get-HarnessContext $primary
Assert-Harness ($primaryContext.ComposeProject -eq 'premierecalendar-dev') 'Primary Compose identity changed.'
if ($root -ne $primary) {
    Assert-Harness ($context.ComposeProject -ne $primaryContext.ComposeProject) 'Worktree Compose identity collided.'
    Assert-Harness ($context.BuildRoot -ne $primaryContext.BuildRoot) 'Worktrees share mutable build state.'
}
Assert-Harness ((ConvertTo-BashLiteral "a'b") -eq "'a'\''b'") 'Bash single-quote escaping regressed.'

# Shadow only the executable name in this test process. Any accidental execution is intercepted.
$global:HarnessCapturedScripts = [Collections.Generic.List[string]]::new()
$global:HarnessWslCalls = 0
function global:wsl.exe {
    $CommandArguments = @($args)
    $global:HarnessWslCalls++
    if ($CommandArguments -contains 'wslpath') {
        $path = $CommandArguments[-1]
        if ($path -notmatch '^([A-Za-z]):/(.*)$') { throw "Unexpected mock path: $path" }
        $drive = $Matches[1].ToLowerInvariant(); $relative = $Matches[2]
        "/mnt/$drive/$relative"
    } elseif ($CommandArguments -contains '-s') {
        $global:HarnessCapturedScripts.Add(($input -join "`n"))
    } else { throw 'Unexpected WSL operation.' }
    $global:LASTEXITCODE = 0
}
try {
    $filter = 'FullyQualifiedName~Calendar&Name!~A''B$(touch NEVER)'
    $plan = & (Join-Path $root 'eng/Test-WithPostgres.ps1') -Project ComponentTests -Filter $filter -Plan
    $check = & (Join-Path $root 'eng/wsl-docker.ps1') check -Plan
    $alternate = & (Join-Path $root 'eng/Test-WithPostgres.ps1') -Project UnitTests -NoRuntimeAsync -Plan
    Assert-Harness ($global:HarnessWslCalls -eq 0) '-Plan invoked WSL.'
    Assert-Harness ($plan.Projects.Count -eq 1 -and $plan.Projects[0] -eq 'ComponentTests') 'Scoped project selection changed.'
    Assert-Harness ($plan.Arguments -contains $filter) 'The exact filter was not preserved.'
    Assert-Harness ($plan.Arguments -notcontains '--no-build' -and $plan.Arguments -notcontains '--no-restore') 'Default checks bypass build/restore validation.'
    Assert-Harness ($alternate.BuildStorage -ne $plan.BuildStorage) 'Runtime-async modes share build state.'
    Assert-Harness ($check.Command -match 'dotnet build PremiereCalendar/PremiereCalendar.csproj') 'App check expands to the full solution.'
    & (Join-Path $root 'eng/wsl-docker.ps1') check
    & (Join-Path $root 'eng/Test-WithPostgres.ps1') -Project ComponentTests -Filter $filter 6>$null | Out-Null
    Assert-Harness ($global:HarnessCapturedScripts.Count -eq 2) 'Expected two intercepted bash scripts.'
    $checkScript = $global:HarnessCapturedScripts[0]
    $testScript = $global:HarnessCapturedScripts[1]
    Assert-Harness ($checkScript -match 'flock -n 9' -and $testScript -match 'flock -n 9') 'Check/test output locking is missing.'
    Assert-Harness ($testScript -match 'trap cleanup EXIT') 'Disposable resource cleanup is missing.'
    Assert-Harness ($testScript -notmatch '/tmp/pcpg-env') 'Test credentials escaped into WSL host temp files.'
    Assert-Harness ($testScript -match "PC_INNER_SCRIPT='([^']+)'") 'Encoded inner test script is missing.'
    $inner = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Matches[1]))
    Assert-Harness ($inner.Contains((ConvertTo-BashLiteral $filter))) 'Shell filter quoting changed.'
    $bash = Join-Path $env:ProgramFiles 'Git/bin/bash.exe'
    if (Test-Path -LiteralPath $bash) {
        foreach ($shellScript in @($checkScript, $testScript, $inner)) {
            ($shellScript -replace "`r`n", "`n") | & $bash -n
            Assert-Harness ($LASTEXITCODE -eq 0) 'Generated bash failed syntax validation.'
        }
        # Execute the lifecycle with a shell-function Docker double. All paths
        # point into this project's sandbox; no Docker executable can be called.
        foreach ($expectedExit in @(0, 7)) {
            $sandbox = Join-Path $root ('artifacts/harness-tests/lifecycle-' + [guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $sandbox -Force | Out-Null
            $linuxSandbox = '/' + $sandbox.Substring(0, 1).ToLowerInvariant() + '/' + ($sandbox.Substring(3) -replace '\\', '/')
            $linuxRoot = '/mnt/' + $root.Substring(0, 1).ToLowerInvariant() + '/' + ($root.Substring(3) -replace '\\', '/')
            $linuxPackages = '/mnt/' + $context.PackageRoot.Substring(0, 1).ToLowerInvariant() + '/' + ($context.PackageRoot.Substring(3) -replace '\\', '/')
            $simulation = $testScript.Replace($linuxPackages, "$linuxSandbox/packages").Replace($linuxRoot, $linuxSandbox)
            $dockerDouble = @"
flock() { return 0; }
docker() {
  printf '%s %s\n' "`$1" "`${2-}" >> '$linuxSandbox/docker-calls.txt'
  if [ "`$1" = run ] && [ "`${2-}" = --rm ]; then return $expectedExit; fi
  return 0
}
"@
            (($dockerDouble + "`n" + $simulation) -replace "`r`n", "`n") | & $bash -s
            Assert-Harness ($LASTEXITCODE -eq $expectedExit) 'Test runner exit status was not preserved.'
            $calls = Get-Content (Join-Path $sandbox 'docker-calls.txt')
            Assert-Harness (@($calls | Where-Object { $_ -eq 'rm -f' }).Count -eq 2) 'Runner/database cleanup did not execute on exit.'
            Assert-Harness ($calls[-1] -eq 'network rm') 'Network cleanup did not execute on exit.'
        }
    } else { Write-Warning 'Git Bash is absent: generated bash syntax validation was not performed.' }
} finally {
    Remove-Item Function:\wsl.exe
    Remove-Variable HarnessCapturedScripts, HarnessWslCalls -Scope Global
}
[xml] $properties = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
Assert-Harness ($properties.Project.PropertyGroup.ArtifactsPath.InnerText -like '*artifacts/build/host') 'Native build output escaped the checkout.'
Write-Host "$count harness checks passed; WSL/Docker commands were intercepted, not executed."
