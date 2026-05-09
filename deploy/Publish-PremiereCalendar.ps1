param(
    [string]$ProjectPath = (Join-Path $PSScriptRoot '..\PremiereCalendar\PremiereCalendar.csproj'),
    [string]$TargetDirectory = 'D:\Apps\PremiereCalendar',
    [string]$PublishOutput = (Join-Path $PSScriptRoot '..\artifacts\publish\PremiereCalendar-net11'),
    [string]$Runtime = 'win-x64',
    [int]$Port = 5298,
    [switch]$SkipServiceInstall,
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'

function Test-IsAdministrator {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dotnetPath = Join-Path $repoRoot '.dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnetPath)) {
    $dotnetPath = 'dotnet'
}

$resolvedProjectPath = (Resolve-Path -LiteralPath $ProjectPath).Path
$resolvedPublishOutput = if (Test-Path -LiteralPath $PublishOutput) {
    (Resolve-Path -LiteralPath $PublishOutput).Path
}
else {
    New-Item -ItemType Directory -Force -Path $PublishOutput | Out-Null
    (Resolve-Path -LiteralPath $PublishOutput).Path
}

if (-not (Test-Path -LiteralPath $TargetDirectory)) {
    New-Item -ItemType Directory -Force -Path $TargetDirectory | Out-Null
}
$resolvedTargetDirectory = (Resolve-Path -LiteralPath $TargetDirectory).Path

& $dotnetPath publish $resolvedProjectPath -c Release -r $Runtime --self-contained true -o $resolvedPublishOutput
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$targetExe = Join-Path $resolvedTargetDirectory 'PremiereCalendar.exe'
$service = Get-Service -Name 'PremiereCalendar' -ErrorAction SilentlyContinue
if ($service -and $service.Status -ne 'Stopped') {
    Stop-Service -Name 'PremiereCalendar' -Force
    $service.WaitForStatus('Stopped', '00:00:30')
}

Get-Process -Name 'PremiereCalendar' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $targetExe } |
    Stop-Process -Force

# Keep App_Data because it contains the local calendar and image caches.
robocopy $resolvedPublishOutput $resolvedTargetDirectory /E /XX /XD App_Data /NFL /NDL /NJH /NJS /NP
$robocopyExitCode = $LASTEXITCODE
if ($robocopyExitCode -gt 7) {
    throw "robocopy failed with exit code $robocopyExitCode"
}

$serviceInstallerPath = Join-Path $PSScriptRoot 'Install-PremiereCalendarService.ps1'
$isAdministrator = Test-IsAdministrator
if (-not $SkipServiceInstall -and $isAdministrator -and (Test-Path -LiteralPath $serviceInstallerPath)) {
    & $serviceInstallerPath -PublishDirectory $resolvedTargetDirectory -Port $Port -NoStart:$NoStart
}
else {
    if (-not $SkipServiceInstall -and -not $isAdministrator) {
        Write-Warning "Not running as Administrator, so the Windows Service was not installed or updated. Re-run this script elevated for automatic startup after reboot."
    }

    if ($service) {
        if (-not $NoStart) {
            Start-Service -Name 'PremiereCalendar'
        }
    }
    elseif (-not $NoStart) {
        Start-Process -FilePath $targetExe -WorkingDirectory $resolvedTargetDirectory -WindowStyle Hidden
    }
}

if (-not $NoStart) {
    Start-Sleep -Seconds 3
    Invoke-WebRequest -UseBasicParsing -Uri "http://localhost:$Port/health" -TimeoutSec 10 |
        Select-Object StatusCode, Content
}
