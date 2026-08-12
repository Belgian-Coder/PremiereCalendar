#Requires -RunAsAdministrator
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string] $ManifestPath,
    [Parameter(Mandatory)][string] $PackagePath,
    [Parameter(Mandatory)][string] $PublicCertificatePath,
    [string] $InstallRoot = 'D:\Apps\PremiereCalendar',
    [string] $DataRoot = 'D:\Apps\PremiereCalendarData',
    [string] $ServiceName = 'PremiereCalendar',
    [int] $Port = 5298,
    [string] $HealthUrl = 'http://localhost:5298/health',
    [string] $VersionUrl = 'http://localhost:5298/health/version',
    [switch] $InitializeTrust
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-ManifestPayload {
    param([Parameter(Mandatory)] $Manifest)
    $notes = ([string]$Manifest.releaseNotes).Replace("`r`n", "`n").Replace("`r", "`n")
    return @(
        [string]$Manifest.schemaVersion,
        [string]$Manifest.version,
        [string]$Manifest.channel,
        [string]$Manifest.packageFileName,
        ([string]$Manifest.packageSha256).ToUpperInvariant(),
        [string]$Manifest.minimumDatabaseSchemaVersion,
        [string]$Manifest.maximumDatabaseSchemaVersion,
        $notes
    ) -join [char]10
}

function Assert-TrustedPackage {
    param(
        [Parameter(Mandatory)][string] $Manifest,
        [Parameter(Mandatory)][string] $Package,
        [Parameter(Mandatory)][string] $Certificate,
        [Parameter(Mandatory)][string] $PinnedCertificate
    )
    foreach ($path in @($Manifest, $Package, $Certificate)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Release input is missing: $path" }
    }
    if ((Get-Item -LiteralPath $Package).Length -gt 1GB) { throw 'Release package exceeds the 1 GB limit.' }
    $suppliedCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($Certificate)
    try {
        if (Test-Path -LiteralPath $PinnedCertificate -PathType Leaf) {
            $pinned = [Security.Cryptography.X509Certificates.X509Certificate2]::new($PinnedCertificate)
            try {
                if ($pinned.Thumbprint -ne $suppliedCertificate.Thumbprint) { throw 'Release certificate does not match the administrator-pinned certificate.' }
            }
            finally { $pinned.Dispose() }
        }
        elseif (-not $InitializeTrust) {
            throw 'No pinned release certificate exists. Use -InitializeTrust for the first administrator-reviewed install.'
        }
        $releaseManifest = Get-Content -LiteralPath $Manifest -Raw | ConvertFrom-Json
        if ([int]$releaseManifest.schemaVersion -ne 1 -or [string]$releaseManifest.channel -ne 'stable') { throw 'Release manifest identity is invalid.' }
        if ([string]$releaseManifest.version -notmatch '^\d+\.\d+\.\d+$') { throw 'Release manifest version is invalid.' }
        $packageName = [IO.Path]::GetFileName([string]$releaseManifest.packageFileName)
        if ($packageName -ne [string]$releaseManifest.packageFileName -or $packageName -ne [IO.Path]::GetFileName($Package)) { throw 'Release package filename does not match its manifest.' }
        $actualHash = (Get-FileHash -LiteralPath $Package -Algorithm SHA256).Hash
        if ($actualHash -ne [string]$releaseManifest.packageSha256) { throw 'Release package hash does not match its manifest.' }
        $rsa = $suppliedCertificate.GetRSAPublicKey()
        try {
            $verified = $null -ne $rsa -and $rsa.VerifyData(
                [Text.Encoding]::UTF8.GetBytes((Get-ManifestPayload $releaseManifest)),
                [Convert]::FromBase64String([string]$releaseManifest.signature),
                [Security.Cryptography.HashAlgorithmName]::SHA256,
                [Security.Cryptography.RSASignaturePadding]::Pkcs1)
            if (-not $verified) { throw 'Release manifest signature is invalid.' }
        }
        finally { if ($null -ne $rsa) { $rsa.Dispose() } }
        return $releaseManifest
    }
    finally { $suppliedCertificate.Dispose() }
}

function Expand-SafeArchive {
    param([Parameter(Mandatory)][string] $ArchivePath, [Parameter(Mandatory)][string] $Destination)
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        if ($archive.Entries.Count -gt 10000) { throw 'Release archive contains too many entries.' }
        $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        $root = [IO.Path]::GetFullPath($Destination).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        [long]$expanded = 0
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            $segments = @($name.Split('/') | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
            if ([string]::IsNullOrWhiteSpace($name) -or $name.StartsWith('/') -or $name.Contains(':') -or
                ($segments | Where-Object { $_ -eq '.' -or $_ -eq '..' }) -or -not $seen.Add($name)) {
                throw 'Release archive contains an unsafe path.'
            }
            if ([long]$entry.Length -gt (2GB - $expanded)) { throw 'Expanded release exceeds the 2 GB limit.' }
            $expanded += [long]$entry.Length
            $target = [IO.Path]::GetFullPath((Join-Path $Destination ($name.Replace('/', [IO.Path]::DirectorySeparatorChar))))
            if (-not $target.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw 'Release archive escaped the staging directory.' }
        }
    }
    finally { $archive.Dispose() }
    Expand-Archive -LiteralPath $ArchivePath -DestinationPath $Destination
}

function Wait-ForHealthyVersion {
    param([Parameter(Mandatory)][string] $ExpectedVersion)
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(75)
    do {
        try {
            $health = Invoke-WebRequest -Uri $HealthUrl -UseBasicParsing -TimeoutSec 5
            $versionResponse = Invoke-RestMethod -Uri $VersionUrl -TimeoutSec 5
            $actualVersion = ([string]$versionResponse.version).Split('+')[0]
            if ($health.StatusCode -eq 200 -and $actualVersion -eq $ExpectedVersion) { return }
        }
        catch { }
        Start-Sleep -Seconds 2
    } while ([DateTimeOffset]::UtcNow -lt $deadline)
    throw "Release $ExpectedVersion did not become healthy at the expected version."
}

$resolvedInstallRoot = [IO.Path]::GetFullPath($InstallRoot)
$resolvedDataRoot = [IO.Path]::GetFullPath($DataRoot)
foreach ($managedRoot in @($resolvedInstallRoot, $resolvedDataRoot)) {
    $pathRoot = [IO.Path]::GetPathRoot($managedRoot)
    $normalizedManagedRoot = $managedRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    $normalizedPathRoot = $pathRoot.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
    if ($normalizedManagedRoot -eq $normalizedPathRoot) {
        throw 'InstallRoot and DataRoot must not be filesystem roots.'
    }
}
if ($resolvedInstallRoot.Equals($resolvedDataRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'InstallRoot and DataRoot must be different directories.'
}
$updaterRoot = Join-Path $resolvedInstallRoot 'updater'
$pinnedCertificate = Join-Path $updaterRoot 'signing.cer'
$manifest = Assert-TrustedPackage -Manifest ([IO.Path]::GetFullPath($ManifestPath)) -Package ([IO.Path]::GetFullPath($PackagePath)) -Certificate ([IO.Path]::GetFullPath($PublicCertificatePath)) -PinnedCertificate $pinnedCertificate
$version = [string]$manifest.version
$releaseRoot = Join-Path (Join-Path $resolvedInstallRoot 'releases') $version
if (Test-Path -LiteralPath $releaseRoot) { throw "Release $version is already installed." }
if (-not $PSCmdlet.ShouldProcess($releaseRoot, "Install and activate PremiereCalendar $version")) { return }

New-Item -ItemType Directory -Path $resolvedInstallRoot, $resolvedDataRoot, $updaterRoot -Force | Out-Null
$installedHelper = Join-Path $updaterRoot 'update-helper.ps1'
if (-not [IO.Path]::GetFullPath($PSCommandPath).Equals([IO.Path]::GetFullPath($installedHelper), [StringComparison]::OrdinalIgnoreCase)) {
    Copy-Item -LiteralPath $PSCommandPath -Destination $installedHelper -Force
}
$githubUpdaterSource = Join-Path $PSScriptRoot 'install-github-release.ps1'
$installedGithubUpdater = Join-Path $updaterRoot 'install-github-release.ps1'
if ((Test-Path -LiteralPath $githubUpdaterSource -PathType Leaf) -and
    -not [IO.Path]::GetFullPath($githubUpdaterSource).Equals([IO.Path]::GetFullPath($installedGithubUpdater), [StringComparison]::OrdinalIgnoreCase)) {
    Copy-Item -LiteralPath $githubUpdaterSource -Destination $installedGithubUpdater -Force
}
if (-not (Test-Path -LiteralPath $pinnedCertificate)) {
    Copy-Item -LiteralPath $PublicCertificatePath -Destination $pinnedCertificate
}
foreach ($subdirectory in @('data', 'cache\calendar', 'cache\images', 'logs', 'backups')) {
    New-Item -ItemType Directory -Path (Join-Path $resolvedDataRoot $subdirectory) -Force | Out-Null
}
$legacyData = Join-Path $resolvedInstallRoot 'App_Data'

$stage = Join-Path $resolvedInstallRoot ".stage-$version-$([Guid]::NewGuid().ToString('N'))"
$current = Join-Path $resolvedInstallRoot 'current'
$previous = Join-Path $resolvedInstallRoot ".previous-$([Guid]::NewGuid().ToString('N'))"
$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
$serviceConfiguration = Get-CimInstance Win32_Service -Filter "Name='$($ServiceName.Replace("'", "''"))'" -ErrorAction SilentlyContinue
$previousServicePath = if ($null -ne $serviceConfiguration) { [string]$serviceConfiguration.PathName } else { $null }
$serviceWasCreated = $false
$hadCurrent = Test-Path -LiteralPath $current
$databaseDirectory = Join-Path $resolvedDataRoot 'data'
$databaseBackup = Join-Path (Join-Path $resolvedDataRoot 'backups') "pre-$version-$([DateTimeOffset]::UtcNow.ToString('yyyyMMddHHmmss'))"
$databaseFiles = @('premiere-calendar.db', 'premiere-calendar.db-wal', 'premiere-calendar.db-shm')
$databaseStateCaptured = $false
try {
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    Expand-SafeArchive -ArchivePath $PackagePath -Destination $stage
    foreach ($required in @('PremiereCalendar.exe', 'PremiereCalendar.dll', 'build-metadata.json')) {
        if (-not (Test-Path -LiteralPath (Join-Path $stage $required) -PathType Leaf)) { throw "Release is missing $required." }
    }
    $metadata = Get-Content -LiteralPath (Join-Path $stage 'build-metadata.json') -Raw | ConvertFrom-Json
    if ([string]$metadata.version -ne $version) { throw 'Build metadata version does not match the manifest.' }
    New-Item -ItemType Directory -Path (Split-Path -Parent $releaseRoot) -Force | Out-Null
    Move-Item -LiteralPath $stage -Destination $releaseRoot
    if ($null -ne $service -and $service.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        $service.WaitForStatus('Stopped', '00:00:45')
    }
    # Take the one-time legacy data snapshot only after the existing service has
    # stopped, so the SQLite database and any WAL files form a consistent set.
    if (Test-Path -LiteralPath $legacyData -PathType Container) {
        Copy-Item -Path (Join-Path $legacyData '*') -Destination $resolvedDataRoot -Recurse -Force -ErrorAction Stop
    }
    $existingDatabaseFiles = @($databaseFiles | Where-Object { Test-Path -LiteralPath (Join-Path $databaseDirectory $_) -PathType Leaf })
    if ($existingDatabaseFiles.Count -gt 0) {
        New-Item -ItemType Directory -Path $databaseBackup -Force | Out-Null
        foreach ($databaseFile in $existingDatabaseFiles) {
            Copy-Item -LiteralPath (Join-Path $databaseDirectory $databaseFile) -Destination $databaseBackup -Force
        }
    }
    $databaseStateCaptured = $true
    $newCurrent = Join-Path $resolvedInstallRoot ".current-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Junction -Path $newCurrent -Target $releaseRoot | Out-Null
    if ($hadCurrent) { Move-Item -LiteralPath $current -Destination $previous }
    Move-Item -LiteralPath $newCurrent -Destination $current

    $exePath = Join-Path $current 'PremiereCalendar.exe'
    $quotedExePath = '"' + $exePath + '"'
    if ($null -eq $service) {
        & sc.exe create $ServiceName binPath= $quotedExePath start= auto DisplayName= 'Premiere Calendar' | Out-Null
        $serviceWasCreated = $true
    }
    else {
        & sc.exe config $ServiceName binPath= $quotedExePath start= auto | Out-Null
    }
    if ($LASTEXITCODE -ne 0) { throw 'Windows service configuration failed.' }
    & sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/300000 | Out-Null
    $environment = @(
        "Urls=http://0.0.0.0:$Port",
        "ASPNETCORE_URLS=http://0.0.0.0:$Port",
        'ASPNETCORE_ENVIRONMENT=Production',
        "AppDatabase__Path=$(Join-Path $resolvedDataRoot 'data\premiere-calendar.db')",
        "CalendarCache__Directory=$(Join-Path $resolvedDataRoot 'cache\calendar')",
        "ImageCache__Directory=$(Join-Path $resolvedDataRoot 'cache\images')"
    )
    $serviceRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
    New-ItemProperty -Path $serviceRegistryPath -Name Environment -PropertyType MultiString -Value $environment -Force | Out-Null
    Start-Service -Name $ServiceName
    (Get-Service -Name $ServiceName).WaitForStatus('Running', '00:00:45')
    Wait-ForHealthyVersion -ExpectedVersion $version
    if (Test-Path -LiteralPath $previous) { Remove-Item -LiteralPath $previous -Force }
    [IO.File]::WriteAllText((Join-Path $updaterRoot 'active-version.txt'), $version, [Text.UTF8Encoding]::new($false))
    Write-Host "PremiereCalendar $version installed and healthy."
}
catch {
    $failure = $_
    try {
        $rollbackService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
        if ($null -ne $rollbackService -and $rollbackService.Status -ne 'Stopped') {
            Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
            $rollbackService.WaitForStatus('Stopped', '00:00:30')
        }
        if (Test-Path -LiteralPath $current) { [IO.Directory]::Delete($current) }
        if (Test-Path -LiteralPath $previous) { Move-Item -LiteralPath $previous -Destination $current }
        if ($databaseStateCaptured) {
            foreach ($databaseFile in $databaseFiles) {
                $backupFile = Join-Path $databaseBackup $databaseFile
                $activeFile = Join-Path $databaseDirectory $databaseFile
                if (Test-Path -LiteralPath $backupFile -PathType Leaf) {
                    Copy-Item -LiteralPath $backupFile -Destination $activeFile -Force
                }
                elseif (Test-Path -LiteralPath $activeFile -PathType Leaf) {
                    Remove-Item -LiteralPath $activeFile -Force
                }
            }
        }
        if ($hadCurrent -and (Test-Path -LiteralPath (Join-Path $current 'PremiereCalendar.exe'))) {
            $rollbackExe = Join-Path $current 'PremiereCalendar.exe'
            $quotedRollbackExe = '"' + $rollbackExe + '"'
            & sc.exe config $ServiceName binPath= $quotedRollbackExe | Out-Null
            Start-Service -Name $ServiceName
        }
        elseif (-not [string]::IsNullOrWhiteSpace($previousServicePath)) {
            & sc.exe config $ServiceName binPath= $previousServicePath | Out-Null
            Start-Service -Name $ServiceName
        }
        elseif ($serviceWasCreated) {
            & sc.exe delete $ServiceName | Out-Null
        }
    }
    catch { Write-Warning "Rollback encountered an additional failure: $($_.Exception.Message)" }
    if (Test-Path -LiteralPath $releaseRoot) { Remove-Item -LiteralPath $releaseRoot -Recurse -Force -ErrorAction SilentlyContinue }
    throw $failure
}
finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue }
}
