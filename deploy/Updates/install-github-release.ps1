#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string] $Repository = 'Belgian-Coder/PremiereCalendar',
    [string] $InstallRoot = 'D:\Apps\PremiereCalendar',
    [string] $DataRoot = 'D:\Apps\PremiereCalendarData'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$pinnedCertificate = Join-Path $InstallRoot 'updater\signing.cer'
if (-not (Test-Path -LiteralPath $pinnedCertificate -PathType Leaf)) {
    throw 'No administrator-pinned release certificate exists. Perform one offline install before enabling GitHub updates.'
}
$release = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repository/releases/latest" -Headers @{ 'User-Agent' = 'PremiereCalendar-Updater/1.0'; Accept = 'application/vnd.github+json' }
if ([bool]$release.draft -or [bool]$release.prerelease) { throw 'The latest GitHub release is not a stable published release.' }
$assets = @{}
foreach ($asset in $release.assets) {
    $uri = [Uri][string]$asset.browser_download_url
    if ($uri.Scheme -ne 'https' -or $uri.Host -ne 'github.com') { throw 'GitHub returned an untrusted asset URL.' }
    $assets[[string]$asset.name] = $asset
}
if (-not $assets.ContainsKey('stable.manifest.json')) { throw 'The release has no stable manifest.' }
$temporary = Join-Path ([IO.Path]::GetTempPath()) "premiere-calendar-release-$([Guid]::NewGuid().ToString('N'))"
try {
    New-Item -ItemType Directory -Path $temporary -Force | Out-Null
    $headers = @{ 'User-Agent' = 'PremiereCalendar-Updater/1.0' }
    $manifestPath = Join-Path $temporary 'stable.manifest.json'
    Invoke-WebRequest -Uri $assets['stable.manifest.json'].browser_download_url -Headers $headers -OutFile $manifestPath
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ([string]$manifest.version -notmatch '^\d+\.\d+\.\d+$') { throw 'The manifest version is invalid.' }
    $activeVersionPath = Join-Path $InstallRoot 'updater\active-version.txt'
    if (Test-Path -LiteralPath $activeVersionPath -PathType Leaf) {
        $activeVersion = (Get-Content -LiteralPath $activeVersionPath -Raw).Trim()
        if ($activeVersion -match '^\d+\.\d+\.\d+$') {
            if ([version]$activeVersion -eq [version][string]$manifest.version) {
                Write-Host "PremiereCalendar $activeVersion is already the latest stable release."
                return
            }
            if ([version]$activeVersion -gt [version][string]$manifest.version) {
                throw "Refusing to downgrade PremiereCalendar from $activeVersion to $($manifest.version)."
            }
        }
    }
    $packageName = [IO.Path]::GetFileName([string]$manifest.packageFileName)
    if ($packageName -ne [string]$manifest.packageFileName -or -not $assets.ContainsKey($packageName)) { throw 'The manifest package asset is unavailable.' }
    if ([long]$assets[$packageName].size -gt 1GB) { throw 'The release package exceeds the 1 GB limit.' }
    $packagePath = Join-Path $temporary $packageName
    Invoke-WebRequest -Uri $assets[$packageName].browser_download_url -Headers $headers -OutFile $packagePath
    & (Join-Path $PSScriptRoot 'update-helper.ps1') -ManifestPath $manifestPath -PackagePath $packagePath -PublicCertificatePath $pinnedCertificate -InstallRoot $InstallRoot -DataRoot $DataRoot
}
finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force -ErrorAction SilentlyContinue }
}
