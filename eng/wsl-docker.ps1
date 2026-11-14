#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('up', 'down', 'build', 'check', 'test', 'logs', 'reset')]
    [string] $Action = 'up',
    [ValidateSet('App', 'UnitTests', 'ComponentTests', 'IntegrationTests', 'BrowserTests')]
    [string] $Project = 'App',
    [string] $Filter,
    [switch] $Plan
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Harness.Common.ps1')
$context = Get-HarnessContext (Split-Path -Parent $PSScriptRoot)

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
    check = "docker run --rm --cpus=4 -v __ROOT__:/src -v __BUILD__:/build -v __PACKAGES__:/root/.nuget/packages -w /src -e ArtifactsPath=/build -e NUGET_PACKAGES=/root/.nuget/packages premierecalendar-dev-app:latest dotnet build $projectPath -c Release --nologo"
    logs = "$compose logs --tail=200 app database"
    reset = "$compose down --volumes --remove-orphans"
}
if ($Plan) {
    [pscustomobject]@{ Action = $Action; Project = $Project; Context = $context; Command = $commands[$Action] }
    return
}
$wslRoot = ConvertTo-WslPath $context.Root
$wslBuild = ConvertTo-WslPath $context.BuildRoot
$wslPackages = ConvertTo-WslPath $context.PackageRoot
$command = $commands[$Action].Replace('__ROOT__', (ConvertTo-BashLiteral $wslRoot)).
    Replace('__BUILD__', (ConvertTo-BashLiteral $wslBuild)).Replace('__PACKAGES__', (ConvertTo-BashLiteral $wslPackages))
$lock = if ($Action -eq 'check') {
    "mkdir -p artifacts; exec 9>artifacts/check.lock; flock -n 9 || { echo 'Another check owns this worktree.' >&2; exit 21; }"
} else { '' }
$script = @"
set -eu
cd $(ConvertTo-BashLiteral $wslRoot)
docker info >/dev/null
export PREMIERECALENDAR_NUGET_CACHE=$(ConvertTo-BashLiteral $wslPackages)
$lock
$command
"@
Invoke-HarnessWsl $script
