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

. (Join-Path $PSScriptRoot '../Wsl.Transport.ps1')
$script:ReleaseRepoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$script:ReleaseScriptRoot = $PSScriptRoot
# Dot-sourcing must work on the macOS static lane (no SystemRoot); execution still requires the Windows/WSL builder.
$script:ReleaseWsl = if ($env:SystemRoot) { Join-Path $env:SystemRoot 'System32\wsl.exe' } else { 'wsl.exe' }
$script:ReleaseDistro = 'Ubuntu-24.04'
$script:ReleaseDockerPrefix = @('-d', $script:ReleaseDistro, '--exec', 'docker', '--host', 'unix:///var/run/docker.sock')
$script:ReleaseAllowMutation = $false
$script:ReleaseEndpointId = 2
$script:ReleasePortainerApi = '\\NAS\Data\Backups\Infrastructure\NUC\windows11-vm\scripts\portainer\Invoke-PortainerApi.ps1'

# One monotonic release-step budget caps successive WSL calls and poll loops;
# the outer stage watchdog still bounds non-WSL work and total completion.
# Binary Restore-Backup retains its separate 900-second stream contract; it is not
# routed through this text transport or claimed to share its cancellation path.
$script:ReleaseStepSeconds = switch ([IO.Path]::GetFileName([string]$MyInvocation.ScriptName)) {
    'Test-BackupCompatibility.ps1' { 900 }
    'Invoke-Smoke.ps1' { 900 }
    'Initialize-Release.ps1' { 120 }
    default { 1800 }
}
$script:ReleaseStepClock = [Diagnostics.Stopwatch]::StartNew()
$script:ReleaseLoopBudget = $null

function Get-ReleaseRemainingSeconds([double] $CallLimit = 0) {
    $remaining = $script:ReleaseStepSeconds - $script:ReleaseStepClock.Elapsed.TotalSeconds
    if ($null -ne $script:ReleaseLoopBudget) {
        $remaining = [Math]::Min($remaining, $script:ReleaseLoopBudget.Seconds - $script:ReleaseLoopBudget.Clock.Elapsed.TotalSeconds)
    }
    if ($CallLimit -gt 0) { $remaining = [Math]::Min($remaining, $CallLimit) }
    if ($remaining -le 0) { throw 'The release step/loop deadline expired; resource cleanup is unconfirmed.' }
    $remaining
}

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

function Invoke-ReleaseDockerClient([string[]] $DockerArguments, [string] $InputText = '',
    [double] $CallLimit = 0, [switch] $CaptureOutput,
    [IO.TextWriter] $StandardOutputWriter, [IO.TextWriter] $StandardErrorWriter) {
    $total = Get-ReleaseRemainingSeconds $CallLimit
    $clock = [Diagnostics.Stopwatch]::StartNew()
    Assert-HarnessWslRunning $script:ReleaseDistro -TimeoutSeconds ([Math]::Min(5, $total))
    $remaining = [Math]::Min((Get-ReleaseRemainingSeconds $CallLimit), $total - $clock.Elapsed.TotalSeconds)
    if ($remaining -lt 6) { throw 'Insufficient release deadline remains for Docker execution and drain.' }
    # Direct argv needs no shell or text quoting. Two seconds for termination
    # and three for drain fit inside this call and the enclosing step/loop.
    $guestSeconds = [Math]::Floor($remaining) - 5
    $words = @('-d', $script:ReleaseDistro, '--exec', 'timeout', '--signal=TERM', '--kill-after=2s', "${guestSeconds}s", 'docker', '--host', 'unix:///var/run/docker.sock') + $DockerArguments
    $result = Invoke-HarnessWslClient (New-HarnessWslStartInfo $words) -InputText $InputText -TimeoutSeconds $remaining -CaptureOutput:$CaptureOutput -StandardOutputWriter $StandardOutputWriter -StandardErrorWriter $StandardErrorWriter
    Assert-WslTransportCompletion $result $remaining
    if ($result.InputFailed -or $result.ExitCode -in 124, 125, 126, 127, 137) { throw "Local Docker transport/deadline failed (exit $($result.ExitCode)); resource cleanup is unconfirmed." }
    $result
}

function ConvertFrom-ReleaseDockerOutput([string] $Text) {
    if ($Text.Length -eq 0) { return }
    # Match native line output without inventing a trailing empty record.
    ($Text -replace '\r?\n$', '') -split '\r?\n'
}

function Invoke-LocalDocker {
    # A plain function on purpose: native flags such as -e and --name must not bind to PowerShell parameters.
    $result = Invoke-ReleaseDockerClient -DockerArguments @($args) -CaptureOutput -StandardErrorWriter ([Console]::Error)
    if ($result.ExitCode -ne 0) { throw "Local Docker command failed (exit $($result.ExitCode))." }
    ConvertFrom-ReleaseDockerOutput $result.Output
}

function Invoke-LocalDockerInput([string] $InputText, [string[]] $DockerArguments) {
    $result = Invoke-ReleaseDockerClient -DockerArguments $DockerArguments -InputText $InputText -CaptureOutput -StandardErrorWriter ([Console]::Error)
    if ($result.ExitCode -ne 0) { throw "Local Docker text-input command failed (exit $($result.ExitCode))." }
    ConvertFrom-ReleaseDockerOutput $result.Output
}

function Test-LocalDocker {
    # Exit status only: true when the command succeeds (used for existence probes).
    $result = Invoke-ReleaseDockerClient -DockerArguments @($args) -CallLimit 30 -StandardOutputWriter ([IO.TextWriter]::Null) -StandardErrorWriter ([IO.TextWriter]::Null)
    $result.ExitCode -eq 0
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
    $stream = $null; $writer = $null
    try {
        [void][IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($logPath)))
        $stream = [IO.FileStream]::new($logPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
        $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false))
        $writer.AutoFlush = $true
        $result = Invoke-ReleaseDockerClient -DockerArguments $dockerArguments -StandardOutputWriter $writer -StandardErrorWriter $writer
        [int] $result.ExitCode
    } finally {
        if ($null -ne $writer) { $writer.Dispose() }
        elseif ($null -ne $stream) { $stream.Dispose() }
    }
}

function Invoke-LocalShell {
    # Runs a bash script inside the WSL distribution; the script travels over stdin so no quoting layer alters it.
    param([Parameter(Mandatory)][string] $Script)
    $remaining = [int][Math]::Floor((Get-ReleaseRemainingSeconds))
    if ($remaining -lt 26) { throw 'Insufficient release deadline remains for guest script/cleanup/drain.' }
    Invoke-HarnessWsl -Script $Script -ReturnExitCode -WslDistribution $script:ReleaseDistro -TimeoutSeconds $remaining
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

# Release-dedicated, persistent, labelled cache volumes. They hold only public data (the Trivy vulnerability DB,
# NuGet packages that are content-hash verified by NuGet); they are created when missing and never deleted by a release.
$script:ReleaseCacheVolumes = @{
    'trivy' = 'premierecalendar-release-trivy-cache'
    'nuget' = 'premierecalendar-release-nuget-packages'
}
$script:ReleaseCacheLabel = 'premierecalendar.release-cache'

function Get-ReleaseCacheVolume {
    param([Parameter(Mandatory)][ValidateSet('trivy', 'nuget')][string] $Kind)
    [pscustomobject]@{ Name = $script:ReleaseCacheVolumes[$Kind]; Label = "$($script:ReleaseCacheLabel)=$Kind"; Kind = $Kind }
}

function Initialize-ReleaseCacheVolume {
    # Creates the shared cache volume when missing; an existing volume of that name must carry the matching label,
    # otherwise it is not ours and the step stops instead of mounting it. One inspect answers both questions.
    param([Parameter(Mandatory)][ValidateSet('trivy', 'nuget')][string] $Kind)
    $spec = Get-ReleaseCacheVolume $Kind
    $probe = Invoke-ReleaseDockerClient -DockerArguments @('volume', 'inspect', '--format', "{{index .Labels `"$($script:ReleaseCacheLabel)`"}}", $spec.Name) -CallLimit 30 -CaptureOutput -StandardErrorWriter ([IO.TextWriter]::Null)
    if ($probe.ExitCode -eq 0) {
        if (([string]$probe.Output).Trim() -cne $Kind) { throw "Refusing to use the volume $($spec.Name): it lacks the label $($spec.Label)." }
    }
    else { $null = Invoke-LocalDocker volume create --label $spec.Label $spec.Name }
    $spec.Name
}

function Get-ConcurrentScanScript {
    # Bash for the WSL guest: Trivy and Syft read the same staged archive read-only and run side by side. Each tool
    # keeps its own log (noclobber: an existing log is never overwritten) and its own exit code. Once Trivy has
    # finished, the optional metadata command (a docker run that prints the Trivy DB metadata of the cache that scan
    # used) writes its output to -MetadataFile. -CleanupPath is removed when the script ends, however it ends.
    # Exit codes: 0 all succeeded; 11 Trivy failed; 12 Syft failed; 13 both failed; 14 only the metadata read failed.
    param([Parameter(Mandatory)][string[]] $TrivyArguments, [Parameter(Mandatory)][string[]] $SyftArguments,
        [Parameter(Mandatory)][string] $TrivyLog, [Parameter(Mandatory)][string] $SyftLog,
        [string[]] $MetadataArguments = @(), [string] $MetadataFile = '', [string] $CleanupPath = '',
        [string] $DockerDefinition = 'docker() { command docker --host unix:///var/run/docker.sock "$@"; }')
    $trivy = ($TrivyArguments | ForEach-Object { ConvertTo-ShellQuoted $_ }) -join ' '
    $syft = ($SyftArguments | ForEach-Object { ConvertTo-ShellQuoted $_ }) -join ' '
    $metadata = ($MetadataArguments | ForEach-Object { ConvertTo-ShellQuoted $_ }) -join ' '
    $trap = if ($CleanupPath) { "trap 'rm -rf $(ConvertTo-ShellQuoted $CleanupPath)' EXIT" } else { '' }
    $metadataStep = if ($MetadataArguments.Count -gt 0) { "if [ `$trivy_exit -eq 0 ]; then docker $metadata > $(ConvertTo-ShellQuoted $MetadataFile) || meta_exit=`$?; fi" } else { '' }
    @"
set -u
set -C
$trap
$DockerDefinition
docker $trivy > $(ConvertTo-ShellQuoted $TrivyLog) 2>&1 &
trivy_pid=`$!
docker $syft > $(ConvertTo-ShellQuoted $SyftLog) 2>&1 &
syft_pid=`$!
trivy_exit=0; syft_exit=0; meta_exit=0
wait `$trivy_pid || trivy_exit=`$?
$metadataStep
wait `$syft_pid || syft_exit=`$?
code=0
if [ `$trivy_exit -ne 0 ]; then code=11; fi
if [ `$syft_exit -ne 0 ]; then if [ `$code -eq 0 ]; then code=12; else code=13; fi; fi
if [ `$code -eq 0 ] && [ `$meta_exit -ne 0 ]; then code=14; fi
echo "trivy_exit=`$trivy_exit syft_exit=`$syft_exit meta_exit=`$meta_exit"
exit `$code
"@
}

function Wait-LocalCondition {
    param([Parameter(Mandatory)][scriptblock] $Condition, [int] $Attempts = 60, [int] $DelaySeconds = 1, [Parameter(Mandatory)][string] $Message)
    $previous = $script:ReleaseLoopBudget
    $script:ReleaseLoopBudget = @{ Seconds = [Math]::Min((Get-ReleaseRemainingSeconds), $Attempts * [Math]::Max(1, $DelaySeconds)); Clock = [Diagnostics.Stopwatch]::StartNew() }
    try {
        for ($i = 0; $i -lt $Attempts; $i++) {
            if (& $Condition) { return }
            $remaining = Get-ReleaseRemainingSeconds
            Start-Sleep -Milliseconds ([int]([Math]::Min($DelaySeconds, $remaining) * 1000))
        }
        throw $Message
    } finally { $script:ReleaseLoopBudget = $previous }
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
