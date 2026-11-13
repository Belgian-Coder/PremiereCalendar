#Requires -Version 7.0
<#
.SYNOPSIS
Runs the PremiereCalendar test projects against a throwaway PostgreSQL server.

.DESCRIPTION
Every test in this repository runs against PostgreSQL. This script starts a disposable postgres container
(the image pinned in compose.yaml, data on tmpfs, max_connections=300) on a private Docker network inside the
Ubuntu-24.04 WSL distribution, runs `dotnet test` for the requested projects inside premierecalendar-dev-app:latest
(build it with eng\wsl-docker.ps1 -Action build), and always removes the server and the network afterwards.

It works from any folder: all paths are derived from the script location, and the repository is mounted into the
container as /src. The network is created with an explicit subnet because the Docker default address pools are
exhausted on the development machine.

.PARAMETER Project
Test projects to run, by short name. Default: UnitTests, ComponentTests, IntegrationTests.
BrowserTests need Playwright browsers and are not part of the default set.

.PARAMETER Filter
Optional dotnet test --filter expression.

.PARAMETER ExtraTestArguments
Extra arguments appended to `dotnet test`, for example '-p:UseRuntimeAsync=false'.

.PARAMETER BuildVolume
Optional named Docker volume mounted at /PremiereCalendar.Build (the Directory.Build.props artifacts path) so that
repeated runs build incrementally. Without it every run builds from scratch inside the container.

.EXAMPLE
pwsh -File D:\Projects\PremiereCalendar\eng\Test-WithPostgres.ps1 -Project UnitTests -Filter "FullyQualifiedName~ViewSync"
#>
[CmdletBinding()]
param(
    [ValidateSet('UnitTests', 'ComponentTests', 'IntegrationTests', 'BrowserTests')]
    [string[]] $Project = @('UnitTests', 'ComponentTests', 'IntegrationTests'),
    [string] $Filter,
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',
    [ValidateRange(1, 64)]
    [int] $Cpus = 4,
    [string[]] $ExtraTestArguments = @(),
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')]
    [string] $BuildVolume,
    [string] $DevImage = 'premierecalendar-dev-app:latest',
    [string] $NugetVolume = 'premierecalendar-dev_nuget-cache'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$composePath = Join-Path $root 'compose.yaml'
$composeText = Get-Content -LiteralPath $composePath -Raw
if ($composeText -notmatch '(?m)^\s*image:\s*(postgres:\S+@sha256:[0-9a-f]{64})\s*$') {
    throw "Could not find the digest-pinned postgres image in $composePath."
}
$postgresImage = $Matches[1]

$wsl = Join-Path $env:SystemRoot 'System32\wsl.exe'
function Invoke-Wsl([string] $Script) {
    # The script travels base64-encoded so no quoting layer can alter it.
    $encoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($Script -replace "`r`n", "`n")))
    & $wsl -d Ubuntu-24.04 -- bash -c "echo $encoded | base64 -d | bash -s" | Out-Host
    return [int] $LASTEXITCODE
}

$wslRoot = (& $wsl -d Ubuntu-24.04 -- wslpath -a ($root -replace '\\', '/')).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($wslRoot)) { throw 'Could not resolve the repository inside Ubuntu-24.04.' }

$runId = [Guid]::NewGuid().ToString('N').Substring(0, 10)
$network = "pcpg-net-$runId"
$server = "pcpg-db-$runId"
$password = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(16)).ToLowerInvariant()
$subnetOctet = Get-Random -Minimum 1 -Maximum 250

$projectList = ($Project | ForEach-Object { "tests/PremiereCalendar.$_/PremiereCalendar.$_.csproj" }) -join ' '
$testArguments = @('-c', $Configuration, '--nologo') + $ExtraTestArguments
if (-not [string]::IsNullOrWhiteSpace($Filter)) { $testArguments += @('--filter', $Filter) }
$quotedTestArguments = ($testArguments | ForEach-Object { "'" + ($_ -replace "'", "'\''") + "'" }) -join ' '
$buildMount = if ($BuildVolume) { "-v '$BuildVolume':/PremiereCalendar.Build" } else { '' }

$innerScript = @"
printf %s "`$PC_TEST_PASSWORD" > /tmp/pc-test-postgres-password
status=0
for project in $projectList; do
  echo "=== dotnet test `$project"
  dotnet test "`$project" $quotedTestArguments || status=1
done
exit `$status
"@
$innerEncoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($innerScript -replace "`r`n", "`n")))

$startScript = @"
set -u
docker image inspect '$DevImage' >/dev/null 2>&1 || { echo "Image $DevImage not found. Build it first: pwsh -File $root\eng\wsl-docker.ps1 -Action build" >&2; exit 20; }
docker volume inspect '$NugetVolume' >/dev/null 2>&1 || { echo "Docker volume $NugetVolume not found (run eng\wsl-docker.ps1 -Action build once)." >&2; exit 21; }
created=0
for attempt in 0 1 2 3 4 5; do
  octet=`$(( ($subnetOctet + attempt * 7) % 250 + 1 ))
  if docker network create --subnet "10.141.`$octet.0/24" '$network' >/dev/null 2>&1; then created=1; break; fi
done
[ "`$created" = 1 ] || { echo 'Could not create a Docker network with an explicit 10.141.x.0/24 subnet.' >&2; exit 22; }
docker run -d --name '$server' --network '$network' --cpus=2 --tmpfs /var/lib/postgresql \
  -e POSTGRES_USER=premierecalendar -e POSTGRES_DB=postgres -e POSTGRES_PASSWORD='$password' \
  '$postgresImage' -c max_connections=300 >/dev/null || exit 23
ready=0
for i in `$(seq 1 90); do
  if docker exec '$server' pg_isready -U premierecalendar -d postgres >/dev/null 2>&1; then ready=1; break; fi
  sleep 2
done
[ "`$ready" = 1 ] || { echo 'The throwaway PostgreSQL server did not become ready.' >&2; docker logs --tail 40 '$server' >&2; exit 24; }
# pg_isready can pass during the image's init-time restart; wait until a TCP login works on the final server.
for i in `$(seq 1 30); do
  docker exec -e PGPASSWORD='$password' '$server' psql -h 127.0.0.1 -U premierecalendar -d postgres -Atc 'select 1' >/dev/null 2>&1 && break
  sleep 1
done
docker run --rm --network '$network' --cpus=$Cpus -v '$wslRoot':/src -v '$NugetVolume':/root/.nuget/packages $buildMount -w /src \
  -e PREMIERECALENDAR_TEST_POSTGRES='Host=$server;Port=5432;Database=postgres;Username=premierecalendar' \
  -e PREMIERECALENDAR_TEST_POSTGRES_PASSWORD_FILE=/tmp/pc-test-postgres-password \
  -e PC_TEST_PASSWORD='$password' -e PC_INNER_SCRIPT='$innerEncoded' '$DevImage' sh -c 'echo "`$PC_INNER_SCRIPT" | base64 -d | sh -s'
"@

$stopScript = @"
docker rm -f '$server' >/dev/null 2>&1
docker network rm '$network' >/dev/null 2>&1
exit 0
"@

$exitCode = 1
try {
    $exitCode = Invoke-Wsl $startScript
}
finally {
    # Always remove the server and its network, even when the run fails or is interrupted.
    [void](Invoke-Wsl $stopScript)
}
if ($exitCode -ne 0) { throw "Tests did not pass (exit code $exitCode)." }
Write-Host 'All requested test projects passed.'
