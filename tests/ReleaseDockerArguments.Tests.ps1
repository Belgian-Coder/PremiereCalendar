#Requires -Version 7.0
# Exercise real release command expressions and the native argv boundary without WSL/Docker.
[CmdletBinding()]
param([string] $PythonExecutable)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $root 'eng/release/ReleaseCommon.ps1')
. (Join-Path $root 'eng/Python.Common.ps1')
$fixturePython = Resolve-PythonExecutable $PythonExecutable
if (-not $fixturePython) { throw 'Python 3.10 or newer is required; pass -PythonExecutable.' }
function Assert-HarnessWslRunning { }
function New-HarnessWslStartInfo([string[]] $Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($fixturePython)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    $first = [array]::IndexOf($Arguments, 'docker') + 3
    if ($first -lt 3 -or $Arguments[$first - 1] -ne 'unix:///var/run/docker.sock') { throw 'Local Docker argv lost its explicit socket.' }
    foreach ($word in (@('-I', '-B', '-c', 'import json,sys; print(json.dumps(sys.argv[1:]))') + $Arguments[$first..($Arguments.Length - 1)])) { $start.ArgumentList.Add($word) }
    $start
}
$cfg = [pscustomobject]@{ sdkImage = 'fixture-sdk'; postgresImage = 'fixture-postgres' }
$ctx = [pscustomobject]@{ BundleName = 'fixture-bundle' }
$db = 'fixture-db'; $network = 'fixture-network'; $label = 'fixture-label'
$networkTarget = $network; [string[]]$pullPolicy = @()
$probeBuild = 'fixture-probe'; $probeRoot = Join-Path $root 'artifacts/fixture-probe'
$app = 'fixture-app'; $image = [pscustomobject]@{ Id = 'fixture-image' }; $connection = 'fixture-connection'
$expected = @{
    'Initialize-RestoreCheck.ps1' = @(,@('/var/lib/postgresql:rw,size=4096m', '/tmp:rw,size=64m', '/run/postgresql:rw,size=16m'))
    'Invoke-Smoke.ps1' = @(@('/tmp:rw,size=512m'), @('/var/lib/postgresql:rw,size=512m', '/tmp:rw,size=64m', '/run/postgresql:rw,size=16m'))
    'Test-BackupCompatibility.ps1' = @(,@('/tmp:rw,noexec,nosuid,size=64m', '/app/App_Data:rw,uid=1654,gid=999,mode=0770,size=128m'))
}
$count = 0
foreach ($file in @('Initialize-RestoreCheck.ps1', 'Invoke-Smoke.ps1', 'Test-BackupCompatibility.ps1')) {
    $tokens = $null; $parseErrors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $root "eng/release/$file"), [ref]$tokens, [ref]$parseErrors)
    if ($parseErrors.Count) { throw "Could not parse $file." }
    $commands = @($ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.CommandAst] -and $node.GetCommandName() -eq 'Invoke-LocalDocker' -and
            $node.Extent.Text.Contains('--tmpfs')
    }, $true))
    if ($commands.Count -ne $expected[$file].Count) { throw "Unexpected tmpfs command inventory in $file." }
    for ($n = 0; $n -lt $commands.Count; $n++) {
        $argv = @((& ([scriptblock]::Create($commands[$n].Extent.Text))) | ConvertFrom-Json)
        if ($argv -contains 'System.Object[]') { throw "$file passed an array through the native argument boundary." }
        $mounts = @()
        for ($i = 0; $i -lt $argv.Count; $i++) {
            if ($argv[$i] -eq '--tmpfs') { $mounts += $argv[$i + 1] }
        }
        if (($mounts -join "`n") -cne ($expected[$file][$n] -join "`n")) { throw "Tmpfs flags changed in $file command $n. Actual native mount values: $($mounts | ConvertTo-Json -Compress)" }
        if ($file -eq 'Test-BackupCompatibility.ps1') {
            foreach ($flag in @('--read-only', '--user', '1654:999', '--cap-drop', 'ALL', '--security-opt', 'no-new-privileges:true')) {
                if ($argv -notcontains $flag) { throw "Compatibility hardening flag lost: $flag" }
            }
        }
        $count++
    }
}
if ($count -ne 4) { throw 'Release Docker command coverage changed.' }
Write-Host "$count release Docker argv checks passed across the native process boundary; no WSL/Docker call."
