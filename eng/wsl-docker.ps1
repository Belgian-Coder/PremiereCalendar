#Requires -Version 7.0
<#
.SYNOPSIS
Runs the development Docker Compose stack inside Ubuntu 24.04 on WSL.

.DESCRIPTION
The Windows host has no .NET SDK or PostgreSQL; the development app (SDK image with `dotnet watch`) and its
PostgreSQL database run with Docker Engine inside the Ubuntu-24.04 WSL distribution, using compose.dev.yaml.
It works from any folder: the repository is found from the script location (so it also works in a git worktree).

Actions:
  up     build and start the development stack (http://localhost:5299, PostgreSQL on 127.0.0.1:55432)
  down   stop the stack, keeping its named volumes
  build  rebuild the development image (premierecalendar-dev-app:latest, also used by Test-WithPostgres.ps1)
  test   build the development image, then run eng\Test-WithPostgres.ps1 with its defaults
  logs   show the last 200 log lines of the app and database
  reset  remove only the premierecalendar-dev containers and named development volumes

The first run creates the ignored development password file .secrets\dev-postgres-password with a random value
(it is never overwritten and never printed).

.EXAMPLE
pwsh -File D:\Projects\PremiereCalendar\eng\wsl-docker.ps1 up
#>
[CmdletBinding()]
param(
    [ValidateSet('up', 'down', 'build', 'test', 'logs', 'reset')]
    [string] $Action = 'up'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# The development stack reads its PostgreSQL password from a Docker secret file that is not committed.
if ($Action -in 'up', 'build', 'test') {
    $secretDirectory = Join-Path $root '.secrets'
    $secretFile = Join-Path $secretDirectory 'dev-postgres-password'
    if (-not (Test-Path -LiteralPath $secretFile)) {
        New-Item -ItemType Directory -Path $secretDirectory -Force | Out-Null
        $password = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(24)).ToLowerInvariant()
        # No trailing newline: the password file is read verbatim.
        [IO.File]::WriteAllText($secretFile, $password, [Text.UTF8Encoding]::new($false))
        Write-Host "Created the development password file $secretFile"
    }
}

$keeperPattern = 'Ubuntu-24.04.+systemctl start docker.+sleep infinity'
$keeper = Get-CimInstance Win32_Process -Filter "Name='wsl.exe'" -ErrorAction SilentlyContinue |
    Where-Object { $_.CommandLine -match $keeperPattern } |
    Select-Object -First 1
if ($null -eq $keeper) {
    Start-Process -FilePath "$env:SystemRoot\System32\wsl.exe" -WindowStyle Hidden -ArgumentList @(
        '-d', 'Ubuntu-24.04', '-u', 'root', '--exec', '/bin/sh', '-c',
        'systemctl start docker; exec sleep infinity') | Out-Null
}
$wslRoot = (& wsl.exe -d Ubuntu-24.04 -- wslpath -a ($root -replace '\\', '/')).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($wslRoot)) { throw 'Could not resolve the repository inside Ubuntu-24.04.' }

if ($Action -eq 'reset') {
    Write-Host 'Reset removes only premierecalendar-dev containers and named development volumes.'
}

$commands = @{
    up = 'docker compose -f compose.dev.yaml up --build -d --wait'
    down = 'docker compose -f compose.dev.yaml down'
    build = 'docker compose -f compose.dev.yaml build --pull'
    test = 'docker compose -f compose.dev.yaml build'
    logs = 'docker compose -f compose.dev.yaml logs --tail=200 app database'
    reset = 'docker compose -f compose.dev.yaml down --volumes --remove-orphans'
}

& wsl.exe -d Ubuntu-24.04 -- bash -lc "cd '$wslRoot' && $($commands[$Action])"
if ($LASTEXITCODE -ne 0) { throw "WSL Docker action '$Action' failed." }

if ($Action -eq 'test') {
    # TEST-COMMANDS: the test invocation lives in Test-WithPostgres.ps1; this action only prepares the image and delegates.
    & (Join-Path $PSScriptRoot 'Test-WithPostgres.ps1')
}
