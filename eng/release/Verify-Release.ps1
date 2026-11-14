#Requires -Version 7.0
<#
.SYNOPSIS
Step 10: proves the activated release on the NAS and on both public addresses.

.DESCRIPTION
Waits for the app container to be healthy on the audited image digest, proves the database and backup containers
were never restarted (ids and start times equal to installed\containers-before.json), checks the production
hardening of the running container, and requests /health/version, /health/ready, / and /_framework/blazor.web.js
on both the direct URL (http://192.168.68.20:18084) and the private HTTPS URL (https://premiere.home.belgiancoder.be) from the
release configuration: version, source revision, schema, healthy database, zero dropped diagnostic events, writer
failures and logging failures. It also checks that the persisted stack revision is one higher than before, pins the new
digest and that the Compose file on the NAS disk pins it too. Each request is retried (the first request right after an
activation can time out); evidence is written only when every check passed, so a failed pass can simply be repeated.
With -AllowMutation the Brussels time-zone file is also checked inside the container (a read-only Portainer exec POST).

.PARAMETER Bundle
Full path of the release bundle.

.PARAMETER AllowMutation
Optional: permits the read-only exec POST for the time-zone file check.

.EXAMPLE
pwsh -File D:\Projects\PremiereCalendar\eng\release\Verify-Release.ps1 -Bundle D:\Projects\PremiereCalendar\artifacts\release\1.2.20 -AllowMutation
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string] $Bundle,
    [switch] $AllowMutation,
    [ValidateRange(1, 30)][int] $HealthyWithinMinutes = 3,
    [ValidateRange(5, 120)][int] $RequestTimeoutSeconds = 30,
    [ValidateRange(1, 10)][int] $RequestAttempts = 3
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
. (Join-Path $PSScriptRoot 'ReleaseCommon.ps1')
. (Join-Path $PSScriptRoot 'ReleaseGates.ps1')
$ctx = Get-ReleaseContext -Bundle $Bundle
$cfg = $ctx.Config
Start-ReleaseSession -Context $ctx -AllowMutation:$AllowMutation
$names = 'containers-after.json', 'health-direct.json', 'health-private.json', 'http-checks.json', 'verification.json'
$outputs = @($names | ForEach-Object { Join-Path $ctx.Installed $_ })
$expectedRevisionAfter = [int]$cfg.expectedStackRevision + 1

$plan = @(
    "Refuse if the installed evidence ($($names -join ', ')) exists",
    "Read-only Portainer checks: $($cfg.appContainer) healthy within $HealthyWithinMinutes min on the audited digest with production hardening; database and backup containers unchanged",
    "GET /health/version, /health/ready, / and /_framework/blazor.web.js on $($cfg.directUrl) and $($cfg.privateUrl) ($RequestAttempts attempts, $RequestTimeoutSeconds s each)",
    "version $($cfg.version), schema $($cfg.databaseSchemaVersion), healthy database, zero dropped events / writer failures / logging failures",
    "Stack $($cfg.stackId) at revision $expectedRevisionAfter pins the new digest; the compose file under $($cfg.composeRoot) pins it too",
    $(if ($AllowMutation) { 'Time-zone file check through a read-only exec POST' } else { 'Time-zone file check skipped (needs -AllowMutation)' }),
    'Write the evidence only when everything passed')
if (Show-ReleasePlan -Title "Verify-Release $($ctx.BundleName)" -Step $plan) { return }

Assert-FilesAbsent $outputs
$build = Read-Json (Join-Path $ctx.Bundle 'build-metadata.json')
$audit = Read-Json (Join-Path $ctx.Bundle "release-$($cfg.version)-audit.json")
$expectedDigest = Get-ArchiveConfigurationDigest $audit
$activation = Read-Json (Join-Path $ctx.Installed 'activation.json')
if ($activation.ImageConfigurationDigest -ne $expectedDigest -or $activation.SourceRevision -ne $build.sourceRevision) { throw 'The activation receipt does not describe the audited image.' }
$endpoint = "/api/endpoints/$($cfg.endpointId)/docker"

$deadline = [DateTimeOffset]::UtcNow.AddMinutes($HealthyWithinMinutes)
do {
    $appContainer = Invoke-ReleaseApi -Path "$endpoint/containers/$($cfg.appContainer)/json"
    if ($appContainer.Image -ne $expectedDigest) { throw 'The activated app image differs from the audited candidate.' }
    if ($appContainer.State.Status -ne 'running') { throw 'The activated app is not running.' }
    if ($appContainer.State.Health.Status -eq 'healthy') { break }
    if ([DateTimeOffset]::UtcNow -ge $deadline) { throw "The activated app did not become healthy within $HealthyWithinMinutes minutes." }
    Start-Sleep -Seconds 2
} while ($true)

$before = @(Read-Json (Join-Path $ctx.Installed 'containers-before.json'))
$after = foreach ($prior in $before) {
    $c = Invoke-ReleaseApi -Path "$endpoint/containers/$($prior.Name.TrimStart('/'))/json"
    if ($prior.Name -ne "/$($cfg.appContainer)" -and ($c.Id -ne $prior.Id -or $c.State.StartedAt -ne $prior.StartedAt)) { throw "The database/backup container changed: $($prior.Name)" }
    if ($prior.Name -eq "/$($cfg.appContainer)") {
        if ($c.Config.User -ne '1654:999' -or -not $c.HostConfig.ReadonlyRootfs -or $c.HostConfig.CapDrop -notcontains 'ALL' -or $c.HostConfig.SecurityOpt -notcontains 'no-new-privileges:true') { throw 'The running container does not have the expected hardening.' }
        if (@($c.Config.Env | Where-Object { $_ -match '^(CalendarCache__WeekCacheHours|CalendarWarmup__Nightly[^=]*|ItemEnrichmentCache__[^=]*)=' }).Count -gt 0) { throw 'An unexpected nightly/cache environment override requires inspection.' }
    }
    [pscustomobject]@{ Name = $c.Name; Id = $c.Id; Image = $c.Image; StartedAt = $c.State.StartedAt; Health = $c.State.Health.Status }
}

function Invoke-VerifiedRequest {
    param([string] $Uri)
    for ($attempt = 1; $attempt -le $RequestAttempts; $attempt++) {
        try { return Invoke-WebRequest $Uri -TimeoutSec $RequestTimeoutSeconds }
        catch { if ($attempt -eq $RequestAttempts) { throw "Request failed after $RequestAttempts attempts: $Uri ($($_.Exception.Message))" }; Start-Sleep -Seconds 10 }
    }
}
$healthDocuments = @{}
$httpChecks = @()
foreach ($target in @(@{ Name = 'direct'; Base = $cfg.directUrl }, @{ Name = 'private'; Base = $cfg.privateUrl })) {
    $versionResponse = Invoke-VerifiedRequest "$($target.Base)/health/version"
    $health = $versionResponse.Content | ConvertFrom-Json
    if ($health.version -ne $cfg.version -or $health.sourceRevision -ne $build.sourceRevision -or -not $health.database.healthy -or $health.databaseSchemaVersion -ne $cfg.databaseSchemaVersion) { throw "The installed identity/schema/database gate failed on $($target.Name)." }
    if (-not $health.performanceDiagnostics.healthy -or $health.performanceDiagnostics.droppedEvents -ne 0 -or $health.performanceDiagnostics.writerFailures -ne 0 -or $health.operationalLogging.failureCount -ne 0) { throw "The installed diagnostics/logging gate failed on $($target.Name)." }
    $healthDocuments[$target.Name] = $health
    $httpChecks += [pscustomobject]@{ Base = $target.Base; Path = '/health/version'; Status = [int]$versionResponse.StatusCode }
    foreach ($path in '/health/ready', '/', '/_framework/blazor.web.js') {
        $response = Invoke-VerifiedRequest "$($target.Base)$path"
        if ($response.StatusCode -ne 200) { throw "The installed HTTP gate failed: $($target.Base)$path" }
        $httpChecks += [pscustomobject]@{ Base = $target.Base; Path = $path; Status = [int]$response.StatusCode }
    }
}

$stack = Invoke-ReleaseApi -Path "/api/stacks/$($cfg.stackId)"
$source = Invoke-ReleaseApi -Path "/api/stacks/$($cfg.stackId)/file"
if ($stack.StackFileVersion -ne $expectedRevisionAfter -or -not $source.StackFileContent.Contains($expectedDigest)) { throw 'The persisted stack revision or pin differs from the activation.' }
$composePath = Join-Path $cfg.composeRoot "$($cfg.stackId)\v$expectedRevisionAfter\docker-compose.yml"
if (-not (Get-Content -LiteralPath $composePath -Raw).Contains($expectedDigest)) { throw 'The persisted Compose file on the NAS disk does not pin the new digest.' }
$zoneChecked = $false
if ($AllowMutation) {
    $null = Invoke-ReleaseExec -Container $cfg.appContainer -Directory $ctx.Private -Tag "$($ctx.Prefix)-verify-zone-present" -Command @('test', '-f', '/usr/share/zoneinfo/Europe/Brussels')
    $zoneChecked = $true
}

Write-NewJson -Path (Join-Path $ctx.Installed 'containers-after.json') -Object @($after) -Depth 8
Write-NewJson -Path (Join-Path $ctx.Installed 'health-direct.json') -Object $healthDocuments['direct'] -Depth 8
Write-NewJson -Path (Join-Path $ctx.Installed 'health-private.json') -Object $healthDocuments['private'] -Depth 8
Write-NewJson -Path (Join-Path $ctx.Installed 'http-checks.json') -Object $httpChecks
$summary = [ordered]@{
    Version = $cfg.version; Schema = $cfg.databaseSchemaVersion; StackRevision = $expectedRevisionAfter; HttpChecks = $httpChecks.Count
    DatabaseAndBackupUnchanged = $true; RuntimeHardeningPassed = $true; BrusselsZoneChecked = $zoneChecked; VerifiedUtc = [DateTimeOffset]::UtcNow.ToString('O') }
Write-NewJson -Path (Join-Path $ctx.Installed 'verification.json') -Object $summary
$summary | ConvertTo-Json
