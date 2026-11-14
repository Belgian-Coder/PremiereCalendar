# Shared helpers for the local release scripts in this folder. Dot-source it; it defines functions only.
#
#   . (Join-Path $PSScriptRoot 'ReleaseCommon.ps1')
#
# Safety properties every script relies on:
#   * evidence is written with CreateNew: an existing file is never overwritten (a retry needs a new bundle/prefix);
#   * every Docker call is pinned to the local WSL engine socket, never to an inherited context (the NAS engine);
#   * Portainer goes through the existing wrapper script, which owns the credentials (nothing here reads or prints
#     them); anything but GET needs the script to have been started with -AllowMutation;
#   * -WhatIf on a release script only prints the plan: no Docker, NAS, Portainer or network call is made.

$script:ReleaseRepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$script:ReleaseScriptRoot = $PSScriptRoot
$script:ReleaseWsl = Join-Path $env:SystemRoot 'System32\wsl.exe'
$script:ReleaseDistro = 'Ubuntu-24.04'
$script:ReleaseDockerPrefix = @('-d', $script:ReleaseDistro, '--exec', 'docker', '--host', 'unix:///var/run/docker.sock')
$script:ReleaseAllowMutation = $false
$script:ReleaseEndpointId = 2
$script:ReleasePortainerApi = '\\NAS\Data\Backups\Infrastructure\NUC\windows11-vm\scripts\portainer\Invoke-PortainerApi.ps1'

# ---------------------------------------------------------------- paths, names, hashes

function ConvertTo-WslPath {
    param([Parameter(Mandatory)][string] $Path)
    $full = [IO.Path]::GetFullPath($Path)
    if ($full -notmatch '^([A-Za-z]):\\(.*)$') { throw "Only local drive paths can be used inside WSL: $Path" }
    '/mnt/' + $Matches[1].ToLowerInvariant() + '/' + ($Matches[2] -replace '\\', '/')
}

function ConvertTo-ShellQuoted {
    param([Parameter(Mandatory)][string] $Value)
    "'" + ($Value -replace "'", "'\''") + "'"
}

function Get-FileSha256 {
    param([Parameter(Mandatory)][string] $Path)
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-FilesAbsent {
    param([Parameter(Mandatory)][string[]] $Path)
    $existing = @($Path | Where-Object { Test-Path -LiteralPath $_ })
    if ($existing.Count -gt 0) {
        throw "Existing evidence must be preserved; refusing to overwrite (use a new bundle or resource prefix): $($existing -join ', ')"
    }
}

function Write-NewText {
    # CreateNew makes "refuse to overwrite" atomic: a concurrent or earlier file is never replaced.
    param([Parameter(Mandatory)][string] $Path, [Parameter(Mandatory)][AllowEmptyString()][string] $Text)
    $directory = Split-Path -Parent $Path
    if ($directory) { New-Item -ItemType Directory -Path $directory -Force | Out-Null }
    try { $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None) }
    catch [IO.IOException] { throw "Existing evidence must be preserved; refusing to overwrite: $Path" }
    try {
        $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Text)
        $stream.Write($bytes, 0, $bytes.Length)
    }
    finally { $stream.Dispose() }
}

function Write-NewJson {
    param([Parameter(Mandatory)][string] $Path, [Parameter(Mandatory)] $Object, [int] $Depth = 30)
    Write-NewText -Path $Path -Text ((ConvertTo-Json -InputObject $Object -Depth $Depth) + "`n")
}

function Read-Json {
    param([Parameter(Mandatory)][string] $Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "Required evidence is missing: $Path" }
    $text = Get-Content -LiteralPath $Path -Raw
    # Keep ISO timestamps as strings so they round-trip into reports unchanged.
    if ((Get-Command ConvertFrom-Json).Parameters.ContainsKey('DateKind')) { return $text | ConvertFrom-Json -DateKind String }
    $text | ConvertFrom-Json
}

function Test-AllChecksTrue {
    # True when an object's `checks` property exists and every member is $true.
    param($Report)
    if ($null -eq $Report -or $null -eq $Report.checks) { return $false }
    @($Report.checks.psobject.Properties | Where-Object { $_.Value -ne $true }).Count -eq 0
}

# ---------------------------------------------------------------- release context

function Get-ReleaseDefaults {
    [ordered]@{
        NasBackupShare    = '\\NAS\Docker\PremiereCalendar\backups'
        PrivateBackupRoot = '\\NAS\Data\Backups\Infrastructure-Secrets\NAS\Portainer\stacks\premierecalendar'
        NasReleaseRoot    = '\\NAS\Docker\PremiereCalendar\releases'
        ComposeRoot       = '\\NAS\Docker\PortainerCE\data\compose'
        PortainerApi      = $script:ReleasePortainerApi
    }
}

function Get-PinnedImages {
    # The release builds and tests with exactly the images the repository pins; nothing is hard-coded here.
    $dockerfile = Get-Content -LiteralPath (Join-Path $script:ReleaseRepoRoot 'Dockerfile') -Raw
    $compose = Get-Content -LiteralPath (Join-Path $script:ReleaseRepoRoot 'compose.yaml') -Raw
    if ($dockerfile -notmatch '(?m)^FROM\s+(mcr\.microsoft\.com/dotnet/sdk:\S+@sha256:[0-9a-f]{64})\s+AS\s+build\s*$') { throw 'Could not find the digest-pinned SDK image (stage "build") in the Dockerfile.' }
    $sdk = $Matches[1]
    if ($dockerfile -notmatch '(?m)^FROM\s+(mcr\.microsoft\.com/dotnet/aspnet:\S+@sha256:[0-9a-f]{64})\s+AS\s+runtime\s*$') { throw 'Could not find the digest-pinned ASP.NET runtime image (stage "runtime") in the Dockerfile.' }
    $runtime = $Matches[1]
    if ($compose -notmatch '(?m)^\s*image:\s*(postgres:\S+@sha256:[0-9a-f]{64})\s*$') { throw 'Could not find the digest-pinned postgres image in compose.yaml.' }
    [pscustomobject]@{ Sdk = $sdk; Runtime = $runtime; Postgres = $Matches[1] }
}

function Get-DatabaseSchemaVersion {
    $props = Get-Content -LiteralPath (Join-Path $script:ReleaseRepoRoot 'Directory.Build.props') -Raw
    if ($props -notmatch '<DatabaseSchemaVersion>(\d+)</DatabaseSchemaVersion>') { throw 'Could not read DatabaseSchemaVersion from Directory.Build.props.' }
    [int]$Matches[1]
}

function Get-ReleaseContext {
    # Loads <Bundle>\release-config.json (written by Initialize-Release.ps1) and derives the working paths.
    param([Parameter(Mandatory)][string] $Bundle)
    if (-not [IO.Path]::IsPathRooted($Bundle)) { throw '-Bundle must be a full path, for example D:\Projects\PremiereCalendar\artifacts\release\1.2.20.' }
    $bundlePath = [IO.Path]::GetFullPath($Bundle).TrimEnd('\')
    $config = Read-Json (Join-Path $bundlePath 'release-config.json')
    if ($config.schema -ne 1) { throw "Unsupported release-config.json schema: $($config.schema)" }
    $name = $config.bundleName
    [pscustomobject]@{
        Config          = $config
        Version         = $config.version
        BundleName      = $name
        ImageTag        = $config.imageTag
        Prefix          = $config.resourcePrefix
        Bundle          = $bundlePath
        BundleWsl       = ConvertTo-WslPath $bundlePath
        Private         = "$bundlePath.private"
        Installed       = Join-Path $bundlePath 'installed'
        Repo            = $script:ReleaseRepoRoot
        SourceArchive   = Join-Path $bundlePath "source-$($config.version).tar"
        ImageArchive    = Join-Path $bundlePath "premierecalendar-$($config.version).tar"
    }
}

function Get-ReleaseSubnet {
    # Docker's default address pools are exhausted on the release machine, so every network gets an explicit
    # /24 inside 10.130-10.149. Roles use consecutive third octets: 0 tests, 1 smoke, 2 restore check.
    param([Parameter(Mandatory)] $Context, [Parameter(Mandatory)][ValidateRange(0, 9)][int] $Offset)
    "$($Context.Config.subnetPrefix).$([int]$Context.Config.subnetOctet + $Offset).0/24"
}

function Start-ReleaseSession {
    # Records the Portainer settings for this run; -AllowMutation must be passed explicitly by the owner.
    param([Parameter(Mandatory)] $Context, [switch] $AllowMutation)
    $script:ReleaseAllowMutation = [bool]$AllowMutation
    $script:ReleaseEndpointId = [int]$Context.Config.endpointId
    $script:ReleasePortainerApi = [string]$Context.Config.portainerApiScript
}

function Show-ReleasePlan {
    # Prints the plan and returns $true when the script runs under -WhatIf (the caller then returns without acting).
    param([Parameter(Mandatory)][string] $Title, [Parameter(Mandatory)][string[]] $Step)
    if (-not $WhatIfPreference) { return $false }
    Write-Host "WHAT IF: $Title (nothing is executed; no Docker, NAS, Portainer or network call is made)"
    $number = 0
    foreach ($line in $Step) { $number++; Write-Host ("  {0}. {1}" -f $number, $line) }
    return $true
}

function Assert-Version {
    param([string] $Value, [string] $Name)
    if ($Value -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') { throw "$Name must look like 1.2.3." }
}

# ---------------------------------------------------------------- source tree

function Invoke-Git {
    $output = & git -C $script:ReleaseRepoRoot @args
    if ($LASTEXITCODE -ne 0) { throw "git $($args -join ' ') failed with exit code $LASTEXITCODE." }
    $output
}

function Assert-CleanSourceAtRevision {
    # The release is built and tested from the committed bytes only: HEAD is the expected commit, tracked files are
    # unmodified and no untracked source file exists that would be missing from the exact `git archive`.
    param([Parameter(Mandatory)][string] $Revision)
    if ($Revision -notmatch '^[0-9a-f]{40}$') { throw 'The release source must be an exact 40-character commit SHA.' }
    $head = (Invoke-Git rev-parse HEAD | Select-Object -First 1).Trim()
    if ($head -cne $Revision) { throw "HEAD ($head) differs from the expected release commit ($Revision)." }
    if (@(Invoke-Git status --porcelain --untracked-files=no).Count -gt 0) { throw 'Tracked files must be clean (no uncommitted changes) before building or testing a release.' }
    $untracked = @(Invoke-Git ls-files --others --exclude-standard PremiereCalendar tests eng docs Dockerfile compose.yaml Directory.Build.props global.json PremiereCalendar.slnx |
            Where-Object { $_ -match '\.(cs|razor|csproj|props|json|ps1|py|sql|yaml|md|css|js)$' })
    if ($untracked.Count -gt 0) { throw "Untracked source files would be absent from the exact archive: $($untracked -join ', ')" }
}

function Invoke-ReleasePython {
    # Host Python, isolated mode: it never imports from the current folder.
    $python = Get-Command python -ErrorAction SilentlyContinue
    if (-not $python) { $python = Get-Command python3 -ErrorAction SilentlyContinue }
    if (-not $python) { throw 'Python 3 is required on the host for the release evidence scripts.' }
    & $python.Source -I @args
    if ($LASTEXITCODE -ne 0) { throw "Python helper failed (exit $LASTEXITCODE): $($args[0])" }
}

# ---------------------------------------------------------------- local Docker (WSL), never the NAS engine

function Invoke-LocalDocker {
    # A plain function on purpose: native flags such as -e and --name must not bind to PowerShell parameters.
    $wsl = $script:ReleaseWsl; $prefix = $script:ReleaseDockerPrefix
    $output = & $wsl @prefix @args
    if ($LASTEXITCODE -ne 0) { throw "Local Docker command failed (exit $LASTEXITCODE): docker $($args -join ' ')" }
    $output
}

function Test-LocalDocker {
    # Exit status only: true when the command succeeds (used for existence probes).
    $wsl = $script:ReleaseWsl; $prefix = $script:ReleaseDockerPrefix
    & $wsl @prefix @args *> $null
    $LASTEXITCODE -eq 0
}

function Invoke-LocalDockerJson {
    $text = (@(Invoke-LocalDocker @args) -join "`n")
    $text | ConvertFrom-Json
}

function Invoke-LocalDockerToFile {
    # Usage: Invoke-LocalDockerToFile <new log path> <docker arguments...>. Writes the combined output to the new
    # file and returns the exit code instead of throwing. A plain function so that native flags pass through.
    $logPath = $args[0]
    $dockerArguments = @($args | Select-Object -Skip 1)
    Assert-FilesAbsent $logPath
    $wsl = $script:ReleaseWsl; $prefix = $script:ReleaseDockerPrefix
    $lines = & $wsl @prefix @dockerArguments 2>&1
    $code = $LASTEXITCODE
    Write-NewText -Path $logPath -Text ((@($lines | ForEach-Object { "$_" }) -join "`n") + "`n")
    $code
}

function Invoke-LocalShell {
    # Runs a bash script inside the WSL distribution; the script travels over stdin so no quoting layer alters it.
    param([Parameter(Mandatory)][string] $Script)
    $wsl = $script:ReleaseWsl
    # Out-Host keeps the script's own output out of the return value, which is only the exit code.
    ((($Script -replace "`r`n", "`n").TrimEnd()) + "`n# end of script") | & $wsl -d $script:ReleaseDistro --exec bash -s | Out-Host
    [int]$LASTEXITCODE
}

function Assert-LocalDockerAvailable {
    if (-not (Test-LocalDocker info)) { throw 'The explicit local Docker socket in Ubuntu-24.04 is unavailable (it must already be running; the helpers never start it).' }
}

function Assert-DockerNamesFree {
    param([string[]] $Container = @(), [string[]] $Network = @(), [string[]] $Volume = @())
    foreach ($name in $Container) { if (Test-LocalDocker container inspect $name) { throw "Refusing container name collision: $name" } }
    foreach ($name in $Network) { if (Test-LocalDocker network inspect $name) { throw "Refusing network name collision: $name" } }
    foreach ($name in $Volume) { if (Test-LocalDocker volume inspect $name) { throw "Refusing volume name collision: $name" } }
}

function Wait-LocalCondition {
    param([Parameter(Mandatory)][scriptblock] $Condition, [int] $Attempts = 60, [int] $DelaySeconds = 1, [Parameter(Mandatory)][string] $Message)
    for ($i = 0; $i -lt $Attempts; $i++) {
        if (& $Condition) { return }
        Start-Sleep -Seconds $DelaySeconds
    }
    throw $Message
}

# ---------------------------------------------------------------- Portainer (NAS), through the credential-holding wrapper

function Invoke-ReleaseApi {
    param(
        [Parameter(Mandatory)][string] $Path, [string] $Method = 'GET', $Body,
        [string] $OutFile, [string] $BodyFile, [string] $ContentType, [int] $TimeoutSec = 120)
    if ($Method -ne 'GET' -and -not $script:ReleaseAllowMutation) {
        throw "Portainer $Method $Path changes state; run this script with -AllowMutation to permit it."
    }
    if (-not (Test-Path -LiteralPath $script:ReleasePortainerApi -PathType Leaf)) { throw 'The Portainer API wrapper is not reachable; check the NAS share.' }
    $parameters = @{ Path = $Path; TimeoutSec = $TimeoutSec }
    if ($Method -ne 'GET') { $parameters.Method = $Method; $parameters.AllowMutation = $true }
    if ($null -ne $Body) { $parameters.BodyJson = ConvertTo-Json -InputObject $Body -Depth 60 -Compress }
    if ($BodyFile) { $parameters.BodyFile = $BodyFile }
    if ($ContentType) { $parameters.ContentType = $ContentType }
    if ($OutFile) { $parameters.OutFile = $OutFile }
    $api = $script:ReleasePortainerApi
    & $api @parameters
}

function Convert-DockerFrames {
    param([byte[]] $Bytes)
    $position = 0
    $result = [Text.StringBuilder]::new()
    while ($position + 8 -le $Bytes.Length -and $Bytes[$position] -le 2 -and $Bytes[$position + 1] -eq 0 -and $Bytes[$position + 2] -eq 0 -and $Bytes[$position + 3] -eq 0) {
        $length = ([int64]$Bytes[$position + 4] * 16777216) + ([int64]$Bytes[$position + 5] * 65536) + ([int64]$Bytes[$position + 6] * 256) + $Bytes[$position + 7]
        if ($position + 8 + $length -gt $Bytes.Length) { throw 'Incomplete Docker frame.' }
        [void]$result.Append([Text.Encoding]::UTF8.GetString($Bytes, $position + 8, $length))
        $position += 8 + $length
    }
    if ($position -eq 0) { return [Text.Encoding]::UTF8.GetString($Bytes) }
    if ($position -ne $Bytes.Length) { throw 'Trailing incomplete Docker frame.' }
    $result.ToString()
}

function Assert-ReleaseExecEvidenceAvailable {
    param([Parameter(Mandatory)][string] $Directory, [Parameter(Mandatory)][string] $Tag)
    if ($Tag -notmatch '^[A-Za-z0-9_.-]+$') { throw 'Unsafe evidence tag.' }
    foreach ($suffix in @('-exec-id.txt', '-exec.bin', '.log')) {
        if (Test-Path -LiteralPath (Join-Path $Directory "$Tag$suffix")) { throw "Existing exec evidence must be preserved; choose a new tag: $Tag$suffix" }
    }
}

function Invoke-ReleaseExec {
    # Runs a command in a NAS container through the Portainer exec API (a POST, hence -AllowMutation) and keeps the
    # id, the raw frames and the decoded log as evidence in $Directory (the private evidence folder).
    param([Parameter(Mandatory)][string] $Container, [Parameter(Mandatory)][string[]] $Command, [Parameter(Mandatory)][string] $Tag, [Parameter(Mandatory)][string] $Directory)
    Assert-ReleaseExecEvidenceAvailable -Directory $Directory -Tag $Tag
    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    $endpoint = "/api/endpoints/$($script:ReleaseEndpointId)/docker"
    $exec = Invoke-ReleaseApi -Path "$endpoint/containers/$Container/exec" -Method POST -Body @{ AttachStdout = $true; AttachStderr = $true; Tty = $false; Cmd = $Command }
    Write-NewText -Path (Join-Path $Directory "$Tag-exec-id.txt") -Text ($exec.Id + "`n")
    $binary = Join-Path $Directory "$Tag-exec.bin"
    $null = Invoke-ReleaseApi -Path "$endpoint/exec/$($exec.Id)/start" -Method POST -Body @{ Detach = $false; Tty = $false } -OutFile $binary
    $status = Invoke-ReleaseApi -Path "$endpoint/exec/$($exec.Id)/json"
    $output = Convert-DockerFrames ([IO.File]::ReadAllBytes($binary))
    Write-NewText -Path (Join-Path $Directory "$Tag.log") -Text $output
    $output
    if ($status.Running -or $status.ExitCode -ne 0) { throw "Exec $Tag failed: running=$($status.Running), exit=$($status.ExitCode)." }
}

function Get-NasContainerBaseline {
    # Name, id, image, start time and health of the three stack containers.
    param([Parameter(Mandatory)] $Context, [string[]] $Name)
    $endpoint = "/api/endpoints/$($script:ReleaseEndpointId)/docker"
    foreach ($containerName in $Name) {
        $container = Invoke-ReleaseApi -Path "$endpoint/containers/$containerName/json"
        [pscustomobject]@{ Name = $container.Name; Id = $container.Id; Image = $container.Image; StartedAt = $container.State.StartedAt; Health = $container.State.Health.Status; Status = $container.State.Status }
    }
}
