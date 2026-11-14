#Requires -Version 7.0
<#
.SYNOPSIS
Step 8b (optional gate): read-only proof that a persisted application setting already has the required value.

.DESCRIPTION
Only meaningful when Initialize-Release.ps1 was given -RequiredSettingKey/-RequiredSettingValue (for example
Sources.Trakt.Enabled = false for a release that must not change that setting). Runs one READ ONLY transaction
(`SELECT Value FROM AppParameters WHERE Key = ...`) in the live PostgreSQL container through Portainer exec, checks
that the app container was not restarted meanwhile and records installed\setting-verified.json, which Deploy-Release.ps1
requires. Nothing is changed. The exec call is a Portainer POST, hence -AllowMutation (the command itself is read-only).

.PARAMETER Bundle
Full path of the release bundle.

.PARAMETER AllowMutation
Permits the Portainer exec POSTs (the executed SQL is read-only).

.EXAMPLE
pwsh -File D:\Projects\PremiereCalendar\eng\release\Test-PersistedSetting.ps1 -Bundle D:\Projects\PremiereCalendar\artifacts\release\1.2.20 -AllowMutation
#>
[CmdletBinding(SupportsShouldProcess)]
param([Parameter(Mandatory)][string] $Bundle, [switch] $AllowMutation)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseCommon.ps1')
$ctx = Get-ReleaseContext -Bundle $Bundle
$cfg = $ctx.Config
Start-ReleaseSession -Context $ctx -AllowMutation:$AllowMutation
$receipt = Join-Path $ctx.Installed 'setting-verified.json'
$tag = "$($ctx.Prefix)-setting-readonly"

$plan = @(
    $(if ($cfg.requiredSettingKey) { "Require the persisted setting '$($cfg.requiredSettingKey)' to equal '$($cfg.requiredSettingValue)'" } else { 'No required setting is configured: nothing to verify (the script stops)' }),
    "Refuse if $receipt or the exec evidence $tag* exist",
    "Compare the live app container with installed\containers-before.json (id and start time)",
    "Exec in $($cfg.databaseContainer): psql -At in BEGIN READ ONLY; SELECT Value FROM AppParameters WHERE Key=...; COMMIT (Portainer POST)",
    "Write $receipt")
if (Show-ReleasePlan -Title 'Test-PersistedSetting' -Step $plan) { return }
if (-not $cfg.requiredSettingKey) { throw 'No required setting is configured for this release (Initialize-Release.ps1 -RequiredSettingKey/-RequiredSettingValue).' }
if (-not $AllowMutation) { throw 'The read-only SQL runs through a Portainer exec POST; run this step with -AllowMutation.' }
Assert-FilesAbsent $receipt
Assert-ReleaseExecEvidenceAvailable -Directory $ctx.Private -Tag $tag

$build = Read-Json (Join-Path $ctx.Bundle 'build-metadata.json')
$before = @(Read-Json (Join-Path $ctx.Installed 'containers-before.json'))
$beforeApp = $before | Where-Object Name -eq "/$($cfg.appContainer)"
$endpoint = "/api/endpoints/$($cfg.endpointId)/docker"
$app = Invoke-ReleaseApi -Path "$endpoint/containers/$($cfg.appContainer)/json"
if ($app.Id -ne $beforeApp.Id -or $app.State.StartedAt -ne $beforeApp.StartedAt) { throw 'The pre-activation app changed since the baseline capture.' }
$sql = "BEGIN READ ONLY; SELECT Value FROM AppParameters WHERE Key='$($cfg.requiredSettingKey)'; COMMIT;"
$value = Invoke-ReleaseExec -Container $cfg.databaseContainer -Directory $ctx.Private -Tag $tag -Command @('psql', '-X', '-q', '-U', 'premierecalendar', '-d', 'premierecalendar', '-At', '-v', 'ON_ERROR_STOP=1', '-c', $sql)
# Quiet psql prints only the selected value (transaction command tags are suppressed).
$lines = @(($value -join [Environment]::NewLine) -split '\r?\n' | Where-Object { $_ -notin @('BEGIN', 'COMMIT', '') })
if ($lines.Count -ne 1 -or $lines[0].Trim() -cne $cfg.requiredSettingValue) { throw "The persisted setting '$($cfg.requiredSettingKey)' is not '$($cfg.requiredSettingValue)'; investigate without changing settings automatically." }
$appAfter = Invoke-ReleaseApi -Path "$endpoint/containers/$($cfg.appContainer)/json"
if ($appAfter.Id -ne $app.Id -or $appAfter.State.StartedAt -ne $app.State.StartedAt) { throw 'The app changed during the read-only verification.' }
Write-NewJson -Path $receipt -Object ([ordered]@{
        version = $cfg.version; sourceRevision = $build.sourceRevision; key = $cfg.requiredSettingKey; value = $cfg.requiredSettingValue
        readOnly = $true; oldAppRestarted = $false; oldAppContainer = $app.Id; verifiedUtc = [DateTimeOffset]::UtcNow.ToString('O') })
Get-Content -LiteralPath $receipt
