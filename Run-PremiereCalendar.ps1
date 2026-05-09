[CmdletBinding()]
param(
    [int]$Port = 5298,
    [string]$Configuration = 'Debug',
    [switch]$SkipBuild,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'

function Get-DotNetPath {
    param([Parameter(Mandatory)][string]$RepoRoot)

    $localDotnet = Join-Path $RepoRoot '.dotnet\dotnet.exe'
    if (Test-Path -LiteralPath $localDotnet) {
        return $localDotnet
    }

    return 'dotnet'
}

function Test-IsWindows {
    return [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)
}

function Test-PortInUse {
    param([Parameter(Mandatory)][int]$Port)

    try {
        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, $Port)
        $listener.Start()
        $listener.Stop()
        return $false
    }
    catch [Net.Sockets.SocketException] {
        return $true
    }
}

$repoRoot = $PSScriptRoot
$projectPath = Join-Path $repoRoot 'PremiereCalendar\PremiereCalendar.csproj'
$solutionPath = Join-Path $repoRoot 'PremiereCalendar.slnx'
$dotnetPath = Get-DotNetPath $repoRoot
$listenUrl = "http://0.0.0.0:$Port"
$browserUrl = "http://localhost:$Port"

if (-not (Test-Path -LiteralPath $projectPath)) {
    throw "Project file not found: $projectPath"
}

if (Test-PortInUse -Port $Port) {
    $owner = $null
    if (Test-IsWindows -and (Get-Command Get-NetTCPConnection -ErrorAction SilentlyContinue)) {
        $listener = Get-NetTCPConnection -LocalPort $Port -ErrorAction SilentlyContinue |
            Where-Object { $_.State -eq 'Listen' } |
            Select-Object -First 1
        if ($listener) {
            $process = Get-Process -Id $listener.OwningProcess -ErrorAction SilentlyContinue
            $owner = if ($process) { $process.ProcessName } else { "PID $($listener.OwningProcess)" }
        }
    }

    $ownerText = if ($owner) { " by $owner" } else { '' }
    throw "Port $Port is already in use$ownerText. Stop that process or run with -Port <other port>."
}

if (-not $SkipBuild) {
    & $dotnetPath build $solutionPath -c $Configuration -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed with exit code $LASTEXITCODE"
    }
}

if (-not $NoLaunch) {
    Start-Process $browserUrl
}

Write-Host "Starting Premiere Calendar on $listenUrl"
Write-Host 'Press Ctrl+C to stop.'

& $dotnetPath run `
    --project $projectPath `
    --configuration $Configuration `
    --no-build `
    --urls $listenUrl

exit $LASTEXITCODE
