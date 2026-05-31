[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RepositoryPath,
    [string]$Remote = 'origin',
    [string]$Branch = 'main',
    [string]$InstallScriptPath = 'Install-PremiereCalendar.ps1',
    [string]$LogPath = '',
    [string]$TargetDirectory = 'D:\Apps\PremiereCalendar',
    [string]$BackupDirectory = 'D:\Apps\PremiereCalendar\App_Data\backups\application-updates',
    [string]$HealthUrl = 'http://localhost:5298/health',
    [bool]$RollbackOnFailure = $true
)

$ErrorActionPreference = 'Stop'

function Invoke-Git {
    param(
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$FailureMessage
    )

    $output = & git @script:GitSafeArguments @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        $details = ($output | Out-String).Trim()
        if ([string]::IsNullOrWhiteSpace($details)) {
            throw $FailureMessage
        }

        throw "$FailureMessage $details"
    }

    return $output
}

function Copy-Directory {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Destination
    )

    if (-not (Test-Path -LiteralPath $Source)) {
        return
    }

    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Get-ChildItem -LiteralPath $Source -Force | Copy-Item -Destination $Destination -Recurse -Force -ErrorAction Stop
}

function New-UpdateBackup {
    param(
        [Parameter(Mandatory)][string]$InstallDirectory,
        [Parameter(Mandatory)][string]$BackupRoot,
        [Parameter(Mandatory)][string]$Stamp
    )

    $appData = Join-Path $InstallDirectory 'App_Data'
    if (-not (Test-Path -LiteralPath $appData)) {
        Write-Host "No App_Data directory found to back up at $appData."
        return ''
    }

    New-Item -ItemType Directory -Path $BackupRoot -Force | Out-Null
    $backupPath = Join-Path $BackupRoot "app-data-$Stamp"
    Copy-Directory -Source $appData -Destination $backupPath
    Write-Host "Backup snapshot: $backupPath"
    return $backupPath
}

function Restore-UpdateBackup {
    param(
        [Parameter(Mandatory)][string]$InstallDirectory,
        [Parameter(Mandatory)][string]$BackupPath
    )

    if ([string]::IsNullOrWhiteSpace($BackupPath) -or -not (Test-Path -LiteralPath $BackupPath)) {
        Write-Host 'No backup snapshot is available to restore.'
        return
    }

    $appData = Join-Path $InstallDirectory 'App_Data'
    if (Test-Path -LiteralPath $appData) {
        Remove-Item -LiteralPath $appData -Recurse -Force
    }

    New-Item -ItemType Directory -Path $appData -Force | Out-Null
    Copy-Directory -Source $BackupPath -Destination $appData
    Write-Host "Restored backup snapshot: $BackupPath"
}

function Test-ApplicationHealth {
    param([Parameter(Mandatory)][string]$Uri)

    if ([string]::IsNullOrWhiteSpace($Uri)) {
        return
    }

    $deadline = (Get-Date).AddSeconds(60)
    do {
        try {
            $response = Invoke-WebRequest -Uri $Uri -UseBasicParsing -TimeoutSec 5
            if ($response.StatusCode -eq 200) {
                Write-Host "Health check passed: $Uri"
                return
            }
        }
        catch {
            Start-Sleep -Seconds 2
        }
    } while ((Get-Date) -lt $deadline)

    throw "Health check failed: $Uri"
}

if (-not [string]::IsNullOrWhiteSpace($LogPath)) {
    $logDirectory = Split-Path -Parent $LogPath
    if (-not [string]::IsNullOrWhiteSpace($logDirectory)) {
        New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
    }

    Start-Transcript -Path $LogPath -Append | Out-Null
}

try {
    $resolvedRepositoryPath = (Resolve-Path -LiteralPath $RepositoryPath).Path
    $script:GitSafeArguments = @('-c', "safe.directory=$resolvedRepositoryPath")
    $gitPath = Join-Path $resolvedRepositoryPath '.git'
    if (-not (Test-Path -LiteralPath $gitPath)) {
        throw "Repository path is not a Git repository: $resolvedRepositoryPath"
    }

    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $resolvedTargetDirectory = if ([System.IO.Path]::IsPathRooted($TargetDirectory)) {
        $TargetDirectory
    }
    else {
        Join-Path $resolvedRepositoryPath $TargetDirectory
    }
    $resolvedBackupDirectory = if ([System.IO.Path]::IsPathRooted($BackupDirectory)) {
        $BackupDirectory
    }
    else {
        Join-Path $resolvedRepositoryPath $BackupDirectory
    }
    $previousHead = ''
    $backupPath = ''

    Push-Location $resolvedRepositoryPath
    try {
        $status = & git @script:GitSafeArguments status --porcelain=v1
        if ($LASTEXITCODE -ne 0) {
            throw 'Could not inspect repository status.'
        }

        if ($status) {
            throw 'Repository has local changes. Commit or stash them before updating from Settings.'
        }

        $previousHead = (Invoke-Git -Arguments @('rev-parse', 'HEAD') -FailureMessage 'Could not resolve local HEAD.').Trim()
        & git @script:GitSafeArguments fetch $Remote $Branch
        if ($LASTEXITCODE -ne 0) {
            throw "Could not fetch $Remote/$Branch."
        }

        $remoteRef = "$Remote/$Branch"
        $head = $previousHead
        $remoteHead = (Invoke-Git -Arguments @('rev-parse', $remoteRef) -FailureMessage "Could not resolve $remoteRef.").Trim()

        if ($head -eq $remoteHead) {
            Write-Host "Already up to date at $head."
            return
        }

        & git @script:GitSafeArguments merge-base --is-ancestor HEAD $remoteRef
        if ($LASTEXITCODE -ne 0) {
            throw "Remote branch $remoteRef cannot fast-forward this checkout. Resolve the branch divergence manually."
        }

        $resolvedInstallScriptPath = if ([System.IO.Path]::IsPathRooted($InstallScriptPath)) {
            $InstallScriptPath
        }
        else {
            Join-Path $resolvedRepositoryPath $InstallScriptPath
        }

        if (-not (Test-Path -LiteralPath $resolvedInstallScriptPath -PathType Leaf)) {
            throw "Install script not found: $resolvedInstallScriptPath"
        }

        $backupPath = New-UpdateBackup -InstallDirectory $resolvedTargetDirectory -BackupRoot $resolvedBackupDirectory -Stamp $stamp

        try {
            & git @script:GitSafeArguments pull --ff-only $Remote $Branch
            if ($LASTEXITCODE -ne 0) {
                throw "Could not fast-forward $Remote/$Branch."
            }

            & $resolvedInstallScriptPath -NoElevate
            if ($LASTEXITCODE -ne 0) {
                throw "Installer failed with exit code $LASTEXITCODE."
            }

            Test-ApplicationHealth -Uri $HealthUrl
            $newHead = (Invoke-Git -Arguments @('rev-parse', 'HEAD') -FailureMessage 'Could not resolve updated HEAD.').Trim()
            Write-Host "Update completed successfully: $previousHead -> $newHead"
        }
        catch {
            $failure = $_
            Write-Host "Update failed: $($failure.Exception.Message)"
            if ($RollbackOnFailure -and -not [string]::IsNullOrWhiteSpace($previousHead)) {
                Write-Host "Rolling back to $previousHead..."
                & git @script:GitSafeArguments reset --hard $previousHead
                if ($LASTEXITCODE -ne 0) {
                    throw "Rollback failed while resetting Git checkout to $previousHead. Original failure: $($failure.Exception.Message)"
                }

                Restore-UpdateBackup -InstallDirectory $resolvedTargetDirectory -BackupPath $backupPath
                & $resolvedInstallScriptPath -NoElevate
                if ($LASTEXITCODE -ne 0) {
                    throw "Rollback install failed with exit code $LASTEXITCODE. Original failure: $($failure.Exception.Message)"
                }

                Test-ApplicationHealth -Uri $HealthUrl
                Write-Host "Rollback completed to $previousHead."
            }

            throw $failure
        }
    }
    finally {
        Pop-Location
    }
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($LogPath)) {
        Stop-Transcript | Out-Null
    }
}
