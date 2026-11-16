#Requires -Version 7.0
# Exercise real release command expressions and the native argv boundary without WSL/Docker.
[CmdletBinding()]
param([Parameter(Mandatory)][string] $PythonExecutable)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $root 'eng/release/ReleaseCommon.ps1')
$script:ReleaseWsl = (Get-Command $PythonExecutable -CommandType Application -ErrorAction Stop).Source
$script:ReleaseDockerPrefix = @('-I', '-B', '-c', 'import json,sys; print(json.dumps(sys.argv[1:]))')
$cfg = [pscustomobject]@{ sdkImage = 'fixture-sdk'; postgresImage = 'fixture-postgres' }
$ctx = [pscustomobject]@{ BundleName = 'fixture-bundle' }
$db = 'fixture-db'; $network = 'fixture-network'; $label = 'fixture-label'
$probeBuild = 'fixture-probe'; $probeRoot = Join-Path $root 'artifacts/fixture-probe'
$app = 'fixture-app'; $image = [pscustomobject]@{ Id = 'fixture-image' }; $connection = 'fixture-connection'
$expected = @{
    'Initialize-RestoreCheck.ps1' = @(,@('/var/lib/postgresql:rw,size=1024m', '/tmp:rw,size=64m', '/run/postgresql:rw,size=16m'))
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
