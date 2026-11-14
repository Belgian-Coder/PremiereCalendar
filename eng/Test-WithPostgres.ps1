#Requires -Version 7.0
<#
.SYNOPSIS
Runs the PremiereCalendar xUnit.net 4 test projects against a throwaway PostgreSQL server.

.DESCRIPTION
USAGE (works from any folder; use the absolute path of the script):

  # all suites (Unit, Component, Integration), results in <repo>\TestResults\<timestamp>\
  pwsh -File D:\Projects\PremiereCalendar\eng\Test-WithPostgres.ps1

  # one suite
  pwsh -File D:\Projects\PremiereCalendar\eng\Test-WithPostgres.ps1 -Suite Unit

  # a subset: every test whose fully qualified name contains "ViewSync" (several values = OR)
  pwsh -File D:\Projects\PremiereCalendar\eng\Test-WithPostgres.ps1 -Suite Unit -Filter ViewSync
  pwsh -File D:\Projects\PremiereCalendar\eng\Test-WithPostgres.ps1 -Filter ViewSync,Wikimedia

  # an xUnit.net query filter (assembly/namespace/class/method[trait=value]); see https://xunit.net/docs/query-filter-language
  pwsh -File D:\Projects\PremiereCalendar\eng\Test-WithPostgres.ps1 -Suite Integration -QueryFilter '/*/*/PostgresResponseCacheTests/*'

  # runner switches (any xUnit.net runner option), for example no parallelism or a thread count
  pwsh -File D:\Projects\PremiereCalendar\eng\Test-WithPostgres.ps1 -Suite Unit -Parallel none
  pwsh -File D:\Projects\PremiereCalendar\eng\Test-WithPostgres.ps1 -Suite Unit -MaxThreads 2 -RunnerArguments:-longRunning,30

Lists (-Suite, -Filter, -RunnerArguments) are comma separated, also when the script is started with `pwsh -File`: -Suite Unit,Component.

What it does: starts a disposable postgres container (the image pinned in compose.yaml, data on tmpfs,
max_connections=300) on a private Docker network inside the Ubuntu-24.04 WSL distribution, builds the requested test
projects inside premierecalendar-dev-app:latest (build that image with eng\wsl-docker.ps1 -Action build), runs each test
executable directly (xUnit.net 4 projects are executables; this skips the `dotnet test` start-up cost) and always
removes the server and the network afterwards. The repository is mounted into the container as /src. The network
is created with an explicit subnet because the Docker default address pools are exhausted on the development machine.

Results: every suite writes <Suite>.trx and <Suite>.ctrf.json (CTRF = Common Test Report Format, JSON) plus the
console log <Suite>.log to <repo>\TestResults\<yyyyMMdd-HHmmss>\ ; TestResults\ is git-ignored. The console shows
the xUnit.net summary and the wall time of each suite; the script exits non-zero when a suite fails.

Incremental builds: the build output lives in the Docker volume premierecalendar-test-build-<id> (one per checkout,
so worktrees never mix). Remove it with: wsl.exe -d Ubuntu-24.04 -- docker volume rm <name> (the name is printed).
Use -FreshBuild to build from scratch into a throwaway location, -NoBuild to run the last build unchanged.

Plain `dotnet test` also works (global.json selects Microsoft Testing Platform), for example inside the dev container:
  dotnet test tests/PremiereCalendar.UnitTests/PremiereCalendar.UnitTests.csproj -c Release --filter-class '*ViewSync*'
It needs PREMIERECALENDAR_TEST_POSTGRES set (see docs/Testing.md); this script sets everything up for you.

.PARAMETER Suite
Test suites to run: Unit, Component, Integration, Browser or All (Unit+Component+Integration+Browser). The full project
names (UnitTests, ...) are accepted too. Default: Unit, Component, Integration. Browser needs Playwright browsers,
which the Alpine dev image does not have, so it is never part of the default set. (Alias: -Project.)

.PARAMETER Filter
One or more substrings of the fully qualified test method name (namespace.class.method), matched case-sensitively and
combined with OR. Example: -Filter ViewSync. The old VSTest form "FullyQualifiedName~ViewSync" is still accepted.

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

.PARAMETER NoRuntimeAsync
Builds and runs the tests with -p:UseRuntimeAsync=false (the .NET 11 runtime-async compiler feature is on by default).

.PARAMETER NoBuild
Skips the build and runs the output of the previous run of the same checkout.

.PARAMETER FreshBuild
Builds from scratch instead of using the per-checkout build volume.

.PARAMETER Cpus
CPU limit of the test container (default 4); the PostgreSQL container gets 2.
#>
[CmdletBinding()]
param(
    [Alias('Project')]
    [string[]] $Suite = @('Unit', 'Component', 'Integration'),
    [string[]] $Filter,
    [string[]] $QueryFilter,
    [ValidateSet('none', 'collections', 'all')]
    [string] $Parallel,
    [string] $MaxThreads,
    [string[]] $RunnerArguments = @(),
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',
    [ValidateRange(1, 64)]
    [int] $Cpus = 4,
    [switch] $NoRuntimeAsync,
    [switch] $NoBuild,
    [switch] $FreshBuild,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')]
    [string] $BuildVolume,
    [string] $DevImage = 'premierecalendar-dev-app:latest',
    [string] $NugetVolume = 'premierecalendar-dev_nuget-cache'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

# `pwsh -File script.ps1 -Suite Unit,Component` delivers "Unit,Component" as ONE string; accept both forms.
$Suite = @($Suite | ForEach-Object { $_ -split ',' } | Where-Object { $_ })
if ($Filter) { $Filter = @($Filter | ForEach-Object { $_ -split ',' } | Where-Object { $_ }) }
$RunnerArguments = @($RunnerArguments | ForEach-Object { $_ -split ',' } | Where-Object { $_ })

# Suite names: Unit / UnitTests, ..., All.
$known = @('Unit', 'Component', 'Integration', 'Browser')
$suites = [System.Collections.Generic.List[string]]::new()
foreach ($item in $Suite) {
    $name = ($item -replace 'Tests$', '')
    if ($name -ieq 'All') { foreach ($k in $known) { if (-not $suites.Contains($k)) { $suites.Add($k) } }; continue }
    $match = $known | Where-Object { $_ -ieq $name }
    if (-not $match) { throw "Unknown suite '$item'. Use Unit, Component, Integration, Browser or All." }
    if (-not $suites.Contains($match)) { $suites.Add($match) }
}
if ($suites.Count -eq 0) { throw 'No suite selected.' }
if ($Filter -and $QueryFilter) { throw '-Filter and -QueryFilter cannot be combined.' }

$composePath = Join-Path $root 'compose.yaml'
$composeText = Get-Content -LiteralPath $composePath -Raw
if ($composeText -notmatch '(?m)^\s*image:\s*(postgres:\S+@sha256:[0-9a-f]{64})\s*$') {
    throw "Could not find the digest-pinned postgres image in $composePath."
}
$postgresImage = $Matches[1]

$wsl = Join-Path $env:SystemRoot 'System32\wsl.exe'
function Invoke-Wsl([string] $Script) {
    # The script travels over stdin: no quoting layer can alter it, and (unlike a command-line argument) it is not
    # visible in any process list. It may contain the throwaway password.
    # PowerShell terminates piped text with CRLF; a trailing comment line absorbs that stray carriage return.
    ((($Script -replace "`r`n", "`n").TrimEnd()) + "`n# end of script") | & $wsl -d Ubuntu-24.04 -- bash -s | Out-Host
    return [int] $LASTEXITCODE
}
function ConvertTo-ShellWord([string] $Value) { return "'" + ($Value -replace "'", "'\''") + "'" }

$wslRoot = (& $wsl -d Ubuntu-24.04 -- wslpath -a ($root -replace '\\', '/')).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($wslRoot)) { throw 'Could not resolve the repository inside Ubuntu-24.04.' }

$runId = [Guid]::NewGuid().ToString('N').Substring(0, 10)
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$network = "pcpg-net-$runId"
$server = "pcpg-db-$runId"
$password = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(16)).ToLowerInvariant()
$subnetOctet = Get-Random -Minimum 1 -Maximum 250
# The password reaches the containers through owner-only env-files inside the WSL distribution, never through a
# `-e NAME=secret` argument (those are visible in process lists). Each container gets only the variables it needs.
# Both files are always deleted below.
$dbEnvFile = "/tmp/pcpg-env-$runId-db"
$testEnvFile = "/tmp/pcpg-env-$runId-test"

# One build volume per checkout: msbuild's incremental checks compare timestamps, which must not mix checkouts.
if (-not $BuildVolume -and -not $FreshBuild) {
    $sha = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($root.ToLowerInvariant()))
    $BuildVolume = 'premierecalendar-test-build-' + [Convert]::ToHexString($sha).Substring(0, 8).ToLowerInvariant()
}
$buildMount = if ($BuildVolume) { "-v '$BuildVolume':/PremiereCalendar.Build" } else { '' }
if ($BuildVolume) { Write-Host "Build volume: $BuildVolume" }

$resultsRelative = "TestResults/$stamp"
$resultsHost = Join-Path (Join-Path $root 'TestResults') $stamp
Write-Host "Results: $resultsHost"

# Runner arguments (xUnit.net in-process runner switches).
$runner = [System.Collections.Generic.List[string]]::new()
if ($Filter) {
    foreach ($f in $Filter) {
        $text = $f -replace '^FullyQualifiedName~', ''
        $runner.Add('-method'); $runner.Add("*$text*")
    }
}
if ($QueryFilter) { foreach ($q in $QueryFilter) { $runner.Add('-filter'); $runner.Add($q) } }
if ($Parallel) { $runner.Add('-parallelMode'); $runner.Add($Parallel) }
if ($MaxThreads) { $runner.Add('-maxThreads'); $runner.Add($MaxThreads) }
foreach ($a in $RunnerArguments) { $runner.Add($a) }
$runnerWords = ($runner | ForEach-Object { ConvertTo-ShellWord $_ }) -join ' '
$buildFlags = '-c ' + $Configuration + ' --nologo -v q' + $(if ($NoRuntimeAsync) { ' -p:UseRuntimeAsync=false' } else { '' })
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
  exe="/PremiereCalendar.Build/bin/$name/@@CONFIGLOWER@@/$name"
  if [ '@@DOBUILD@@' = 1 ]; then
    echo "=== build $name"
    t0=$(date +%s)
    dotnet build "$proj" @@BUILDFLAGS@@ || { echo "BUILD FAILED: $name"; status=1; summary="$summary\n$name: build failed"; continue; }
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
$inner = $innerTemplate.Replace('@@RESULTS@@', $resultsRelative).Replace('@@SUITES@@', $suiteList).Replace('@@CONFIGLOWER@@', $configLower).Replace('@@DOBUILD@@', $doBuild).Replace('@@BUILDFLAGS@@', $buildFlags).Replace('@@RUNNERWORDS@@', $runnerWords)
$innerEncoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($inner -replace "`r`n", "`n")))

$startScript = @"
set -u
umask 077
printf 'POSTGRES_PASSWORD=%s\nPGPASSWORD=%s\n' '$password' '$password' > '$dbEnvFile'
printf 'PC_TEST_PASSWORD=%s\n' '$password' > '$testEnvFile'
docker image inspect '$DevImage' >/dev/null 2>&1 || { echo "Image $DevImage not found. Build it first: pwsh -File $root\eng\wsl-docker.ps1 -Action build" >&2; exit 20; }
docker volume inspect '$NugetVolume' >/dev/null 2>&1 || { echo "Docker volume $NugetVolume not found (run eng\wsl-docker.ps1 -Action build once)." >&2; exit 21; }
created=0
for attempt in 0 1 2 3 4 5; do
  octet=`$(( ($subnetOctet + attempt * 7) % 250 + 1 ))
  if docker network create --subnet "10.141.`$octet.0/24" '$network' >/dev/null 2>&1; then created=1; break; fi
done
[ "`$created" = 1 ] || { echo 'Could not create a Docker network with an explicit 10.141.x.0/24 subnet.' >&2; exit 22; }
docker run -d --name '$server' --network '$network' --cpus=2 --tmpfs /var/lib/postgresql \
  --env-file '$dbEnvFile' -e POSTGRES_USER=premierecalendar -e POSTGRES_DB=postgres \
  '$postgresImage' -c max_connections=300 >/dev/null || exit 23
ready=0
for i in `$(seq 1 90); do
  if docker exec '$server' pg_isready -U premierecalendar -d postgres >/dev/null 2>&1; then ready=1; break; fi
  sleep 2
done
[ "`$ready" = 1 ] || { echo 'The throwaway PostgreSQL server did not become ready.' >&2; docker logs --tail 40 '$server' >&2; exit 24; }
# pg_isready can pass during the image's init-time restart; wait until a TCP login works on the final server.
for i in `$(seq 1 30); do
  docker exec --env-file '$dbEnvFile' '$server' psql -h 127.0.0.1 -U premierecalendar -d postgres -Atc 'select 1' >/dev/null 2>&1 && break
  sleep 1
done
docker run --rm --network '$network' --cpus=$Cpus -v '$wslRoot':/src -v '$NugetVolume':/root/.nuget/packages $buildMount -w /src \
  -e PREMIERECALENDAR_TEST_POSTGRES='Host=$server;Port=5432;Database=postgres;Username=premierecalendar' \
  -e PREMIERECALENDAR_TEST_POSTGRES_PASSWORD_FILE=/tmp/pc-test-postgres-password \
  -e DOTNET_NOLOGO=1 -e DOTNET_CLI_TELEMETRY_OPTOUT=1 \
  --env-file '$testEnvFile' -e PC_INNER_SCRIPT='$innerEncoded' '$DevImage' sh -c 'echo "`$PC_INNER_SCRIPT" | base64 -d | sh -s' </dev/null
"@

$stopScript = @"
docker rm -f '$server' >/dev/null 2>&1
docker network rm '$network' >/dev/null 2>&1
rm -f '$dbEnvFile' '$testEnvFile'
exit 0
"@

$exitCode = 1
$started = Get-Date
try {
    $exitCode = Invoke-Wsl $startScript
}
finally {
    # Always remove the server and its network, even when the run fails or is interrupted.
    [void](Invoke-Wsl $stopScript)
}
Write-Host ("Total wall time: {0} s. Results: {1}" -f [int] ((Get-Date) - $started).TotalSeconds, $resultsHost)
if ($exitCode -ne 0) { throw "Tests did not pass (exit code $exitCode)." }
Write-Host "All requested test suites passed ($($suites -join ', '))."
