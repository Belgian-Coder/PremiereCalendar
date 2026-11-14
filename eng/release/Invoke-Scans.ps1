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
$syftLog = Join-Path $ctx.Bundle 'release-syft.log'
$inspectFile = Join-Path $ctx.Private 'image-inspect.json'
$names = @{ Trivy = "$($ctx.Prefix)_trivy"; Syft = "$($ctx.Prefix)_syft"; Cache = "$($ctx.Prefix)_trivy_cache" }
$staging = "/tmp/$($ctx.Prefix)-scan"

$plan = @(
    "Refuse if any scan evidence exists ($trivyJson, $sbomJson, $auditJson, $summaryJson) or the resources $($names.Trivy), $($names.Syft), $($names.Cache), $staging exist",
    "Check $(Join-Path $ctx.Bundle 'release-nuget-audit.log') exists (step 4) and copy the image archive to $staging with a SHA-256 check against build-metadata.json",
    "Trivy $($cfg.trivyImage): --scanners vuln,misconfig,secret on the archive$(if ($IgnoreUnfixed) { ' (--ignore-unfixed)' })",
    "Syft $($cfg.syftImage): CycloneDX JSON SBOM of the archive",
    'audit_image_archive.py (archive/labels/platform/source proof) and summarize_scan.py; fail on any finding',
    'Remove the temporary copy, the scan cache volume and the finished containers')
if (Show-ReleasePlan -Title 'Invoke-Scans' -Step $plan) { return }

$nugetLog = Join-Path $ctx.Bundle 'release-nuget-audit.log'
if (-not (Test-Path -LiteralPath $nugetLog)) { throw 'Run Invoke-NuGetAudit.ps1 first (release-nuget-audit.log is missing).' }
Assert-FilesAbsent @($trivyJson, $sbomJson, $auditJson, $summaryJson, $scannersJson, $trivyLog, $syftLog, $inspectFile)
Assert-LocalDockerAvailable
Assert-DockerNamesFree -Container @($names.Trivy, $names.Syft) -Volume $names.Cache
$metadata = Read-Json (Join-Path $ctx.Bundle 'build-metadata.json')
if ($metadata.version -ne $v -or $metadata.os -ne 'linux' -or $metadata.architecture -ne 'amd64' -or $metadata.archive -ne "premierecalendar-$v.tar") { throw 'build-metadata.json does not describe this release archive.' }
if ((Get-Item -LiteralPath $ctx.ImageArchive).Length -ne $metadata.bytes -or (Get-FileSha256 $ctx.ImageArchive) -ne $metadata.archiveSha256) { throw 'The image archive differs from the build metadata.' }

# Stage a native copy (reading through /mnt is slow) and verify it again inside WSL.
$archiveName = $metadata.archive
$stage = @"
set -eu
if [ -e $(ConvertTo-ShellQuoted $staging) ]; then echo 'Refusing to reuse an existing scan staging directory.' >&2; exit 2; fi
mkdir -p $(ConvertTo-ShellQuoted $staging)
cp $(ConvertTo-ShellQuoted (ConvertTo-WslPath $ctx.ImageArchive)) $(ConvertTo-ShellQuoted "$staging/$archiveName")
echo '$($metadata.archiveSha256)  $staging/$archiveName' | sha256sum -c -
"@
if ((Invoke-LocalShell $stage) -ne 0) { throw 'Staging the archive for the scan failed.' }
$created = $false
try {
    $null = Invoke-LocalDocker volume create --label "premierecalendar.validation=scan-$($ctx.BundleName)" $names.Cache
    $created = $true
    $trivyArguments = @('run', '--name', $names.Trivy, '--rm', '-v', "${staging}:/in:ro", '-v', "$($ctx.BundleWsl):/out", '-v', "$($names.Cache):/root/.cache", $cfg.trivyImage,
        'image', '--input', "/in/$archiveName", '--scanners', 'vuln,misconfig,secret', '--format', 'json', '--output', "/out/release-$v-trivy.json", '--skip-version-check')
    if ($IgnoreUnfixed) { $trivyArguments += '--ignore-unfixed' }
    if ((Invoke-LocalDockerToFile $trivyLog @trivyArguments) -ne 0) { Get-Content -LiteralPath $trivyLog -Tail 30; throw 'The Trivy scan failed to run.' }
    $syftArguments = @('run', '--name', $names.Syft, '--rm', '-v', "${staging}:/in:ro", '-v', "$($ctx.BundleWsl):/out", $cfg.syftImage,
        "docker-archive:/in/$archiveName", '-o', "cyclonedx-json=/out/release-$v-sbom.cdx.json")
    if ((Invoke-LocalDockerToFile $syftLog @syftArguments) -ne 0) { Get-Content -LiteralPath $syftLog -Tail 30; throw 'The Syft SBOM generation failed.' }
}
finally {
    $null = Invoke-LocalShell "rm -rf $(ConvertTo-ShellQuoted $staging)"
    if ($created) { $null = Test-LocalDocker volume rm $names.Cache }
}
Write-NewText -Path $inspectFile -Text ((@(Invoke-LocalDocker image inspect $cfg.imageTag) -join "`n") + "`n")
Write-NewJson -Path $scannersJson -Object ([ordered]@{
        trivyImage = $cfg.trivyImage; trivyImageId = (Invoke-LocalDocker image inspect --format '{{.Id}}' $cfg.trivyImage | Select-Object -First 1)
        syftImage = $cfg.syftImage; syftImageId = (Invoke-LocalDocker image inspect --format '{{.Id}}' $cfg.syftImage | Select-Object -First 1) })
Invoke-ReleasePython (Join-Path $PSScriptRoot 'audit_image_archive.py') --bundle $ctx.Bundle --version $v --image-tag $cfg.imageTag --image-inspect $inspectFile
$summaryArguments = @((Join-Path $PSScriptRoot 'summarize_scan.py'), '--bundle', $ctx.Bundle, '--version', $v)
if ($IgnoreUnfixed) { $summaryArguments += '--ignore-unfixed' }
Invoke-ReleasePython @summaryArguments
