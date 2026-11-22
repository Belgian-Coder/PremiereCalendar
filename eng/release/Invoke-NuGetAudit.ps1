#Requires -Version 7.0
<#
.SYNOPSIS
Step 4: audits the NuGet dependencies of the shipped project for known vulnerabilities.

.DESCRIPTION
Extracts the verified source archive (not the working folder) into a new directory inside the WSL distribution and
runs `dotnet restore` plus `dotnet list package --vulnerable --include-transitive` for PremiereCalendar.csproj in
the Dockerfile's digest-pinned SDK image (read-only root, tmpfs scratch, network only for the restore). Downloaded
packages are kept in the persistent release volume premierecalendar-release-nuget-packages (NuGet verifies their
content hashes); the NuGet HTTP cache is tmpfs so every run fetches fresh vulnerability metadata. The full
output is kept as release-nuget-audit.log; later gates require the sentence "has no vulnerable packages". The
temporary source copy and the container are removed after a successful run.

.PARAMETER Bundle
Full path of the release bundle.

.EXAMPLE
pwsh -File D:\Projects\PremiereCalendar\eng\release\Invoke-NuGetAudit.ps1 -Bundle D:\Projects\PremiereCalendar\artifacts\release\1.2.20
#>
[CmdletBinding(SupportsShouldProcess)]
param([Parameter(Mandatory)][string] $Bundle)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseCommon.ps1')
$ctx = Get-ReleaseContext -Bundle $Bundle
$cfg = $ctx.Config
$log = Join-Path $ctx.Bundle 'release-nuget-audit.log'
$container = "$($ctx.Prefix)_nuget_audit"
$staging = "/tmp/$($ctx.Prefix)-nuget-src"
$packageCache = Get-ReleaseCacheVolume 'nuget'

$plan = @(
    "Refuse if $log, container $container or the WSL directory $staging already exist",
    "Verify $($ctx.SourceArchive) against build-metadata.json (SHA-256) and extract it to $staging inside Ubuntu-24.04",
    "Create the persistent release volume $($packageCache.Name) ($($packageCache.Label)) if missing; it holds only public NuGet packages and is never deleted",
    "docker run $($cfg.sdkImage) (NuGet HTTP cache on tmpfs, so vulnerability data is always fetched fresh): dotnet restore + dotnet list package --vulnerable --include-transitive for PremiereCalendar/PremiereCalendar.csproj",
    "Save the output as $log; fail unless it reports no vulnerable packages; remove the temporary copy and container")
if (Show-ReleasePlan -Title 'Invoke-NuGetAudit' -Step $plan) { return }

Assert-FilesAbsent $log
Assert-LocalDockerAvailable
Assert-DockerNamesFree -Container $container
$null = Initialize-ReleaseCacheVolume 'nuget'
$metadata = Read-Json (Join-Path $ctx.Bundle 'build-metadata.json')
if ($metadata.version -ne $cfg.version -or $metadata.sourceRevision -notmatch '^[0-9a-f]{40}$') { throw 'build-metadata.json does not describe this release.' }
if ((Get-FileSha256 $ctx.SourceArchive) -ne $metadata.sourceArchiveSha256) { throw 'The source archive differs from the build metadata.' }
$wslArchive = ConvertTo-WslPath $ctx.SourceArchive
$wslLog = ConvertTo-WslPath $log
$script = @"
set -eu
docker() { command docker --host unix:///var/run/docker.sock "`$@"; }
if [ -e $(ConvertTo-ShellQuoted $staging) ]; then echo 'Refusing to reuse an existing staging directory.' >&2; exit 2; fi
mkdir -p $(ConvertTo-ShellQuoted $staging)
trap 'rm -rf $(ConvertTo-ShellQuoted $staging)' EXIT
tar -xf $(ConvertTo-ShellQuoted $wslArchive) -C $(ConvertTo-ShellQuoted $staging)
docker run --rm --name $container --read-only --tmpfs /tmp:rw,size=1536m -v $(ConvertTo-ShellQuoted $staging):/src:rw -v $(ConvertTo-ShellQuoted $packageCache.Name):/nuget-packages:rw \
  -e DOTNET_CLI_HOME=/tmp/dotnet-home -e NUGET_PACKAGES=/nuget-packages -e NUGET_HTTP_CACHE_PATH=/tmp/nuget-http-cache -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e ArtifactsPath=/tmp/PremiereCalendar.Build \
  --entrypoint sh $($cfg.sdkImage) -ec 'dotnet restore /src/PremiereCalendar/PremiereCalendar.csproj && dotnet list /src/PremiereCalendar/PremiereCalendar.csproj package --vulnerable --include-transitive --no-restore' > $(ConvertTo-ShellQuoted $wslLog) 2>&1
"@
$exit = Invoke-LocalShell $script
if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Tail 40 }
if ($exit -ne 0) { throw 'The NuGet restore/audit command failed.' }
if (-not (Select-String -LiteralPath $log -Pattern 'has no vulnerable packages' -Quiet)) { throw 'The NuGet audit did not report "has no vulnerable packages"; see the log.' }
Write-Host 'NuGet audit passed: no vulnerable packages.'
