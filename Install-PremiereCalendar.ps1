[CmdletBinding()]
param(
    [string]$TargetDirectory = 'D:\Apps\PremiereCalendar',
    [string]$Runtime = 'win-x64',
    [int]$Port = 5298,
    [switch]$NoStart,
    [switch]$SkipServiceInstall,
    [switch]$NoElevate
)

$ErrorActionPreference = 'Stop'

function Test-IsWindows {
    return [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)
}

function Test-IsAdministrator {
    $principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Add-Argument {
    param(
        [Parameter(Mandatory)][System.Collections.Generic.List[string]]$Arguments,
        [Parameter(Mandatory)][string]$Name,
        [AllowNull()][string]$Value
    )

    $Arguments.Add($Name)
    $Arguments.Add($Value ?? '')
}

if (-not (Test-IsWindows)) {
    throw 'Windows Service installation is only supported on Windows. Use .\Run-PremiereCalendar.ps1 for a foreground local run.'
}

$repoRoot = $PSScriptRoot
$publishScript = Join-Path $repoRoot 'deploy\Publish-PremiereCalendar.ps1'
if (-not (Test-Path -LiteralPath $publishScript)) {
    throw "Publish script not found: $publishScript"
}

if (-not $SkipServiceInstall -and -not $NoElevate -and -not (Test-IsAdministrator)) {
    Write-Host 'Restarting elevated to install or update the Windows Service...'
    $arguments = [System.Collections.Generic.List[string]]::new()
    $arguments.Add('-NoProfile')
    $arguments.Add('-ExecutionPolicy')
    $arguments.Add('Bypass')
    $arguments.Add('-File')
    $arguments.Add($PSCommandPath)
    Add-Argument $arguments '-TargetDirectory' $TargetDirectory
    Add-Argument $arguments '-Runtime' $Runtime
    Add-Argument $arguments '-Port' $Port.ToString([Globalization.CultureInfo]::InvariantCulture)
    if ($NoStart) {
        $arguments.Add('-NoStart')
    }

    $arguments.Add('-NoElevate')
    $process = Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments.ToArray() -Verb RunAs -Wait -PassThru
    exit $process.ExitCode
}

& $publishScript `
    -TargetDirectory $TargetDirectory `
    -Runtime $Runtime `
    -Port $Port `
    -SkipServiceInstall:$SkipServiceInstall `
    -NoStart:$NoStart

if (-not $NoStart) {
    Write-Host ''
    Write-Host "Premiere Calendar is available at http://localhost:$Port"
    if (-not $SkipServiceInstall) {
        Write-Host 'Windows Service: PremiereCalendar'
    }
}
