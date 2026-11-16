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
Assert-Harness ($context.BuildRoot -match '^premierecalendar-linux-[0-9a-f]{10}-build$') 'Linux build state is not in a checkout-specific named volume.'
$common = & git -C $root rev-parse --path-format=absolute --git-common-dir
$primary = Split-Path -Parent $common
Assert-Harness ($context.PackageRoot -match '^premierecalendar-linux-[0-9a-f]{10}-nuget$') 'Linux dependencies are not in a checkout-specific named volume.'
Assert-Harness ($context.WatchRoot -ne $context.BuildRoot) 'Watcher and checks share mutable build state.'
Assert-Harness ($context.CacheRoot -match '^premierecalendar-linux-[0-9a-f]{10}-sdk-cache$') 'SDK caches are not persistent Linux storage.'
$primaryContext = Get-HarnessContext $primary
Assert-Harness ($primaryContext.ComposeProject -eq 'premierecalendar-dev') 'Primary Compose identity changed.'
if ($root -ne $primary) {
    Assert-Harness ($context.ComposeProject -ne $primaryContext.ComposeProject) 'Worktree Compose identity collided.'
    Assert-Harness ($context.BuildRoot -ne $primaryContext.BuildRoot) 'Worktrees share mutable build state.'
    Assert-Harness ($context.PackageRoot -ne $primaryContext.PackageRoot) 'Worktrees share writable dependency state.'
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
    # -Filter takes test-name substrings (xUnit.net 4 runs the test executables with -method *text*); the shell
    # metacharacters must reach the runner as one literal argument.
    $filter = 'FullyQualifiedName~Calendar.A''B$(touch NEVER)'
    $runnerFilter = '*Calendar.A''B$(touch NEVER)*'
    $plan = & (Join-Path $root 'eng/Test-WithPostgres.ps1') -Project ComponentTests -Filter $filter -Plan
    $check = & (Join-Path $root 'eng/wsl-docker.ps1') check -Plan
    $alternate = & (Join-Path $root 'eng/Test-WithPostgres.ps1') -Project UnitTests -NoRuntimeAsync -Plan
    $default = & (Join-Path $root 'eng/Test-WithPostgres.ps1') -Plan
    $suiteAlias = & (Join-Path $root 'eng/Test-WithPostgres.ps1') -Suite Unit,Integration -Filter ViewSync,Wikimedia -Parallel none -MaxThreads 2 -Plan
    $query = & (Join-Path $root 'eng/Test-WithPostgres.ps1') -Project Unit -QueryFilter '/*/*/PostgresResponseCacheTests/*' -RunnerArguments:-longRunning,30 -FreshBuild -NoRestore -Plan
    $viaDocker = & (Join-Path $root 'eng/wsl-docker.ps1') test -Project Unit -Filter ViewSync -Plan
    Assert-Harness ($global:HarnessWslCalls -eq 0) '-Plan invoked WSL.'
    Assert-Harness ($plan.Projects.Count -eq 1 -and $plan.Projects[0] -eq 'ComponentTests') 'Scoped project selection changed.'
    Assert-Harness ($plan.RunnerArguments -contains $runnerFilter) 'The exact filter was not preserved.'
    Assert-Harness ($plan.Build -and $plan.BuildArguments -notcontains '--no-restore') 'Default checks bypass build/restore validation.'
    Assert-Harness ($plan.Results.StartsWith((Join-Path $root 'artifacts/test-results') + [IO.Path]::DirectorySeparatorChar)) 'Test results escaped this checkout''s artifacts folder.'
    Assert-Harness (@($default.Projects) -join ',' -eq 'UnitTests,ComponentTests,IntegrationTests') 'Default test projects changed.'
    Assert-Harness (@($suiteAlias.Projects) -join ',' -eq 'UnitTests,IntegrationTests') '-Suite alias or short suite names regressed.'
    Assert-Harness (@($suiteAlias.RunnerArguments) -join ' ' -eq '-method *ViewSync* -method *Wikimedia* -parallelMode none -maxThreads 2') 'Filter/parallel runner arguments changed.'
    Assert-Harness (@($query.RunnerArguments) -join ' ' -eq '-filter /*/*/PostgresResponseCacheTests/* -longRunning 30') 'Query filter / runner arguments changed.'
    Assert-Harness ($query.BuildArguments -contains '--no-restore' -and $query.BuildStorage -like 'container-local*') '-NoRestore / -FreshBuild regressed.'
    Assert-Harness ($viaDocker.Projects.Count -eq 1 -and $viaDocker.Projects[0] -eq 'UnitTests' -and $viaDocker.RunnerArguments -contains '*ViewSync*') 'wsl-docker test no longer forwards -Project/-Filter.'
    Assert-Harness ($plan.BuildStorage -eq $context.BuildRoot) 'Test build storage differs from the check storage lane.'
    $rejected = $false
    try { & (Join-Path $root 'eng/Test-WithPostgres.ps1') -Filter 'FullyQualifiedName~A&Name~B' -Plan | Out-Null } catch { $rejected = $true }
    Assert-Harness $rejected 'VSTest filter expressions are silently accepted.'
    Assert-Harness ($alternate.BuildStorage -ne $plan.BuildStorage) 'Runtime-async modes share build state.'
    Assert-Harness ($check.Command -match 'dotnet build PremiereCalendar/PremiereCalendar.csproj') 'App check expands to the full solution.'
    & (Join-Path $root 'eng/wsl-docker.ps1') check
    & (Join-Path $root 'eng/Test-WithPostgres.ps1') -Project ComponentTests -Filter $filter 6>$null | Out-Null
    & (Join-Path $root 'eng/Test-WithPostgres.ps1') -Project Unit -NoRuntimeAsync -NoBuild 6>$null | Out-Null
    Assert-Harness ($global:HarnessCapturedScripts.Count -eq 3) 'Expected three intercepted bash scripts.'
    $checkScript = $global:HarnessCapturedScripts[0]
    $testScript = $global:HarnessCapturedScripts[1]
    $alternateScript = $global:HarnessCapturedScripts[2]
    Assert-Harness ($checkScript -match 'flock -n 9' -and $testScript -match 'flock -n 9') 'Check/test output locking is missing.'
    Assert-Harness ($testScript -match 'trap cleanup EXIT') 'Disposable resource cleanup is missing.'
    Assert-Harness ($testScript -notmatch '/tmp/pcpg-env') 'Test credentials escaped into WSL host temp files.'
    Assert-Harness ($testScript -match "PC_INNER_SCRIPT='([^']+)'") 'Encoded inner test script is missing.'
    $inner = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Matches[1]))
    Assert-Harness ($inner.Contains((ConvertTo-BashLiteral $runnerFilter))) 'Shell filter quoting changed.'
    Assert-Harness ($inner.Contains('exe="/build/bin/$name/release/$name"') -and $inner.Contains('for suite in Component; do') -and $inner.Contains('-result-ctrf')) 'Test executables are not run directly with TRX/CTRF results.'
    Assert-Harness ($inner.Contains('NO TESTS RAN')) 'A filter that matches no test is reported as a pass.'
    Assert-Harness ($testScript.Contains("'$($context.BuildRoot)':/build") -and $checkScript.Contains("$($context.BuildRoot):/build")) 'Build/test do not use the same persistent Linux build volume.'
    Assert-Harness ($testScript.Contains("$($context.CacheRoot):/cache") -and $checkScript.Contains("$($context.CacheRoot):/cache")) 'SDK cache mount is missing.'
    foreach ($laneScript in @($checkScript, $testScript, $alternateScript)) {
        Assert-Harness ($laneScript.Contains('NUGET_SCRATCH=/cache/nuget-scratch') -and $laneScript.Contains("$($context.CacheRoot):/cache")) 'Restore lanes do not share NuGet scratch locks with their package cache.'
    }
    $composeDev = Get-Content (Join-Path $root 'compose.dev.yaml') -Raw
    Assert-Harness ($composeDev -match 'NUGET_SCRATCH: /cache/nuget-scratch' -and $composeDev -match 'linux-sdk-cache:/cache' -and $composeDev -match 'name: \$\{PREMIERECALENDAR_SDK_CACHE:') 'Watcher NuGet scratch locks do not use the helper-provided SDK cache volume.'
    Assert-Harness ($testScript -notmatch 'docker (volume rm|volume prune|system prune)' -and $checkScript -notmatch 'docker (volume rm|volume prune|system prune)') 'Ordinary checks discard reusable caches.'
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
            $simulation = $testScript.Replace($linuxRoot, $linuxSandbox)
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
            # The sandbox is this test's own: remove it so repeated runs leave nothing behind.
            if ($sandbox.StartsWith((Join-Path $root 'artifacts/harness-tests') + [IO.Path]::DirectorySeparatorChar)) { Remove-Item -LiteralPath $sandbox -Recurse -Force }
        }
    } else { Write-Warning 'Git Bash is absent: generated bash syntax validation was not performed.' }
} finally {
    Remove-Item Function:\wsl.exe
    Remove-Variable HarnessCapturedScripts, HarnessWslCalls -Scope Global
}
[xml] $properties = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
Assert-Harness ($properties.Project.PropertyGroup.ArtifactsPath.InnerText -like '*artifacts/build/host') 'Native build output escaped the checkout.'
Write-Host "$count harness checks passed; WSL/Docker commands were intercepted, not executed."
