#Requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string] $ReleaseDirectory,
    [string] $InstallRoot = 'D:\Apps\PremiereCalendar',
    [string] $DataRoot = 'D:\Apps\PremiereCalendarData'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($ReleaseDirectory)
$manifestPath = Join-Path $root 'stable.manifest.json'
$certificatePath = Join-Path $root 'premiere-calendar-release-signing.cer'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw 'stable.manifest.json is missing.' }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$packagePath = Join-Path $root ([string]$manifest.packageFileName)
$arguments = @{
    ManifestPath = $manifestPath
    PackagePath = $packagePath
    PublicCertificatePath = $certificatePath
    InstallRoot = $InstallRoot
    DataRoot = $DataRoot
    InitializeTrust = $true
}
& (Join-Path $PSScriptRoot 'update-helper.ps1') @arguments
