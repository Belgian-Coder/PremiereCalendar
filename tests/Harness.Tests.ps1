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
$global:HarnessCapturedArgv = [Collections.Generic.List[object]]::new()
$global:HarnessRunningDistros = @('Ubuntu-24.04', 'PC-MARC-Dev')
$global:HarnessRunningListExit = 0
function global:wsl.exe {
    $CommandArguments = @($args)
    $global:HarnessWslCalls++
    $global:HarnessCapturedArgv.Add($CommandArguments)
    if ($CommandArguments -contains '--list') {
        if ($global:HarnessRunningListExit -ne 0) { $global:LASTEXITCODE = $global:HarnessRunningListExit; return }
        $global:HarnessRunningDistros
    } elseif ($CommandArguments -contains 'wslpath') {
        $path = $CommandArguments[-1]
        if ($path -match '^([A-Za-z]):/(.*)$') {
            $drive = $Matches[1].ToLowerInvariant(); $relative = $Matches[2]
            "/mnt/$drive/$relative"
        } elseif ($path.StartsWith('/')) { $path }
        else { throw "Unexpected mock path: $path" }
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
    $bounded = & (Join-Path $root 'eng/Test-WithPostgres.ps1') -Project All -Cpus 3 -BuildMaxCpuCount 3 -MaxThreads 3 -Plan
    $sharedCacheA = & (Join-Path $root 'eng/Test-WithPostgres.ps1') -Project Unit -NugetVolume fixture-linux-amd64-nuget -Plan
    $sharedCacheB = & (Join-Path $root 'eng/Test-WithPostgres.ps1') -Project Component -NugetVolume fixture-linux-amd64-nuget -NoRuntimeAsync -Plan
    $portable = & (Join-Path $root 'eng/wsl-docker.ps1') test -Project All -WslDistribution PC-MARC-Dev -DevImage prepared-browser:local -CacheFlavor glibc -NoRuntimeAsync -Cpus 3 -BuildMaxCpuCount 3 -MaxThreads 3 -Plan
    $watch = & (Join-Path $root 'eng/wsl-docker.ps1') up -Plan
    $checkPortable = & (Join-Path $root 'eng/wsl-docker.ps1') check -WslDistribution PC-MARC-Dev -BuildVolume fixture-build -NugetVolume fixture-packages -Cpus 3 -BuildMaxCpuCount 3 -Plan
    Assert-Harness ($global:HarnessWslCalls -eq 0) '-Plan invoked WSL.'
    Assert-Harness ($plan.Projects.Count -eq 1 -and $plan.Projects[0] -eq 'ComponentTests') 'Scoped project selection changed.'
    Assert-Harness ($plan.RunnerArguments -contains $runnerFilter) 'The exact filter was not preserved.'
    Assert-Harness ($plan.Build -and $plan.BuildArguments -notcontains '--no-restore') 'Default checks bypass build/restore validation.'
    Assert-Harness ($plan.Results.StartsWith((Join-Path $root 'artifacts/test-results') + [IO.Path]::DirectorySeparatorChar)) 'Test results escaped this checkout''s artifacts folder.'
    Assert-Harness (@($default.Projects) -join ',' -eq 'UnitTests,ComponentTests,IntegrationTests') 'Default test projects changed.'
    Assert-Harness (@($suiteAlias.Projects) -join ',' -eq 'UnitTests,IntegrationTests') '-Suite alias or short suite names regressed.'
    Assert-Harness (@($suiteAlias.RunnerArguments) -join ' ' -eq '-method *ViewSync* -method *Wikimedia* -parallelMode none -maxThreads 2') 'Filter/parallel runner arguments changed.'
    Assert-Harness (@($query.RunnerArguments) -join ' ' -eq '-filter /*/*/PostgresResponseCacheTests/* -maxThreads 2 -longRunning 30') 'Query filter / runner arguments changed.'
    Assert-Harness ($query.BuildArguments -contains '--no-restore' -and $query.BuildStorage -like 'container-local*') '-NoRestore / -FreshBuild regressed.'
    Assert-Harness ($viaDocker.Projects.Count -eq 1 -and $viaDocker.Projects[0] -eq 'UnitTests' -and $viaDocker.RunnerArguments -contains '*ViewSync*') 'wsl-docker test no longer forwards -Project/-Filter.'
    Assert-Harness ($bounded.Projects.Count -eq 4 -and $bounded.Cpus -eq 3 -and $bounded.BuildArguments -contains '-maxcpucount:3' -and $bounded.RunnerArguments -contains '3') 'Worker budgets changed the complete suite roster or were not propagated.'
    Assert-Harness ($sharedCacheA.PackageStorage -eq $sharedCacheB.PackageStorage -and $sharedCacheA.NugetScratch -eq $sharedCacheB.NugetScratch -and $sharedCacheA.NugetScratch -eq $plan.NugetScratch) 'Shared named packages use different extraction-lock namespaces.'
    Assert-Harness ($sharedCacheA.NugetHttpCache -eq $sharedCacheB.NugetHttpCache -and $sharedCacheA.NugetHttpCache -eq '/root/.nuget/packages/.http-cache' -and $check.NugetHttpCache -eq $plan.NugetHttpCache) 'Package consumers do not share the HTTP cache location.'
    Assert-Harness ($check.Command.Contains("NUGET_SCRATCH=$($plan.NugetScratch)")) 'Scoped checks and default shared packages use different extraction-lock namespaces.'
    Assert-Harness ($plan.BuildStorage -eq $check.BuildStorage -and $plan.BuildStorage -match '-linux-amd64-musl-async-check$') 'Test/check default Linux volumes differ.'
    Assert-Harness ($default.Cpus -eq 2 -and $default.BuildArguments -contains '-maxcpucount:2' -and $default.RunnerArguments -contains '2') 'Default shared-host budgets changed.'
    Assert-Harness (-not (Test-Path -LiteralPath $plan.Results)) 'Plan created its result directory.'
    Assert-Harness ($portable.Projects.Count -eq 4 -and $portable.WslDistribution -eq 'PC-MARC-Dev' -and $portable.CacheFlavor -eq 'glibc' -and $portable.Cpus -eq 3 -and $portable.BuildArguments -contains '-maxcpucount:3' -and $portable.RunnerArguments -contains '3') 'Local test forwarding lost profile/budgets or changed All.'
    Assert-Harness ($portable.BuildStorage -ne $plan.BuildStorage -and $portable.PackageStorage -eq $plan.PackageStorage) 'ABI isolation or shared project packages regressed.'
    Assert-Harness ($portable.BuildStorage -match '-glibc-sync-check$' -and $portable.BuildArguments -contains '-p:UseRuntimeAsync=false') 'Local test forwarding lost runtime-async mode.'
    Assert-Harness ($watch.BuildStorage -ne $plan.BuildStorage -and $watch.BuildStorage -match '-watch$' -and $watch.PackageStorage -eq $plan.PackageStorage) 'Watcher state is not separate with shared packages.'
    Assert-Harness ($checkPortable.BuildStorage -eq 'fixture-build' -and $checkPortable.PackageStorage -eq 'fixture-packages' -and $checkPortable.Command -match '--cpus=3' -and $checkPortable.Command -match '-maxcpucount:3') 'Scoped check override/budget propagation failed.'
    $primaryStorage = Get-HarnessStorage $primaryContext
    $sameStorage = Get-HarnessStorage (Get-HarnessContext $root)
    Assert-Harness ($sameStorage.BuildVolume -eq $plan.BuildStorage -and $primaryStorage.NugetVolume -eq $plan.PackageStorage) 'Volume names are unstable or packages are not project-scoped.'
    if ($root -ne $primary) { Assert-Harness ($primaryStorage.BuildVolume -ne $plan.BuildStorage) 'Sibling worktrees share build volumes.' }
    $rejected = $false
    try { & (Join-Path $root 'eng/Test-WithPostgres.ps1') -Project Browser -DevImage prepared-browser:local -Plan | Out-Null } catch { $rejected = $true }
    Assert-Harness $rejected 'Custom prepared images silently assume a build ABI.'
    $sandbox = Join-Path $root ('artifacts/harness-tests/release-plan-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $sandbox -Force | Out-Null
    try {
        [IO.File]::WriteAllText((Join-Path $sandbox 'release-config.json'), '{"schema":1,"bundleName":"fixture","version":"0.0.0","imageTag":"fixture","resourcePrefix":"fixture"}')
        $callsBeforeReleasePlan = $global:HarnessWslCalls
        $releasePlan = & (Join-Path $root 'eng/release/Invoke-ReleaseTests.ps1') -Bundle $sandbox -ExpectedRevision ('a' * 40) -WslDistribution PC-MARC-Dev -WhatIf 6>&1
        $releasePlanText = ($releasePlan | ForEach-Object { "$_" }) -join "`n"
        Assert-Harness ($global:HarnessWslCalls -eq $callsBeforeReleasePlan -and @(Get-ChildItem -LiteralPath $sandbox -Force).Count -eq 1) 'Canonical release test plan invoked WSL or created evidence.'
        Assert-Harness ($releasePlanText.Contains($plan.BuildStorage) -and $releasePlanText.Contains($plan.PackageStorage) -and $releasePlanText.Contains('WslDistribution=PC-MARC-Dev')) 'Canonical release test plan did not resolve default local storage/profile.'
    } finally {
        if ($sandbox.StartsWith((Join-Path $root 'artifacts/harness-tests') + [IO.Path]::DirectorySeparatorChar)) { Remove-Item -LiteralPath $sandbox -Recurse -Force }
    }
    $rejected = $false
    try { & (Join-Path $root 'eng/Test-WithPostgres.ps1') -Filter 'FullyQualifiedName~A&Name~B' -Plan | Out-Null } catch { $rejected = $true }
    Assert-Harness $rejected 'VSTest filter expressions are silently accepted.'
    Assert-Harness ($alternate.BuildStorage -ne $plan.BuildStorage) 'Runtime-async modes share build state.'
    Assert-Harness ($check.Command -match 'dotnet build PremiereCalendar/PremiereCalendar.csproj') 'App check expands to the full solution.'
    & (Join-Path $root 'eng/wsl-docker.ps1') check
    & (Join-Path $root 'eng/Test-WithPostgres.ps1') -Project ComponentTests -Filter $filter -WslDistribution PC-MARC-Dev 6>$null | Out-Null
    Assert-Harness ($global:HarnessCapturedScripts.Count -eq 2) 'Expected two intercepted bash scripts.'
    $checkScript = $global:HarnessCapturedScripts[0]
    $testScript = $global:HarnessCapturedScripts[1]
    Assert-Harness ($global:HarnessCapturedArgv.Count -eq 8) 'Named-volume execution unexpectedly converts mutable host cache paths.'
    Assert-Harness (($global:HarnessCapturedArgv[1][0..4] -join '|') -eq '-d|Ubuntu-24.04|--|wslpath|-a' -and ($global:HarnessCapturedArgv[3] -join '|') -eq '-d|Ubuntu-24.04|--|bash|-s') 'Default distro did not reach the actual WSL argv boundary.'
    Assert-Harness (($global:HarnessCapturedArgv[5][0..4] -join '|') -eq '-d|PC-MARC-Dev|--|wslpath|-a' -and ($global:HarnessCapturedArgv[7] -join '|') -eq '-d|PC-MARC-Dev|--|bash|-s') 'Explicit distro did not reach both WSL operations.'
    $callsBeforeStopped = $global:HarnessWslCalls
    $global:HarnessRunningDistros = @()
    foreach ($operation in @('path', 'bash')) {
        $rejected = $false
        try {
            if ($operation -eq 'path') { ConvertTo-WslPath $root -WslDistribution PC-MARC-Dev | Out-Null }
            else { Invoke-HarnessWsl 'true' -WslDistribution PC-MARC-Dev }
        } catch { $rejected = $true }
        Assert-Harness $rejected 'A stopped distribution was allowed to start implicitly.'
    }
    Assert-Harness ($global:HarnessWslCalls -eq $callsBeforeStopped + 2 -and ($global:HarnessCapturedArgv[-1] -join '|') -eq '--list|--running|--quiet') 'Stopped-distro refusal invoked a distro transport.'
    $global:HarnessRunningDistros = @("pC-mARC-dEV`0")
    $normalizedPath = ConvertTo-WslPath $root -WslDistribution PC-MARC-Dev
    Assert-Harness (-not [string]::IsNullOrWhiteSpace($normalizedPath)) 'Running-distro matching lost NUL/case normalization.'
    $global:HarnessRunningListExit = 1
    $callsBeforeListFailure = $global:HarnessWslCalls
    $rejected = $false
    try { ConvertTo-WslPath $root -WslDistribution PC-MARC-Dev | Out-Null } catch { $rejected = $_.Exception.Message.Contains('--list --running --quiet') }
    Assert-Harness ($rejected -and $global:HarnessWslCalls -eq $callsBeforeListFailure + 1) 'A failed running-distro query invoked transport or lacked an actionable error.'
    $global:HarnessRunningListExit = 0
    $global:HarnessRunningDistros = @('Ubuntu-24.04', 'PC-MARC-Dev')
    foreach ($captured in @($checkScript, $testScript)) {
        Assert-Harness ($captured.StartsWith('unset DOCKER_CONTEXT; export DOCKER_HOST=unix:///var/run/docker.sock')) 'Docker calls can inherit a remote context.'
        Assert-Harness ($captured.Contains('NUGET_SCRATCH=/root/.nuget/packages/.scratch')) 'Shared NuGet packages have isolated extraction locks.'
        Assert-Harness ($captured.Contains('NUGET_HTTP_CACHE_PATH=/root/.nuget/packages/.http-cache')) 'Disposable checks discard the shared NuGet HTTP cache.'
    }
    $compose = Get-Content -LiteralPath (Join-Path $root 'compose.dev.yaml') -Raw
    Assert-Harness ($compose -match 'NUGET_SCRATCH: /root/\.nuget/packages/\.scratch') 'Development restores do not share the package-cache lock namespace.'
    Assert-Harness ($compose -match 'NUGET_HTTP_CACHE_PATH: /root/\.nuget/packages/\.http-cache') 'Watcher restores do not share the NuGet HTTP cache.'
    Assert-Harness ($compose -match '(?s)nuget-cache:\s+external: true\s+name: \$\{PREMIERECALENDAR_NUGET_VOLUME:' -and $compose -match '(?s)watch-build:\s+external: true\s+name: \$\{PREMIERECALENDAR_BUILD_VOLUME:') 'Compose cache mapping/reset no longer preserves external shared caches.'
    Assert-Harness ($checkScript -match 'flock -n 9' -and $testScript -match 'flock -n 9') 'Check/test output locking is missing.'
    Assert-Harness ($testScript -match 'trap cleanup EXIT') 'Disposable resource cleanup is missing.'
    Assert-Harness ($testScript -notmatch '/tmp/pcpg-env') 'Test credentials escaped into WSL host temp files.'
    Assert-Harness ($testScript -match "PC_INNER_SCRIPT='([^']+)'") 'Encoded inner test script is missing.'
    $inner = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Matches[1]))
    Assert-Harness ($inner.Contains((ConvertTo-BashLiteral $runnerFilter))) 'Shell filter quoting changed.'
    Assert-Harness ($inner.Contains('exe="/build/bin/$name/release/$name"') -and $inner.Contains('for suite in Component; do') -and $inner.Contains('-result-ctrf')) 'Test executables are not run directly with TRX/CTRF results.'
    Assert-Harness ($inner.Contains('NO TESTS RAN')) 'A filter that matches no test is reported as a pass.'
    Assert-Harness ($testScript.Contains("'$($plan.BuildStorage)':/build") -and $testScript.Contains("'$($plan.PackageStorage)':/root/.nuget/packages") -and $testScript -notmatch '/artifacts/(build|packages)/linux') 'Default mutable Linux caches are not named volume mounts.'
    Assert-Harness ($testScript.Contains('"$dev_image" sh -c') -and $testScript.Contains('"$postgres_image" -c') -and $checkScript.Contains('"$dev_image" dotnet build')) 'An inspected image identity is not pinned for execution.'
    Assert-Harness ($testScript.Contains('--pull=never') -and $checkScript.Contains('--pull=never')) 'Execution may implicitly pull an image.'
    # Invoke the canonical wrapper's actual child-launch function with only the
    # child executable intercepted. Release/NAS transports are never loaded.
    $releaseTokens = $null; $releaseErrors = $null
    $releaseAst = [Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'eng/release/Invoke-ReleaseTests.ps1'), [ref]$releaseTokens, [ref]$releaseErrors)
    Assert-Harness ($releaseErrors.Count -eq 0) 'Canonical release test wrapper failed parsing.'
    $launchFunction = $releaseAst.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-TestRunner' }, $true)
    $sandbox = Join-Path $root ('artifacts/harness-tests/release-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $sandbox -Force | Out-Null
    $global:HarnessReleaseArgv = @()
    try {
        $releaseRun = & {
            function Get-Command { [pscustomobject]@{ Source = 'Invoke-FakeHarnessChild' } }
            function Invoke-FakeHarnessChild {
                $global:HarnessReleaseArgv = @($args)
                $global:LASTEXITCODE = 0
                'Total wall time: 0 s. Results: fixture-results'
            }
            $WslDistribution = 'PC-MARC-Dev'; $Cpus = 3; $MaxThreads = '3'; $BuildMaxCpuCount = 3
            $BuildVolume = 'fixture-build'; $NugetVolume = 'fixture-packages'; $testScript = 'fixture-test-runner.ps1'
            . ([scriptblock]::Create($launchFunction.Extent.Text))
            Invoke-TestRunner -LogPath (Join-Path $sandbox 'test-run.log')
        }
        Assert-Harness ($releaseRun.ExitCode -eq 0 -and $releaseRun.ResultsFolder -eq 'fixture-results') 'Release wrapper child result handling changed.'
        $releaseWords = $global:HarnessReleaseArgv -join '|'
        Assert-Harness ($releaseWords -match '-WslDistribution:?\|PC-MARC-Dev' -and $releaseWords -match '-BuildVolume:?\|fixture-build' -and $releaseWords -match '-NugetVolume:?\|fixture-packages') 'Canonical test wrapper lost distribution/storage forwarding.'
        Assert-Harness ($releaseWords -match '-Cpus\|3' -and $releaseWords -match '-MaxThreads\|3' -and $releaseWords -match '-BuildMaxCpuCount\|3' -and $global:HarnessReleaseArgv -notcontains '-Project') 'Canonical test wrapper changed full roster/budget forwarding.'
    } finally {
        Remove-Variable HarnessReleaseArgv -Scope Global
        if ($sandbox.StartsWith((Join-Path $root 'artifacts/harness-tests') + [IO.Path]::DirectorySeparatorChar)) { Remove-Item -LiteralPath $sandbox -Recurse -Force }
    }
    # Exercise the real entry point from an isolated checkout-shaped directory;
    # never inspect, create or change this checkout's actual development secret.
    $sandbox = Join-Path $root ('artifacts/harness-tests/secret-actions-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path (Join-Path $sandbox 'eng') -Force | Out-Null
    try {
        foreach ($name in @('wsl-docker.ps1', 'Harness.Common.ps1')) {
            Copy-Item -LiteralPath (Join-Path $root "eng/$name") -Destination (Join-Path $sandbox "eng/$name")
        }
        Copy-Item -LiteralPath (Join-Path $root 'compose.dev.yaml') -Destination (Join-Path $sandbox 'compose.dev.yaml')
        $entry = Join-Path $sandbox 'eng/wsl-docker.ps1'
        & $entry build 6>$null | Out-Null
        Assert-Harness (-not (Test-Path -LiteralPath (Join-Path $sandbox '.secrets'))) 'Image-only build created development authentication material.'
        & $entry up 6>$null | Out-Null
        $secret = Join-Path $sandbox '.secrets/dev-postgres-password'
        Assert-Harness ((Get-Item -LiteralPath $secret).Length -eq 48) 'Explicit up did not create the development password in its own checkout.'
        $secretHash = (Get-FileHash -LiteralPath $secret -Algorithm SHA256).Hash
        & $entry up 6>$null | Out-Null
        Assert-Harness ((Get-FileHash -LiteralPath $secret -Algorithm SHA256).Hash -eq $secretHash) 'Explicit up overwrote an existing development password.'
    } finally {
        $sandboxPath = [IO.Path]::GetFullPath($sandbox)
        $sandboxParent = [IO.Path]::GetFullPath((Join-Path $root 'artifacts/harness-tests')) + [IO.Path]::DirectorySeparatorChar
        if (-not $sandboxPath.StartsWith($sandboxParent) -or (Get-Item -LiteralPath $sandboxPath).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'Preserving an unexpected secret-action test sandbox path.'
        }
        Remove-Item -LiteralPath $sandboxPath -Recurse -Force
    }
    $bash = if ($IsWindows) { Join-Path $env:ProgramFiles 'Git/bin/bash.exe' } else {
        $bashCommand = Get-Command bash -ErrorAction SilentlyContinue
        if ($bashCommand) { $bashCommand.Source }
    }
    if ($bash -and (Test-Path -LiteralPath $bash)) {
        foreach ($shellScript in @($checkScript, $testScript, $inner)) {
            ($shellScript -replace "`r`n", "`n") | & $bash -n
            Assert-Harness ($LASTEXITCODE -eq 0) 'Generated bash failed syntax validation.'
        }
        # Execute the lifecycle with a shell-function Docker double. All paths
        # point into this project's sandbox; no Docker executable can be called.
        foreach ($expectedExit in @(0, 7)) {
            $sandbox = Join-Path $root ('artifacts/harness-tests/lifecycle-' + [guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $sandbox -Force | Out-Null
            $linuxSandbox = if ($IsWindows) { '/' + $sandbox.Substring(0, 1).ToLowerInvariant() + '/' + ($sandbox.Substring(3) -replace '\\', '/') } else { $sandbox }
            $linuxRoot = if ($IsWindows) { '/mnt/' + $root.Substring(0, 1).ToLowerInvariant() + '/' + ($root.Substring(3) -replace '\\', '/') } else { $root }
            $linuxPackages = if ($IsWindows) { '/mnt/' + $context.PackageRoot.Substring(0, 1).ToLowerInvariant() + '/' + ($context.PackageRoot.Substring(3) -replace '\\', '/') } else { $context.PackageRoot }
            $simulation = $testScript.Replace($linuxPackages, "$linuxSandbox/packages").Replace($linuxRoot, $linuxSandbox)
            $dockerDouble = @"
flock() { return 0; }
docker() {
  printf '%s %s\n' "`$1" "`${2-}" >> '$linuxSandbox/docker-calls.txt'
  if [ "`$1" = info ]; then echo linux/amd64; return 0; fi
  if [ "`$1" = image ]; then echo 'linux/amd64 sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'; return 0; fi
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
        # Exercise the emitted preflight, rather than matching its implementation.
        # Every rejected platform/identity must fail before ANY project resource.
        $validImage = 'linux/amd64 sha256:' + ('a' * 64)
        $rejections = @(
            @{ Engine = 'windows/amd64'; Image = $validImage; InfoExit = 0; ImageExit = 0; Code = 26 },
            @{ Engine = 'linux/aarch64'; Image = $validImage; InfoExit = 0; ImageExit = 0; Code = 26 },
            @{ Engine = ''; Image = $validImage; InfoExit = 1; ImageExit = 0; Code = 25 },
            @{ Engine = 'linux/amd64'; Image = 'windows/amd64 sha256:' + ('a' * 64); InfoExit = 0; ImageExit = 0; Code = 27 },
            @{ Engine = 'linux/amd64'; Image = 'linux/arm64 sha256:' + ('a' * 64); InfoExit = 0; ImageExit = 0; Code = 27 },
            @{ Engine = 'linux/amd64'; Image = 'linux/amd64 sha256:' + ('z' * 64); InfoExit = 0; ImageExit = 0; Code = 27 },
            @{ Engine = 'linux/amd64'; Image = ''; InfoExit = 0; ImageExit = 1; Code = 20 }
        )
        foreach ($captured in @($checkScript, $testScript)) {
            foreach ($rejection in $rejections) {
                $sandbox = Join-Path $root ('artifacts/harness-tests/preflight-' + [guid]::NewGuid().ToString('N'))
                New-Item -ItemType Directory -Path $sandbox -Force | Out-Null
                $linuxSandbox = if ($IsWindows) { '/' + $sandbox.Substring(0, 1).ToLowerInvariant() + '/' + ($sandbox.Substring(3) -replace '\\', '/') } else { $sandbox }
                $linuxRoot = if ($IsWindows) { '/mnt/' + $root.Substring(0, 1).ToLowerInvariant() + '/' + ($root.Substring(3) -replace '\\', '/') } else { $root }
                $simulation = $captured.Replace($linuxRoot, $linuxSandbox)
                $dockerDouble = @"
docker() {
  printf '%s\n' "`$1" >> '$linuxSandbox/docker-calls.txt'
  if [ "`$1" = info ]; then echo '$($rejection.Engine)'; return $($rejection.InfoExit); fi
  if [ "`$1" = image ]; then echo '$($rejection.Image)'; return $($rejection.ImageExit); fi
  echo 'Unexpected project resource operation' >&2; return 99
}
"@
                (($dockerDouble + "`n" + $simulation) -replace "`r`n", "`n") | & $bash -s 2>$null
                Assert-Harness ($LASTEXITCODE -eq $rejection.Code) 'Platform/identity preflight did not reject the execution double.'
                $calls = @(Get-Content -LiteralPath (Join-Path $sandbox 'docker-calls.txt'))
                Assert-Harness (@($calls | Where-Object { $_ -notin 'info', 'image' }).Count -eq 0 -and -not (Test-Path -LiteralPath (Join-Path $sandbox 'artifacts'))) 'Rejected preflight created project resources or output state.'
                if ($sandbox.StartsWith((Join-Path $root 'artifacts/harness-tests') + [IO.Path]::DirectorySeparatorChar)) { Remove-Item -LiteralPath $sandbox -Recurse -Force }
            }
        }
    } else { Write-Warning 'Bash is absent: generated bash syntax and lifecycle validation were not performed.' }
} finally {
    Remove-Item Function:\wsl.exe
    Remove-Variable HarnessCapturedScripts, HarnessWslCalls, HarnessCapturedArgv, HarnessRunningDistros, HarnessRunningListExit -Scope Global
}
[xml] $properties = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
Assert-Harness ($properties.Project.PropertyGroup.ArtifactsPath.InnerText -like '*artifacts/build/host') 'Native build output escaped the checkout.'
Write-Host "$count harness checks passed; WSL/Docker commands were intercepted, not executed."
