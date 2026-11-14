#Requires -Version 7.0
<#
.SYNOPSIS
Step 3: builds the release image from the exact committed source archive and exports it as a Docker archive.

.DESCRIPTION
Requires HEAD to be -ExpectedRevision with a clean tracked tree and no untracked source file. It archives the
committed source (git archive, core.autocrlf=false), proves every archived C# and SQL file is byte-identical to the
committed Git blob (verify_source_archive.py; with tested-source-fingerprints.json present they are bound too),
builds linux/amd64 in the local WSL Docker engine from that archive only (never from the working folder), exports
the image with docker save, checks the version/revision labels and writes build-metadata.json. It refuses to
overwrite any earlier candidate asset or to replace an existing local image tag.

The test, review and scan evidence is checked later (Load/Deploy/Close), so this step can run before the tests.

.PARAMETER Bundle
Full path of the release bundle.

.PARAMETER ExpectedRevision
The exact 40-character release commit.

.EXAMPLE
pwsh -File D:\Projects\PremiereCalendar\eng\release\Build-Image.ps1 -Bundle D:\Projects\PremiereCalendar\artifacts\release\1.2.20 -ExpectedRevision 0123456789abcdef0123456789abcdef01234567
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string] $Bundle,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedRevision
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
. (Join-Path $PSScriptRoot 'ReleaseCommon.ps1')
$ctx = Get-ReleaseContext -Bundle $Bundle
$cfg = $ctx.Config
$verification = Join-Path $ctx.Bundle 'source-archive-verification.json'
$buildLog = Join-Path $ctx.Bundle 'build.log'
$metadataPath = Join-Path $ctx.Bundle 'build-metadata.json'
$fingerprints = Join-Path $ctx.Bundle 'tested-source-fingerprints.json'

$plan = @(
    "Require HEAD = $ExpectedRevision, a clean tracked tree and no untracked source files",
    "Refuse if $($ctx.SourceArchive), $verification, $($ctx.ImageArchive), $buildLog or $metadataPath already exist, or if the local tag $($cfg.imageTag) exists",
    "git -c core.autocrlf=false archive the commit to $($ctx.SourceArchive), then verify_source_archive.py against the committed blobs",
    "docker buildx build --platform linux/amd64 --load -t $($cfg.imageTag) from that archive on stdin (local WSL engine), VERSION=$($cfg.version)",
    "docker save -o $($ctx.ImageArchive); check the revision/version labels; write $metadataPath")
if (Show-ReleasePlan -Title "Build-Image $($ctx.BundleName)" -Step $plan) { return }

Assert-CleanSourceAtRevision -Revision $ExpectedRevision
Assert-FilesAbsent @($ctx.SourceArchive, $verification, $ctx.ImageArchive, $buildLog, $metadataPath)
Assert-LocalDockerAvailable
if (Test-LocalDocker image inspect $cfg.imageTag) { throw "The local candidate tag already exists; refusing to replace it: $($cfg.imageTag)" }

$buildId = "local-release-$($cfg.version)." + $ExpectedRevision.Substring(0, 12)
$buildTime = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
& git -c core.autocrlf=false -C $ctx.Repo archive --format=tar -o $ctx.SourceArchive $ExpectedRevision
if ($LASTEXITCODE -ne 0) { throw 'The exact source archive could not be created.' }
$verifyArguments = @((Join-Path $PSScriptRoot 'verify_source_archive.py'), '--repo', $ctx.Repo, '--revision', $ExpectedRevision,
    '--archive', $ctx.SourceArchive, '--output', $verification, '--version', $cfg.version)
if (Test-Path -LiteralPath $fingerprints) { $verifyArguments += @('--tested-fingerprints', $fingerprints) }
Invoke-ReleasePython @verifyArguments
$proof = Read-Json $verification
if (-not $proof.allChecksPass -or $proof.sourceRevision -cne $ExpectedRevision -or $proof.verifiedFiles.'.cs' -lt 1) { throw 'The source archive differs from the committed blobs; the build was not started.' }

$wslArchive = ConvertTo-WslPath $ctx.SourceArchive
$wslLog = ConvertTo-WslPath $buildLog
$timer = [Diagnostics.Stopwatch]::StartNew()
# Only the committed archive is the build context, so unrelated or untracked local files can never enter the image.
$script = @"
set -u
docker --host unix:///var/run/docker.sock buildx build --builder default --platform linux/amd64 --load \
  --build-arg VERSION=$($cfg.version) --build-arg SOURCE_REVISION=$ExpectedRevision --build-arg BUILD_ID=$buildId \
  --build-arg BUILD_TIME_UTC=$buildTime -t $(ConvertTo-ShellQuoted $cfg.imageTag) - < $(ConvertTo-ShellQuoted $wslArchive) > $(ConvertTo-ShellQuoted $wslLog) 2>&1
"@
$exit = Invoke-LocalShell $script
if ($exit -ne 0) { Get-Content -LiteralPath $buildLog -Tail 45; throw 'The image build failed.' }
$buildElapsed = $timer.ElapsedMilliseconds
$null = Invoke-LocalDocker save -o (ConvertTo-WslPath $ctx.ImageArchive) $cfg.imageTag
$image = (Invoke-LocalDockerJson image inspect $cfg.imageTag)[0]
if ($image.Config.Labels.'org.opencontainers.image.revision' -ne $ExpectedRevision -or $image.Config.Labels.'org.opencontainers.image.version' -ne $cfg.version) { throw 'The image source/version labels do not match the release.' }
$archive = Get-Item -LiteralPath $ctx.ImageArchive
$metadata = [ordered]@{
    version = $cfg.version; sourceRevision = $ExpectedRevision; buildId = $buildId; buildTimeUtc = $buildTime
    archive = $archive.Name; bytes = $archive.Length; archiveSha256 = Get-FileSha256 $archive.FullName
    localImageIdentity = $image.Id; repoDigests = $image.RepoDigests; os = $image.Os; architecture = $image.Architecture; labels = $image.Config.Labels
    buildMilliseconds = $buildElapsed; buildAndExportMilliseconds = $timer.ElapsedMilliseconds
    localStagingTag = $cfg.imageTag
    testedSourceFingerprintsSha256 = if (Test-Path -LiteralPath $fingerprints) { Get-FileSha256 $fingerprints } else { $null }
    sourceArchiveVerificationSha256 = Get-FileSha256 $verification
    sourceArchiveSha256 = Get-FileSha256 $ctx.SourceArchive
    sdkImage = $cfg.sdkImage; runtimeImage = $cfg.runtimeImage
}
Write-NewJson -Path $metadataPath -Object $metadata -Depth 12
$metadata | ConvertTo-Json -Depth 12
