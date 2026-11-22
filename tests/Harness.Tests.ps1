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
Assert-Harness ((Get-HarnessStorage $context).BuildVolume -match '^pc-[0-9a-f]{10}-[0-9a-f]{10}-linux-amd64-musl-async-check$') 'Build storage lost its owned checkout/ABI/mode/lane identity.'
$common = & git -C $root rev-parse --path-format=absolute --git-common-dir
$primary = Split-Path -Parent $common
Assert-Harness ((Get-HarnessStorage $context).NugetVolume -eq (Get-HarnessStorage (Get-HarnessContext $primary)).NugetVolume) 'Pure package storage is not shared through the validated primary.'
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
# Generate the real scripts with only the native transport replaced. The owned
# process implementation is exercised separately by HarnessProcess.Tests.ps1.
$transportSandbox = Join-Path $root ('artifacts/harness-tests/transport-' + [guid]::NewGuid().ToString('N'))
[void] (New-Item -ItemType Directory -Path $transportSandbox)
$fixtureCommon = Join-Path $transportSandbox 'Harness.Common.ps1'
$mockTransport = @'
function New-HarnessWslStartInfo([string[]] $Arguments, [switch] $UnicodeOutput) {
    $start = [Diagnostics.ProcessStartInfo]::new('wsl.exe')
    foreach ($word in $Arguments) { $start.ArgumentList.Add($word) }
    $start
}
function Invoke-HarnessWslClient($StartInfo, [string] $InputText = '', [double] $TimeoutSeconds = 5, [switch] $CaptureOutput) {
    $words = @($StartInfo.ArgumentList)
    $text = if ($words -contains '-s') { $InputText | wsl.exe @words } else { wsl.exe @words }
    [pscustomobject]@{ Output = ($text -join "`n"); ExitCode = $global:LASTEXITCODE; TimedOut = $false; OutputDrained = $true; InputFailed = $false; ClientPid = 0 }
}
'@
$commonText = Get-Content -LiteralPath (Join-Path $root 'eng/Harness.Common.ps1') -Raw
$commonText = $commonText.Replace('. (Join-Path $PSScriptRoot ''Wsl.Transport.ps1'')', '')
$commonText = (Get-Content -LiteralPath (Join-Path $root 'eng/Wsl.Transport.ps1') -Raw) + "`n" + $commonText
$tokens = $null; $errors = $null
$commonAst = [Management.Automation.Language.Parser]::ParseInput($commonText, [ref]$tokens, [ref]$errors)
$replace = @($commonAst.FindAll({ param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -in 'New-HarnessWslStartInfo', 'Invoke-HarnessWslClient' }, $false))
foreach ($node in ($replace | Sort-Object { $_.Extent.StartOffset } -Descending)) { $commonText = $commonText.Remove($node.Extent.StartOffset, $node.Extent.EndOffset - $node.Extent.StartOffset) }
$commonText = $mockTransport + "`n" + $commonText
[IO.File]::WriteAllText($fixtureCommon, $commonText)
. $fixtureCommon
function Invoke-HarnessFixtureScript([string] $Path, [hashtable] $Parameters) {
    $text = Get-Content -LiteralPath $Path -Raw
    $text = $text.Replace('. (Join-Path $PSScriptRoot ''Harness.Common.ps1'')', ('. ' + "'" + $fixtureCommon.Replace("'", "''") + "'"))
    $t = $null; $e = $null
    $ast = [Management.Automation.Language.Parser]::ParseInput($text, [ref]$t, [ref]$e)
    $text = $text.Insert($ast.ParamBlock.Extent.EndOffset, ("`n`$PSScriptRoot = '" + (Split-Path -Parent $Path).Replace("'", "''") + "'`n"))
    & ([scriptblock]::Create($text)) @Parameters
}
$bashSandboxes = [Collections.Generic.List[string]]::new()
function New-BashSandbox([string] $Prefix) {
    $path = Join-Path $root ("artifacts/harness-tests/$Prefix-" + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $path -Force | Out-Null
    $bashSandboxes.Add($path)
    $path
}
try {
    # -Filter takes test-name substrings (xUnit.net 4 runs the test executables with -method *text*); the shell
    # metacharacters must reach the runner as one literal argument.
    $filter = 'FullyQualifiedName~Calendar.A''B$(touch NEVER)'
    $runnerFilter = '*Calendar.A''B$(touch NEVER)*'
    # -Plan invocations are independent and read-only, so they run concurrently (each costs about a second of
    # script load plus one Git read). A parallel runspace cannot see the intercepted wsl.exe above, so each defines
    # its own counting double: any WSL call from a plan is still detected and the real executable is never reached.
    $planSpecs = [ordered]@{
        plan = @('eng/Test-WithPostgres.ps1', @{ Project = 'ComponentTests'; Filter = $filter; Plan = $true })
        check = @('eng/wsl-docker.ps1', @{ Action = 'check'; Plan = $true })
        alternate = @('eng/Test-WithPostgres.ps1', @{ Project = 'UnitTests'; NoRuntimeAsync = $true; Plan = $true })
        default = @('eng/Test-WithPostgres.ps1', @{ Plan = $true })
        suiteAlias = @('eng/Test-WithPostgres.ps1', @{ Suite = @('Unit', 'Integration'); Filter = @('ViewSync', 'Wikimedia'); Parallel = 'none'; MaxThreads = 2; Plan = $true })
        query = @('eng/Test-WithPostgres.ps1', @{ Project = 'Unit'; QueryFilter = '/*/*/PostgresResponseCacheTests/*'; RunnerArguments = @('-longRunning', '30'); FreshBuild = $true; NoRestore = $true; Plan = $true })
        viaDocker = @('eng/wsl-docker.ps1', @{ Action = 'test'; Project = 'Unit'; Filter = 'ViewSync'; Plan = $true })
        bounded = @('eng/Test-WithPostgres.ps1', @{ Project = 'All'; Cpus = 3; BuildMaxCpuCount = 3; MaxThreads = 3; Plan = $true })
        sharedCacheA = @('eng/Test-WithPostgres.ps1', @{ Project = 'Unit'; NugetVolume = 'fixture-linux-amd64-nuget'; Plan = $true })
        sharedCacheB = @('eng/Test-WithPostgres.ps1', @{ Project = 'Component'; NugetVolume = 'fixture-linux-amd64-nuget'; NoRuntimeAsync = $true; Plan = $true })
        portable = @('eng/wsl-docker.ps1', @{ Action = 'test'; Project = 'All'; WslDistribution = 'PC-MARC-Dev'; DevImage = 'prepared-browser:local'; CacheFlavor = 'glibc'; NoRuntimeAsync = $true; Cpus = 3; BuildMaxCpuCount = 3; MaxThreads = 3; TimeoutSeconds = 2400; Plan = $true })
        watch = @('eng/wsl-docker.ps1', @{ Action = 'up'; Plan = $true })
        checkPortable = @('eng/wsl-docker.ps1', @{ Action = 'check'; WslDistribution = 'PC-MARC-Dev'; BuildVolume = 'fixture-build'; NugetVolume = 'fixture-packages'; Cpus = 3; BuildMaxCpuCount = 3; Plan = $true })
    }
    $planWslCalls = [Collections.Concurrent.ConcurrentQueue[string]]::new()
    $planResults = @($planSpecs.Keys | ForEach-Object -ThrottleLimit 6 -Parallel {
        $name = $_
        $spec = ($using:planSpecs)[$name]
        $global:PlanWslCalls = $using:planWslCalls
        function global:wsl.exe { $global:PlanWslCalls.Enqueue("$args"); $global:LASTEXITCODE = 1 }
        $script = Join-Path ($using:root) $spec[0]
        $parameters = $spec[1]
        [pscustomobject]@{ Name = $name; Value = (& $script @parameters) }
    })
    Assert-Harness ($planResults.Count -eq $planSpecs.Count) 'A -Plan invocation did not return a result.'
    $planned = @{}
    foreach ($result in $planResults) { $planned[$result.Name] = $result.Value }
    $plan = $planned.plan; $check = $planned.check; $alternate = $planned.alternate; $default = $planned.default
    $suiteAlias = $planned.suiteAlias; $query = $planned.query; $viaDocker = $planned.viaDocker; $bounded = $planned.bounded
    $sharedCacheA = $planned.sharedCacheA; $sharedCacheB = $planned.sharedCacheB; $portable = $planned.portable
    $watch = $planned.watch; $checkPortable = $planned.checkPortable
    Assert-Harness ($global:HarnessWslCalls -eq 0 -and $planWslCalls.Count -eq 0) '-Plan invoked WSL.'
    Assert-Harness ($plan.Projects.Count -eq 1 -and $plan.Projects[0] -eq 'ComponentTests') 'Scoped project selection changed.'
    Assert-Harness ($plan.RunnerArguments -contains $runnerFilter) 'The exact filter was not preserved.'
    Assert-Harness ($plan.Build -and $plan.BuildArguments -notcontains '--no-restore') 'Default checks bypass build/restore validation.'
    Assert-Harness ($plan.Results.StartsWith((Join-Path $root 'artifacts/test-results') + [IO.Path]::DirectorySeparatorChar)) 'Test results escaped this checkout''s artifacts folder.'
    Assert-Harness (@($default.Projects) -join ',' -eq 'UnitTests,ComponentTests,IntegrationTests') 'Default test projects changed.'
    Assert-Harness (@($suiteAlias.Projects) -join ',' -eq 'UnitTests,IntegrationTests') '-Suite alias or short suite names regressed.'
    Assert-Harness (@($suiteAlias.RunnerArguments) -join ' ' -eq '-method *ViewSync* -method *Wikimedia* -parallelMode none -maxThreads 2') 'Filter/parallel runner arguments changed.'
    Assert-Harness (@($query.RunnerArguments) -join ' ' -eq '-filter /*/*/PostgresResponseCacheTests/* -maxThreads auto -longRunning 30') 'Query filter / runner arguments changed.'
    Assert-Harness ($query.BuildArguments -contains '--no-restore' -and $query.BuildStorage -like 'container-local*') '-NoRestore / -FreshBuild regressed.'
    Assert-Harness ($viaDocker.Projects.Count -eq 1 -and $viaDocker.Projects[0] -eq 'UnitTests' -and $viaDocker.RunnerArguments -contains '*ViewSync*') 'wsl-docker test no longer forwards -Project/-Filter.'
    Assert-Harness ($bounded.Projects.Count -eq 4 -and $bounded.Cpus -eq 3 -and $bounded.BuildArguments -contains '-maxcpucount:3' -and $bounded.RunnerArguments -contains '3') 'Worker budgets changed the complete suite roster or were not propagated.'
    Assert-Harness ($sharedCacheA.PackageStorage -eq $sharedCacheB.PackageStorage -and $sharedCacheA.NugetScratch -eq $sharedCacheB.NugetScratch -and $sharedCacheA.NugetScratch -eq $plan.NugetScratch) 'Shared named packages use different extraction-lock namespaces.'
    Assert-Harness ($sharedCacheA.NugetHttpCache -eq $sharedCacheB.NugetHttpCache -and $sharedCacheA.NugetHttpCache -eq '/root/.nuget/packages/.http-cache' -and $check.NugetHttpCache -eq $plan.NugetHttpCache) 'Package consumers do not share the HTTP cache location.'
    Assert-Harness ($check.Command.Contains("NUGET_SCRATCH=$($plan.NugetScratch)")) 'Scoped checks and default shared packages use different extraction-lock namespaces.'
    Assert-Harness ($plan.BuildStorage -eq $check.BuildStorage -and $plan.BuildStorage -match '-linux-amd64-musl-async-check$') 'Test/check default Linux volumes differ.'
    Assert-Harness ($default.Cpus -eq 'auto' -and $default.BuildArguments -contains '-maxcpucount:auto' -and $default.RunnerArguments -contains 'auto') 'Default budgets are no longer derived from the engine CPU count.'
    Assert-Harness (-not (Test-Path -LiteralPath $plan.Results)) 'Plan created its result directory.'
    Assert-Harness ($portable.Projects.Count -eq 4 -and $portable.WslDistribution -eq 'PC-MARC-Dev' -and $portable.CacheFlavor -eq 'glibc' -and $portable.Cpus -eq 3 -and $portable.BuildArguments -contains '-maxcpucount:3' -and $portable.RunnerArguments -contains '3') 'Local test forwarding lost profile/budgets or changed All.'
    Assert-Harness ($portable.BuildStorage -ne $plan.BuildStorage -and $portable.PackageStorage -eq $plan.PackageStorage) 'ABI isolation or shared project packages regressed.'
    Assert-Harness ($portable.BuildStorage -match '-glibc-sync-check$' -and $portable.BuildArguments -contains '-p:UseRuntimeAsync=false') 'Local test forwarding lost runtime-async mode.'
    Assert-Harness ($default.TimeoutSeconds -eq 3300 -and $plan.TimeoutSeconds -eq 1500 -and $check.TimeoutSeconds -eq 1800 -and $portable.TimeoutSeconds -eq 2400) 'Total deadlines were not explicit or forwarded.'
    Assert-Harness ($watch.BuildStorage -ne $plan.BuildStorage -and $watch.BuildStorage -match '-watch$' -and $watch.PackageStorage -eq $plan.PackageStorage) 'Watcher state is not separate with shared packages.'
    Assert-Harness ($checkPortable.BuildStorage -eq 'fixture-build' -and $checkPortable.PackageStorage -eq 'fixture-packages' -and $checkPortable.Command -match '--cpus=3' -and $checkPortable.Command -match '-maxcpucount:3') 'Scoped check override/budget propagation failed.'
    Assert-Harness ($check.Command.Contains('--cpus=$pc_budget') -and $check.Command.Contains('-maxcpucount:$pc_budget') -and $check.Cpus -eq 'auto') 'Scoped check no longer defaults to the engine-derived auto budget.'
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
    Invoke-HarnessFixtureScript (Join-Path $root 'eng/wsl-docker.ps1') @{ Action = 'check' }
    Invoke-HarnessFixtureScript (Join-Path $root 'eng/Test-WithPostgres.ps1') @{ Project = 'ComponentTests'; Filter = $filter; WslDistribution = 'PC-MARC-Dev' } 6>$null | Out-Null
    Assert-Harness ($global:HarnessCapturedScripts.Count -eq 2) 'Expected two intercepted bash scripts.'
    $checkScript = $global:HarnessCapturedScripts[0]
    $testScript = $global:HarnessCapturedScripts[1]
    Assert-Harness ($global:HarnessCapturedArgv.Count -eq 8) 'Named-volume execution unexpectedly converts mutable host cache paths.'
    Assert-Harness (($global:HarnessCapturedArgv[1][0..4] -join '|') -eq '-d|Ubuntu-24.04|--|wslpath|-a' -and ($global:HarnessCapturedArgv[3][0..4] -join '|') -eq '-d|Ubuntu-24.04|--exec|timeout|--signal=TERM' -and ($global:HarnessCapturedArgv[3][-2..-1] -join '|') -eq 'bash|-s') 'Default distro/deadline did not reach the WSL argv boundary.'
    Assert-Harness (($global:HarnessCapturedArgv[5][0..4] -join '|') -eq '-d|PC-MARC-Dev|--|wslpath|-a' -and ($global:HarnessCapturedArgv[7][0..4] -join '|') -eq '-d|PC-MARC-Dev|--exec|timeout|--signal=TERM') 'Explicit distro did not reach both WSL operations.'
    Assert-Harness ($global:HarnessCapturedArgv[3][5] -eq '--kill-after=15s' -and $global:HarnessCapturedArgv[7][5] -eq '--kill-after=120s') 'Test cleanup reserve changed another caller or did not reach the guest.'
    Invoke-HarnessFixtureScript (Join-Path $root 'eng/Test-WithPostgres.ps1') @{ Project = 'Unit'; NoRuntimeAsync = $true; NoBuild = $true } 6>$null | Out-Null
    $alternateScript = $global:HarnessCapturedScripts[2]
    foreach ($laneScript in @($checkScript, $testScript, $alternateScript)) {
        Assert-Harness ($laneScript.Contains('DOTNET_CLI_HOME=/build/.sdk-cache/dotnet') -and $laneScript.Contains('NUGET_PLUGINS_CACHE_PATH=/build/.sdk-cache/nuget-plugins')) 'SDK CLI/plugin state escaped isolated build storage.'
        Assert-Harness ($laneScript.Contains('DOTNET_GENERATE_ASPNET_CERTIFICATE=false')) 'A fresh SDK home may generate an HTTPS identity.'
        Assert-Harness ($laneScript.Contains('NUGET_SCRATCH=/root/.nuget/packages/.scratch') -and $laneScript.Contains('NUGET_HTTP_CACHE_PATH=/root/.nuget/packages/.http-cache')) 'Same-package consumers lost shared scratch/HTTP state.'
        Assert-Harness ($laneScript.Contains('sh /src/eng/run-with-sdk-cache.sh') -and $laneScript.Contains('PC_SDK_CACHE_OWNER=')) 'An SDK execution bypasses its marked build-cache guard.'
        Assert-Harness ($laneScript -notmatch 'docker (volume rm|volume prune|system prune)') 'Ordinary helpers remove persistent caches.'
    }
    Assert-Harness ($alternateScript.Contains("'$($alternate.BuildStorage)':/build") -and $alternate.SdkCacheStorage.BuildStorage -eq $alternate.BuildStorage -and $alternate.SdkCacheStorage.Subdirectory -eq '.sdk-cache') 'Runtime modes share SDK state or report a standalone cache volume.'
    Assert-Harness ($plan.SdkCacheStorage.BuildStorage -eq $plan.BuildStorage -and $query.SdkCacheStorage.BuildStorage -eq 'container-local (discarded)') 'SDK cache view does not follow normal/FreshBuild storage.'
    Assert-Harness ($portable.SdkCacheStorage.BuildStorage -eq $portable.BuildStorage -and $portable.BuildStorage -ne $plan.BuildStorage) 'Browser ABI shares mutable SDK state with musl.'
    Assert-Harness ($watch.SdkCacheStorage.BuildStorage -eq $watch.BuildStorage -and $watch.BuildStorage -ne $check.BuildStorage) 'Watcher shares mutable SDK state with checks.'
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
    }
    $compose = Get-Content -LiteralPath (Join-Path $root 'compose.dev.yaml') -Raw
    Assert-Harness ($compose -match 'NUGET_SCRATCH: /root/\.nuget/packages/\.scratch') 'Development restores do not share the package-cache lock namespace.'
    Assert-Harness ($compose.Contains('DOTNET_CLI_HOME: /build/.sdk-cache/dotnet') -and $compose.Contains('NUGET_PLUGINS_CACHE_PATH: /build/.sdk-cache/nuget-plugins') -and $compose.Contains('PC_SDK_CACHE_OWNER: ${PREMIERECALENDAR_BUILD_VOLUME:') -and $compose.Contains('/src/eng/run-with-sdk-cache.sh')) 'Watcher bypasses the build-owned SDK cache guard.'
    Assert-Harness ($compose.Contains('DOTNET_GENERATE_ASPNET_CERTIFICATE: "false"')) 'Watcher first-use may generate an HTTPS identity.'
    Assert-Harness ($compose -match 'NUGET_HTTP_CACHE_PATH: /root/\.nuget/packages/\.http-cache') 'Watcher restores do not share the NuGet HTTP cache.'
    Assert-Harness ($compose -match '(?s)nuget-cache:\s+external: true\s+name: \$\{PREMIERECALENDAR_NUGET_VOLUME:' -and $compose -match '(?s)watch-build:\s+external: true\s+name: \$\{PREMIERECALENDAR_BUILD_VOLUME:') 'Compose cache mapping/reset no longer preserves external shared caches.'
    Assert-Harness ($checkScript -match 'flock -n 9' -and $testScript -match 'flock -n 9') 'Check/test output locking is missing.'
    Assert-Harness ($testScript -notmatch '/tmp/pcpg-env') 'Test credentials escaped into WSL host temp files.'
    Assert-Harness ($testScript -match "export POSTGRES_PASSWORD='([0-9a-f]{32})'") 'Disposable test password is not confined to the input script.'
    $testPassword = $Matches[1]
    Assert-Harness (-not (($global:HarnessCapturedArgv | ForEach-Object { $_ -join ' ' }) -join "`n").Contains($testPassword)) 'Test password escaped into native command arguments.'
    Assert-Harness ($testScript -match "PC_INNER_SCRIPT='([^']+)'") 'Encoded inner test script is missing.'
    $inner = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Matches[1]))
    Assert-Harness ($inner.Contains((ConvertTo-BashLiteral $runnerFilter))) 'Shell filter quoting changed.'
    Assert-Harness ($inner.Contains('exe="/build/bin/$name/release/$name"') -and $inner.Contains('for suite in Component; do') -and $inner.Contains('-result-ctrf')) 'Test executables are not run directly with TRX/CTRF results.'
    Assert-Harness ($inner.Contains('NO TESTS RAN')) 'A filter that matches no test is reported as a pass.'
    Assert-Harness ($testScript.Contains("'$($plan.BuildStorage)':/build") -and $testScript.Contains("'$($plan.PackageStorage)':/root/.nuget/packages") -and $testScript -notmatch '/artifacts/(build|packages)/linux') 'Default mutable Linux caches are not named volume mounts.'
    Assert-Harness ($testScript.Contains('"$dev_image" sh /src/eng/run-with-sdk-cache.sh sh -c') -and $testScript.Contains('"$postgres_image" -c') -and $checkScript.Contains('"$dev_image" sh /src/eng/run-with-sdk-cache.sh dotnet build')) 'An inspected image identity is not pinned for execution.'
    Assert-Harness ($testScript.Contains('--pull=never') -and $checkScript.Contains('--pull=never')) 'Execution may implicitly pull an image.'
    # Several suites share ONE MSBuild invocation (solution filter in container-local /tmp); PostgreSQL start-up
    # overlaps it and is bounded by a time budget. Run the real inner script with a dotnet double.
    Invoke-HarnessFixtureScript (Join-Path $root 'eng/Test-WithPostgres.ps1') @{ Project = 'Unit', 'Component', 'Integration'; WslDistribution = 'PC-MARC-Dev' } 6>$null | Out-Null
    $multiScript = $global:HarnessCapturedScripts[-1]
    Assert-Harness ($multiScript -match "PC_INNER_SCRIPT='([^']+)'") 'Encoded multi-suite inner script is missing.'
    $multiInner = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Matches[1]))
    Assert-Harness ($multiInner.Contains('for suite in Unit Component Integration; do')) 'Multi-suite roster changed.'
    # auto budgets are resolved in the guest (half of nproc, at least 2); explicit numbers still win.
    Assert-Harness ($multiScript.Contains('--cpus=$pc_budget') -and $multiScript.Contains('-e PC_BUDGET=$pc_budget') -and $multiInner.Contains('"-maxcpucount:$PC_BUDGET"') -and $multiInner.Contains('''-maxThreads'' "$PC_BUDGET"')) 'Auto worker budgets do not reach the runner, MSBuild and xUnit.net.'
    Assert-Harness ($multiScript -match 'PC_TEST_BUDGET engineCpus') 'The resolved worker budget is not recorded in the run output.'
    $explicitWords = & (Join-Path $root 'eng/Test-WithPostgres.ps1') -Project Unit -Cpus 3 -MaxThreads 4 -BuildMaxCpuCount 5 -TimeoutSeconds 90 -Plan
    Assert-Harness ($explicitWords.Cpus -eq '3' -and $explicitWords.BuildArguments -contains '-maxcpucount:5' -and $explicitWords.RunnerArguments -contains '4' -and $explicitWords.TimeoutSeconds -eq 90) 'Explicit budgets/timeout no longer win over auto.'
    Assert-Harness ((& (Join-Path $root 'eng/Test-WithPostgres.ps1') -Project All -Plan).TimeoutSeconds -eq 5100 -and (& (Join-Path $root 'eng/Test-WithPostgres.ps1') -Project Browser -Plan).TimeoutSeconds -eq 2400) 'Default deadline does not scale with the selected suites.'
    Assert-Harness ($multiScript -match 'deadline=\$\(\(SECONDS \+ 180\)\)' -and $multiScript -match 'sleep 0\.2' -and $multiScript -notmatch 'sleep 2\b') 'PostgreSQL readiness is not a short-interval poll bounded by its budget.'
    Assert-Harness ($multiScript.IndexOf('docker create') -lt $multiScript.IndexOf('psql -h 127.0.0.1') -and $multiScript.Contains('touch /tmp/pc-postgres-ready')) 'PostgreSQL start-up no longer overlaps the build.'
    $innerBash = if ($IsWindows) { Join-Path $env:ProgramFiles 'Git/bin/bash.exe' } else { (Get-Command bash -ErrorAction SilentlyContinue)?.Source }
    if ($innerBash -and (Test-Path -LiteralPath $innerBash)) {
        $budgetLines = (($multiScript -split "`n") | Where-Object { $_ -match '^pc_(ncpu|budget)=' }) -join "`n"
        foreach ($engineCpus in @(@(12, 6), @(4, 2), @(2, 2), @(1, 2), @(256, 64))) {
            $resolved = "nproc() { echo $($engineCpus[0]); }`n$budgetLines`necho `$pc_budget" | & $innerBash -s
            Assert-Harness ([int] $resolved -eq $engineCpus[1]) "Auto budget for $($engineCpus[0]) engine CPUs was $resolved."
        }
        foreach ($buildCase in @(@{ Fail = ''; Expected = 'combined' }, @{ Fail = 'combined'; Expected = 'per-project' })) {
            $sandbox = Join-Path $root ('artifacts/harness-tests/inner-' + [guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $sandbox -Force | Out-Null
            $linuxSandbox = if ($IsWindows) { '/' + $sandbox.Substring(0, 1).ToLowerInvariant() + '/' + ($sandbox.Substring(3) -replace '\\', '/') } else { $sandbox }
            $simulatedInner = $multiInner.Replace('/tmp/', "$linuxSandbox/").Replace('/src/', "$linuxSandbox/src/").Replace('/build/bin/', "$linuxSandbox/bin/")
            $double = @"
mkdir -p '$linuxSandbox/src' '$linuxSandbox/bin'
for n in Unit Component Integration; do
  d='$linuxSandbox/bin/PremiereCalendar.'`$n'Tests/release'; mkdir -p "`$d"
  printf '#!/bin/sh\necho "Total: 1"\n' > "`$d/PremiereCalendar.`$n"Tests; chmod +x "`$d/PremiereCalendar.`$n"Tests
done
touch '$linuxSandbox/pc-postgres-ready'
dotnet() {
  echo "`$*" >> '$linuxSandbox/builds.txt'
  case "`$2" in *.slnf) cat "`$2" >> '$linuxSandbox/builds.txt'; [ '$($buildCase.Fail)' != combined ] || return 1 ;; esac
}
"@
            try {
                $observed = (($double + "`n" + $simulatedInner) -replace "`r`n", "`n") | & $innerBash -s 2>&1
                Assert-Harness ($LASTEXITCODE -eq 0) "Inner script failed with the dotnet double ($($buildCase.Expected)): $($observed -join ' ')"
                $builds = @(Get-Content -LiteralPath (Join-Path $sandbox 'builds.txt'))
                $invocations = @($builds | Where-Object { $_ -match '^build ' })
                if ($buildCase.Expected -eq 'combined') {
                    Assert-Harness ($invocations.Count -eq 1 -and $invocations[0] -match '\.slnf ') 'Selected suites were not built by one MSBuild invocation.'
                    $listed = ((($builds -join '') | Select-String -Pattern 'tests/PremiereCalendar\.(\w+)Tests/' -AllMatches).Matches | ForEach-Object { $_.Groups[1].Value }) -join ','
                    Assert-Harness ($listed -eq 'Unit,Component,Integration' -and ($builds -join '').Contains('"PremiereCalendar/PremiereCalendar.csproj"')) 'The solution filter does not list exactly the selected projects plus the application.'
                } else {
                    Assert-Harness ($invocations.Count -eq 4 -and @($invocations | Where-Object { $_ -match '\.csproj ' }).Count -eq 3) 'A failed combined build did not fall back to per-project attribution.'
                }
                Assert-Harness (($observed -join "`n") -match 'IntegrationTests: exit 0') 'A suite did not run after the build.'
            } finally {
                $resolved = [IO.Path]::GetFullPath($sandbox)
                if ($resolved.StartsWith([IO.Path]::GetFullPath((Join-Path $root 'artifacts/harness-tests')) + [IO.Path]::DirectorySeparatorChar)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
            }
        }
    }
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
            $WslDistribution = 'PC-MARC-Dev'; $Cpus = 3; $MaxThreads = '3'; $BuildMaxCpuCount = 3; $TimeoutSeconds = 2400
            $BuildVolume = 'fixture-build'; $NugetVolume = 'fixture-packages'; $testScript = 'fixture-test-runner.ps1'
            . ([scriptblock]::Create($launchFunction.Extent.Text))
            Invoke-TestRunner -LogPath (Join-Path $sandbox 'test-run.log')
        }
        Assert-Harness ($releaseRun.ExitCode -eq 0 -and $releaseRun.ResultsFolder -eq 'fixture-results') 'Release wrapper child result handling changed.'
        $releaseWords = $global:HarnessReleaseArgv -join '|'
        Assert-Harness ($releaseWords -match '-WslDistribution:?\|PC-MARC-Dev' -and $releaseWords -match '-BuildVolume:?\|fixture-build' -and $releaseWords -match '-NugetVolume:?\|fixture-packages') 'Canonical test wrapper lost distribution/storage forwarding.'
        Assert-Harness ($releaseWords -match '-Cpus\|3' -and $releaseWords -match '-MaxThreads\|3' -and $releaseWords -match '-BuildMaxCpuCount\|3' -and $global:HarnessReleaseArgv -notcontains '-Project') 'Canonical test wrapper changed full roster/budget forwarding.'
        Assert-Harness ($releaseWords -match '-TimeoutSeconds:?\|2400') 'Canonical wrapper did not forward its explicit total deadline.'
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
        [IO.File]::WriteAllText((Join-Path $sandbox 'eng/Harness.Common.ps1'), $commonText)
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
        # Every shell case below is independent and owns a unique sandbox, so all bash processes (each costs
        # ~0.3 s to start on Windows, and the real-timeout fakes sleep) run concurrently; assertions then run
        # here in the original order. Nothing is skipped or reordered between a case and its own assertions.
        $bashJobs = [Collections.Generic.List[hashtable]]::new()
        function Add-BashJob([string] $Kind, [string] $Script, [string[]] $Arguments, [bool] $DiscardErrors, $Case, [string] $Sandbox, [string] $Label = '') {
            $bashJobs.Add(@{ Index = $bashJobs.Count; Kind = $Kind; Script = ($Script -replace "`r`n", "`n"); Arguments = $Arguments; DiscardErrors = $DiscardErrors; Case = $Case; Sandbox = $Sandbox; Label = $Label })
        }
        function ConvertTo-LinuxSandbox([string] $Path) { if ($IsWindows) { '/' + $Path.Substring(0, 1).ToLowerInvariant() + '/' + ($Path.Substring(3) -replace '\\', '/') } else { $Path } }
        $linuxRoot = if ($IsWindows) { '/mnt/' + $root.Substring(0, 1).ToLowerInvariant() + '/' + ($root.Substring(3) -replace '\\', '/') } else { $root }
        foreach ($shellScript in @($checkScript, $testScript, $inner)) { Add-BashJob 'syntax' $shellScript @('-n') $false $null '' }
        # Execute the lifecycle with a shell-function Docker double. All paths
        # point into this project's sandbox; no Docker executable can be called.
        foreach ($case in @(
            @{ TestExit = 0; Cleanup = 'ok'; Expected = 0 },
            @{ TestExit = 7; Cleanup = 'ok'; Expected = 7 },
            @{ TestExit = 7; Cleanup = 'fail'; Expected = 7 },
            @{ TestExit = 0; Cleanup = 'fail'; Expected = 75 },
            @{ TestExit = 0; Cleanup = 'foreign'; Expected = 75 },
            @{ TestExit = 0; Cleanup = 'timeout'; Expected = 75 }
            @{ TestExit = 0; Cleanup = 'creation'; Expected = 9 }
            @{ TestExit = 0; Cleanup = 'ok'; Expected = 24; Unready = $true }
        )) {
            $sandbox = New-BashSandbox 'lifecycle'
            $linuxSandbox = ConvertTo-LinuxSandbox $sandbox
            $simulation = $testScript.Replace($linuxRoot, $linuxSandbox)
            # A server that never answers must fail at the readiness budget (shortened here) and still clean up.
            $unready = $case.ContainsKey('Unready')
            if ($unready) { $simulation = $simulation.Replace('SECONDS + 180', 'SECONDS + 1') }
            Assert-Harness ($simulation -match 'premierecalendar.test=([0-9a-f]{10})') 'Test run label missing.'
            $label = $Matches[1]
            $runnerId = 'a' * 64; $serverId = 'b' * 64; $networkId = 'c' * 64
            $dockerDouble = @"
flock() { return 0; }
# Intercept timeout as well as Docker: never let a shell function double fall
# through to a real Docker executable via timeout's child-process lookup.
timeout() {
  [ "`$1" = --kill-after=1s ] && [ "`$2" = 18s ] || return 99
  shift 2
  if [ '$($case.Cleanup)' = timeout ] && [ "`$3" = inspect ]; then return 124; fi
  "`$@"
}
docker() {
  printf '%s %s\n' "`$1" "`${2-}" >> '$linuxSandbox/docker-calls.txt'
  if [ "`$1" = info ]; then echo linux/amd64; return 0; fi
  if [ "`$1" = image ]; then echo 'linux/amd64 sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'; return 0; fi
  if [ "`$1" = network ] && [ "`$2" = create ]; then echo '$networkId'; return 0; fi
  if [ "`$1" = run ]; then if [ '$($case.Cleanup)' = creation ]; then return 9; fi; echo '$serverId'; return 0; fi
  if [ "`$1" = create ]; then echo '$runnerId'; return 0; fi
  if [ "`$1" = exec ] && [ '$unready' = True ]; then return 1; fi
  if [ "`$1" = start ]; then [ '$unready' != True ] || sleep 3; return $($case.TestExit); fi
  if [ "`${2-}" = inspect ]; then
    if [ '$($case.Cleanup)' = foreign ]; then echo "`${!#}|foreign-run"; else echo "`${!#}|$label"; fi
    return 0
  fi
  if [ "`${2-}" = rm ] && [ '$($case.Cleanup)' = fail ]; then return 1; fi
  return 0
}
"@
            Add-BashJob 'lifecycle' ($dockerDouble + "`n" + $simulation) @('-s') $false $case $sandbox
        }
        # Real GNU timeout must execute an external fake Docker, rather than a
        # shell-function double that bypasses child startup/connection latency.
        $cleanupStart = $testScript.IndexOf('cleanup() {')
        $cleanupEnd = $testScript.IndexOf("runner_id=''")
        $cleanupFunctions = $testScript.Substring($cleanupStart, $cleanupEnd - $cleanupStart)
        foreach ($case in @(
            @{ Profile = 'current'; Mode = 'slow'; Expected = 0; Removals = 3 },
            @{ Profile = 'old-1s'; Mode = 'slow'; Expected = 75; Removals = 0 },
            @{ Profile = 'current'; Mode = 'foreign'; Expected = 75; Removals = 0 }
        )) {
            $sandbox = New-BashSandbox 'real-timeout'
            New-Item -ItemType Directory -Path (Join-Path $sandbox 'bin') -Force | Out-Null
            $linuxSandbox = ConvertTo-LinuxSandbox $sandbox
            # The fake client's latency (1.5 s) must sit between the old 1 s and the current 18 s bound.
            $fakeDocker = @'
#!/bin/sh
printf '%s %s\n' "$1" "$2" >> "$PC_FAKE_LOG"
if [ "$2" = inspect ]; then
  sleep 1.5
  for id do :; done
  label="$PC_FAKE_LABEL"
  [ "$PC_FAKE_MODE" != foreign ] || label=foreign-run
  printf '%s|%s\n' "$id" "$label"
  exit 0
fi
[ "$2" = rm ] && exit 0
exit 99
'@
            [IO.File]::WriteAllText((Join-Path $sandbox 'bin/docker'), ($fakeDocker -replace "`r`n", "`n"), [Text.UTF8Encoding]::new($false))
            $functions = if ($case.Profile -eq 'old-1s') { $cleanupFunctions.Replace('--kill-after=1s 18s docker', '--kill-after=1s 1s docker') } else { $cleanupFunctions }
            $fixtureScript = @"
set -eu
# Limit PATH to the fake and Git Bash/POSIX tools; a failed fake cannot select a host Docker executable.
export PATH='$linuxSandbox/bin:/usr/bin:/bin'
timeout --version | grep 'GNU coreutils' >/dev/null
chmod +x '$linuxSandbox/bin/docker'
export PC_FAKE_LOG='$linuxSandbox/calls.txt' PC_FAKE_LABEL='$label' PC_FAKE_MODE='$($case.Mode)'
$functions
runner_id='$runnerId'; server_id='$serverId'; network_id='$networkId'
runner_attempted=1; server_attempted=1; network_attempted=1
trap cleanup EXIT
exit 0
"@
            Add-BashJob 'timeout' $fixtureScript @('-s') $false $case $sandbox
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
                $sandbox = New-BashSandbox 'preflight'
                $linuxSandbox = ConvertTo-LinuxSandbox $sandbox
                $simulation = $captured.Replace($linuxRoot, $linuxSandbox)
                $dockerDouble = @"
docker() {
  printf '%s\n' "`$1" >> '$linuxSandbox/docker-calls.txt'
  if [ "`$1" = info ]; then echo '$($rejection.Engine)'; return $($rejection.InfoExit); fi
  if [ "`$1" = image ]; then echo '$($rejection.Image)'; return $($rejection.ImageExit); fi
  echo 'Unexpected project resource operation' >&2; return 99
}
"@
                Add-BashJob 'preflight' ($dockerDouble + "`n" + $simulation) @('-s') $true $rejection $sandbox
            }
        }
        $bashResults = @($bashJobs | ForEach-Object -ThrottleLimit 8 -Parallel {
            $job = $_
            $bashExe = $using:bash
            $jobArguments = [string[]] $job.Arguments
            $text = $job.Script
            $output = if ($job.DiscardErrors) { $text | & $bashExe @jobArguments 2>$null } else { $text | & $bashExe @jobArguments 2>&1 }
            [pscustomobject]@{ Index = $job.Index; ExitCode = $LASTEXITCODE; Output = @($output) }
        } | Sort-Object Index)
        Assert-Harness ($bashResults.Count -eq $bashJobs.Count) 'A shell validation case did not report a result.'
        foreach ($job in $bashJobs) {
            $result = $bashResults[$job.Index]
            $case = $job.Case; $sandbox = $job.Sandbox; $observed = $result.Output
            switch ($job.Kind) {
                'syntax' { Assert-Harness ($result.ExitCode -eq 0) 'Generated bash failed syntax validation.' }
                'lifecycle' {
                    Assert-Harness ($result.ExitCode -eq $case.Expected) "Test/cleanup exit status was not preserved: $($case.Cleanup), $($case.TestExit) (exit $($result.ExitCode), expected $($case.Expected)):`n$($observed -join "`n")"
                    $calls = Get-Content (Join-Path $sandbox 'docker-calls.txt')
                    if ($case.Cleanup -eq 'creation') {
                        Assert-Harness (@($calls | Where-Object { $_ -eq 'container rm' }).Count -eq 0 -and $calls[-1] -eq 'network rm') 'Unknown creation was removed by name or the verified network was retained.'
                        Assert-Harness (($observed -join "`n") -match 'state=unresolved-creation') 'Unknown creation was falsely reported as cleaned.'
                    } elseif ($case.Cleanup -in 'foreign', 'timeout') {
                        Assert-Harness (@($calls | Where-Object { $_ -match ' rm$' }).Count -eq 0) 'Cleanup removed resources without verified ownership.'
                        Assert-Harness (($observed -join "`n") -match 'state=unresolved-identity') 'Unverified cleanup was not reported.'
                    } else {
                        Assert-Harness (@($calls | Where-Object { $_ -eq 'container rm' }).Count -eq 2) 'Runner/database cleanup did not execute on exit.'
                        Assert-Harness ($calls[-1] -eq 'network rm') 'Network cleanup did not execute on exit.'
                        Assert-Harness (($observed -join "`n") -match $(if ($case.Cleanup -eq 'ok') { 'state=removed' } else { 'state=unresolved-removal' })) 'Cleanup disposition was not reported.'
                    }
                }
                'timeout' {
                    Assert-Harness ($result.ExitCode -eq $case.Expected) "Real timeout changed cleanup disposition: $($case.Profile), $($case.Mode)."
                    $calls = Get-Content -LiteralPath (Join-Path $sandbox 'calls.txt')
                    Assert-Harness (@($calls | Where-Object { $_ -match ' rm$' }).Count -eq $case.Removals) 'Real timeout cleanup removed an unverified resource or retained a verified resource.'
                }
                'preflight' {
                    Assert-Harness ($result.ExitCode -eq $case.Code) 'Platform/identity preflight did not reject the execution double.'
                    $calls = @(Get-Content -LiteralPath (Join-Path $sandbox 'docker-calls.txt'))
                    Assert-Harness (@($calls | Where-Object { $_ -notin 'info', 'image' }).Count -eq 0 -and -not (Test-Path -LiteralPath (Join-Path $sandbox 'artifacts'))) 'Rejected preflight created project resources or output state.'
                }
            }
        }
    } else { Write-Warning 'Bash is absent: generated bash syntax and lifecycle validation were not performed.' }
} finally {
    # Only the unique sandboxes this run created for its shell cases; each must still be inside artifacts/harness-tests.
    $sandboxParent = [IO.Path]::GetFullPath((Join-Path $root 'artifacts/harness-tests')) + [IO.Path]::DirectorySeparatorChar
    foreach ($owned in $bashSandboxes) {
        $resolved = [IO.Path]::GetFullPath($owned)
        if ($resolved.StartsWith($sandboxParent) -and (Test-Path -LiteralPath $resolved) -and -not ((Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    }
    Remove-Item Function:\wsl.exe
    Remove-Variable HarnessCapturedScripts, HarnessWslCalls, HarnessCapturedArgv, HarnessRunningDistros, HarnessRunningListExit -Scope Global
    $transportPath = [IO.Path]::GetFullPath($transportSandbox)
    if (-not $transportPath.StartsWith([IO.Path]::GetFullPath((Join-Path $root 'artifacts/harness-tests')) + [IO.Path]::DirectorySeparatorChar) -or (Get-Item -LiteralPath $transportPath).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unexpected transport fixture path; preserving it.' }
    Remove-Item -LiteralPath $transportPath -Recurse
}
[xml] $properties = Get-Content (Join-Path $root 'Directory.Build.props') -Raw
Assert-Harness ($properties.Project.PropertyGroup.ArtifactsPath.InnerText -like '*artifacts/build/host') 'Native build output escaped the checkout.'
Write-Host "$count harness checks passed; WSL/Docker commands were intercepted, not executed."
