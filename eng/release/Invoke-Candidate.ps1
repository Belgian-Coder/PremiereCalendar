#Requires -Version 7.0
<#
.SYNOPSIS
Plans, validates or packages an exact committed linux/amd64 candidate using the canonical release scripts.

.DESCRIPTION
-Output is a fresh request directory under this checkout's artifacts directory. The canonical initializer
creates its bundle there using the supplied real version and installed baseline; this adapter never
guesses them or contacts the NAS. Validate runs the full canonical service suites; Package builds the candidate
archive and writes candidate-artifacts.json. Neither mode establishes full release acceptance.

Plan and -WhatIf print the commands without invoking WSL, Docker, network calls or writing files, even on Mac.
Execution requires Windows x64 with the supported Ubuntu-24.04 WSL engine. Upload is deliberately unsupported:
there is no draft upload destination, and feature candidates must not enter the production NAS engine.
Production operators must use the separately authorized canonical Load-Image.ps1 runbook and all its gates.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][ValidateSet('linux-amd64')][string] $Target,
    [Parameter(Mandatory)][ValidateSet('plan', 'validate', 'package', 'upload')][string] $Mode,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $Revision,
    [Parameter(Mandatory)][string] $Output,
    [ValidatePattern('^[0-9]{1,6}\.[0-9]{1,6}\.[0-9]{1,6}$')][string] $Version,
    [ValidatePattern('^[0-9]{1,6}\.[0-9]{1,6}\.[0-9]{1,6}$')][string] $ExpectedOldVersion,
    [ValidatePattern('^sha256:[0-9a-f]{64}$')][string] $ExpectedOldDigest,
    [ValidateRange(0, 1000000)][int] $ExpectedStackRevision = 0,
    [ValidateRange(1, 16)][int] $Jobs = 2
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathRooted($Output)) { throw '-Output must be an absolute request-directory path.' }
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$outputPath = [IO.Path]::GetFullPath($Output).TrimEnd('\', '/')
$prefix = (Join-Path $root 'artifacts').TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
if (-not $outputPath.StartsWith($prefix, $comparison)) { throw '-Output must stay under this checkout''s artifacts directory.' }
if ($Mode -eq 'upload') {
    throw 'Candidate upload is unsupported: no draft destination exists. Use the separately authorized production Load-Image.ps1 runbook with all existing gates; feature/ci-test must not upload production artifacts.'
}
$identity = if ($IsWindows) { $outputPath.ToLowerInvariant() } else { $outputPath }
$suffix = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($identity + $Revision))).Substring(0, 12).ToLowerInvariant()
$bundleVersion = if ($Version) { $Version } else { '<version>' }
$bundleName = "$bundleVersion-candidate-$suffix"
$bundleRoot = Join-Path $outputPath 'release'
$bundle = Join-Path $bundleRoot $bundleName
$commands = [ordered]@{
    initialize = @('pwsh', '-NoProfile', '-File', (Join-Path $PSScriptRoot 'Initialize-Release.ps1'), '-Version', $Version, '-BundleName', $bundleName, '-BundleRoot', $bundleRoot, '-ResourcePrefix', "pcc$suffix", '-ExpectedOldVersion', $ExpectedOldVersion, '-ExpectedOldDigest', $ExpectedOldDigest, '-ExpectedStackRevision', "$ExpectedStackRevision")
    validate = @('pwsh', '-NoProfile', '-File', (Join-Path $PSScriptRoot 'Invoke-ReleaseTests.ps1'), '-Bundle', $bundle, '-ExpectedRevision', $Revision, '-Cpus', "$Jobs", '-MaxThreads', "$Jobs", '-BuildMaxCpuCount', "$Jobs")
    package = @('pwsh', '-NoProfile', '-File', (Join-Path $PSScriptRoot 'Build-Image.ps1'), '-Bundle', $bundle, '-ExpectedRevision', $Revision)
}
if ($Mode -eq 'plan' -or $WhatIfPreference) {
    [pscustomobject]@{
        Target = $Target; Revision = $Revision; Output = $outputPath
        Builder = 'Windows x64 / Ubuntu-24.04 WSL / local Docker socket'
        RequiredInput = 'Version, ExpectedOldVersion, ExpectedOldDigest and ExpectedStackRevision (real version/installed baseline)'
        Commands = $commands; Jobs = $Jobs; UploadSupported = $false; ReleaseAccepted = $false
        ArtifactManifest = Join-Path $bundle 'candidate-artifacts.json'
    }
    return
}
if (-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') {
    throw 'Candidate execution requires the supported Windows x64 / Ubuntu-24.04 WSL builder. Native macOS/ARM64 execution is not qualified; use -Mode plan for a read-only preview.'
}
if (-not $Version -or -not $ExpectedOldVersion -or -not $ExpectedOldDigest -or $ExpectedStackRevision -lt 1) {
    throw 'Supply Version, ExpectedOldVersion, ExpectedOldDigest and ExpectedStackRevision using the real version and installed baseline.'
}
# Prevent linked output paths from escaping this worktree, without moving or removing anything.
$ancestorPath = $outputPath
while (-not (Test-Path -LiteralPath $ancestorPath)) { $ancestorPath = Split-Path -Parent $ancestorPath }
$ancestor = Get-Item -LiteralPath $ancestorPath -Force
while ($ancestor -and -not $ancestor.FullName.Equals($root, $comparison)) {
    if ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Candidate output must not traverse a linked directory.' }
    $ancestor = $ancestor.Parent
}
. (Join-Path $PSScriptRoot 'ReleaseCommon.ps1')
Assert-CleanSourceAtRevision -Revision $Revision
[void][IO.Directory]::CreateDirectory($outputPath)
# Each request owns its lock. Existing release evidence and warm build caches stay untouched.
$lock = [IO.File]::Open((Join-Path $outputPath 'candidate.lock'), [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::Write, [IO.FileShare]::None)
try {
    $binding = [ordered]@{ schema = 1; sourceRevision = $Revision; target = $Target; version = $Version; oldVersion = $ExpectedOldVersion; oldDigest = $ExpectedOldDigest; stackRevision = $ExpectedStackRevision; jobs = $Jobs }
    $bindingPath = Join-Path $outputPath 'candidate-request.json'
    if (Test-Path -LiteralPath $bindingPath) {
        $existing = Read-Json $bindingPath
        foreach ($key in $binding.Keys) {
            if ([string]$existing.$key -cne [string]$binding[$key]) { throw "Request output is already bound to different $key." }
        }
    } else {
        if (Test-Path -LiteralPath $bundleRoot) { throw 'Request output already contains release data without a matching source binding.' }
        Write-NewJson -Path $bindingPath -Object $binding
        $argv = $commands.initialize
        & $argv[0] @($argv[1..($argv.Count - 1)])
        if ($LASTEXITCODE -ne 0) { throw "Canonical initialization failed (exit $LASTEXITCODE); preserve this output and use a fresh request." }
    }
    $context = Get-ReleaseContext -Bundle $bundle
    $manifestPath = Join-Path $bundle 'candidate-artifacts.json'
    if ($context.Config.version -cne $Version -or $context.Config.oldVersion -cne $ExpectedOldVersion -or $context.Config.oldImageDigest -cne $ExpectedOldDigest -or $context.Config.expectedStackRevision -ne $ExpectedStackRevision) {
        throw 'Initialized release configuration differs from the request parameters.'
    }
    if ($Mode -eq 'package') { Assert-FilesAbsent @($manifestPath) }
    $argv = $commands[$Mode]
    & $argv[0] @($argv[1..($argv.Count - 1)])
    if ($LASTEXITCODE -ne 0) { throw "Canonical candidate $Mode failed (exit $LASTEXITCODE)." }
    if ($Mode -eq 'package') {
        $metadata = Read-Json (Join-Path $bundle 'build-metadata.json')
        $hash = Get-FileSha256 $context.ImageArchive
        if ($metadata.sourceRevision -cne $Revision -or $metadata.os -ne 'linux' -or $metadata.architecture -ne 'amd64' -or $metadata.archiveSha256 -cne $hash) {
            throw 'Candidate artifact identity does not match the requested source/platform/hash.'
        }
        Write-NewJson -Path $manifestPath -Object ([ordered]@{
            schema = 1; target = $Target; sourceRevision = $Revision
            candidateOnly = $true; releaseAccepted = $false; uploaded = $false
            artifacts = @([ordered]@{
                path = [IO.Path]::GetFileName($context.ImageArchive)
                bytes = (Get-Item -LiteralPath $context.ImageArchive).Length
                sha256 = $hash; imageIdentity = $metadata.localImageIdentity
            })
            buildEvidence = 'build-metadata.json'
            acceptance = 'Separate full regression/browser, vulnerability/SBOM, review, backup and exact-image runtime gates remain required.'
        })
        Get-Content -LiteralPath $manifestPath
    }
} finally { $lock.Dispose() }
