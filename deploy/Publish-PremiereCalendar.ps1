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

function Assert-SafeMirrorTarget {
    param([Parameter(Mandatory)][string]$Path)

    $fullPath = [System.IO.Path]::GetFullPath($Path)
    $root = [System.IO.Path]::GetPathRoot($fullPath)
    if ([string]::IsNullOrWhiteSpace($root) -or $fullPath.TrimEnd('\') -eq $root.TrimEnd('\')) {
        throw "Refusing to mirror publish output into unsafe target path: $fullPath"
    }
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
Assert-SafeMirrorTarget $resolvedTargetDirectory

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

# Mirror published files so removed assets do not remain servable. Keep App_Data because it contains local settings and caches.
robocopy $resolvedPublishOutput $resolvedTargetDirectory /MIR /XD App_Data /NFL /NDL /NJH /NJS /NP
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
        $previousAspNetCoreUrls = $env:ASPNETCORE_URLS
        $previousUrls = $env:Urls
        $previousAspNetCoreEnvironment = $env:ASPNETCORE_ENVIRONMENT
        $previousDotnetEnvironment = $env:DOTNET_ENVIRONMENT
        try {
            $env:ASPNETCORE_URLS = "http://0.0.0.0:$Port"
            $env:Urls = "http://0.0.0.0:$Port"
            $env:ASPNETCORE_ENVIRONMENT = 'Production'
            $env:DOTNET_ENVIRONMENT = 'Production'
            $startedProcess = Start-Process -FilePath $targetExe -WorkingDirectory $resolvedTargetDirectory -WindowStyle Hidden -PassThru
        }
        finally {
            $env:ASPNETCORE_URLS = $previousAspNetCoreUrls
            $env:Urls = $previousUrls
            $env:ASPNETCORE_ENVIRONMENT = $previousAspNetCoreEnvironment
            $env:DOTNET_ENVIRONMENT = $previousDotnetEnvironment
        }
    }
}

if (-not $NoStart) {
    Start-Sleep -Seconds 3
    try {
        Invoke-WebRequest -UseBasicParsing -Uri "http://localhost:$Port/health" -TimeoutSec 10 |
            Select-Object StatusCode, Content
    }
    catch {
        if ($startedProcess -and -not $startedProcess.HasExited) {
            Stop-Process -Id $startedProcess.Id -Force -ErrorAction SilentlyContinue
        }

        throw
    }
}
