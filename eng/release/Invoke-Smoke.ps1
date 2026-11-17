#Requires -Version 7.0
<#
.SYNOPSIS
Step 7a: hardened runtime smoke test of the exact exported image against an empty internal PostgreSQL.

.DESCRIPTION
Starts the image by its local image id (verified against build-metadata.json) with the production hardening
(read-only root, user 1654:999, all capabilities dropped, no-new-privileges, tmpfs scratch) on a new internal network
with an explicit /24 subnet, next to a tmpfs-only PostgreSQL from compose.yaml's pin. Verifies readiness, the
/health/version identity (version, source revision, schema, healthy database, zero dropped diagnostic events, writer
failures and logging failures), the root HTML and the Blazor boot asset, the Brussels time zone data (and a .NET
time-zone probe built from eng\release\timezone-probe in the pinned SDK image), the nightly planner log lines, actual
log-file writes under the read-only root, and the `database verify` and `database prepare-legacy-checkpoints`
commands. Evidence files are smoke-*.* in the bundle; existing ones are never overwritten. The smoke containers are
removed after a successful run (use -KeepResources to keep them), and left in place after a failure for inspection.

.PARAMETER Bundle
Full path of the release bundle.

.PARAMETER KeepResources
Do not remove the smoke containers and network after success.

.EXAMPLE
pwsh -File D:\Projects\PremiereCalendar\eng\release\Invoke-Smoke.ps1 -Bundle D:\Projects\PremiereCalendar\artifacts\release\1.2.20
#>
[CmdletBinding(SupportsShouldProcess)]
param([Parameter(Mandatory)][string] $Bundle, [switch] $KeepResources)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseCommon.ps1')
$ctx = Get-ReleaseContext -Bundle $Bundle
$cfg = $ctx.Config
$p = $ctx.Prefix
$network = "${p}_smoke_net"; $db = "${p}_smoke_db"; $app = "${p}_smoke_app"; $cli = "${p}_smoke_cli"; $probeBuild = "${p}_smoke_probe"
$subnet = Get-ReleaseSubnet $ctx 1
$label = "premierecalendar.validation=smoke-$($ctx.BundleName)"
$probeRoot = Join-Path $ctx.Bundle 'smoke-timezone-probe'
$outputs = @('smoke-health.json', 'smoke-assets.json', 'smoke-zoneinfo.log', 'smoke-timezone-probe.log', 'smoke-runtime.log', 'smoke-nightly-planner.log',
    'smoke-security.log', 'smoke-logfiles.log', 'smoke-downgrade-command.json', 'smoke-database-verify.json') | ForEach-Object { Join-Path $ctx.Bundle $_ }

$plan = @(
    "Refuse if smoke evidence, $probeRoot, containers $db/$app/$cli or network $network exist",
    "Verify the local image $($cfg.imageTag) against build-metadata.json (id, linux/amd64, version and revision labels)",
    "Build the time-zone probe (eng\release\timezone-probe) with $($cfg.sdkImage)",
    "docker network create --internal --subnet $subnet $network; start $($cfg.postgresImage) (tmpfs, no ports) and the image hardened like production",
    'Check /health/ready, /health/version, root HTML, the Blazor asset, zoneinfo and the time-zone probe, nightly planner logs, log-file writes, UID/GID, hardening flags',
    'Run database verify and database prepare-legacy-checkpoints with the image entrypoint; remove the smoke resources on success')
if (Show-ReleasePlan -Title 'Invoke-Smoke' -Step $plan) { return }

Assert-FilesAbsent ($outputs + $probeRoot)
Assert-LocalDockerAvailable
Assert-DockerNamesFree -Container @($db, $app, $cli, $probeBuild) -Network $network
$metadata = Read-Json (Join-Path $ctx.Bundle 'build-metadata.json')
$image = (Invoke-LocalDockerJson image inspect $cfg.imageTag)[0]
if ($image.Id -ne $metadata.localImageIdentity) { throw 'The local image id differs from build-metadata.json.' }
if ($image.Os -ne 'linux' -or $image.Architecture -ne 'amd64' -or $metadata.os -ne 'linux' -or $metadata.architecture -ne 'amd64') { throw 'Image platform mismatch.' }
if ($image.Config.Labels.'org.opencontainers.image.revision' -ne $metadata.sourceRevision -or $image.Config.Labels.'org.opencontainers.image.version' -ne $cfg.version) { throw 'Image labels differ from build-metadata.json.' }
$imageId = $image.Id

# Time-zone probe: compiled here with the pinned SDK so it always matches the runtime under test.
$null = New-Item -ItemType Directory -Path $probeRoot
$null = New-Item -ItemType Directory -Path (Join-Path $probeRoot 'src'), (Join-Path $probeRoot 'out')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'timezone-probe\Program.cs'), (Join-Path $PSScriptRoot 'timezone-probe\TimezoneProbe.csproj') -Destination (Join-Path $probeRoot 'src')
$null = Invoke-LocalDocker run --rm --name $probeBuild -v "$(ConvertTo-WslPath (Join-Path $probeRoot 'src')):/probe-src:ro" -v "$(ConvertTo-WslPath (Join-Path $probeRoot 'out')):/out" `
    --tmpfs '/tmp:rw,size=512m' -e DOTNET_CLI_HOME=/tmp/dotnet-home -e DOTNET_CLI_TELEMETRY_OPTOUT=1 --entrypoint sh $cfg.sdkImage `
    -ec 'cp -r /probe-src /tmp/probe && dotnet publish /tmp/probe -c Release -p:UseAppHost=false -o /out'
if (-not (Test-Path -LiteralPath (Join-Path $probeRoot 'out\TimezoneProbe.dll'))) { throw 'The time-zone probe was not built.' }

$connection = 'Host=smoke-db;Database=premierecalendar;Username=premierecalendar;GSS Encryption Mode=Disable'
$null = Invoke-LocalDocker network create --internal --subnet $subnet --label $label $network
$null = Invoke-LocalDocker run -d --name $db --label $label --network $network --network-alias smoke-db --read-only `
    --tmpfs '/var/lib/postgresql:rw,size=512m' --tmpfs '/tmp:rw,size=64m' --tmpfs '/run/postgresql:rw,size=16m' `
    -e PGDATA=/var/lib/postgresql/data/pgdata -e POSTGRES_USER=premierecalendar -e POSTGRES_DB=premierecalendar -e POSTGRES_HOST_AUTH_METHOD=trust $cfg.postgresImage
Wait-LocalCondition -Attempts 30 -Message 'The smoke PostgreSQL did not become ready.' -Condition { Test-LocalDocker exec $db pg_isready -U premierecalendar -d premierecalendar }
$hardening = @('--read-only', '--user', '1654:999', '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges:true', '--tmpfs', '/tmp:rw,noexec,nosuid,size=64m', '--tmpfs', '/app/App_Data:rw,uid=1654,gid=999,mode=0770,size=128m')
$null = Invoke-LocalDocker run -d --name $app --label $label --network $network @hardening `
    -v "$(ConvertTo-WslPath (Join-Path $probeRoot 'out')):/probe:ro" `
    -e TZ=Europe/Brussels -e AppDatabase__Provider=PostgreSql -e "AppDatabase__ConnectionString=$connection" `
    -e ProviderScheduler__Enabled=false -e ProviderDeltaSync__Enabled=false `
    -e CalendarWarmup__Enabled=true -e CalendarWarmup__RunOnStartup=false -e CalendarWarmup__NightlyEnabled=true `
    -e CalendarWarmup__NightlyTimeZone=Europe/Brussels -e CalendarWarmup__NightlyLocalTime=02:00:00 `
    -e CalendarCache__Enabled=false -e CalendarCache__AdjacentWeekPrefetchEnabled=false -e ItemEnrichmentCache__Enabled=false -e ImdbDataset__Enabled=false `
    $imageId
Wait-LocalCondition -Attempts 90 -Message 'The candidate app did not become ready.' -Condition { Test-LocalDocker exec $app wget -qO- http://127.0.0.1:8080/health/ready }

$healthText = (@(Invoke-LocalDocker exec $app wget -qO- http://127.0.0.1:8080/health/version) -join "`n")
$health = $healthText | ConvertFrom-Json
if ($health.version -ne $cfg.version -or $health.sourceRevision -ne $metadata.sourceRevision) { throw 'The running identity differs from the build metadata.' }
if ($health.databaseSchemaVersion -ne $cfg.databaseSchemaVersion -or -not $health.database.healthy) { throw 'Schema version or database health is wrong.' }
if (-not $health.performanceDiagnostics.healthy -or $health.performanceDiagnostics.droppedEvents -ne 0 -or $health.performanceDiagnostics.writerFailures -ne 0 -or $health.operationalLogging.failureCount -ne 0) { throw 'Diagnostics or operational logging are unhealthy.' }
Write-NewText -Path (Join-Path $ctx.Bundle 'smoke-health.json') -Text ($healthText + "`n")

$root = (@(Invoke-LocalDocker exec $app wget -qO- http://127.0.0.1:8080/) -join "`n")
if ($root -notmatch '_framework/blazor\.web\.js') { throw 'The root HTML does not reference the Blazor boot asset.' }
$assetInfo = (@(Invoke-LocalDocker exec $app sh -ec 'wget -qO- http://127.0.0.1:8080/_framework/blazor.web.js | tee /tmp/blazor.js | sha256sum | cut -c1-64; wc -c < /tmp/blazor.js') | ForEach-Object { "$_".Trim() })
if ($assetInfo.Count -lt 2 -or [long]$assetInfo[1] -le 0) { throw 'The Blazor boot asset is empty.' }
Write-NewJson -Path (Join-Path $ctx.Bundle 'smoke-assets.json') -Object ([ordered]@{ rootHtmlBytes = $root.Length; blazorWebJsSha256 = $assetInfo[0]; blazorWebJsBytes = [long]$assetInfo[1] })

Write-NewText -Path (Join-Path $ctx.Bundle 'smoke-zoneinfo.log') -Text ((@(Invoke-LocalDocker exec $app sh -ec 'test -f /usr/share/zoneinfo/Europe/Brussels && stat -c %n:%s /usr/share/zoneinfo/Europe/Brussels') -join "`n") + "`n")
# .NET memory-maps its runtime files; stage the read-only host bind on container-local scratch.
$probeLog = (@(Invoke-LocalDocker exec $app sh -ec 'mkdir /tmp/timezone-probe && cp /probe/TimezoneProbe.dll /probe/TimezoneProbe.deps.json /probe/TimezoneProbe.runtimeconfig.json /probe/TimezoneProbe.pdb /tmp/timezone-probe/ && dotnet /tmp/timezone-probe/TimezoneProbe.dll') -join "`n")
Write-NewText -Path (Join-Path $ctx.Bundle 'smoke-timezone-probe.log') -Text ($probeLog + "`n")
if ($probeLog -notmatch 'RESULT=PASS') { throw 'The time-zone probe did not pass.' }

Start-Sleep -Seconds 2
$runtimeLog = Join-Path $ctx.Bundle 'smoke-runtime.log'
$null = Invoke-LocalDockerToFile $runtimeLog logs $app
$runtimeText = Get-Content -LiteralPath $runtimeLog -Raw
if ($runtimeText -notmatch 'Nightly calendar plan .* started' -or $runtimeText -notmatch 'Nightly calendar coverage') { throw 'The nightly planner did not log its plan and coverage.' }
if ($runtimeText -match 'Nightly calendar plan scheduling failed' -or $runtimeText -match 'Nightly calendar schedule is invalid') { throw 'The nightly planner reported a scheduling failure.' }
Write-NewText -Path (Join-Path $ctx.Bundle 'smoke-nightly-planner.log') -Text ((@(($runtimeText -split "`r?`n") | Where-Object { $_ -match 'Nightly calendar plan .* started|Nightly calendar coverage' }) -join "`n") + "`n")

$logFiles = (@(Invoke-LocalDocker exec $app sh -ec 'test $(id -u) = 1654 && test $(id -g) = 999 && find /app/App_Data/logs -type f -name \*.ndjson -exec wc -c {} +') -join "`n")
if ($logFiles -notmatch '\d') { throw 'No log file was written under the read-only runtime.' }
Write-NewText -Path (Join-Path $ctx.Bundle 'smoke-logfiles.log') -Text ($logFiles + "`n")
$security = (@(Invoke-LocalDocker inspect $app --format '{{.Image}} {{.Config.User}} {{.HostConfig.ReadonlyRootfs}} {{.HostConfig.SecurityOpt}} {{.HostConfig.CapDrop}}') -join "`n")
$labels = (@(Invoke-LocalDocker inspect $app --format '{{json .Config.Labels}}') -join "`n")
if ($security -notmatch '1654:999 true .*no-new-privileges:true.*ALL') { throw 'The hardened runtime flags are not as expected.' }
Write-NewText -Path (Join-Path $ctx.Bundle 'smoke-security.log') -Text ($security + "`n" + $labels + "`n")

# Database commands run through the image entrypoint, as an operator would run them.
$cliArguments = @('run', '--rm', '--name', $cli, '--network', $network) + $hardening + @('-e', 'AppDatabase__Provider=PostgreSql', '-e', "AppDatabase__ConnectionString=$connection", '--entrypoint', 'dotnet', $imageId, 'PremiereCalendar.dll', 'database')
$downgradeArguments = $cliArguments + 'prepare-legacy-checkpoints'
$downgrade = (@(Invoke-LocalDocker @downgradeArguments) -join "`n")
Write-NewText -Path (Join-Path $ctx.Bundle 'smoke-downgrade-command.json') -Text ($downgrade + "`n")
if ($downgrade -notmatch '"Success"\s*:\s*true') { throw 'database prepare-legacy-checkpoints did not report success.' }
$verifyArguments = $cliArguments + 'verify'
$verify = (@(Invoke-LocalDocker @verifyArguments) -join "`n")
Write-NewText -Path (Join-Path $ctx.Bundle 'smoke-database-verify.json') -Text ($verify + "`n")

if (-not $KeepResources) {
    foreach ($name in $app, $db) { $null = Test-LocalDocker rm -f $name }
    $null = Test-LocalDocker network rm $network
}
Write-Host "Smoke test passed for $($cfg.imageTag) ($imageId)."
