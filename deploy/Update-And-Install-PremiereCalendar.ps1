[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RepositoryPath,
    [string]$Remote = 'origin',
    [string]$Branch = 'feature/view-sync',
    [string]$InstallScriptPath = 'Install-PremiereCalendar.ps1',
    [string]$LogPath = ''
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

    Push-Location $resolvedRepositoryPath
    try {
        $status = & git @script:GitSafeArguments status --porcelain=v1
        if ($LASTEXITCODE -ne 0) {
            throw 'Could not inspect repository status.'
        }

        if ($status) {
            throw 'Repository has local changes. Commit or stash them before updating from Settings.'
        }

        & git @script:GitSafeArguments fetch $Remote $Branch
        if ($LASTEXITCODE -ne 0) {
            throw "Could not fetch $Remote/$Branch."
        }

        $remoteRef = "$Remote/$Branch"
        $head = (Invoke-Git -Arguments @('rev-parse', 'HEAD') -FailureMessage 'Could not resolve local HEAD.').Trim()
        $remoteHead = (Invoke-Git -Arguments @('rev-parse', $remoteRef) -FailureMessage "Could not resolve $remoteRef.").Trim()

        if ($head -eq $remoteHead) {
            Write-Host "Already up to date at $head."
            return
        }

        & git @script:GitSafeArguments merge-base --is-ancestor HEAD $remoteRef
        if ($LASTEXITCODE -ne 0) {
            throw "Remote branch $remoteRef cannot fast-forward this checkout. Resolve the branch divergence manually."
        }

        & git @script:GitSafeArguments pull --ff-only $Remote $Branch
        if ($LASTEXITCODE -ne 0) {
            throw "Could not fast-forward $Remote/$Branch."
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

        & $resolvedInstallScriptPath -NoElevate
        if ($LASTEXITCODE -ne 0) {
            throw "Installer failed with exit code $LASTEXITCODE."
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
