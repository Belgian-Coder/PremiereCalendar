#Requires -Version 7.0
<#
.SYNOPSIS
Step 5: vulnerability, configuration and secret scan plus CycloneDX SBOM of the exact exported image archive.

.DESCRIPTION
Scans the docker-save archive that will be deployed (not a registry image) with Trivy and generates the SBOM with
Syft, both in containers of the local WSL engine. The archive is copied to a new native WSL directory and its SHA-256
is checked against build-metadata.json first. audit_image_archive.py then binds archive, labels, platform and source
proof to the local image, and summarize_scan.py writes release-<version>-scan-summary.json. The run fails unless the
image has no vulnerabilities, no misconfigurations and no secrets, the SBOM is a non-empty CycloneDX document and the
NuGet audit passed. With -IgnoreUnfixed only vulnerabilities that have a fix count (the policy of the former hosted
workflow); the choice is recorded in the summary. Existing evidence is never overwritten.

.PARAMETER Bundle
Full path of the release bundle.

.PARAMETER IgnoreUnfixed
Count only vulnerabilities that have a fixed version.

.EXAMPLE
pwsh -File D:\Projects\PremiereCalendar\eng\release\Invoke-Scans.ps1 -Bundle D:\Projects\PremiereCalendar\artifacts\release\1.2.20
#>
[CmdletBinding(SupportsShouldProcess)]
param([Parameter(Mandatory)][string] $Bundle, [switch] $IgnoreUnfixed)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseCommon.ps1')
$ctx = Get-ReleaseContext -Bundle $Bundle
$cfg = $ctx.Config
$v = $cfg.version
$trivyJson = Join-Path $ctx.Bundle "release-$v-trivy.json"
$sbomJson = Join-Path $ctx.Bundle "release-$v-sbom.cdx.json"
$auditJson = Join-Path $ctx.Bundle "release-$v-audit.json"
$summaryJson = Join-Path $ctx.Bundle "release-$v-scan-summary.json"
$scannersJson = Join-Path $ctx.Bundle "release-$v-scanners.json"
$trivyLog = Join-Path $ctx.Bundle 'release-trivy.log'
$trivyDbFile = Join-Path $ctx.Bundle "release-$v-trivy-db-metadata.json"
$syftLog = Join-Path $ctx.Bundle 'release-syft.log'
$inspectFile = Join-Path $ctx.Private 'image-inspect.json'
$names = @{ Trivy = "$($ctx.Prefix)_trivy"; Syft = "$($ctx.Prefix)_syft"; TrivyDb = "$($ctx.Prefix)_trivy_db"; TrivyAge = "$($ctx.Prefix)_trivy_db_age" }
$trivyCache = Get-ReleaseCacheVolume 'trivy'
# Only Trivy's vulnerability DB directory persists; the rest of its cache (layer analysis, checks) stays in the
# disposable container, so no earlier scan's analysis can stand in for re-reading this archive.
$trivyCachePath = '/root/.cache/trivy/db'
$trivyDbMetadata = "$trivyCachePath/metadata.json"
$staging = "/tmp/$($ctx.Prefix)-scan"

$plan = @(
    "Refuse if any scan evidence exists ($trivyJson, $sbomJson, $auditJson, $summaryJson, $trivyDbFile) or the resources $($names.Trivy), $($names.Syft), $($names.TrivyDb), $($names.TrivyAge), $staging exist",
    "Discard the cached DB when this cache downloaded it more than 5 h ago, so the scan uses a DB as current as a fresh download",
    "Check $(Join-Path $ctx.Bundle 'release-nuget-audit.log') exists (step 4) and copy the image archive to $staging with a SHA-256 check against build-metadata.json",
    "Create the persistent release cache volume $($trivyCache.Name) ($($trivyCache.Label)) if missing; it is mounted at Trivy's DB directory only (layer analysis stays disposable) and is never deleted",
    "Run side by side on the same read-only staged archive: Trivy $($cfg.trivyImage) (--scanners vuln,misconfig,secret$(if ($IgnoreUnfixed) { ', --ignore-unfixed' })) and Syft $($cfg.syftImage) (CycloneDX JSON SBOM); each tool keeps its own log and exit code",
    "Read the Trivy DB metadata (UpdatedAt/NextUpdate/DownloadedAt) from the cache into $trivyDbFile (embedded in $scannersJson); summarize_scan.py fails when the DB is older than 48 h or was downloaded more than 6 h before the scan",
    'audit_image_archive.py (archive/labels/platform/source proof) and summarize_scan.py; fail on any finding',
    'Remove the temporary copy and the finished containers (the cache volume stays)')
if (Show-ReleasePlan -Title 'Invoke-Scans' -Step $plan) { return }

$nugetLog = Join-Path $ctx.Bundle 'release-nuget-audit.log'
if (-not (Test-Path -LiteralPath $nugetLog)) { throw 'Run Invoke-NuGetAudit.ps1 first (release-nuget-audit.log is missing).' }
Assert-FilesAbsent @($trivyJson, $sbomJson, $auditJson, $summaryJson, $scannersJson, $trivyDbFile, $trivyLog, $syftLog, $inspectFile)
Assert-LocalDockerAvailable
Assert-DockerNamesFree -Container @($names.Trivy, $names.Syft, $names.TrivyDb, $names.TrivyAge)
$metadata = Read-Json (Join-Path $ctx.Bundle 'build-metadata.json')
if ($metadata.version -ne $v -or $metadata.os -ne 'linux' -or $metadata.architecture -ne 'amd64' -or $metadata.archive -ne "premierecalendar-$v.tar") { throw 'build-metadata.json does not describe this release archive.' }
if ((Get-Item -LiteralPath $ctx.ImageArchive).Length -ne $metadata.bytes) { throw 'The image archive differs from the build metadata.' }

# Stage a native copy (reading through /mnt is slow). The copy that is scanned is hashed inside WSL against the build
# metadata, and audit_image_archive.py hashes the original bundle file again, so no extra host-side hash is needed here.
$archiveName = $metadata.archive
$stage = @"
set -eu
if [ -e $(ConvertTo-ShellQuoted $staging) ]; then echo 'Refusing to reuse an existing scan staging directory.' >&2; exit 2; fi
mkdir -p $(ConvertTo-ShellQuoted $staging)
cp $(ConvertTo-ShellQuoted (ConvertTo-WslPath $ctx.ImageArchive)) $(ConvertTo-ShellQuoted "$staging/$archiveName")
echo '$($metadata.archiveSha256)  $staging/$archiveName' | sha256sum -c -
"@
$null = Initialize-ReleaseCacheVolume 'trivy'
# The cache only saves the download: a copy that this cache downloaded more than 5 hours ago is discarded so Trivy
# fetches the current DB, as a fresh volume did. A failing find (permission or I/O error) counts as stale and discards the
# copy, never as fresh (summarize_scan.py also rejects a download older than 6 hours).
$null = Invoke-LocalDocker run --rm --name $names.TrivyAge -v "$($trivyCache.Name):$trivyCachePath" --entrypoint sh $cfg.trivyImage -c `
    "stale=`$(find $trivyCachePath -maxdepth 1 -name metadata.json -mmin +300) || stale=find-failed; if [ -n `"`$stale`" ]; then rm -f $trivyCachePath/trivy.db $trivyCachePath/metadata.json; fi"
$stageExit = Invoke-LocalShell $stage
if ($stageExit -ne 0) {
    # Exit 2 means the directory already existed and is not ours: never remove it.
    if ($stageExit -ne 2) { $null = Invoke-LocalShell "rm -rf $(ConvertTo-ShellQuoted $staging)" }
    throw 'Staging the archive for the scan failed.'
}
$scanScriptRan = $false
try {
    $trivyArguments = @('run', '--name', $names.Trivy, '--rm', '-v', "${staging}:/in:ro", '-v', "$($ctx.BundleWsl):/out", '-v', "$($trivyCache.Name):$trivyCachePath", $cfg.trivyImage,
        'image', '--input', "/in/$archiveName", '--scanners', 'vuln,misconfig,secret', '--format', 'json', '--output', "/out/release-$v-trivy.json", '--skip-version-check')
    if ($IgnoreUnfixed) { $trivyArguments += '--ignore-unfixed' }
    $syftArguments = @('run', '--name', $names.Syft, '--rm', '-v', "${staging}:/in:ro", '-v', "$($ctx.BundleWsl):/out", $cfg.syftImage,
        "docker-archive:/in/$archiveName", '-o', "cyclonedx-json=/out/release-$v-sbom.cdx.json")
    # The metadata of the DB the scan used is read from the same cache right after Trivy finished.
    $metadataArguments = @('run', '--rm', '--name', $names.TrivyDb, '-v', "$($trivyCache.Name):${trivyCachePath}:ro", '--entrypoint', 'cat', $cfg.trivyImage, $trivyDbMetadata)
    # Trivy and Syft are independent read-only consumers of the same archive, so they run side by side; the staged
    # copy is removed by the script itself, whatever happens.
    $scanScript = Get-ConcurrentScanScript -TrivyArguments $trivyArguments -SyftArguments $syftArguments -TrivyLog (ConvertTo-WslPath $trivyLog) -SyftLog (ConvertTo-WslPath $syftLog) `
        -MetadataArguments $metadataArguments -MetadataFile (ConvertTo-WslPath $trivyDbFile) -CleanupPath $staging
    $scanScriptRan = $true
    $scanExit = Invoke-LocalShell $scanScript
    if ($scanExit -in 11, 13) { Get-Content -LiteralPath $trivyLog -Tail 30 }
    if ($scanExit -in 12, 13) { Get-Content -LiteralPath $syftLog -Tail 30 }
    switch ($scanExit) {
        0 { }
        11 { throw 'The Trivy scan failed to run.' }
        12 { throw 'The Syft SBOM generation failed.' }
        13 { throw 'The Trivy scan and the Syft SBOM generation both failed.' }
        14 { throw 'The Trivy vulnerability DB metadata could not be read from the cache.' }
        default { throw "The scan step failed unexpectedly (exit $scanExit); resource cleanup is unconfirmed." }
    }
    # summarize_scan.py rejects a DB older than 48 hours; the raw metadata file is kept as evidence next to the scanners record.
    $dbText = (Get-Content -LiteralPath $trivyDbFile -Raw).Trim()
    $checkedAt = [DateTime]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
}
finally {
    # The scan script removes the staged copy itself; repeat it unconditionally because a guest killed at its
    # deadline may not have run its trap. rm -rf of an absent path is harmless.
    $null = Invoke-LocalShell "rm -rf $(ConvertTo-ShellQuoted $staging)"
}
Write-NewText -Path $inspectFile -Text ((@(Invoke-LocalDocker image inspect $cfg.imageTag) -join "`n") + "`n")
$scannerIds = @(Invoke-LocalDocker image inspect --format '{{.Id}}' $cfg.trivyImage $cfg.syftImage)
if ($scannerIds.Count -ne 2) { throw 'Could not read the scanner image identities.' }
Write-NewJson -Path $scannersJson -Object ([ordered]@{
        trivyCacheVolume = $trivyCache.Name
        trivyDbMetadataRaw = $dbText; trivyDbCheckedAtUtc = $checkedAt
        trivyImage = $cfg.trivyImage; trivyImageId = $scannerIds[0]
        syftImage = $cfg.syftImage; syftImageId = $scannerIds[1] })
Invoke-ReleasePython (Join-Path $PSScriptRoot 'audit_image_archive.py') --bundle $ctx.Bundle --version $v --image-tag $cfg.imageTag --image-inspect $inspectFile
$summaryArguments = @((Join-Path $PSScriptRoot 'summarize_scan.py'), '--bundle', $ctx.Bundle, '--version', $v)
if ($IgnoreUnfixed) { $summaryArguments += '--ignore-unfixed' }
Invoke-ReleasePython @summaryArguments
