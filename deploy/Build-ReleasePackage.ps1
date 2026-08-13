[CmdletBinding()]
param(
    [string]$ProjectPath = (Join-Path $PSScriptRoot '..\PremiereCalendar\PremiereCalendar.csproj'),
    [string]$SolutionPath = (Join-Path $PSScriptRoot '..\PremiereCalendar.slnx'),
    [string]$OutputRoot = (Join-Path $PSScriptRoot '..\artifacts\release'),
    [string]$PackageName = 'PremiereCalendar',
    [string]$Version,
    [string]$Runtime = 'win-x64',
    [switch]$SkipTests,
    [switch]$NoZip
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

function Get-ProjectVersion {
    param([Parameter(Mandatory)][string]$ResolvedProjectPath)

    [xml]$projectXml = Get-Content -LiteralPath $ResolvedProjectPath -Raw
    $versionNode = $projectXml.Project.PropertyGroup |
        ForEach-Object { $_.Version } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -First 1

    if (-not [string]::IsNullOrWhiteSpace($versionNode)) {
        return [string]$versionNode
    }

    $repositoryPropertiesPath = Join-Path (Split-Path -Parent (Split-Path -Parent $ResolvedProjectPath)) 'Directory.Build.props'
    if (Test-Path -LiteralPath $repositoryPropertiesPath -PathType Leaf) {
        [xml]$repositoryProperties = Get-Content -LiteralPath $repositoryPropertiesPath -Raw
        $fallbackVersion = [string]$repositoryProperties.Project.PropertyGroup.Version.InnerText
        if (-not [string]::IsNullOrWhiteSpace($fallbackVersion)) { return $fallbackVersion }
    }

    return '0.0.0-local'
}

function Assert-ChildPath {
    param(
        [Parameter(Mandatory)][string]$ChildPath,
        [Parameter(Mandatory)][string]$ParentPath
    )

    $fullChild = [System.IO.Path]::GetFullPath($ChildPath)
    $fullParent = [System.IO.Path]::GetFullPath($ParentPath).TrimEnd('\') + '\'
    if (-not $fullChild.StartsWith($fullParent, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove path outside output root: $fullChild"
    }
}

function Set-HashtableValue {
    param(
        [Parameter(Mandatory)][System.Collections.IDictionary]$Object,
        [Parameter(Mandatory)][string[]]$Path,
        [AllowNull()]$Value
    )

    $current = $Object
    for ($i = 0; $i -lt $Path.Length - 1; $i++) {
        $key = $Path[$i]
        if (-not $current.Contains($key) -or -not ($current[$key] -is [System.Collections.IDictionary])) {
            $current[$key] = @{}
        }

        $current = $current[$key]
    }

    $current[$Path[-1]] = $Value
}

function Clear-ReleaseSecrets {
    param([Parameter(Mandatory)][string]$AppSettingsPath)

    if (-not (Test-Path -LiteralPath $AppSettingsPath)) {
        return
    }

    $config = Get-Content -LiteralPath $AppSettingsPath -Raw | ConvertFrom-Json -AsHashtable

    Set-HashtableValue $config @('Tmdb', 'BearerToken') ''
    Set-HashtableValue $config @('Omdb', 'ApiKey') ''
    Set-HashtableValue $config @('Omdb', 'Enabled') $false
    Set-HashtableValue $config @('Fanart', 'ApiKey') ''
    Set-HashtableValue $config @('Fanart', 'Enabled') $false
    Set-HashtableValue $config @('Trakt', 'ClientId') ''
    Set-HashtableValue $config @('Trakt', 'ClientSecret') ''
    Set-HashtableValue $config @('TheTvdb', 'ApiKey') ''
    Set-HashtableValue $config @('TheTvdb', 'Enabled') $false
    Set-HashtableValue $config @('Watchmode', 'ApiKey') ''
    Set-HashtableValue $config @('Watchmode', 'Enabled') $false
    Set-HashtableValue $config @('Simkl', 'ClientId') ''
    Set-HashtableValue $config @('Simkl', 'ClientSecret') ''
    Set-HashtableValue $config @('Simkl', 'AccessToken') ''
    Set-HashtableValue $config @('Simkl', 'Enabled') $false

    $config | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $AppSettingsPath -Encoding UTF8
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$dotnetPath = Get-DotNetPath $repoRoot
$resolvedProjectPath = (Resolve-Path -LiteralPath $ProjectPath).Path
$resolvedSolutionPath = (Resolve-Path -LiteralPath $SolutionPath).Path

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-ProjectVersion $resolvedProjectPath
}

$safeVersion = $Version -replace '[^A-Za-z0-9_.-]', '-'
$resolvedOutputRoot = if (Test-Path -LiteralPath $OutputRoot) {
    (Resolve-Path -LiteralPath $OutputRoot).Path
}
else {
    New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
    (Resolve-Path -LiteralPath $OutputRoot).Path
}

$stagingDirectory = Join-Path $resolvedOutputRoot "$PackageName-$safeVersion-$Runtime"
$publishDirectory = Join-Path $stagingDirectory 'app'
Assert-ChildPath $stagingDirectory $resolvedOutputRoot

if (Test-Path -LiteralPath $stagingDirectory) {
    Remove-Item -LiteralPath $stagingDirectory -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $publishDirectory | Out-Null
$sourceRevision = (& git -C $repoRoot rev-parse HEAD 2>$null)
if ($LASTEXITCODE -ne 0) { $sourceRevision = 'unknown' }
$buildTimeUtc = [DateTimeOffset]::UtcNow.ToString('O')
$buildId = ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes("$sourceRevision`n$Version")))).ToLowerInvariant()
$fileVersion = if ($Version -match '^\d+\.\d+\.\d+$') { "$Version.0" } else { '0.0.0.0' }
[xml]$buildProperties = Get-Content -LiteralPath (Join-Path $repoRoot 'Directory.Build.props') -Raw
$databaseSchemaVersion = [int]$buildProperties.Project.PropertyGroup.DatabaseSchemaVersion

if (-not $SkipTests) {
    & $dotnetPath test $resolvedSolutionPath --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet test failed with exit code $LASTEXITCODE"
    }
}

& $dotnetPath publish $resolvedProjectPath `
    -c Release `
    -r $Runtime `
    --self-contained true `
    -o $publishDirectory `
    /p:Version=$Version `
    /p:FileVersion=$fileVersion `
    /p:InformationalVersion=$Version+$buildId `
    /p:BuildId=$buildId `
    /p:SourceRevisionId=$sourceRevision `
    /p:BuildTimeUtc=$buildTimeUtc
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

$metadata = [ordered]@{
    schemaVersion = 1
    version = $Version
    sourceRevision = $sourceRevision
    buildId = $buildId
    builtUtc = $buildTimeUtc
    databaseSchemaVersion = $databaseSchemaVersion
}
[IO.File]::WriteAllText((Join-Path $publishDirectory 'build-metadata.json'), ($metadata | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))

Get-ChildItem -LiteralPath $publishDirectory -Filter 'appsettings*.json' | ForEach-Object {
    Clear-ReleaseSecrets $_.FullName
}

$publishedAppDataDirectory = Join-Path $publishDirectory 'App_Data'
if (Test-Path -LiteralPath $publishedAppDataDirectory) {
    Remove-Item -LiteralPath $publishedAppDataDirectory -Recurse -Force
}

Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'release\Install-PremiereCalendar.ps1') -Destination $stagingDirectory
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'release\Uninstall-PremiereCalendar.ps1') -Destination $stagingDirectory
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'release\Install-PremiereCalendar.cmd') -Destination $stagingDirectory
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'release\Uninstall-PremiereCalendar.cmd') -Destination $stagingDirectory
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'release\README.md') -Destination (Join-Path $stagingDirectory 'README.md')

$docsDirectory = Join-Path $stagingDirectory 'docs'
New-Item -ItemType Directory -Force -Path $docsDirectory | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\ReleaseInstaller.md') -Destination $docsDirectory
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs\Configuration.md') -Destination $docsDirectory

@"
Package: $PackageName
Version: $Version
Runtime: $Runtime
BuiltUtc: $([DateTimeOffset]::UtcNow.ToString('O'))
"@ | Set-Content -LiteralPath (Join-Path $stagingDirectory 'VERSION.txt') -Encoding UTF8

if (-not $NoZip) {
    $zipPath = Join-Path $resolvedOutputRoot "$PackageName-$safeVersion-$Runtime.zip"
    $hashPath = "$zipPath.sha256"
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    Compress-Archive -Path (Join-Path $stagingDirectory '*') -DestinationPath $zipPath -CompressionLevel Optimal
    $hash = Get-FileHash -LiteralPath $zipPath -Algorithm SHA256
    "$($hash.Hash)  $(Split-Path -Leaf $zipPath)" | Set-Content -LiteralPath $hashPath -Encoding ASCII
}

Write-Host ''
Write-Host 'Release package created.'
Write-Host "Staging: $stagingDirectory"
if (-not $NoZip) {
    Write-Host "Zip:     $zipPath"
    Write-Host "SHA256:  $hashPath"
}
