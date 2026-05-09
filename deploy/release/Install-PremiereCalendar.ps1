#requires -RunAsAdministrator

[CmdletBinding()]
param(
    [string]$PayloadDirectory = (Join-Path $PSScriptRoot 'app'),
    [string]$InstallDirectory = (Join-Path $env:ProgramFiles 'PremiereCalendar'),
    [string]$DataDirectory = (Join-Path $env:ProgramData 'PremiereCalendar'),
    [int]$Port = 5298,
    [string]$ServiceName = 'PremiereCalendar',
    [string]$DisplayName = 'Premiere Calendar',
    [string]$TmdbBearerToken,
    [string]$TraktClientId,
    [string]$OmdbApiKey,
    [string]$FanartApiKey,
    [string]$TheTvdbApiKey,
    [string]$FirewallRemoteAddress = 'LocalSubnet',
    [switch]$SkipFirewall,
    [switch]$NoStart,
    [switch]$NonInteractive
)

$ErrorActionPreference = 'Stop'

function Get-FullPath {
    param([Parameter(Mandatory)][string]$Path)

    return [System.IO.Path]::GetFullPath($ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Path))
}

function Assert-InstallPath {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Name
    )

    $fullPath = Get-FullPath $Path
    $root = [System.IO.Path]::GetPathRoot($fullPath)
    $windowsPath = [Environment]::GetFolderPath('Windows')

    if ([string]::Equals($fullPath.TrimEnd('\'), $root.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Name cannot be a drive root: $fullPath"
    }

    if ([string]::Equals($fullPath.TrimEnd('\'), $windowsPath.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Name cannot be the Windows directory: $fullPath"
    }

    return $fullPath
}

function Convert-EnvironmentListToMap {
    param([string[]]$Values)

    $map = [ordered]@{}
    foreach ($value in @($Values)) {
        if ($value -match '^\s*([^=]+)=(.*)$') {
            $map[$Matches[1]] = $Matches[2]
        }
    }

    return $map
}

function Set-NonEmptyEnvironmentValue {
    param(
        [Parameter(Mandatory)]$Map,
        [Parameter(Mandatory)][string]$Name,
        [AllowNull()][string]$Value
    )

    if (-not [string]::IsNullOrWhiteSpace($Value)) {
        $Map[$Name] = $Value
    }
}

function Remove-PremiereCalendarStartupEntries {
    $startupDirectory = [Environment]::GetFolderPath('Startup')
    if ([string]::IsNullOrWhiteSpace($startupDirectory) -or -not (Test-Path -LiteralPath $startupDirectory)) {
        return
    }

    Get-ChildItem -LiteralPath $startupDirectory -Force -ErrorAction SilentlyContinue |
        Where-Object {
            $_.Name -in @(
                'Premiere Calendar.lnk',
                'PremiereCalendar.lnk',
                'PremiereCalendar.cmd',
                'Premiere Calendar.cmd'
            )
        } |
        Remove-Item -Force -ErrorAction SilentlyContinue
}

$resolvedPayloadDirectory = Get-FullPath $PayloadDirectory
$payloadExe = Join-Path $resolvedPayloadDirectory 'PremiereCalendar.exe'
if (-not (Test-Path -LiteralPath $payloadExe)) {
    throw "Release payload was not found. Expected executable: $payloadExe"
}

$resolvedInstallDirectory = Assert-InstallPath $InstallDirectory 'InstallDirectory'
$resolvedDataDirectory = Assert-InstallPath $DataDirectory 'DataDirectory'
$targetExe = Join-Path $resolvedInstallDirectory 'PremiereCalendar.exe'

New-Item -ItemType Directory -Force -Path $resolvedInstallDirectory | Out-Null
New-Item -ItemType Directory -Force -Path $resolvedDataDirectory | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $resolvedDataDirectory 'cache\calendar') | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $resolvedDataDirectory 'cache\images') | Out-Null

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service -and $service.Status -ne 'Stopped') {
    Write-Host "Stopping service $ServiceName..."
    Stop-Service -Name $ServiceName -Force
    $service.WaitForStatus('Stopped', '00:00:45')
}

Get-Process -Name 'PremiereCalendar' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $targetExe } |
    Stop-Process -Force

Write-Host "Copying release files to $resolvedInstallDirectory..."
robocopy $resolvedPayloadDirectory $resolvedInstallDirectory /MIR /XD App_Data /NFL /NDL /NJH /NJS /NP
$robocopyExitCode = $LASTEXITCODE
if ($robocopyExitCode -gt 7) {
    throw "robocopy failed with exit code $robocopyExitCode"
}

$serviceRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
$existingEnvironment = [ordered]@{}
if (Test-Path -LiteralPath $serviceRegistryPath) {
    $existingProperty = Get-ItemProperty -LiteralPath $serviceRegistryPath -Name Environment -ErrorAction SilentlyContinue
    if ($existingProperty -and $existingProperty.Environment) {
        $existingEnvironment = Convert-EnvironmentListToMap ([string[]]$existingProperty.Environment)
    }
}

$environment = [ordered]@{}
foreach ($key in $existingEnvironment.Keys) {
    $environment[$key] = $existingEnvironment[$key]
}

$environment['ASPNETCORE_URLS'] = "http://0.0.0.0:$Port"
$environment['ASPNETCORE_ENVIRONMENT'] = 'Production'
$environment['DOTNET_ENVIRONMENT'] = 'Production'
$environment['Urls'] = "http://0.0.0.0:$Port"
$environment['AppDatabase__Path'] = Join-Path $resolvedDataDirectory 'data\premiere-calendar.db'
$environment['CalendarCache__Directory'] = Join-Path $resolvedDataDirectory 'cache\calendar'
$environment['ImageCache__Directory'] = Join-Path $resolvedDataDirectory 'cache\images'

if ($PSBoundParameters.ContainsKey('TmdbBearerToken')) {
    Set-NonEmptyEnvironmentValue $environment 'Tmdb__BearerToken' $TmdbBearerToken
}
elseif (-not $environment.Contains('Tmdb__BearerToken') -and -not $NonInteractive) {
    $enteredToken = Read-Host 'TMDb API read access token'
    Set-NonEmptyEnvironmentValue $environment 'Tmdb__BearerToken' $enteredToken
}

if ($PSBoundParameters.ContainsKey('TraktClientId')) {
    Set-NonEmptyEnvironmentValue $environment 'Trakt__ClientId' $TraktClientId
}

if ($PSBoundParameters.ContainsKey('OmdbApiKey') -and -not [string]::IsNullOrWhiteSpace($OmdbApiKey)) {
    $environment['Omdb__ApiKey'] = $OmdbApiKey
    $environment['Omdb__Enabled'] = 'true'
}

if ($PSBoundParameters.ContainsKey('FanartApiKey') -and -not [string]::IsNullOrWhiteSpace($FanartApiKey)) {
    $environment['Fanart__ApiKey'] = $FanartApiKey
    $environment['Fanart__Enabled'] = 'true'
}

if ($PSBoundParameters.ContainsKey('TheTvdbApiKey') -and -not [string]::IsNullOrWhiteSpace($TheTvdbApiKey)) {
    $environment['TheTvdb__ApiKey'] = $TheTvdbApiKey
    $environment['TheTvdb__Enabled'] = 'true'
}

if (-not $environment.Contains('Tmdb__BearerToken')) {
    Write-Warning 'No TMDb token is configured. The service can start, but live calendar data will not load until Tmdb__BearerToken is set.'
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    Write-Host "Updating Windows Service $ServiceName..."
    & sc.exe config $ServiceName binPath= "`"$targetExe`"" start= auto DisplayName= "`"$DisplayName`"" | Out-Null
}
else {
    Write-Host "Creating Windows Service $ServiceName..."
    & sc.exe create $ServiceName binPath= "`"$targetExe`"" start= auto DisplayName= "`"$DisplayName`"" | Out-Null
}

& sc.exe description $ServiceName 'Premiere Calendar self-hosted .NET application.' | Out-Null
& sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/300000 | Out-Null
& sc.exe failureflag $ServiceName 1 | Out-Null

if (-not (Test-Path -LiteralPath $serviceRegistryPath)) {
    New-Item -Path $serviceRegistryPath -Force | Out-Null
}

$environmentValues = @($environment.GetEnumerator() | ForEach-Object { "$($_.Key)=$($_.Value)" })
New-ItemProperty -Path $serviceRegistryPath -Name Environment -PropertyType MultiString -Value ([string[]]$environmentValues) -Force | Out-Null

Remove-PremiereCalendarStartupEntries

if (-not $SkipFirewall) {
    $firewallRuleName = "$DisplayName $Port"
    Write-Host "Configuring firewall rule $firewallRuleName..."
    Get-NetFirewallRule -DisplayName $firewallRuleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule `
        -DisplayName $firewallRuleName `
        -Direction Inbound `
        -Protocol TCP `
        -LocalPort $Port `
        -Action Allow `
        -RemoteAddress $FirewallRemoteAddress `
        -Profile Any | Out-Null
}

if (-not $NoStart) {
    Write-Host "Starting service $ServiceName..."
    Start-Service -Name $ServiceName
    (Get-Service -Name $ServiceName).WaitForStatus('Running', '00:00:45')

    $healthUri = "http://localhost:$Port/health"
    $healthy = $false
    for ($attempt = 1; $attempt -le 15; $attempt++) {
        try {
            $response = Invoke-WebRequest -UseBasicParsing -Uri $healthUri -TimeoutSec 5
            if ($response.StatusCode -eq 200) {
                $healthy = $true
                break
            }
        }
        catch {
            Start-Sleep -Seconds 2
        }
    }

    if (-not $healthy) {
        throw "Service started, but the health endpoint did not return 200: $healthUri"
    }
}

Write-Host ''
Write-Host 'Premiere Calendar installation complete.'
Write-Host "Install directory: $resolvedInstallDirectory"
Write-Host "Data directory:    $resolvedDataDirectory"
Write-Host "Local URL:         http://localhost:$Port/"
Write-Host "LAN URL:           http://<this-machine-ip>:$Port/"
