#Requires -Version 7.0
<#
.SYNOPSIS
Step 0 of a local release: creates the evidence bundle and records every release parameter in release-config.json.

.DESCRIPTION
All later scripts take only -Bundle (the full path printed here) and read the rest from release-config.json, so a
release cannot be half-run with mixed parameters. Nothing outside the bundle is touched, nothing on the NAS is read,
and the script refuses to overwrite an existing configuration: a retry or a candidate needs a new -BundleName and a
new -ResourcePrefix.

The pinned SDK, runtime and PostgreSQL images are read from the Dockerfile and compose.yaml, and the target
schema version from Directory.Build.props, so they can never drift from what the repository builds.

.PARAMETER Version
The release version, for example 1.2.20. It becomes the image's version label and the /health/version value.

.PARAMETER BundleName
Folder name of the evidence bundle under -BundleRoot. Defaults to the version; use 1.2.20-candidate2 for a retry.

.PARAMETER ResourcePrefix
Lower-case prefix of every Docker network/container/volume the release creates locally (for example pcrel1220).
Must be unique per attempt: the scripts refuse name collisions instead of reusing resources.

.PARAMETER SubnetPrefix
First two octets of the explicit /24 subnets (10.130 to 10.149). Docker's default address pools are exhausted on
the release machine, so a network is never created without --subnet. With -SubnetOctet N the roles use N (tests),
N+1 (smoke) and N+2 (restore check). The default (0) derives N from the bundle name (20 to 227, step 3), so a new
candidate's networks do not overlap an earlier candidate's leftover restore network.

.PARAMETER ExpectedOldVersion
/health/version value of the installed release that will be replaced.

.PARAMETER ExpectedOldDigest
Image configuration digest (sha256:...) that the live stack currently pins; it must appear exactly once in the
stack file.

.PARAMETER ExpectedStackRevision
Portainer StackFileVersion of the stack before this release; after activation it must be one higher.

.PARAMETER RequiredSettingKey
Optional persisted AppParameters key (for example Sources.Trakt.Enabled) that must hold -RequiredSettingValue
before activation; verified read-only by Test-PersistedSetting.ps1 and required by Deploy-Release.ps1.

.EXAMPLE
pwsh -File D:\Projects\PremiereCalendar\eng\release\Initialize-Release.ps1 -Version 1.2.20 -ExpectedOldVersion 1.2.19 `
  -ExpectedOldDigest sha256:f7e7ad1d4ec21eff6e2ea33e30c77eb003b12d68d787c23ac519282b7f4b0f54 -ExpectedStackRevision 18 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+$')][string] $Version,
    [ValidatePattern('^[0-9A-Za-z][0-9A-Za-z._-]{0,63}$')][string] $BundleName,
    [string] $BundleRoot = (Join-Path $PSScriptRoot '..\..\artifacts\release'),
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}(:[A-Za-z0-9_.-]{1,64})?$')][string] $ImageTag,
    [ValidatePattern('^[a-z][a-z0-9]{2,23}$')][string] $ResourcePrefix,
    [ValidatePattern('^10\.(13[0-9]|14[0-9])$')][string] $SubnetPrefix = '10.139',
    # 0 = derive from the bundle name, so a new candidate never collides with an earlier candidate's leftover networks.
    [ValidateRange(0, 240)][int] $SubnetOctet = 0,
    [Parameter(Mandatory)][ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+$')][string] $ExpectedOldVersion,
    [Parameter(Mandatory)][ValidatePattern('^sha256:[0-9a-f]{64}$')][string] $ExpectedOldDigest,
    [ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedOldSourceRevision,
    [Parameter(Mandatory)][ValidateRange(1, 1000000)][int] $ExpectedStackRevision,
    [ValidateRange(0, 100000)][int] $ExpectedOldSchemaVersion = -1,
    [ValidateRange(1, 100000)][int] $StackId = 20,
    [ValidateRange(1, 100000)][int] $EndpointId = 2,
    [ValidatePattern('^[A-Za-z0-9_.-]+$')][string] $AppContainer = 'premierecalendar-app-1',
    [ValidatePattern('^[A-Za-z0-9_.-]+$')][string] $DatabaseContainer = 'premierecalendar-db-1',
    [ValidatePattern('^[A-Za-z0-9_.-]+$')][string] $BackupContainer = 'premierecalendar-backup-1',
    [string] $DirectUrl = 'http://192.168.68.20:18084',
    [string] $PrivateUrl = 'https://premiere.home.belgiancoder.be',
    [string] $RequiredComputerName = 'WINDOWS11-VM',
    [ValidatePattern('^[A-Za-z0-9_.:-]*$')][string] $RequiredSettingKey = '',
    [string] $RequiredSettingValue = '',
    [string] $NasBackupShare,
    [string] $PrivateBackupRoot,
    [string] $NasReleaseRoot,
    [string] $ComposeRoot,
    [string] $PortainerApiScript,
    [string] $TrivyImage = 'aquasec/trivy:0.76.2',
    [string] $SyftImage = 'anchore/syft:v1.57.2'
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseCommon.ps1')

if ([string]::IsNullOrWhiteSpace($BundleName)) { $BundleName = $Version }
if (-not $BundleName.StartsWith($Version)) { throw "-BundleName must start with the version ($Version), for example $Version-candidate2." }
if ([string]::IsNullOrWhiteSpace($ImageTag)) { $ImageTag = "premierecalendar:$BundleName" }
if ([string]::IsNullOrWhiteSpace($ResourcePrefix)) { $ResourcePrefix = 'pcrel' + (($BundleName -replace '[^0-9a-zA-Z]', '').ToLowerInvariant()) }
if ($ResourcePrefix.Length -gt 24) { throw "The derived resource prefix '$ResourcePrefix' is longer than 24 characters; pass -ResourcePrefix." }
$defaults = Get-ReleaseDefaults
# Plain assignments (not Set-Variable): under -WhatIf every ShouldProcess-aware cmdlet would skip its work.
if ([string]::IsNullOrWhiteSpace($NasBackupShare)) { $NasBackupShare = $defaults.NasBackupShare }
if ([string]::IsNullOrWhiteSpace($PrivateBackupRoot)) { $PrivateBackupRoot = $defaults.PrivateBackupRoot }
if ([string]::IsNullOrWhiteSpace($NasReleaseRoot)) { $NasReleaseRoot = $defaults.NasReleaseRoot }
if ([string]::IsNullOrWhiteSpace($ComposeRoot)) { $ComposeRoot = $defaults.ComposeRoot }
if ([string]::IsNullOrWhiteSpace($PortainerApiScript)) { $PortainerApiScript = $defaults.PortainerApi }
if (($RequiredSettingKey -eq '') -ne ($RequiredSettingValue -eq '')) { throw '-RequiredSettingKey and -RequiredSettingValue must be given together.' }

if ($SubnetOctet -eq 0) {
    $nameHash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($BundleName))
    $SubnetOctet = 20 + ([int]$nameHash[0] % 70) * 3   # 20..227; the roles use N, N+1 and N+2
}
$images = Get-PinnedImages
$schema = Get-DatabaseSchemaVersion
if ($ExpectedOldSchemaVersion -lt 0) { $ExpectedOldSchemaVersion = $schema }
if ($ExpectedOldSchemaVersion -gt $schema) { throw "The installed schema ($ExpectedOldSchemaVersion) is newer than this source's schema ($schema)." }
$bundlePath = [IO.Path]::GetFullPath((Join-Path $BundleRoot $BundleName))
$config = [ordered]@{
    schema = 1
    createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
    version = $Version
    bundleName = $BundleName
    imageTag = $ImageTag
    resourcePrefix = $ResourcePrefix
    subnetPrefix = $SubnetPrefix
    subnetOctet = $SubnetOctet
    oldVersion = $ExpectedOldVersion
    oldImageDigest = $ExpectedOldDigest
    oldSourceRevision = if ($ExpectedOldSourceRevision) { $ExpectedOldSourceRevision } else { $null }
    oldSchemaVersion = $ExpectedOldSchemaVersion
    databaseSchemaVersion = $schema
    stackId = $StackId
    endpointId = $EndpointId
    expectedStackRevision = $ExpectedStackRevision
    appContainer = $AppContainer
    databaseContainer = $DatabaseContainer
    backupContainer = $BackupContainer
    directUrl = $DirectUrl.TrimEnd('/')
    privateUrl = $PrivateUrl.TrimEnd('/')
    requiredComputerName = $RequiredComputerName
    requiredSettingKey = if ($RequiredSettingKey) { $RequiredSettingKey } else { $null }
    requiredSettingValue = if ($RequiredSettingKey) { $RequiredSettingValue } else { $null }
    nasBackupShare = $NasBackupShare
    privateBackupRoot = $PrivateBackupRoot
    nasReleaseRoot = $NasReleaseRoot
    composeRoot = $ComposeRoot
    portainerApiScript = $PortainerApiScript
    sdkImage = $images.Sdk
    runtimeImage = $images.Runtime
    postgresImage = $images.Postgres
    trivyImage = $TrivyImage
    syftImage = $SyftImage
}

$plan = @(
    "Create the evidence bundle $bundlePath (public evidence), $bundlePath.private (restricted evidence) and $bundlePath\installed",
    "Write $bundlePath\release-config.json (refuses to overwrite an existing one)",
    "Version $Version, image tag $ImageTag, resource prefix $ResourcePrefix, subnets $(Get-ReleaseSubnet ([pscustomobject]@{ Config = [pscustomobject]$config }) 0), .../1, .../2 (explicit /24s)",
    "Replaces v$ExpectedOldVersion ($ExpectedOldDigest) in stack $StackId revision $ExpectedStackRevision on endpoint $EndpointId; schema $ExpectedOldSchemaVersion -> $schema",
    "Pins read from the repository: SDK $($images.Sdk); PostgreSQL $($images.Postgres)")
if (Show-ReleasePlan -Title 'Initialize-Release' -Step $plan) { return }

if (-not $PSCmdlet.ShouldProcess($bundlePath, 'Create the release bundle and release-config.json')) { return }
Assert-FilesAbsent (Join-Path $bundlePath 'release-config.json')
New-Item -ItemType Directory -Path $bundlePath, "$bundlePath.private", (Join-Path $bundlePath 'installed') -Force | Out-Null
Write-NewJson -Path (Join-Path $bundlePath 'release-config.json') -Object $config
Write-Host "Release bundle created: $bundlePath"
Write-Host 'Use this value as -Bundle in every following script:'
Write-Host "  $bundlePath"
