#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string] $Repository = 'Belgian-Coder/PremiereCalendar',
    [string] $InstallRoot = 'D:\Apps\PremiereCalendar',
    [string] $DataRoot = 'D:\Apps\PremiereCalendarData',
    [string] $LogPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Net.Http

function Save-BoundedReleaseAsset {
    param(
        [Parameter(Mandatory)][Uri] $Uri,
        [Parameter(Mandatory)][string] $Destination,
        [Parameter(Mandatory)][long] $MaxBytes
    )
    if ($Uri.Scheme -ne 'https' -or $Uri.Host -ne 'github.com') { throw 'Release asset URL is not trusted.' }
    if (Test-Path -LiteralPath $Destination) { throw "Refusing to overwrite release asset: $Destination" }
    $client = [System.Net.Http.HttpClient]::new()
    $client.Timeout = [TimeSpan]::FromMinutes(10)
    $client.DefaultRequestHeaders.UserAgent.ParseAdd('PremiereCalendar-Updater/1.0')
    try {
        $response = $client.GetAsync($Uri, [System.Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        try {
            [void]$response.EnsureSuccessStatusCode()
            if ($response.Content.Headers.ContentLength -gt $MaxBytes) { throw 'Release asset exceeds its size limit.' }
            $input = $response.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            try {
                $output = [IO.FileStream]::new($Destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None, 65536, $false)
                try {
                    $buffer = [byte[]]::new(65536)
                    [long]$total = 0
                    while (($read = $input.Read($buffer, 0, $buffer.Length)) -gt 0) {
                        $total += $read
                        if ($total -gt $MaxBytes) { throw 'Release asset exceeds its size limit.' }
                        $output.Write($buffer, 0, $read)
                    }
                }
                finally { $output.Dispose() }
            }
            finally { $input.Dispose() }
        }
        finally { $response.Dispose() }
    }
    catch {
        if (Test-Path -LiteralPath $Destination) { Remove-Item -LiteralPath $Destination -Force }
        throw
    }
    finally { $client.Dispose() }
}

$transcriptStarted = $false
$updateMutex = $null
if (-not [string]::IsNullOrWhiteSpace($LogPath)) {
    $resolvedLogPath = [IO.Path]::GetFullPath($LogPath)
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($resolvedLogPath)) -Force | Out-Null
    Start-Transcript -LiteralPath $resolvedLogPath -Force | Out-Null
    $transcriptStarted = $true
}
try {
$updateMutex = [Threading.Mutex]::new($false, 'Global\PremiereCalendar-SignedReleaseUpdate')
if (-not $updateMutex.WaitOne(0)) { throw 'Another PremiereCalendar update is already running.' }
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
    $manifestPath = Join-Path $temporary 'stable.manifest.json'
    Save-BoundedReleaseAsset -Uri ([Uri]$assets['stable.manifest.json'].browser_download_url) -Destination $manifestPath -MaxBytes 1MB
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
    Save-BoundedReleaseAsset -Uri ([Uri]$assets[$packageName].browser_download_url) -Destination $packagePath -MaxBytes 1GB
    & (Join-Path $PSScriptRoot 'update-helper.ps1') -ManifestPath $manifestPath -PackagePath $packagePath -PublicCertificatePath $pinnedCertificate -InstallRoot $InstallRoot -DataRoot $DataRoot
}
finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force -ErrorAction SilentlyContinue }
}
}
finally {
    if ($null -ne $updateMutex) {
        try { $updateMutex.ReleaseMutex() } catch [ApplicationException] { }
        $updateMutex.Dispose()
    }
    if ($transcriptStarted) { Stop-Transcript | Out-Null }
}
