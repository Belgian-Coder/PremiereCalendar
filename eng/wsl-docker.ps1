#Requires -Version 7.0
<#
.SYNOPSIS
Runs the development Docker Compose stack, a scoped build check and the test helper inside Ubuntu 24.04 on WSL.

.DESCRIPTION
The Windows host has no .NET SDK or PostgreSQL; the development app (SDK image with `dotnet watch`) and its
PostgreSQL database run with Docker Engine inside the Ubuntu-24.04 WSL distribution, using compose.dev.yaml.
It works from any folder: the repository is found from the script location (so it also works in a git worktree), and
a worktree gets its own Compose project name and build directories.

The Docker daemon must already be running; this script never starts WSL services or Docker. Add -Plan to print the
command without invoking WSL or Docker.

Actions:
  up     build and start the development stack (http://localhost:5299, PostgreSQL on 127.0.0.1:55432)
  down   stop the stack, keeping its named volumes
  build  rebuild the development image (premierecalendar-dev-app:latest, also used by Test-WithPostgres.ps1)
  check  compile one project (-Project App, the default, or a test project) in the development image; no PostgreSQL
  test   run eng\Test-WithPostgres.ps1 (project/filter, distro, cache/profile and scheduling budgets are forwarded)
  logs   show the last 200 log lines of the app and database
  reset  remove only this checkout's development containers and named development volumes (needs permission)

The first up creates the ignored development password file .secrets\dev-postgres-password with a random value
(it is never overwritten and never printed).

.EXAMPLE
pwsh -File D:\Projects\PremiereCalendar\eng\wsl-docker.ps1 check -Project App -Plan

.EXAMPLE
pwsh -File D:\Projects\PremiereCalendar\eng\wsl-docker.ps1 up
#>
[CmdletBinding()]
param(
    [ValidateSet('up', 'down', 'build', 'check', 'test', 'logs', 'reset')]
    [string] $Action = 'up',
    [Alias('Suite')]
    [ValidateSet('App', 'All', 'Unit', 'UnitTests', 'Component', 'ComponentTests', 'Integration', 'IntegrationTests', 'Browser', 'BrowserTests')]
    [string] $Project = 'App',
    [string] $Filter,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')]
    [string] $WslDistribution = 'Ubuntu-24.04',
    [ValidateRange(1, 64)][int] $Cpus = 2,
    [ValidateRange(1, 64)][int] $BuildMaxCpuCount = 2,
    [string] $MaxThreads = '2',
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')][string] $BuildVolume,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')][string] $NugetVolume,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.:/@-]*$')][string] $DevImage = 'premierecalendar-dev-app:latest',
    [ValidateSet('musl', 'glibc')][string] $CacheFlavor,
    [switch] $NoRuntimeAsync,
    [switch] $Plan
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Harness.Common.ps1')
$context = Get-HarnessContext (Split-Path -Parent $PSScriptRoot)
if ($Project -notin 'App', 'All') { $Project = ($Project -replace 'Tests$', '') + 'Tests' }

if ($Action -eq 'test') {
    $arguments = @{ Plan = $Plan; WslDistribution = $WslDistribution; Cpus = $Cpus; BuildMaxCpuCount = $BuildMaxCpuCount;
        MaxThreads = $MaxThreads; DevImage = $DevImage; NoRuntimeAsync = $NoRuntimeAsync }
    foreach ($name in @('BuildVolume', 'NugetVolume', 'CacheFlavor')) {
        $value = Get-Variable -Name $name -ValueOnly
        if ($value) { $arguments[$name] = $value }
    }
    if ($Project -ne 'App') { $arguments.Project = @($Project) }
    if ($Filter) { $arguments.Filter = $Filter }
    & (Join-Path $PSScriptRoot 'Test-WithPostgres.ps1') @arguments
    return
}
if ($Filter) { throw '-Filter is supported only with -Action test.' }
if ($Project -eq 'All') { throw '-Project All is supported only with -Action test.' }
if (-not $CacheFlavor) {
    if ($DevImage -ne 'premierecalendar-dev-app:latest') { throw 'A custom -DevImage requires explicit -CacheFlavor musl or glibc.' }
    $CacheFlavor = 'musl'
}
if ($DevImage -eq 'premierecalendar-dev-app:latest' -and $CacheFlavor -ne 'musl') { throw 'The default development image requires -CacheFlavor musl.' }
if ($Action -ne 'check' -and ($NoRuntimeAsync -or $DevImage -ne 'premierecalendar-dev-app:latest' -or $CacheFlavor -ne 'musl')) {
    throw 'Custom image/ABI and -NoRuntimeAsync are supported only with check/test; Compose uses the musl development image.'
}
$storage = Get-HarnessStorage $context -CacheFlavor $CacheFlavor -NoRuntimeAsync:$NoRuntimeAsync -Watcher:($Action -ne 'check') -BuildVolume $BuildVolume -NugetVolume $NugetVolume
$runtimeArgument = if ($NoRuntimeAsync) { ' -p:UseRuntimeAsync=false' } else { '' }
$projectPath = if ($Project -eq 'App') { 'PremiereCalendar/PremiereCalendar.csproj' } else { "tests/PremiereCalendar.$Project/PremiereCalendar.$Project.csproj" }
$compose = 'docker compose -p ' + $context.ComposeProject + ' -f compose.dev.yaml'
$commands = @{
    up = "$compose up --build --pull never -d --wait"
    down = "$compose down"
    build = "$compose build"
    check = "docker run --rm --pull=never --cpus=$Cpus -v __ROOT__:/src -v __BUILD__:/build -v __PACKAGES__:/root/.nuget/packages -w /src -e ArtifactsPath=/build -e NUGET_PACKAGES=/root/.nuget/packages -e NUGET_SCRATCH=/root/.nuget/packages/.scratch -e NUGET_HTTP_CACHE_PATH=/root/.nuget/packages/.http-cache `"`$dev_image`" dotnet build $projectPath -c Release --nologo -maxcpucount:$BuildMaxCpuCount$runtimeArgument"
    logs = "$compose logs --tail=200 app database"
    reset = "$compose down --volumes --remove-orphans"
}
if ($Plan) {
    [pscustomobject]@{ Action = $Action; Project = $Project; Context = $context; Command = $commands[$Action];
        WslDistribution = $WslDistribution; CacheFlavor = $CacheFlavor; DevImage = $DevImage;
        BuildStorage = $storage.BuildVolume; PackageStorage = $storage.NugetVolume; Cpus = $Cpus; BuildMaxCpuCount = $BuildMaxCpuCount;
        NugetHttpCache = '/root/.nuget/packages/.http-cache' }
    return
}
$wslRoot = ConvertTo-WslPath $context.Root -WslDistribution $WslDistribution
$preflight = if ($Action -eq 'check') { Get-HarnessPreflight -Image $DevImage } else { Get-HarnessPreflight }
if ($Action -eq 'up') {
    $composeText = Get-Content -LiteralPath (Join-Path $context.Root 'compose.dev.yaml') -Raw
    if ($composeText -notmatch '(?m)^\s*image:\s*(postgres:\S+@sha256:[0-9a-f]{64})\s*$') { throw 'Could not find the digest-pinned development PostgreSQL image.' }
    $preflight = Get-HarnessPreflight -Image $Matches[1] -ImageVariable 'postgres_image'
}
# Refuse an unavailable/wrong engine before writing even the native dev secret.
if ($Action -in 'up', 'build') { Invoke-HarnessWsl "set -eu`n$preflight" -WslDistribution $WslDistribution }
# The development stack reads its PostgreSQL password from a Docker secret file that is not committed. Creating it is a
# local file write only: the image build needs no password, and an existing file is never touched.
if ($Action -eq 'up') {
    $secretFile = Join-Path $context.Root '.secrets/dev-postgres-password'
    if (-not (Test-Path -LiteralPath $secretFile)) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $secretFile) -Force | Out-Null
        $password = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24)).ToLowerInvariant()
        # No trailing newline: the password file is read verbatim.
        [IO.File]::WriteAllText($secretFile, $password, [Text.UTF8Encoding]::new($false))
        Write-Host "Created the development password file $secretFile"
    }
}
$wslBuild = $storage.BuildVolume
$wslPackages = $storage.NugetVolume
$command = $commands[$Action].Replace('__ROOT__', (ConvertTo-BashLiteral $wslRoot)).
    Replace('__BUILD__', (ConvertTo-BashLiteral $wslBuild)).Replace('__PACKAGES__', (ConvertTo-BashLiteral $wslPackages))
$lock = if ($Action -eq 'check') {
    "mkdir -p artifacts; exec 9>artifacts/check.lock; flock -n 9 || { echo 'Another check owns this worktree.' >&2; exit 21; }"
} else { '' }
$volumes = if ($Action -in 'up', 'build') {
    "docker volume create $(ConvertTo-BashLiteral $wslBuild) >/dev/null`ndocker volume create $(ConvertTo-BashLiteral $wslPackages) >/dev/null"
} else { '' }
$script = @"
set -eu
cd $(ConvertTo-BashLiteral $wslRoot)
$preflight
export PREMIERECALENDAR_NUGET_VOLUME=$(ConvertTo-BashLiteral $wslPackages)
export PREMIERECALENDAR_BUILD_VOLUME=$(ConvertTo-BashLiteral $wslBuild)
export PREMIERECALENDAR_CPUS=$Cpus
$lock
$volumes
$command
"@
Invoke-HarnessWsl $script -WslDistribution $WslDistribution
