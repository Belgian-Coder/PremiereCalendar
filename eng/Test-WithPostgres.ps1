#Requires -Version 7.0
<#
.SYNOPSIS
Runs selected tests against disposable PostgreSQL using the existing WSL Docker SDK image.
.DESCRIPTION
No service startup or image build is implicit. Builds persist inside this checkout, packages are shared only
with other worktrees of this project. -Plan reports paths and arguments without invoking WSL or Docker.
BrowserTests remain opt-in and require a prepared Playwright-compatible image.
#>
[CmdletBinding()]
param(
    [ValidateSet('UnitTests', 'ComponentTests', 'IntegrationTests', 'BrowserTests')]
    [ValidateNotNullOrEmpty()]
    [string[]] $Project = @('UnitTests', 'ComponentTests', 'IntegrationTests'),
    [string] $Filter,
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',
    [ValidateRange(1, 64)]
    [int] $Cpus = 4,
    [string[]] $ExtraTestArguments = @(),
    [switch] $NoRuntimeAsync,
    [switch] $NoRestore,
    [switch] $NoBuild,
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
$buildRoot = if ($NoRuntimeAsync) { $context.BuildRoot + '-no-runtime-async' } else { $context.BuildRoot }
$composeText = Get-Content -LiteralPath (Join-Path $context.Root 'compose.yaml') -Raw
if ($composeText -notmatch '(?m)^\s*image:\s*(postgres:\S+@sha256:[0-9a-f]{64})\s*$') {
    throw 'Could not find the digest-pinned postgres image in compose.yaml.'
}
$postgresImage = $Matches[1]
$runId = [Guid]::NewGuid().ToString('N').Substring(0, 10)
$resultsDirectory = "artifacts/test-results/$runId"
$testArguments = @('-c', $Configuration, '--nologo', '--results-directory', "/src/$resultsDirectory", '--logger', 'trx;LogFilePrefix=tests') + $ExtraTestArguments
if ($NoRuntimeAsync) { $testArguments += '-p:UseRuntimeAsync=false' }
if ($NoRestore) { $testArguments += '--no-restore' }
if ($NoBuild) { $testArguments += '--no-build' }
if (-not [string]::IsNullOrWhiteSpace($Filter)) { $testArguments += @('--filter', $Filter) }
$quotedTestArguments = ($testArguments | ForEach-Object { ConvertTo-BashLiteral $_ }) -join ' '
if ($Plan) {
    [pscustomobject]@{
        Projects = $Project
        Arguments = $testArguments
        BuildStorage = if ($BuildVolume) { $BuildVolume } else { $buildRoot }
        PackageStorage = if ($NugetVolume) { $NugetVolume } else { $context.PackageRoot }
        Results = Join-Path $context.Root $resultsDirectory
        PostgreSqlImage = $postgresImage
        DevImage = $DevImage
    }
    return
}

$wslRoot = ConvertTo-WslPath $context.Root
$wslBuild = if ($BuildVolume) { $BuildVolume } else { ConvertTo-WslPath $buildRoot }
$wslPackages = if ($NugetVolume) { $NugetVolume } else { ConvertTo-WslPath $context.PackageRoot }
$bindDirectories = @("$wslRoot/artifacts")
if (-not $BuildVolume) { $bindDirectories += $wslBuild }
if (-not $NugetVolume) { $bindDirectories += $wslPackages }
$quotedDirectories = ($bindDirectories | ForEach-Object { ConvertTo-BashLiteral $_ }) -join ' '
$network = "pcpg-net-$runId"
$server = "pcpg-db-$runId"
$runner = "pcpg-test-$runId"
$password = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(16)).ToLowerInvariant()
$subnetOctet = Get-Random -Minimum 1 -Maximum 250
$projectList = ($Project | ForEach-Object { "tests/PremiereCalendar.$_/PremiereCalendar.$_.csproj" }) -join ' '
$innerScript = @"
set -eu
umask 077
printf %s "`$PC_TEST_PASSWORD" > /tmp/pc-test-postgres-password
for project in $projectList; do
  echo "Testing `$project"
  dotnet test "`$project" $quotedTestArguments
 done
"@
$innerEncoded = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($innerScript -replace "`r`n", "`n")))
# A single WSL shell owns cleanup. Passwords travel on stdin and inherited environment variables, never arguments
# or WSL host temporary files. No production or development database is joined.
$script = @"
set -eu
cleanup() {
  docker rm -f '$runner' >/dev/null 2>&1 || true
  docker rm -f '$server' >/dev/null 2>&1 || true
  docker network rm '$network' >/dev/null 2>&1 || true
}
docker info >/dev/null
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
[ "`$ready" = 1 ] || { echo 'Disposable PostgreSQL did not become ready.' >&2; exit 24; }
docker run --rm --name '$runner' --network '$network' --cpus=$Cpus -v $(ConvertTo-BashLiteral $wslRoot):/src \
  -v $(ConvertTo-BashLiteral $wslPackages):/root/.nuget/packages -v $(ConvertTo-BashLiteral $wslBuild):/build -w /src \
  -e ArtifactsPath=/build -e NUGET_PACKAGES=/root/.nuget/packages -e PREMIERECALENDAR_TEST_POSTGRES='Host=$server;Port=5432;Database=postgres;Username=premierecalendar' \
  -e PREMIERECALENDAR_TEST_POSTGRES_PASSWORD_FILE=/tmp/pc-test-postgres-password \
  -e PC_TEST_PASSWORD -e PC_INNER_SCRIPT='$innerEncoded' '$DevImage' sh -c 'echo "`$PC_INNER_SCRIPT" | base64 -d | sh -s' </dev/null
"@
Invoke-HarnessWsl $script
Write-Host "All requested test projects passed. Results: $(Join-Path $context.Root $resultsDirectory)"
