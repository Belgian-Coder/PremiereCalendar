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
the requested test projects inside premierecalendar-dev-app:latest, runs each test executable directly (xUnit.net 4
projects are executables; this skips the `dotnet test` start-up cost) and removes the server, the runner and the
network when it exits, however it exits. The repository is mounted into the container as /src. The network is created
with an explicit subnet because the Docker default address pools can be exhausted. The test password is generated per
run and travels only over stdin and inherited environment variables, never through arguments or WSL temp files.

Nothing is started or built implicitly: the Docker daemon in Ubuntu-24.04 must already be running and the development
image must exist (build it with D:\Projects\PremiereCalendar\eng\wsl-docker.ps1 build). A second check in the same
worktree is refused by the lock instead of waiting.

Results: every suite writes <Suite>.trx and <Suite>.ctrf.json (CTRF = Common Test Report Format, JSON) plus the console
log <Suite>.log to <checkout>\artifacts\test-results\<yyyyMMdd-HHmmss>\ (artifacts\ is git-ignored). The console shows
the xUnit.net summary and the wall time of each suite and ends with a "Results:" line; the script exits non-zero when a
suite fails. All requested suites run even when an earlier one fails.

Incremental builds: the build output lives in this checkout's artifacts\build\linux (artifacts\build\linux-no-runtime-async
with -NoRuntimeAsync), so worktrees never mix and runtime-async modes never share objects. Linux NuGet packages are
shared through the primary project's artifacts\packages\linux. Use -FreshBuild to build from scratch into a throwaway
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
Overrides the maximum number of test threads: a number, default, unlimited or a multiplier such as 2x.

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

.PARAMETER Cpus
CPU limit of the test container (default 4); the PostgreSQL container gets 2.

.PARAMETER BuildVolume
Legacy override: a Docker volume name used instead of this checkout's artifacts\build\linux. It must belong exclusively
to this worktree and runtime-async mode.

.PARAMETER NugetVolume
Legacy override: a Docker volume name used instead of the shared artifacts\packages\linux directory.

.PARAMETER Plan
Prints the plan (projects, build and runner arguments, storage, results folder, images) and returns without invoking
WSL or Docker.
#>
[CmdletBinding()]
param(
    [Alias('Suite')]
    [string[]] $Project = @('UnitTests', 'ComponentTests', 'IntegrationTests'),
    [string[]] $Filter,
    [string[]] $QueryFilter,
    [ValidateSet('none', 'collections', 'all')]
    [string] $Parallel,
    [string] $MaxThreads,
    [Alias('ExtraTestArguments')]
    [string[]] $RunnerArguments = @(),
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',
    [ValidateRange(1, 64)]
    [int] $Cpus = 4,
    [switch] $NoRuntimeAsync,
    [switch] $NoRestore,
    [switch] $NoBuild,
    [switch] $FreshBuild,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')]
    [string] $BuildVolume,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.:/@-]*$')]
    [string] $DevImage = 'premierecalendar-dev-app:latest',
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')]
    [string] $NugetVolume,
    [switch] $Plan
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Harness.Common.ps1')
$context = Get-HarnessContext (Split-Path -Parent $PSScriptRoot)

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
$buildRoot = if ($NoRuntimeAsync) { $context.BuildRoot + '-no-runtime-async' } else { $context.BuildRoot }
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
if ($MaxThreads) { $runner.Add('-maxThreads'); $runner.Add($MaxThreads) }
foreach ($a in $RunnerArguments) { $runner.Add($a) }
$buildArguments = @('-c', $Configuration, '--nologo', '-v', 'q')
if ($NoRuntimeAsync) { $buildArguments += '-p:UseRuntimeAsync=false' }
if ($NoRestore) { $buildArguments += '--no-restore' }

if ($Plan) {
    [pscustomobject]@{
        Projects = @($suites | ForEach-Object { "$($_)Tests" })
        Build = -not $NoBuild
        BuildArguments = $buildArguments
        RunnerArguments = $runner.ToArray()
        BuildStorage = if ($FreshBuild) { 'container-local (discarded)' } elseif ($BuildVolume) { $BuildVolume } else { $buildRoot }
        PackageStorage = if ($NugetVolume) { $NugetVolume } else { $context.PackageRoot }
        Results = $resultsHost
        PostgreSqlImage = $postgresImage
        DevImage = $DevImage
    }
    return
}

$wslRoot = ConvertTo-WslPath $context.Root
$wslBuild = if ($BuildVolume) { $BuildVolume } else { ConvertTo-WslPath $buildRoot }
$wslPackages = if ($NugetVolume) { $NugetVolume } else { ConvertTo-WslPath $context.PackageRoot }
$bindDirectories = @("$wslRoot/artifacts")
if (-not $BuildVolume -and -not $FreshBuild) { $bindDirectories += $wslBuild }
if (-not $NugetVolume) { $bindDirectories += $wslPackages }
$quotedDirectories = ($bindDirectories | ForEach-Object { ConvertTo-BashLiteral $_ }) -join ' '
$buildMount = if ($FreshBuild) { '' } else { "-v $(ConvertTo-BashLiteral $wslBuild):/build" }
$runId = [Guid]::NewGuid().ToString('N').Substring(0, 10)
$network = "pcpg-net-$runId"
$server = "pcpg-db-$runId"
$runnerName = "pcpg-test-$runId"
$password = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(16)).ToLowerInvariant()
$subnetOctet = Get-Random -Minimum 1 -Maximum 250
$runnerWords = ($runner | ForEach-Object { ConvertTo-BashLiteral $_ }) -join ' '
$buildWords = ($buildArguments | ForEach-Object { ConvertTo-BashLiteral $_ }) -join ' '
$doBuild = if ($NoBuild) { '0' } else { '1' }
$suiteList = $suites -join ' '
$configLower = $Configuration.ToLowerInvariant()

$innerTemplate = @'
printf %s "$PC_TEST_PASSWORD" > /tmp/pc-test-postgres-password
mkdir -p '/src/@@RESULTS@@'
status=0
summary=''
for suite in @@SUITES@@; do
  name="PremiereCalendar.${suite}Tests"
  proj="tests/$name/$name.csproj"
  exe="/build/bin/$name/@@CONFIGLOWER@@/$name"
  if [ '@@DOBUILD@@' = 1 ]; then
    echo "=== build $name"
    t0=$(date +%s)
    dotnet build "$proj" @@BUILDWORDS@@ || { echo "BUILD FAILED: $name"; status=1; summary="$summary\n$name: build failed"; continue; }
    echo "build time: $(( $(date +%s) - t0 )) s"
  fi
  [ -x "$exe" ] || { echo "Missing test executable $exe (run without -NoBuild)"; status=1; summary="$summary\n$name: no executable"; continue; }
  echo "=== run $name"
  t0=$(date +%s)
  # The exit code travels through a file because busybox sh has no PIPESTATUS.
  { "$exe" -noLogo -result-trx "/src/@@RESULTS@@/$suite.trx" -result-ctrf "/src/@@RESULTS@@/$suite.ctrf.json" @@RUNNERWORDS@@ 2>&1; echo $? > /tmp/pc-suite-rc; } | tee "/src/@@RESULTS@@/$suite.log"
  rc=$(cat /tmp/pc-suite-rc)
  elapsed=$(( $(date +%s) - t0 ))
  [ "$rc" = 0 ] || status=1
  summary="$summary\n$name: exit $rc, ${elapsed} s"
done
printf "\n=== summary%b\n" "$summary"
exit $status
'@
$inner = $innerTemplate.Replace('@@RESULTS@@', $resultsRelative).Replace('@@SUITES@@', $suiteList).Replace('@@CONFIGLOWER@@', $configLower).
    Replace('@@DOBUILD@@', $doBuild).Replace('@@BUILDWORDS@@', $buildWords).Replace('@@RUNNERWORDS@@', $runnerWords)
$innerEncoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($inner -replace "`r`n", "`n")))

# A single WSL shell owns the lock and the cleanup. Passwords travel on stdin and inherited environment variables,
# never arguments or WSL host temporary files. No production or development database is joined.
$script = @"
set -eu
cleanup() {
  docker rm -f '$runnerName' >/dev/null 2>&1 || true
  docker rm -f '$server' >/dev/null 2>&1 || true
  docker network rm '$network' >/dev/null 2>&1 || true
}
docker info >/dev/null 2>&1 || { echo 'The Docker daemon in Ubuntu-24.04 is not running; helpers never start it. Start it yourself, then retry.' >&2; exit 25; }
docker image inspect '$DevImage' >/dev/null 2>&1 || { echo 'Build the development SDK image first with eng/wsl-docker.ps1 build.' >&2; exit 20; }
command -v flock >/dev/null
mkdir -p $quotedDirectories
exec 9>$(ConvertTo-BashLiteral "$wslRoot/artifacts/check.lock")
flock -n 9 || { echo 'Another build/test check owns this worktree. Use another worktree or wait.' >&2; exit 21; }
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM
export POSTGRES_PASSWORD='$password' PGPASSWORD='$password' PC_TEST_PASSWORD='$password'
created=0
for attempt in 0 1 2 3 4 5; do
  octet=`$(( ($subnetOctet + attempt * 7) % 250 + 1 ))
  if docker network create --subnet "10.141.`$octet.0/24" '$network' >/dev/null 2>&1; then created=1; break; fi
done
[ "`$created" = 1 ] || { echo 'Could not create an isolated 10.141.x.0/24 network.' >&2; exit 22; }
docker run -d --name '$server' --network '$network' --cpus=2 --tmpfs /var/lib/postgresql \
  -e POSTGRES_PASSWORD -e POSTGRES_USER=premierecalendar -e POSTGRES_DB=postgres \
  '$postgresImage' -c max_connections=300 >/dev/null
ready=0
for i in `$(seq 1 90); do
  if docker exec -e PGPASSWORD '$server' psql -h 127.0.0.1 -U premierecalendar -d postgres -Atc 'select 1' >/dev/null 2>&1; then ready=1; break; fi
  sleep 2
done
[ "`$ready" = 1 ] || { echo 'Disposable PostgreSQL did not become ready.' >&2; docker logs --tail 40 '$server' >&2 || true; exit 24; }
docker run --rm --name '$runnerName' --network '$network' --cpus=$Cpus -v $(ConvertTo-BashLiteral $wslRoot):/src \
  -v $(ConvertTo-BashLiteral $wslPackages):/root/.nuget/packages $buildMount -w /src \
  -e ArtifactsPath=/build -e NUGET_PACKAGES=/root/.nuget/packages -e DOTNET_NOLOGO=1 -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  -e PREMIERECALENDAR_TEST_POSTGRES='Host=$server;Port=5432;Database=postgres;Username=premierecalendar' \
  -e PREMIERECALENDAR_TEST_POSTGRES_PASSWORD_FILE=/tmp/pc-test-postgres-password \
  -e PC_TEST_PASSWORD -e PC_INNER_SCRIPT='$innerEncoded' '$DevImage' sh -c 'echo "`$PC_INNER_SCRIPT" | base64 -d | sh -s' </dev/null
"@

Write-Host "Results: $resultsHost"
$started = Get-Date
$exitCode = Invoke-HarnessWsl $script -ReturnExitCode
Write-Host ("Total wall time: {0} s. Results: {1}" -f [int] ((Get-Date) - $started).TotalSeconds, $resultsHost)
if ($exitCode -ne 0) { throw "Tests did not pass (exit code $exitCode; see the messages above)." }
Write-Host "All requested test projects passed ($($suites -join ', '))."
