[CmdletBinding(SupportsShouldProcess, ConfirmImpact = 'High')]
param(
    [Parameter(Mandatory)]
    [ValidateSet('Publish', 'Deploy', 'Rollback')]
    [string] $Action,

    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version,

    [string] $QualificationReport,
    [string] $PublicationEvidence,

    [ValidatePattern('^ghcr\.io/belgian-coder/premierecalendar@sha256:[0-9a-f]{64}$')]
    [string] $RollbackImageDigest,

    [ValidatePattern('^[0-9a-f]{40}$')]
    [string] $RollbackSourceRevision,

    [uri] $InstalledBaseUri,
    [switch] $ConfirmExternalAction,
    [int] $PortainerStackId = 20,
    [string] $PortainerClient = '\\NAS\Data\Backups\Infrastructure\NUC\windows11-vm\scripts\portainer\Invoke-PortainerApi.ps1',
    [string] $ValidatedPortainerBackup,
    [string] $RecoveryRoot = '\\NAS\Data\Backups\Infrastructure-Secrets\NAS\Portainer\stacks\premierecalendar\revisions'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$workflow = Join-Path $root 'eng\Invoke-ProjectWorkflow.ps1'
$workflowArtifacts = Join-Path $root 'artifacts\workflow'

function Get-FileHashValue([string] $Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-QualifiedIdentity {
    if ([string]::IsNullOrWhiteSpace($QualificationReport)) {
        $script:QualificationReport = Join-Path $workflowArtifacts 'latest-qualification.json'
    }
    if (-not (Test-Path -LiteralPath $QualificationReport -PathType Leaf)) {
        throw 'A local qualification report is required.'
    }
    $report = Get-Content -LiteralPath $QualificationReport -Raw | ConvertFrom-Json
    if ($report.mode -ne 'Qualify' -or $report.result -ne 'passed') {
        throw 'The selected qualification did not pass.'
    }
    $head = (& git -C $root rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $head -ne $report.identity.commit) {
        throw 'The qualification commit is not the current commit.'
    }
    $status = @(& git -C $root status --porcelain=v1)
    if ($status.Count -gt 0 -or $report.identity.dirty) {
        throw 'Publication and deployment require a clean, cleanly-qualified worktree.'
    }
    $upstream = (& git -C $root rev-parse '@{upstream}').Trim()
    if ($LASTEXITCODE -ne 0 -or $upstream -ne $head) {
        throw 'The qualified commit must already be pushed to the configured upstream.'
    }
    $report
}

function Invoke-Publish {
    if ([string]::IsNullOrWhiteSpace($Version)) { throw '-Version is required for Publish.' }
    $qualified = Get-QualifiedIdentity
    if (-not $ConfirmExternalAction -and -not $WhatIfPreference) {
        throw 'Publish is external and requires -ConfirmExternalAction.'
    }
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw 'GitHub CLI is required.' }
    & gh auth status | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'GitHub CLI authentication is unavailable.' }
    $commit = [string]$qualified.identity.commit
    if (-not $PSCmdlet.ShouldProcess("container workflow at $commit", "publish version $Version")) { return }

    & gh workflow run container.yml --repo Belgian-Coder/PremiereCalendar --ref $commit -f "version=$Version"
    if ($LASTEXITCODE -ne 0) { throw 'Container publication workflow dispatch failed.' }
    $run = $null
    for ($attempt = 0; $attempt -lt 20 -and $null -eq $run; $attempt++) {
        Start-Sleep -Seconds 2
        $candidate = & gh run list --repo Belgian-Coder/PremiereCalendar --workflow container.yml --event workflow_dispatch --commit $commit --limit 1 --json databaseId,headSha,status,url | ConvertFrom-Json
        if ($candidate.Count -gt 0 -and $candidate[0].headSha -eq $commit) { $run = $candidate[0] }
    }
    if ($null -eq $run) { throw 'The dispatched publication run could not be identified.' }
    & gh run watch $run.databaseId --repo Belgian-Coder/PremiereCalendar --exit-status
    if ($LASTEXITCODE -ne 0) { throw "Publication workflow $($run.databaseId) failed." }
    $log = & gh run view $run.databaseId --repo Belgian-Coder/PremiereCalendar --log
    if ($LASTEXITCODE -ne 0) { throw 'Publication log retrieval failed.' }
    $digestMatch = [regex]::Match(($log -join "`n"), 'ghcr\.io/belgian-coder/premierecalendar@sha256:[0-9a-f]{64}')
    if (-not $digestMatch.Success) { throw 'The published immutable image digest was not found in workflow evidence.' }
    $evidence = [ordered]@{
        schemaVersion = 1
        action = 'Publish'
        result = 'passed'
        version = $Version
        commit = $commit
        imageDigest = $digestMatch.Value
        workflowRunId = [long]$run.databaseId
        workflowRunUrl = [string]$run.url
        qualificationReport = [IO.Path]::GetFullPath($QualificationReport)
        qualificationSha256 = Get-FileHashValue $QualificationReport
        completedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    }
    $path = Join-Path $workflowArtifacts "publication-$Version.json"
    $evidence | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $path -Encoding utf8NoBOM
    Write-Host "Publication evidence: $path"
}

function Get-DeploymentTarget {
    if ($Action -eq 'Deploy') {
        if ([string]::IsNullOrWhiteSpace($PublicationEvidence) -or -not (Test-Path -LiteralPath $PublicationEvidence -PathType Leaf)) {
            throw '-PublicationEvidence from the Publish action is required for Deploy.'
        }
        $qualified = Get-QualifiedIdentity
        $published = Get-Content -LiteralPath $PublicationEvidence -Raw | ConvertFrom-Json
        if ($published.action -ne 'Publish' -or $published.result -ne 'passed' -or $published.commit -ne $qualified.identity.commit) {
            throw 'Publication evidence does not match the clean qualified commit.'
        }
        return [ordered]@{ imageDigest = [string]$published.imageDigest; sourceRevision = [string]$published.commit; publication = $published }
    }
    if ([string]::IsNullOrWhiteSpace($RollbackImageDigest) -or [string]::IsNullOrWhiteSpace($RollbackSourceRevision)) {
        throw '-RollbackImageDigest and -RollbackSourceRevision are required for Rollback.'
    }
    [ordered]@{ imageDigest = $RollbackImageDigest; sourceRevision = $RollbackSourceRevision; publication = $null }
}

function Invoke-Deployment {
    $target = Get-DeploymentTarget
    if ($target.imageDigest -notmatch '^ghcr\.io/belgian-coder/premierecalendar@sha256:[0-9a-f]{64}$') {
        throw 'Deployment requires an immutable PremiereCalendar GHCR digest.'
    }
    if ($null -eq $InstalledBaseUri) { throw '-InstalledBaseUri is required for deployment acceptance.' }
    if (-not $ConfirmExternalAction -and -not $WhatIfPreference) { throw "$Action is external and requires -ConfirmExternalAction." }
    if (-not (Test-Path -LiteralPath $PortainerClient -PathType Leaf)) { throw 'The guarded Portainer API client is unavailable.' }
    if ([string]::IsNullOrWhiteSpace($ValidatedPortainerBackup) -or -not (Test-Path -LiteralPath $ValidatedPortainerBackup -PathType Leaf)) {
        throw '-ValidatedPortainerBackup must identify a fresh, operator-validated native Portainer backup.'
    }
    $backup = Get-Item -LiteralPath $ValidatedPortainerBackup
    if ($backup.Length -le 0 -or $backup.LastWriteTimeUtc -lt [DateTime]::UtcNow.AddHours(-24)) {
        throw 'The Portainer backup is empty or older than 24 hours.'
    }
    if (-not ([IO.Path]::GetFullPath($RecoveryRoot)).StartsWith('\\NAS\Data\Backups\Infrastructure-Secrets\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Recovery exports must remain under the restricted Infrastructure-Secrets tree.'
    }
    $operation = "$Action stack $PortainerStackId to $($target.imageDigest)"
    if (-not $PSCmdlet.ShouldProcess('NAS Portainer production', $operation)) { return }

    $timestamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')
    $recovery = Join-Path $RecoveryRoot "$timestamp-before-$($Action.ToLowerInvariant())"
    New-Item -ItemType Directory -Path $recovery -Force | Out-Null
    $composePath = Join-Path $recovery 'compose.yaml'
    $stackFile = & $PortainerClient -Path "/api/stacks/$PortainerStackId/file"
    $composeContent = if ($stackFile.PSObject.Properties.Name -contains 'StackFileContent') {
        [string]$stackFile.StackFileContent
    } else {
        [string]$stackFile
    }
    if ([string]::IsNullOrWhiteSpace($composeContent)) { throw 'Active Portainer Compose export is empty.' }
    $composeContent | Set-Content -LiteralPath $composePath -Encoding utf8NoBOM
    $stack = & $PortainerClient -Path "/api/stacks/$PortainerStackId"
    $stack | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $recovery 'stack-metadata.json') -Encoding utf8NoBOM
    if ($stack.Name -ne 'premierecalendar') { throw 'Resolved Portainer stack identity is not premierecalendar.' }
    $currentImage = @($stack.Env | Where-Object name -eq 'PREMIERECALENDAR_IMAGE')
    if ($currentImage.Count -ne 1) { throw 'Active stack has no unique PREMIERECALENDAR_IMAGE environment entry.' }
    $previousDigest = [string]$currentImage[0].value
    $currentImage[0].value = $target.imageDigest
    $body = [ordered]@{
        stackFileContent = Get-Content -LiteralPath $composePath -Raw
        env = @($stack.Env | ForEach-Object { [ordered]@{ name = $_.name; value = $_.value } })
        prune = $false
        pullImage = $true
    } | ConvertTo-Json -Depth 12 -Compress
    & $PortainerClient -Method PUT -Path "/api/stacks/$PortainerStackId?endpointId=$($stack.EndpointId)" -BodyJson $body -AllowMutation | Out-Null

    & $workflow -Mode Installed -InstalledBaseUri $InstalledBaseUri
    if ($LASTEXITCODE -ne 0) { throw 'Installed HTTP acceptance failed after Portainer update; use the retained previous digest for rollback.' }
    $version = Invoke-RestMethod -Uri "$($InstalledBaseUri.AbsoluteUri.TrimEnd('/'))/health/version" -TimeoutSec 20
    if ($version.sourceRevision -ne $target.sourceRevision) {
        throw "Runtime source revision '$($version.sourceRevision)' does not match expected '$($target.sourceRevision)'."
    }
    $evidence = [ordered]@{
        schemaVersion = 1
        action = $Action
        result = 'passed'
        stackId = $PortainerStackId
        endpointId = $stack.EndpointId
        previousImageDigest = $previousDigest
        imageDigest = $target.imageDigest
        expectedSourceRevision = $target.sourceRevision
        runtime = $version
        recoveryExport = $recovery
        portainerBackup = [IO.Path]::GetFullPath($ValidatedPortainerBackup)
        completedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    }
    $path = Join-Path $workflowArtifacts "$($Action.ToLowerInvariant())-$timestamp.json"
    $evidence | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $path -Encoding utf8NoBOM
    Write-Host "$Action evidence: $path"
}

switch ($Action) {
    'Publish' { Invoke-Publish }
    'Deploy' { Invoke-Deployment }
    'Rollback' { Invoke-Deployment }
}
