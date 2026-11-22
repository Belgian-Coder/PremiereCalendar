#Requires -Version 7.0
<#
.SYNOPSIS
Runs the PremiereCalendar xUnit.net 4 test projects against a throwaway PostgreSQL server, using the existing WSL
Docker SDK image.

.DESCRIPTION
USAGE (works from any folder; use the absolute path of the script). Add -Plan to any command to print the selected
projects, runner arguments and output paths without invoking WSL or Docker.

  # all suites (Unit, Component, Integration), results in <checkout>\artifacts\test-results\<timestamp>\
  pwsh -File D:\Projects\PremiereCalendar\eng\Test-WithPostgres.ps1

  # one suite (-Project UnitTests and -Suite Unit are the same thing)
  pwsh -File D:\Projects\PremiereCalendar\eng\Test-WithPostgres.ps1 -Project Unit

  # a subset: every test whose fully qualified name contains "ViewSync" (several values = OR)
  pwsh -File D:\Projects\PremiereCalendar\eng\Test-WithPostgres.ps1 -Project Unit -Filter ViewSync
  pwsh -File D:\Projects\PremiereCalendar\eng\Test-WithPostgres.ps1 -Filter ViewSync,Wikimedia

  # an xUnit.net query filter (assembly/namespace/class/method[trait=value]); see https://xunit.net/docs/query-filter-language
  pwsh -File D:\Projects\PremiereCalendar\eng\Test-WithPostgres.ps1 -Project Integration -QueryFilter '/*/*/PostgresResponseCacheTests/*'

  # runner switches (any xUnit.net runner option), for example no parallelism or a thread count
  pwsh -File D:\Projects\PremiereCalendar\eng\Test-WithPostgres.ps1 -Project Unit -Parallel none
  pwsh -File D:\Projects\PremiereCalendar\eng\Test-WithPostgres.ps1 -Project Unit -MaxThreads 2 -RunnerArguments:-longRunning,30

Lists (-Project, -Filter, -RunnerArguments) are comma separated, also when the script is started with `pwsh -File`:
-Project Unit,Component.

What it does: inside one WSL shell (Ubuntu-24.04) it takes this checkout's check lock, starts a disposable postgres
container (the image pinned in compose.yaml, data on tmpfs, max_connections=300) on a private Docker network, builds
the requested test projects inside premierecalendar-dev-app:latest (one MSBuild invocation through a solution filter in
container-local /tmp when several suites are selected; on a combined failure each project is rebuilt to attribute it),
runs each test executable directly (xUnit.net 4
projects are executables; this skips the `dotnet test` start-up cost) and attempts bounded, ownership-verified cleanup
of the server, runner and network on exit. Unresolved cleanup is reported and fails the run. The source is mounted into the container as /src (a verified ext4 mirror of this checkout by default, see -SourceMount). PostgreSQL's start-up overlaps the build: the runner is started
immediately, the shell polls the server every 0.2 s (bounded to 180 s) and the runner waits for a readiness marker
before its first suite. The default worker budgets are `auto` (see -Cpus). The network is created
with an explicit subnet because the Docker default address pools can be exhausted. The test password is generated per
run and travels only over stdin and inherited environment variables, never through arguments or WSL temp files.

Nothing is started or built implicitly: the Docker daemon in Ubuntu-24.04 must already be running and the development
image must exist (build it with D:\Projects\PremiereCalendar\eng\wsl-docker.ps1 build). A second check in the same
worktree is refused by the lock instead of waiting.

Results: every suite writes <Suite>.trx and <Suite>.ctrf.json (CTRF = Common Test Report Format, JSON) plus the console
log <Suite>.log to <checkout>\artifacts\test-results\<yyyyMMdd-HHmmss>\ (artifacts\ is git-ignored). The console shows
the xUnit.net summary and the wall time of each suite and ends with a "Results:" line; the script exits non-zero when a
suite fails or when no test ran (a filter that matches nothing). All requested suites run even when an earlier one
fails.

Incremental builds use named Linux/amd64 Docker volumes isolated by worktree, ABI and runtime-async mode.
Linux NuGet packages and extraction locks share a project volume. Use -FreshBuild to build from scratch into a throwaway
container location, -NoBuild to run the last build unchanged, -NoRestore to skip the restore of an unchanged build.

Plain `dotnet test` also works (global.json selects Microsoft Testing Platform), for example inside the dev container:
  dotnet test tests/PremiereCalendar.UnitTests/PremiereCalendar.UnitTests.csproj -c Release --filter-class '*ViewSync*'
It needs PREMIERECALENDAR_TEST_POSTGRES set (see docs/Testing.md); this script sets everything up for you.

.PARAMETER Project
Test suites to run: Unit, Component, Integration, Browser or All (all four). The full project names (UnitTests, ...)
are accepted too. Default: Unit, Component, Integration. Browser needs Playwright browsers, which the Alpine dev image
does not have (use -DevImage with a prepared image), so it is never part of the default set. (Alias: -Suite.)

.PARAMETER Filter
One or more substrings of the fully qualified test method name (namespace.class.method), matched case-sensitively and
combined with OR. Example: -Filter ViewSync. The old VSTest form "FullyQualifiedName~ViewSync" is still accepted;
VSTest expressions with & or | are not (use -QueryFilter).

.PARAMETER QueryFilter
One or more xUnit.net query filters (see the link above), combined with OR. Cannot be combined with -Filter.

.PARAMETER Parallel
Overrides the assembly parallelization mode: none, collections or all (see tests/*/AssemblyConfiguration.cs).

.PARAMETER MaxThreads
Overrides the maximum number of test threads (default auto, same rule as -Cpus): a number, auto, default, unlimited or a
multiplier such as 2x.

.PARAMETER RunnerArguments
Extra arguments for the xUnit.net test executable (single-dash switches such as -longRunning 30 or -explicit on),
comma separated. Values that start with a dash need the colon form: -RunnerArguments:-longRunning,30 or
-RunnerArguments:-failSkips (the colon form works for lists too). Run a suite executable with -? to list every switch.
(Alias: -ExtraTestArguments.)

.PARAMETER NoRuntimeAsync
Builds and runs the tests with -p:UseRuntimeAsync=false (the .NET 11 runtime-async compiler feature is on by default)
in a separate build directory.

.PARAMETER NoRestore
Passes --no-restore to the build; only valid after a successful restore of the same worktree, configuration and mode.

.PARAMETER NoBuild
Skips the build and runs the output of the previous run of the same worktree and mode.

.PARAMETER FreshBuild
Builds from scratch in a throwaway container location instead of this checkout's build directory.

.PARAMETER SourceMount
Mirror (default) copies this checkout's source (tracked plus untracked-not-ignored files) into a per-checkout directory on
the WSL distribution's ext4 disk and mounts that as /src: the Windows drive is slow to read from Linux. The copy is
content addressed and verified in the guest against a Windows manifest of Git blob ids before any container starts; only
new or changed files are copied after the first run. Windows mounts the live checkout as /src (the former behaviour) for
diagnosis. Results and logs always land in this checkout's artifacts folder.

.PARAMETER NoCache
Runs the tests even when the same inputs already passed. By default a run is skipped ("REUSED passing results from ...")
when a previous run of this checkout recorded an identical key (verified source manifest, development image identity,
suites, filters, -Parallel/-RunnerArguments, configuration and runtime-async mode; not worker budgets or timeouts) and all
of its suites passed. -NoBuild runs never use or record the cache.

.PARAMETER Cpus
CPU limit of the test container: a number or auto (default). auto is half of the engine's CPUs read with nproc in the WSL
guest (at least 2, at most 64); the PostgreSQL container always gets 2. The resolved value is printed as PC_TEST_BUDGET.

.PARAMETER BuildMaxCpuCount
Maximum MSBuild worker count: a number or auto (default, same rule as -Cpus). Suites run sequentially.

.PARAMETER BuildVolume
Override for the generated Docker build volume. It must belong exclusively
to this worktree and runtime-async mode.

.PARAMETER NugetVolume
Override for the generated shared Linux NuGet Docker volume.

.PARAMETER WslDistribution
Explicit WSL distribution (default Ubuntu-24.04); PC-MARC uses PC-MARC-Dev.

.PARAMETER CacheFlavor
Linux ABI for build isolation: musl (default image) or glibc. Required with a custom prepared -DevImage.

.PARAMETER Plan
Prints the plan (projects, build and runner arguments, storage, results folder, images) and returns without invoking
WSL or Docker.

.PARAMETER TimeoutSeconds
Total WSL execution, bounded guest cleanup and output-drain budget. Default: 600 s plus 900 s per Unit/Component/
Integration suite (1800 s per Browser suite), at most 7200 s (3300 s for the default three suites).
Browser qualification explicitly uses 2400 seconds. A timeout never proves Linux cleanup.
#>
[CmdletBinding()]
param(
    [Alias('Suite')]
    [string[]] $Project = @('UnitTests', 'ComponentTests', 'IntegrationTests'),
    [string[]] $Filter,
    [string[]] $QueryFilter,
    [ValidateSet('none', 'collections', 'all')]
    [string] $Parallel,
    [string] $MaxThreads = 'auto',
    [Alias('ExtraTestArguments')]
    [string[]] $RunnerArguments = @(),
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',
    [ValidatePattern('^(auto|[1-9][0-9]?)$')]
    [string] $Cpus = 'auto',
    [ValidatePattern('^(auto|[1-9][0-9]?)$')]
    [string] $BuildMaxCpuCount = 'auto',
    [switch] $NoRuntimeAsync,
    [switch] $NoRestore,
    [switch] $NoBuild,
    [switch] $FreshBuild,
    [ValidateSet('Mirror', 'Windows')]
    [string] $SourceMount = 'Mirror',
    [switch] $NoCache,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')]
    [string] $BuildVolume,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.:/@-]*$')]
    [string] $DevImage = 'premierecalendar-dev-app:latest',
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')]
    [string] $NugetVolume,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')]
    [string] $WslDistribution = 'Ubuntu-24.04',
    [ValidateSet('musl', 'glibc')]
    [string] $CacheFlavor,
    [ValidateRange(0, 86400)][int] $TimeoutSeconds = 0,
    [ValidateSet('Auto', 'Native', 'Wsl')]
    [string] $Lane = 'Auto',
    [string] $PostgresBin,
    [switch] $Plan
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Harness.Common.ps1')
$context = Get-HarnessContext (Split-Path -Parent $PSScriptRoot)
if (-not $CacheFlavor) {
    if ($DevImage -ne 'premierecalendar-dev-app:latest') { throw 'A custom -DevImage requires explicit -CacheFlavor musl or glibc.' }
    $CacheFlavor = 'musl'
}
if ($DevImage -eq 'premierecalendar-dev-app:latest' -and $CacheFlavor -ne 'musl') { throw 'The default development image requires -CacheFlavor musl.' }
$storage = Get-HarnessStorage $context -CacheFlavor $CacheFlavor -NoRuntimeAsync:$NoRuntimeAsync -BuildVolume $BuildVolume -NugetVolume $NugetVolume

# `pwsh -File script.ps1 -Project Unit,Component` delivers "Unit,Component" as ONE string; accept both forms.
$Project = @($Project | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
if ($Filter) { $Filter = @($Filter | ForEach-Object { $_ -split ',' } | Where-Object { $_ }) }
if ($QueryFilter) { $QueryFilter = @($QueryFilter | ForEach-Object { $_ -split ',' } | Where-Object { $_ }) }
$RunnerArguments = @($RunnerArguments | ForEach-Object { $_ -split ',' } | Where-Object { $_ })

# Suite names: Unit / UnitTests, ..., All. Projects are reported by their full names (UnitTests, ...).
$known = @('Unit', 'Component', 'Integration', 'Browser')
$suites = [System.Collections.Generic.List[string]]::new()
foreach ($item in $Project) {
    $name = ($item -replace 'Tests$', '')
    if ($name -ieq 'All') { foreach ($k in $known) { if (-not $suites.Contains($k)) { $suites.Add($k) } }; continue }
    $match = $known | Where-Object { $_ -ieq $name }
    if (-not $match) { throw "Unknown project '$item'. Use Unit, Component, Integration, Browser or All." }
    if (-not $suites.Contains($match)) { $suites.Add($match) }
}
if ($suites.Count -eq 0) { throw 'No project selected.' }
foreach ($budget in @($Cpus, $BuildMaxCpuCount)) { if ($budget -ne 'auto' -and [int] $budget -gt 64) { throw 'Worker budgets must be auto or 1-64.' } }
# Default total deadline scales with the selection: 600 s plus 900 s per Unit/Component/Integration suite (1800 s for
# Browser), at most 7200 s. An explicit -TimeoutSeconds always wins.
if ($TimeoutSeconds -eq 0) {
    $TimeoutSeconds = [Math]::Min(7200, 600 + 900 * @($suites | Where-Object { $_ -ne 'Browser' }).Count + 1800 * @($suites | Where-Object { $_ -eq 'Browser' }).Count)
} elseif ($TimeoutSeconds -lt 30) { throw '-TimeoutSeconds must be at least 30.' }
if ($Filter -and $QueryFilter) { throw '-Filter and -QueryFilter cannot be combined.' }
if ($FreshBuild -and ($NoBuild -or $BuildVolume)) { throw '-FreshBuild cannot be combined with -NoBuild or -BuildVolume.' }
foreach ($f in @($Filter)) {
    if (($f -replace '^FullyQualifiedName~', '') -match '[&|]') {
        throw "-Filter takes test-name substrings, not VSTest expressions ('$f'). Use -QueryFilter for xUnit.net query filters."
    }
}

$composeText = Get-Content -LiteralPath (Join-Path $context.Root 'compose.yaml') -Raw
if ($composeText -notmatch '(?m)^\s*image:\s*(postgres:\S+@sha256:[0-9a-f]{64})\s*$') {
    throw 'Could not find the digest-pinned postgres image in compose.yaml.'
}
$postgresImage = $Matches[1]
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$resultsRelative = "artifacts/test-results/$stamp"
$resultsHost = Join-Path $context.Root $resultsRelative

# Runner arguments (xUnit.net in-process runner switches).
$runner = [System.Collections.Generic.List[string]]::new()
if ($Filter) {
    foreach ($f in $Filter) {
        $runner.Add('-method'); $runner.Add('*' + ($f -replace '^FullyQualifiedName~', '') + '*')
    }
}
if ($QueryFilter) { foreach ($q in $QueryFilter) { $runner.Add('-filter'); $runner.Add($q) } }
if ($Parallel) { $runner.Add('-parallelMode'); $runner.Add($Parallel) }
$maxThreadsIndex = -1
if ($MaxThreads) { $runner.Add('-maxThreads'); $maxThreadsIndex = $runner.Count; $runner.Add($MaxThreads) }
foreach ($a in $RunnerArguments) { $runner.Add($a) }
$buildArguments = @('-c', $Configuration, '--nologo', '-v', 'q', "-maxcpucount:$BuildMaxCpuCount")
if ($NoRuntimeAsync) { $buildArguments += '-p:UseRuntimeAsync=false' }
if ($NoRestore) { $buildArguments += '--no-restore' }

$hostLane = Resolve-HarnessHostLane $Lane
if ($hostLane -eq 'Native') {
    # Native feedback lane (macOS/Linux): host SDK + throwaway local PostgreSQL. Not release acceptance.
    foreach ($containerOnly in 'SourceMount', 'WslDistribution', 'DevImage', 'BuildVolume', 'NugetVolume', 'CacheFlavor') {
        if ($PSBoundParameters.ContainsKey($containerOnly)) { throw "-$containerOnly applies to the WSL/container lane only." }
    }
    if ($suites.Contains('Browser')) { throw 'The Browser suite needs the prepared linux browser image and runs only on the Windows/WSL container lane; the native lane covers Unit, Component and Integration.' }
    $platform = Get-HarnessNativePlatform
    $nativeStorage = Get-HarnessNativeStorage $context $platform -NoRuntimeAsync:$NoRuntimeAsync
    $buildBudget = Get-HarnessNativeBudget $BuildMaxCpuCount
    $runBudget = Get-HarnessNativeBudget $Cpus
    $nativeBuildArguments = @('-c', $Configuration, '--nologo', '-v', 'q', "-maxcpucount:$buildBudget", '-nodeReuse:false', '-p:UseSharedCompilation=false')
    if ($NoRuntimeAsync) { $nativeBuildArguments += '-p:UseRuntimeAsync=false' }
    if ($NoRestore) { $nativeBuildArguments += '--no-restore' }
    $nativeRunner = @($runner | ForEach-Object { if ($_ -eq 'auto') { [string] $runBudget } else { $_ } })
    $nativeBuildDirectory = if ($FreshBuild) { Join-Path $context.Root "artifacts/build/native-$($platform.Name)-fresh-$([Guid]::NewGuid().ToString('N').Substring(0, 8))" } else { $nativeStorage.BuildDirectory }
    $postgres = Resolve-HarnessPostgresBin $PostgresBin
    if ($Plan) {
        $planned = Resolve-HarnessDotnet $context.Root
        [pscustomobject]@{
            Lane = 'native'
            Projects = @($suites | ForEach-Object { "$($_)Tests" })
            Build = -not $NoBuild
            BuildArguments = $nativeBuildArguments
            RunnerArguments = $nativeRunner
            Platform = $platform.Name
            RuntimeIdentifier = $platform.Rid
            Dotnet = if ($planned.Ok) { $planned.Path } else { "(not found) $($planned.Error)" }
            Sdk = if ($planned.Ok) { $planned.Sdk } else { "(none; global.json requires $($planned.Required))" }
            SdkRequired = $planned.Required
            PostgresBin = if ($postgres.Ok) { $postgres.Bin } else { "(not found) $($postgres.Error)" }
            PostgreSql = 'throwaway initdb in a temporary folder (unix socket there plus 127.0.0.1 on a free port, SCRAM password file, max_connections=300, fsync off); stopped and deleted on exit'
            BuildOutput = $nativeBuildDirectory
            PackageStorage = $nativeStorage.PackageDirectory
            Results = $resultsHost
            Cpus = $Cpus
            BuildMaxCpuCount = $BuildMaxCpuCount
            RunnerBudget = $runBudget
            BuildBudget = $buildBudget
            TimeoutSeconds = $TimeoutSeconds
            AutoBudget = 'auto = max(2, CPU count / 2)'
            ResultCache = if ($NoCache) { 'off (-NoCache)' } elseif ($NoBuild) { 'off (-NoBuild)' } else { 'on (key: source manifest, SDK version, RID, PostgreSQL version, selection, configuration)' }
            Acceptance = 'feedback only; release acceptance stays on the Windows/WSL linux/amd64 container builder'
        }
        return
    }
    if (-not $postgres.Ok) { throw $postgres.Error }
    $nativeDotnet = Resolve-HarnessDotnet $context.Root -Probe
    if (-not $nativeDotnet.Ok) { throw $nativeDotnet.Error }
    $cacheEnabled = -not $NoCache -and -not $NoBuild
    $nativeClock = [Diagnostics.Stopwatch]::StartNew()
    $started = Get-Date
    $postgresVersion = ''
    $versionProbe = Invoke-HarnessNativeCommand (Get-HarnessPostgresTool $postgres.Bin 'postgres') @('--version') -TimeoutSeconds 30
    if ($versionProbe.ExitCode -eq 0) { $postgresVersion = $versionProbe.Output.Trim() }
    $environmentId = Get-HarnessNativeEnvironmentId $nativeDotnet.Sdk $platform.Rid $postgresVersion
    if ($cacheEnabled) {
        $manifestSha256 = Get-HarnessTextSha256 (ConvertTo-HarnessManifestText (Get-HarnessSourceManifest $context.Root))
        $cacheInputs = New-HarnessNativeCacheInputs $manifestSha256 @($suites) @($Filter) @($QueryFilter) $Parallel @($RunnerArguments) $Configuration (-not $NoRuntimeAsync) $nativeDotnet.Sdk $platform.Rid
        $cacheInputsHash = Get-HarnessResultInputsHash $cacheInputs
        $reused = @(Find-HarnessReusableResults (Join-Path $context.Root 'artifacts/test-results') $cacheInputsHash @($suites)) | Where-Object { $_.ImageId -ceq $environmentId } | Select-Object -First 1
        if ($reused) {
            Write-Host "REUSED passing results from $($reused.Folder) (key $($reused.Key.Substring(0, 16))); nothing was built or run. Use -NoCache to run anyway."
            Write-Host ("Total wall time: {0} s. Results: {1}" -f [int] $nativeClock.Elapsed.TotalSeconds, $reused.Folder)
            Write-Host "All requested test projects passed ($($suites -join ', ')), reused."
            return
        }
    }
    [void] (New-Item -ItemType Directory -Path (Join-Path $context.Root 'artifacts') -Force)
    $lockStream = $null
    try { $lockStream = [IO.File]::Open((Join-Path $context.Root 'artifacts/check.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
    catch [IO.IOException] { throw 'Another build/test check owns this worktree. Use another worktree or wait.' }
    [void] (New-Item -ItemType Directory -Path $resultsHost -Force)
    Write-Host "Results folder: $resultsHost"
    Write-Host "Native feedback lane: $($platform.Name) ($($platform.Rid)); SDK $($nativeDotnet.Sdk) at $($nativeDotnet.Path); PostgreSQL $postgresVersion at $($postgres.Bin)"
    Write-Host "PC_TEST_BUDGET cpus=$([Environment]::ProcessorCount) runnerCpus=$runBudget maxThreads=$(if ($MaxThreads -eq 'auto') { $runBudget } else { $MaxThreads }) buildMaxCpuCount=$buildBudget timeoutSeconds=$TimeoutSeconds"
    $nativeEnvironment = @{
        ArtifactsPath = $nativeBuildDirectory; NUGET_PACKAGES = $nativeStorage.PackageDirectory; DOTNET_ROOT = $nativeDotnet.Root
        DOTNET_NOLOGO = '1'; DOTNET_CLI_TELEMETRY_OPTOUT = '1'; DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'; DOTNET_CLI_HOME = (Join-Path $nativeBuildDirectory '.sdk-cache/dotnet')
    }
    [void] (New-Item -ItemType Directory -Path $nativeStorage.PackageDirectory -Force)
    $remainingSeconds = { [Math]::Max(1, [int][Math]::Floor($TimeoutSeconds - $nativeClock.Elapsed.TotalSeconds)) }
    $names = @{}; foreach ($s in $suites) { $names[$s] = "PremiereCalendar.$($s)Tests" }
    $summary = [Collections.Generic.List[string]]::new()
    $status = 0
    $built = $false
    $failedBuild = @{}
    try {
        if (-not $NoBuild) {
            [void] (New-Item -ItemType Directory -Path $nativeBuildDirectory -Force)
            $target = $null
            if ($suites.Count -gt 1) {
                # One MSBuild evaluation/restore for every selected project, like the container lane.
                $target = Join-Path $nativeBuildDirectory 'pc-selected-tests.slnf'
                $solution = [IO.Path]::GetRelativePath($nativeBuildDirectory, (Join-Path $context.Root 'PremiereCalendar.slnx')).Replace('\', '/')
                $projects = @('PremiereCalendar/PremiereCalendar.csproj') + @($suites | ForEach-Object { "tests/$($names[$_])/$($names[$_]).csproj" })
                [IO.File]::WriteAllText($target, (@{ solution = @{ path = $solution; projects = $projects } } | ConvertTo-Json -Depth 5 -Compress), [Text.UTF8Encoding]::new($false))
                Write-Host "=== build $($suites -join ' ') (one MSBuild invocation)"
                $clock = [Diagnostics.Stopwatch]::StartNew()
                $result = Invoke-HarnessNativeCommand $nativeDotnet.Path (@('build', $target) + $nativeBuildArguments) -Environment $nativeEnvironment -WorkingDirectory $context.Root -Echo -TimeoutSeconds (& $remainingSeconds)
                if ($result.ExitCode -eq 0) { $built = $true; Write-Host ("build time: {0} s" -f [int] $clock.Elapsed.TotalSeconds) }
                else { Write-Host 'COMBINED BUILD FAILED: building each project separately to attribute the failure' }
            }
            if (-not $built) {
                foreach ($suite in $suites) {
                    $name = $names[$suite]
                    Write-Host "=== build $name"
                    $clock = [Diagnostics.Stopwatch]::StartNew()
                    $result = Invoke-HarnessNativeCommand $nativeDotnet.Path (@('build', "tests/$name/$name.csproj") + $nativeBuildArguments) -Environment $nativeEnvironment -WorkingDirectory $context.Root -Echo -TimeoutSeconds (& $remainingSeconds)
                    if ($result.ExitCode -ne 0) { Write-Host "BUILD FAILED: $name"; $failedBuild[$suite] = $true; $status = 1; $summary.Add("${name}: build failed"); continue }
                    Write-Host ("build time: {0} s" -f [int] $clock.Elapsed.TotalSeconds)
                }
            }
        }
        $runnable = @($suites | Where-Object { -not $failedBuild.ContainsKey($_) })
        if ($runnable.Count -gt 0) {
            Invoke-HarnessWithNativePostgres $postgres.Bin {
                param($variables)
                $suiteEnvironment = @{ DOTNET_ROOT = $nativeDotnet.Root; DOTNET_NOLOGO = '1' } + $variables
                foreach ($suite in $runnable) {
                    $name = $names[$suite]
                    $apphost = Join-Path $nativeBuildDirectory "bin/$name/$($Configuration.ToLowerInvariant())/$name"
                    $arguments = @('-noLogo', '-result-trx', (Join-Path $resultsHost "$suite.trx"), '-result-ctrf', (Join-Path $resultsHost "$suite.ctrf.json")) + $nativeRunner
                    if ([IO.File]::Exists($apphost)) { $file = $apphost }
                    elseif ([IO.File]::Exists("$apphost.dll")) { $file = $nativeDotnet.Path; $arguments = @("$apphost.dll") + $arguments }
                    else { Write-Host "Missing test executable $apphost (run without -NoBuild)"; $script:status = 1; $summary.Add("${name}: no executable"); continue }
                    Write-Host "=== run $name"
                    $clock = [Diagnostics.Stopwatch]::StartNew()
                    $logPath = Join-Path $resultsHost "$suite.log"
                    $result = Invoke-HarnessNativeCommand $file $arguments -Environment $suiteEnvironment -WorkingDirectory $context.Root -Echo -LogPath $logPath -TimeoutSeconds (& $remainingSeconds)
                    $code = $result.ExitCode
                    # A filter that matches nothing makes the xUnit.net runner exit 0 with "Total: 0"; that must not count as a pass.
                    if ($code -eq 0 -and (Select-String -LiteralPath $logPath -Pattern 'Total: 0($|[^0-9])' -Quiet)) { Write-Host "NO TESTS RAN in ${name}: the filter matched nothing"; $code = 9 }
                    if ($code -ne 0) { $script:status = 1 }
                    $summary.Add(("{0}: exit {1}, {2} s" -f $name, $code, [int] $clock.Elapsed.TotalSeconds))
                }
            }
        }
    } finally {
        if ($lockStream) { $lockStream.Dispose() }
        if ($FreshBuild -and (Test-Path -LiteralPath $nativeBuildDirectory) -and ([IO.Path]::GetFileName($nativeBuildDirectory) -match '^native-.*-fresh-[0-9a-f]{8}$')) {
            Remove-Item -LiteralPath $nativeBuildDirectory -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
    Write-Host "`n=== summary"
    foreach ($line in $summary) { Write-Host $line }
    if ($status -eq 0 -and $cacheEnabled -and (Test-HarnessResultsPassed $resultsHost @($suites))) {
        Write-HarnessResultKey $resultsHost $cacheInputs $cacheInputsHash $environmentId @($suites)
    }
    Write-Host ("Total wall time: {0} s. Results: {1}" -f [int] ((Get-Date) - $started).TotalSeconds, $resultsHost)
    if ($status -ne 0) { throw 'Tests did not pass (see the messages above).' }
    Write-Host "All requested test projects passed ($($suites -join ', '))."
    return
}

if ($Plan) {
    [pscustomobject]@{
        Projects =@($suites | ForEach-Object { "$($_)Tests" })
        Build = -not $NoBuild
        BuildArguments = $buildArguments
        RunnerArguments = $runner.ToArray()
        BuildStorage = if ($FreshBuild) { 'container-local (discarded)' } else { $storage.BuildVolume }
        PackageStorage = $storage.NugetVolume
        SdkCacheStorage = [pscustomobject]@{ BuildStorage = if ($FreshBuild) { 'container-local (discarded)' } else { $storage.BuildVolume }; Subdirectory = $storage.SdkCacheSubdirectory }
        DotnetCliHome = $storage.DotnetCliHome
        NugetPluginsCache = $storage.NugetPluginsCache
        WslDistribution = $WslDistribution
        CacheFlavor = $CacheFlavor
        NugetScratch = '/root/.nuget/packages/.scratch'
        NugetHttpCache = '/root/.nuget/packages/.http-cache'
        Results = $resultsHost
        PostgreSqlImage = $postgresImage
        DevImage = $DevImage
        Cpus = $Cpus
        BuildMaxCpuCount = $BuildMaxCpuCount
        TimeoutSeconds = $TimeoutSeconds
        AutoBudget = 'auto = max(2, engine CPUs / 2), read with nproc in the WSL guest at run time'
        SourceMount = $SourceMount
        SourceStorage = if ($SourceMount -eq 'Mirror') { "wsl:~/$($storage.SourceMirror)" } else { 'windows' }
        ResultCache = if ($NoCache) { 'off (-NoCache)' } elseif ($NoBuild) { 'off (-NoBuild)' } else { 'on' }
    }
    return
}

$budgetClock = [Diagnostics.Stopwatch]::StartNew()
$wslRoot = ConvertTo-WslPath $context.Root -WslDistribution $WslDistribution
$wslBuild = $storage.BuildVolume
$wslPackages = $storage.NugetVolume
$bindDirectories = @("$wslRoot/artifacts")
$quotedDirectories = ($bindDirectories | ForEach-Object { ConvertTo-BashLiteral $_ }) -join ' '
$buildMount = if ($FreshBuild) { '' } else { "-v $(ConvertTo-BashLiteral $wslBuild):/build" }
$runId = [Guid]::NewGuid().ToString('N').Substring(0, 10)
$sdkCacheOwner = if ($FreshBuild) { 'container-local' } else { $storage.BuildVolume }
$network = "pcpg-net-$runId"
$server = "pcpg-db-$runId"
$runnerName = "pcpg-test-$runId"
$password = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(16)).ToLowerInvariant()
$subnetOctet = Get-Random -Minimum 1 -Maximum 250
# `auto` budgets are resolved in the guest shell (PC_BUDGET) before containers start; explicit numbers pass through.
$runnerWords = (0..($runner.Count - 1) | Where-Object { $runner.Count -gt 0 } | ForEach-Object {
    if ($_ -eq $maxThreadsIndex -and $runner[$_] -eq 'auto') { '"$PC_BUDGET"' } else { ConvertTo-BashLiteral $runner[$_] }
}) -join ' '
$buildWords = ($buildArguments | ForEach-Object { if ($_ -eq '-maxcpucount:auto') { '"-maxcpucount:$PC_BUDGET"' } else { ConvertTo-BashLiteral $_ } }) -join ' '
$cpusWord = if ($Cpus -eq 'auto') { '$pc_budget' } else { $Cpus }
$threadsWord = if ($MaxThreads -eq 'auto') { '"$pc_budget"' } else { ConvertTo-BashLiteral $MaxThreads }
$buildCpusWord = if ($BuildMaxCpuCount -eq 'auto') { '$pc_budget' } else { $BuildMaxCpuCount }
$doBuild = if ($NoBuild) { '0' } else { '1' }
$suiteList = $suites -join ' '
$PostgresReadySeconds = 180
$srcMounts = if ($SourceMount -eq 'Mirror') { "-v `"`$pc_src`":/src -v $(ConvertTo-BashLiteral "$wslRoot/artifacts"):/src/artifacts" } else { "-v `"`$pc_src`":/src" }
$cacheEnabled = -not $NoCache -and -not $NoBuild
$configLower = $Configuration.ToLowerInvariant()

$innerTemplate = @'
printf %s "$PC_TEST_PASSWORD" > /tmp/pc-test-postgres-password
mkdir -p '/src/@@RESULTS@@'
status=0
summary=''
built=0
# One MSBuild evaluation/restore for every selected project (the application project is shared). A solution filter
# in container-local /tmp lists exactly the selected test projects plus the application; the checkout is not written.
# If the combined build fails, each project is built separately below so the failure is attributed per suite.
if [ '@@DOBUILD@@' = 1 ] && [ @@SUITECOUNT@@ -gt 1 ]; then
  filter=/tmp/pc-selected-tests.slnf
  printf '{"solution":{"path":"/src/PremiereCalendar.slnx","projects":["PremiereCalendar/PremiereCalendar.csproj"' > "$filter"
  for suite in @@SUITES@@; do printf ',"tests/PremiereCalendar.%sTests/PremiereCalendar.%sTests.csproj"' "$suite" "$suite" >> "$filter"; done
  printf ']}}\n' >> "$filter"
  echo "=== build @@SUITES@@ (one MSBuild invocation)"
  t0=$(date +%s)
  if dotnet build "$filter" @@BUILDWORDS@@; then built=1; echo "build time: $(( $(date +%s) - t0 )) s"
  else echo "COMBINED BUILD FAILED: building each project separately to attribute the failure"; fi
fi
waited=0
for suite in @@SUITES@@; do
  name="PremiereCalendar.${suite}Tests"
  proj="tests/$name/$name.csproj"
  exe="/build/bin/$name/@@CONFIGLOWER@@/$name"
  if [ '@@DOBUILD@@' = 1 ] && [ "$built" = 0 ]; then
    echo "=== build $name"
    t0=$(date +%s)
    dotnet build "$proj" @@BUILDWORDS@@ || { echo "BUILD FAILED: $name"; status=1; summary="$summary\n$name: build failed"; continue; }
    echo "build time: $(( $(date +%s) - t0 )) s"
  fi
  [ -x "$exe" ] || { echo "Missing test executable $exe (run without -NoBuild)"; status=1; summary="$summary\n$name: no executable"; continue; }
  # PostgreSQL starts while the build runs; the host creates this marker once it answers queries.
  if [ "$waited" = 0 ]; then
    waited=1
    n=0
    while [ ! -e /tmp/pc-postgres-ready ] && [ "$n" -lt 1500 ]; do sleep 0.2; n=$((n + 1)); done
    [ -e /tmp/pc-postgres-ready ] || { echo 'Disposable PostgreSQL did not become ready.'; exit 24; }
  fi
  echo "=== run $name"
  t0=$(date +%s)
  # The exit code travels through a file because busybox sh has no PIPESTATUS.
  { "$exe" -noLogo -result-trx "/src/@@RESULTS@@/$suite.trx" -result-ctrf "/src/@@RESULTS@@/$suite.ctrf.json" @@RUNNERWORDS@@ 2>&1; echo $? > /tmp/pc-suite-rc; } | tee "/src/@@RESULTS@@/$suite.log"
  rc=$(cat /tmp/pc-suite-rc)
  # A filter that matches nothing makes the xUnit.net runner exit 0 with "Total: 0"; that must not count as a pass.
  if [ "$rc" = 0 ] && grep -Eq 'Total: 0($|[^0-9])' "/src/@@RESULTS@@/$suite.log"; then echo "NO TESTS RAN in $name: the filter matched nothing"; rc=9; fi
  elapsed=$(( $(date +%s) - t0 ))
  [ "$rc" = 0 ] || status=1
  summary="$summary\n$name: exit $rc, ${elapsed} s"
done
printf "\n=== summary%b\n" "$summary"
exit $status
'@
$inner = $innerTemplate.Replace('@@RESULTS@@', $resultsRelative).Replace('@@SUITES@@', $suiteList).Replace('@@CONFIGLOWER@@', $configLower).
    Replace('@@DOBUILD@@', $doBuild).Replace('@@SUITECOUNT@@', [string] $suites.Count).Replace('@@BUILDWORDS@@', $buildWords).Replace('@@RUNNERWORDS@@', $runnerWords)
$innerEncoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($inner -replace "`r`n", "`n")))

# A single WSL shell owns the lock and the cleanup. Passwords travel on stdin and inherited environment variables,
# never arguments or WSL host temporary files. No production or development database is joined.
# Built per attempt: the source-sync script and the cache decision depend on this attempt's verified source manifest.
$buildScript = { param($sync)
$cacheBlock = ''
if ($cacheEnabled) {
    $script:cacheInputs = @{
        schema = 1; manifest = $sync.ManifestSha256; suites = @($suites | Sort-Object); filter = @($Filter | Sort-Object)
        queryFilter = @($QueryFilter | Sort-Object); parallel = [string] $Parallel; runnerArguments = @($RunnerArguments)
        configuration = $Configuration; runtimeAsync = (-not $NoRuntimeAsync); postgresImage = $postgresImage; cacheFlavor = $CacheFlavor
    }
    $script:cacheInputsHash = Get-HarnessResultInputsHash $script:cacheInputs
    $script:cacheCandidates = @(Find-HarnessReusableResults (Join-Path $context.Root 'artifacts/test-results') $script:cacheInputsHash @($suites))
    $reuseLoop = ''
    if ($script:cacheCandidates.Count -gt 0) {
        $words = ($script:cacheCandidates | ForEach-Object { ConvertTo-BashLiteral "$($_.Name)|$($_.ImageId)" }) -join ' '
        $reuseLoop = @"
for pc_candidate in $words; do
  [ "`${pc_candidate#*|}" = "`$dev_image" ] || continue
  printf %s "`${pc_candidate%%|*}" > $(ConvertTo-BashLiteral "$($sync.RunDirectoryWsl)/reuse.txt")
  exit 60
done
"@
    }
    $cacheBlock = "mkdir -p $(ConvertTo-BashLiteral $sync.RunDirectoryWsl)`nprintf %s `"`$dev_image`" > $(ConvertTo-BashLiteral "$($sync.RunDirectoryWsl)/image-id.txt")`n$reuseLoop"
}
@"
set -eu
cleanup() {
  original=`$?
  trap - EXIT INT TERM
  set +e
  cleanup_failed=0
  cleanup_resource container "`$runner_id" '$runnerName' "`$runner_attempted"
  cleanup_resource container "`$server_id" '$server' "`$server_attempted"
  cleanup_resource network "`$network_id" '$network' "`$network_attempted"
  [ "`$original" = 0 ] || exit "`$original"
  [ "`$cleanup_failed" = 0 ] || exit 75
}
cleanup_resource() {
  kind=`$1; id=`$2; name=`$3; attempted=`$4
  [ "`$attempted" = 1 ] || return 0
  if [ -z "`$id" ]; then
    echo "PC_TEST_CLEANUP kind=`$kind name=`$name label=$runId state=unresolved-creation" >&2
    cleanup_failed=1; return 0
  fi
  # Each inspect/removal is independently bounded, including timeout's kill
  # grace: at most 6 x 19s = 114s for all three resources within the guest's
  # 120s grace. A loaded shared engine has been measured at 19s per inspect,
  # so the former 5s bound reported live resources as unresolved and leaked them.
  case "`$id" in *[!0-9a-f]*|'') cleanup_failed=1; echo "PC_TEST_CLEANUP kind=`$kind name=`$name state=invalid-id" >&2; return 0 ;; esac
  if [ "`${#id}" != 64 ]; then cleanup_failed=1; echo "PC_TEST_CLEANUP kind=`$kind name=`$name state=invalid-id" >&2; return 0; fi
  format='{{.Id}}|{{index .Labels "premierecalendar.test"}}'
  if [ "`$kind" = container ]; then
    format='{{.Id}}|{{index .Config.Labels "premierecalendar.test"}}'
  fi
  identity=`$(timeout --kill-after=1s 18s docker "`$kind" inspect --format "`$format" "`$id" 2>/dev/null)
  if [ "`$identity" != "`$id|$runId" ]; then
    echo "PC_TEST_CLEANUP kind=`$kind id=`$id label=$runId state=unresolved-identity" >&2
    cleanup_failed=1; return 0
  fi
  if [ "`$kind" = container ]; then
    timeout --kill-after=1s 18s docker container rm -f "`$id" >/dev/null 2>&1
  else
    timeout --kill-after=1s 18s docker network rm "`$id" >/dev/null 2>&1
  fi
  if [ "`$?" = 0 ]; then echo "PC_TEST_CLEANUP kind=`$kind id=`$id label=$runId state=removed"
  else echo "PC_TEST_CLEANUP kind=`$kind id=`$id label=$runId state=unresolved-removal" >&2; cleanup_failed=1; fi
}
runner_id=''; server_id=''; network_id=''
runner_attempted=0; server_attempted=0; network_attempted=0
$(Get-HarnessPreflight -Image $DevImage)
command -v flock >/dev/null
command -v timeout >/dev/null
mkdir -p $quotedDirectories
$cacheBlock
$(Get-HarnessPreflight -Image $postgresImage -ImageVariable 'postgres_image' -SkipEngine)
exec 9>$(ConvertTo-BashLiteral "$wslRoot/artifacts/check.lock")
flock -n 9 || { echo 'Another build/test check owns this worktree. Use another worktree or wait.' >&2; exit 21; }
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
$($sync.SyncScript)
export POSTGRES_PASSWORD='$password' PGPASSWORD='$password' PC_TEST_PASSWORD='$password'
# Worker budget: auto = half of the engine's CPUs (other tenants share it), at least 2, at most 64.
pc_ncpu=`$(nproc 2>/dev/null || echo 2)
pc_budget=`$(( pc_ncpu / 2 )); [ "`$pc_budget" -ge 2 ] || pc_budget=2; [ "`$pc_budget" -le 64 ] || pc_budget=64
# PostgreSQL gets half of the runner budget (at least 2): every parallel test clones and queries its own database,
# and a fixed 2-CPU server starved under 6 test threads (Npgsql read timeouts).
pc_pg_cpus=`$(( $cpusWord / 2 )); [ "`$pc_pg_cpus" -ge 2 ] || pc_pg_cpus=2
printf 'PC_TEST_BUDGET engineCpus=%s runnerCpus=%s maxThreads=%s buildMaxCpuCount=%s postgresCpus=%s timeoutSeconds=%s\n' "`$pc_ncpu" "$cpusWord" $threadsWord "$buildCpusWord" "`$pc_pg_cpus" '$TimeoutSeconds'
created=0
for attempt in 0 1 2 3 4 5; do
  octet=`$(( ($subnetOctet + attempt * 7) % 250 + 1 ))
  network_attempted=1
  if network_id=`$(docker network create --label premierecalendar.test=$runId --subnet "10.141.`$octet.0/24" '$network' 2>/dev/null); then created=1; break; fi
done
[ "`$created" = 1 ] || { echo 'Could not create an isolated 10.141.x.0/24 network.' >&2; exit 22; }
server_attempted=1
server_id=`$(docker run --pull=never -d --name '$server' --label premierecalendar.test=$runId --network '$network' --cpus=`$pc_pg_cpus --tmpfs /var/lib/postgresql \
  -e POSTGRES_PASSWORD -e POSTGRES_USER=premierecalendar -e POSTGRES_DB=postgres \
  "`$postgres_image" -c max_connections=300)
runner_attempted=1
runner_id=`$(docker create --pull=never --name '$runnerName' --label premierecalendar.test=$runId --network '$network' --cpus=$cpusWord -e PC_BUDGET=`$pc_budget $srcMounts \
  -v $(ConvertTo-BashLiteral $wslPackages):/root/.nuget/packages $buildMount -w /src \
  -e DOTNET_CLI_HOME=/build/.sdk-cache/dotnet -e DOTNET_GENERATE_ASPNET_CERTIFICATE=false -e NUGET_PLUGINS_CACHE_PATH=/build/.sdk-cache/nuget-plugins -e PC_SDK_CACHE_OWNER=$(ConvertTo-BashLiteral $sdkCacheOwner) -e ArtifactsPath=/build -e NUGET_PACKAGES=/root/.nuget/packages -e NUGET_SCRATCH=/root/.nuget/packages/.scratch -e NUGET_HTTP_CACHE_PATH=/root/.nuget/packages/.http-cache -e DOTNET_NOLOGO=1 -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  -e PREMIERECALENDAR_TEST_POSTGRES='Host=$server;Port=5432;Database=postgres;Username=premierecalendar;Timeout=60;Command Timeout=120' \
  -e PREMIERECALENDAR_TEST_POSTGRES_PASSWORD_FILE=/tmp/pc-test-postgres-password \
  -e PC_TEST_PASSWORD -e PC_INNER_SCRIPT='$innerEncoded' "`$dev_image" sh /src/eng/run-with-sdk-cache.sh sh -c 'echo "`$PC_INNER_SCRIPT" | base64 -d | sh -s')
case "`$runner_id`$server_id`$network_id" in *[!0-9a-f]*|'') echo 'Invalid immutable test resource identity.' >&2; exit 28 ;; esac
[ "`${#runner_id}" = 64 ] && [ "`${#server_id}" = 64 ] && [ "`${#network_id}" = 64 ] || exit 28
echo "PC_TEST_RESOURCES runner=`$runner_id server=`$server_id network=`$network_id label=$runId"
# The build needs no database: start the runner now and let PostgreSQL's start-up overlap it. The runner waits for
# /tmp/pc-postgres-ready (created below once the server answers queries) before it runs its first suite.
docker start --attach "`$runner_id" </dev/null &
attach_pid=`$!
ready=0
deadline=`$((SECONDS + $PostgresReadySeconds))
while [ "`$SECONDS" -lt "`$deadline" ]; do
  if docker exec -e PGPASSWORD '$server' psql -h 127.0.0.1 -U premierecalendar -d postgres -Atc 'select 1' >/dev/null 2>&1; then ready=1; break; fi
  kill -0 "`$attach_pid" 2>/dev/null || break
  sleep 0.2
done
if [ "`$ready" = 1 ]; then
  until docker exec "`$runner_id" touch /tmp/pc-postgres-ready >/dev/null 2>&1; do
    kill -0 "`$attach_pid" 2>/dev/null || break
    sleep 0.2
  done
elif kill -0 "`$attach_pid" 2>/dev/null; then
  echo 'Disposable PostgreSQL did not become ready.' >&2; docker logs --tail 40 '$server' >&2 || true; exit 24
fi
wait "`$attach_pid"
"@
}

Write-Host "Results folder: $resultsHost"
Write-Host "Test run label: premierecalendar.test=$runId; resource names: $runnerName, $server, $network"
$started = Get-Date
$cleanupGraceSeconds = 120
$remaining = [int][Math]::Floor($TimeoutSeconds - $budgetClock.Elapsed.TotalSeconds)
if ($remaining -lt ($cleanupGraceSeconds + 11)) { throw 'Insufficient total test deadline remains for execution, cleanup and drain.' }
try {
    $run = Invoke-HarnessMirroredWsl -BuildScript $buildScript -Context $context -Storage $storage -WslRoot $wslRoot -RunId $runId `
        -SourceMount $SourceMount -WslDistribution $WslDistribution -CleanupGraceSeconds $cleanupGraceSeconds `
        -RemainingSeconds { [int][Math]::Floor($TimeoutSeconds - $budgetClock.Elapsed.TotalSeconds) }
    $exitCode = $run.ExitCode
} catch {
    [Console]::Error.WriteLine("Test resources with label premierecalendar.test=$runId may remain: $runnerName, $server, $network. Guest cleanup is unconfirmed; inspect exact ownership before removal.")
    throw
}
if ($exitCode -eq 60) {
    # The guest found a recorded passing run of identical inputs on this image before it took the lock or started a container.
    $reused = @($script:cacheCandidates | Where-Object { $_.Name -ceq $run.ReuseFolder }) | Select-Object -First 1
    if (-not $reused) { throw 'The reuse decision named a results folder that was not offered.' }
    Write-Host "REUSED passing results from $($reused.Folder) (key $($reused.Key.Substring(0, 16))); no container was started. Use -NoCache to run anyway."
    Write-Host ("Total wall time: {0} s. Results: {1}" -f [int] ((Get-Date) - $started).TotalSeconds, $reused.Folder)
    Write-Host "All requested test projects passed ($($suites -join ', ')), reused."
    return
}
if ($exitCode -eq 0 -and $cacheEnabled -and $run.ImageId -match '^sha256:[0-9a-f]{64}$' -and (Test-HarnessResultsPassed $resultsHost @($suites))) {
    Write-HarnessResultKey $resultsHost $script:cacheInputs $script:cacheInputsHash $run.ImageId @($suites)
}
Write-Host ("Total wall time: {0} s. Results: {1}" -f [int] ((Get-Date) - $started).TotalSeconds, $resultsHost)
if ($exitCode -ne 0) { throw "Tests did not pass (exit code $exitCode; see the messages above)." }
Write-Host "All requested test projects passed ($($suites -join ', '))."
