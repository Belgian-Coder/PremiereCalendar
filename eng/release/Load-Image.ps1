#Requires -Version 7.0
<#
.SYNOPSIS
Step 8: loads the verified image archive into the NAS Docker engine through Portainer (no pull, no registry).

.DESCRIPTION
Re-validates every gate first (source-bound tests and review, exact-image identity, scans, SBOM, NuGet audit, smoke,
restored-backup compatibility, fresh backup binding) and the archive SHA-256, refuses to load when the tag already
exists on the NAS engine, uploads the docker-save archive (POST /images/load) and checks that the loaded image's
configuration digest and labels equal the audited archive. Evidence: installed\image-load.json and
installed\image-load-response.ndjson. Needs -AllowMutation (Portainer POST). Nothing is activated here.

.PARAMETER Bundle
Full path of the release bundle.

.PARAMETER AllowMutation
Permits the Portainer image-load POST.

.EXAMPLE
pwsh -File D:\Projects\PremiereCalendar\eng\release\Load-Image.ps1 -Bundle D:\Projects\PremiereCalendar\artifacts\release\1.2.20 -AllowMutation
#>
[CmdletBinding(SupportsShouldProcess)]
param([Parameter(Mandatory)][string] $Bundle, [switch] $AllowMutation)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
. (Join-Path $PSScriptRoot 'ReleaseCommon.ps1')
. (Join-Path $PSScriptRoot 'ReleaseGates.ps1')
$ctx = Get-ReleaseContext -Bundle $Bundle
$cfg = $ctx.Config
Start-ReleaseSession -Context $ctx -AllowMutation:$AllowMutation
$loadJson = Join-Path $ctx.Installed 'image-load.json'
$loadResponse = Join-Path $ctx.Installed 'image-load-response.ndjson'

$plan = @(
    'Re-validate all release gates (source-bound tests and review, image identity, scans, SBOM, NuGet audit, smoke, backup compatibility)',
    "Refuse if $loadJson or $loadResponse exist, or if $($cfg.imageTag) already exists on the NAS engine",
    "Read-only Portainer check: endpoint $($cfg.endpointId) is the NAS",
    "POST the archive $($ctx.ImageArchive) to /api/endpoints/$($cfg.endpointId)/docker/images/load (needs -AllowMutation)",
    "Verify the loaded $($cfg.imageTag): image configuration digest and revision/version labels equal the audited archive",
    "Write $loadJson")
if (Show-ReleasePlan -Title 'Load-Image' -Step $plan) { return }
if (-not $AllowMutation) { throw 'Loading an image changes the NAS Docker engine; run this step with -AllowMutation.' }

$build = Read-Json (Join-Path $ctx.Bundle 'build-metadata.json')
Assert-ReleaseSourceEvidence -Context $ctx -ExpectedRevision $build.sourceRevision
$gates = Assert-ReleaseImageGates -Context $ctx
$null = Assert-RestoreEvidenceBinding -Context $ctx -Compat $gates.Compat
Assert-FilesAbsent @($loadJson, $loadResponse)

$endpoint = "/api/endpoints/$($cfg.endpointId)/docker"
$target = Invoke-ReleaseApi -Path "$endpoint/info"
if ($target.Name -ne 'NAS') { throw 'The load target is not the NAS engine.' }
$existing = Invoke-ReleaseApi -Path "$endpoint/images/json"
if (@($existing | Where-Object { $_.RepoTags -contains $cfg.imageTag }).Count -gt 0) { throw "The tag already exists on the NAS engine; refusing to replace it: $($cfg.imageTag)" }
$watch = [Diagnostics.Stopwatch]::StartNew()
$null = Invoke-ReleaseApi -Path "$endpoint/images/load?quiet=0" -Method POST -BodyFile $ctx.ImageArchive -ContentType 'application/x-tar' -TimeoutSec 600 -OutFile $loadResponse
$loaded = Invoke-ReleaseApi -Path "$endpoint/images/$($cfg.imageTag)/json"
if ($loaded.Id -ne $gates.ConfigDigest -or $loaded.Config.Labels.'org.opencontainers.image.revision' -ne $build.sourceRevision -or $loaded.Config.Labels.'org.opencontainers.image.version' -ne $cfg.version) { throw 'The loaded NAS image identity differs from the audited archive.' }
Write-NewJson -Path $loadJson -Object ([ordered]@{
        Version = $cfg.version; SourceRevision = $build.sourceRevision; ImageConfigurationDigest = $loaded.Id; ArchiveSha256 = $build.archiveSha256
        DurationMilliseconds = $watch.Elapsed.TotalMilliseconds; Target = $target.Name })
Get-Content -LiteralPath $loadJson
