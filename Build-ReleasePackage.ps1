[CmdletBinding()]
param(
    [string]$Version,
    [string]$Runtime = 'win-x64',
    [switch]$SkipTests,
    [switch]$NoZip
)

$ErrorActionPreference = 'Stop'

$releaseScript = Join-Path $PSScriptRoot 'deploy\Build-ReleasePackage.ps1'
if (-not (Test-Path -LiteralPath $releaseScript)) {
    throw "Release package script not found: $releaseScript"
}

$arguments = @{
    Runtime = $Runtime
}

if (-not [string]::IsNullOrWhiteSpace($Version)) {
    $arguments.Version = $Version
}

if ($SkipTests) {
    $arguments.SkipTests = $true
}

if ($NoZip) {
    $arguments.NoZip = $true
}

& $releaseScript @arguments
