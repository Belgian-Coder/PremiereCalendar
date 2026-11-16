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
  test   run eng\Test-WithPostgres.ps1 (-Project and -Filter are forwarded; use that script directly for more options)
  logs   show the last 200 log lines of the app and database
  reset  remove only this checkout's development containers and named development volumes (needs permission)

The first up or build creates the ignored development password file .secrets\dev-postgres-password with a random value
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
    [ValidateSet('App', 'Unit', 'UnitTests', 'Component', 'ComponentTests', 'Integration', 'IntegrationTests', 'Browser', 'BrowserTests')]
    [string] $Project = 'App',
    [string] $Filter,
    [switch] $Plan
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Harness.Common.ps1')
$context = Get-HarnessContext (Split-Path -Parent $PSScriptRoot)
if ($Project -ne 'App') { $Project = ($Project -replace 'Tests$', '') + 'Tests' }

if ($Action -eq 'test') {
    $arguments = @{ Plan = $Plan }
    if ($Project -ne 'App') { $arguments.Project = @($Project) }
    if ($Filter) { $arguments.Filter = $Filter }
    & (Join-Path $PSScriptRoot 'Test-WithPostgres.ps1') @arguments
    return
}
if ($Filter) { throw '-Filter is supported only with -Action test.' }
$projectPath = if ($Project -eq 'App') { 'PremiereCalendar/PremiereCalendar.csproj' } else { "tests/PremiereCalendar.$Project/PremiereCalendar.$Project.csproj" }
$compose = 'docker compose -p ' + $context.ComposeProject + ' -f compose.dev.yaml'
$commands = @{
    up = "$compose up --build -d --wait"
    down = "$compose down"
    build = "$compose build"
    check = "docker run --rm --cpus=4 -v __ROOT__:/src -v __BUILD__:/build -v __PACKAGES__:/root/.nuget/packages -v __CACHE__:/cache -w /src -e ArtifactsPath=/build -e NUGET_PACKAGES=/root/.nuget/packages -e DOTNET_CLI_HOME=/cache/dotnet -e NUGET_HTTP_CACHE_PATH=/cache/nuget-http -e NUGET_PLUGINS_CACHE_PATH=/cache/nuget-plugins -e NUGET_SCRATCH=/cache/nuget-scratch premierecalendar-dev-app:latest dotnet build $projectPath -c Release --nologo"
    logs = "$compose logs --tail=200 app database"
    reset = "$compose down --volumes --remove-orphans"
}
if ($Plan) {
    [pscustomobject]@{ Action = $Action; Project = $Project; Context = $context; Command = $commands[$Action] }
    return
}
# The development stack reads its PostgreSQL password from a Docker secret file that is not committed. Creating it is a
# local file write only: no WSL or Docker is involved, and an existing file is never touched.
if ($Action -in 'up', 'build') {
    $secretFile = Join-Path $context.Root '.secrets/dev-postgres-password'
    if (-not (Test-Path -LiteralPath $secretFile)) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $secretFile) -Force | Out-Null
        $password = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24)).ToLowerInvariant()
        # No trailing newline: the password file is read verbatim.
        [IO.File]::WriteAllText($secretFile, $password, [Text.UTF8Encoding]::new($false))
        Write-Host "Created the development password file $secretFile"
    }
}
$wslRoot = ConvertTo-WslPath $context.Root
$command = $commands[$Action].Replace('__ROOT__', (ConvertTo-BashLiteral $wslRoot)).
    Replace('__BUILD__', $context.BuildRoot).Replace('__PACKAGES__', $context.PackageRoot).Replace('__CACHE__', $context.CacheRoot)
$lock = if ($Action -eq 'check') {
    "mkdir -p artifacts; exec 9>artifacts/check.lock; flock -n 9 || { echo 'Another check owns this worktree.' >&2; exit 21; }"
} else { '' }
$prepareCaches = if ($Action -eq 'up') {
    "docker volume create $($context.PackageRoot) >/dev/null; docker volume create $($context.WatchRoot) >/dev/null; docker volume create $($context.CacheRoot) >/dev/null"
} else { '' }
$script = @"
set -eu
cd $(ConvertTo-BashLiteral $wslRoot)
docker info >/dev/null
export PREMIERECALENDAR_NUGET_CACHE=$($context.PackageRoot)
export PREMIERECALENDAR_BUILD_CACHE=$($context.WatchRoot)
export PREMIERECALENDAR_SDK_CACHE=$($context.CacheRoot)
$lock
$prepareCaches
$command
"@
Invoke-HarnessWsl $script
