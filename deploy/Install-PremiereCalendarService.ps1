#requires -RunAsAdministrator

param(
    [string]$PublishDirectory = 'D:\Apps\PremiereCalendar',
    [int]$Port = 5298,
    [string]$ServiceName = 'PremiereCalendar',
    [string]$DisplayName = 'Premiere Calendar',
    [switch]$NoStart
)

$ErrorActionPreference = 'Stop'

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

$exePath = Join-Path $PublishDirectory 'PremiereCalendar.exe'
if (-not (Test-Path -LiteralPath $exePath)) {
    throw "Published app executable not found: $exePath"
}

$service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($service) {
    if ($service.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        $service.WaitForStatus('Stopped', '00:00:30')
    }

    & sc.exe config $ServiceName binPath= "`"$exePath`"" start= auto DisplayName= "`"$DisplayName`"" | Out-Null
}
else {
    & sc.exe create $ServiceName binPath= "`"$exePath`"" start= auto DisplayName= "`"$DisplayName`"" | Out-Null
}

& sc.exe description $ServiceName 'Premiere Calendar .NET application hosted on this VM.' | Out-Null
& sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/300000 | Out-Null
& sc.exe failureflag $ServiceName 1 | Out-Null

$environment = New-Object System.Collections.Generic.List[string]
$environment.Add("ASPNETCORE_URLS=http://0.0.0.0:$Port")
$environment.Add('ASPNETCORE_ENVIRONMENT=Production')
$environment.Add('DOTNET_ENVIRONMENT=Production')
$environment.Add("AppDatabase__Path=$(Join-Path $PublishDirectory 'App_Data\data\premiere-calendar.db')")
$environment.Add("CalendarCache__Directory=$(Join-Path $PublishDirectory 'App_Data\cache\calendar')")
$environment.Add("ImageCache__Directory=$(Join-Path $PublishDirectory 'App_Data\cache\images')")

$secretsPath = Join-Path $env:APPDATA 'Microsoft\UserSecrets\e9fb65ab-fad7-4bf7-9c12-b076a6fc56a5\secrets.json'
if (Test-Path -LiteralPath $secretsPath) {
    $secrets = Get-Content -LiteralPath $secretsPath -Raw | ConvertFrom-Json -AsHashtable
    foreach ($key in $secrets.Keys) {
        $value = [string]$secrets[$key]
        if (-not [string]::IsNullOrWhiteSpace($value)) {
            $environmentName = $key.Replace(':', '__')
            $environment.Add("$environmentName=$value")
        }
    }
}
else {
    Write-Warning "User-secrets file was not found at $secretsPath. Configure TMDb__BearerToken for the service before starting it."
}

$serviceRegistryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
New-ItemProperty -Path $serviceRegistryPath -Name Environment -PropertyType MultiString -Value $environment.ToArray() -Force | Out-Null

$firewallRuleName = 'Premiere Calendar 5298'
Get-NetFirewallRule -DisplayName $firewallRuleName -ErrorAction SilentlyContinue | Remove-NetFirewallRule
New-NetFirewallRule `
    -DisplayName $firewallRuleName `
    -Direction Inbound `
    -Protocol TCP `
    -LocalPort $Port `
    -Action Allow `
    -RemoteAddress LocalSubnet `
    -Profile Any | Out-Null

$runningProcesses = Get-Process -Name 'PremiereCalendar' -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -eq $exePath }
if ($runningProcesses) {
    $runningProcesses | Stop-Process -Force
}

Remove-PremiereCalendarStartupEntries

if (-not $NoStart) {
    Start-Service -Name $ServiceName
    (Get-Service -Name $ServiceName).WaitForStatus('Running', '00:00:45')
}

Get-Service -Name $ServiceName | Select-Object Name, Status, StartType
Get-NetFirewallRule -DisplayName $firewallRuleName | Select-Object DisplayName, Enabled, Direction, Action, Profile
