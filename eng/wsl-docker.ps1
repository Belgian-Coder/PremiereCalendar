[CmdletBinding()]
param(
    [ValidateSet('up', 'down', 'build', 'test', 'logs', 'reset')]
    [string] $Action = 'up'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
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
    test = 'docker compose -f compose.dev.yaml run --rm app dotnet test PremiereCalendar.slnx -c Release --nologo --filter FullyQualifiedName!~PremiereCalendar.BrowserTests'
    logs = 'docker compose -f compose.dev.yaml logs --tail=200 app database'
    reset = 'docker compose -f compose.dev.yaml down --volumes --remove-orphans'
}

& wsl.exe -d Ubuntu-24.04 -- bash -lc "cd '$wslRoot' && $($commands[$Action])"
if ($LASTEXITCODE -ne 0) { throw "WSL Docker action '$Action' failed." }
