#requires -RunAsAdministrator

[CmdletBinding()]
param(
    [string]$InstallDirectory = (Join-Path $env:ProgramFiles 'PremiereCalendar'),
    [string]$DataDirectory = (Join-Path $env:ProgramData 'PremiereCalendar'),
    [int]$Port = 5298,
    [string]$ServiceName = 'PremiereCalendar',
    [string]$DisplayName = 'Premiere Calendar',
    [switch]$KeepBinaries,
    [switch]$RemoveData,
    [switch]$SkipFirewall
)

$ErrorActionPreference = 'Stop'

function Get-FullPath {
    param([Parameter(Mandatory)][string]$Path)

    return [System.IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path))
}

function Assert-RemovablePath {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Name
    )

    $fullPath = Get-FullPath $Path
    $root = [System.IO.Path]::GetPathRoot($fullPath)
    $windowsPath = [Environment]::GetFolderPath('Windows')
    $programFiles = [Environment]::GetFolderPath('ProgramFiles')
    $programData = [Environment]::GetFolderPath('CommonApplicationData')

    $blocked = @(
        $root.TrimEnd('\'),
        $windowsPath.TrimEnd('\'),
        $programFiles.TrimEnd('\'),
        $programData.TrimEnd('\')
    )

    if ($blocked | Where-Object { [string]::Equals($fullPath.TrimEnd('\'), $_, [StringComparison]::OrdinalIgnoreCase) }) {
        throw "$Name is too broad to remove safely: $fullPath"
    }

    return $fullPath
}

$resolvedInstallDirectory = Assert-RemovablePath $InstallDirectory 'InstallDirectory'
$resolvedDataDirectory = Assert-RemovablePath $DataDirectory 'DataDirectory'
$targetExe = Join-Path $resolvedInstallDirectory 'PremiereCalendar.exe'

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        Write-Host "Stopping service $ServiceName..."
        Stop-Service -Name $ServiceName -Force
        $service.WaitForStatus('Stopped', '00:00:45')
    }

    Write-Host "Deleting service $ServiceName..."
    & sc.exe delete $ServiceName | Out-Null
}

Get-Process -Name 'PremiereCalendar' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $targetExe } |
    Stop-Process -Force

if (-not $SkipFirewall) {
    $firewallRuleName = "$DisplayName $Port"
    Get-NetFirewallRule -DisplayName $firewallRuleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
}

if (-not $KeepBinaries -and (Test-Path -LiteralPath $resolvedInstallDirectory)) {
    Write-Host "Removing binaries from $resolvedInstallDirectory..."
    Remove-Item -LiteralPath $resolvedInstallDirectory -Recurse -Force
}

if ($RemoveData -and (Test-Path -LiteralPath $resolvedDataDirectory)) {
    Write-Host "Removing data from $resolvedDataDirectory..."
    Remove-Item -LiteralPath $resolvedDataDirectory -Recurse -Force
}
elseif (Test-Path -LiteralPath $resolvedDataDirectory) {
    Write-Host "Preserved data directory: $resolvedDataDirectory"
}

Write-Host 'Premiere Calendar uninstall complete.'
