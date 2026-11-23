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
# Per-bundle network creation: bounded alternative subnets, never a role of this bundle, always an explicit /24 in the prefix.
$subnetContext = [pscustomobject]@{ Config = [pscustomobject]@{ subnetPrefix = '10.139'; subnetOctet = 20 } }
$candidates = @(Get-ReleaseSubnetCandidates $subnetContext 1)
if ($candidates[0] -ne '10.139.21.0/24' -or $candidates.Count -ne 6 -or @($candidates | Select-Object -Unique).Count -ne 6) { throw "Unexpected subnet candidates: $($candidates -join ', ')" }
foreach ($candidate in $candidates[1..5]) { if ($candidate -notmatch '^10\.139\.(\d+)\.0/24$' -or [int]$Matches[1] -lt 1 -or [int]$Matches[1] -gt 250 -or ([int]$Matches[1] -ge 20 -and [int]$Matches[1] -le 29)) { throw "Subnet candidate collides with the bundle's own roles or leaves the range: $candidate" } }
if (-not (Test-ReleaseSubnetOverlap 'Error response from daemon: Pool overlaps with other one on this address space') -or (Test-ReleaseSubnetOverlap 'network name already in use')) { throw 'Subnet overlap detection changed.' }
foreach ($script in 'Invoke-Smoke.ps1', 'Initialize-RestoreCheck.ps1') {
    $text = Get-Content -Raw -LiteralPath (Join-Path $root "eng/release/$script")
    if ($text -notmatch 'New-ReleaseNetwork' -or $text -match 'Invoke-LocalDocker network create') { throw "$script must create its network through New-ReleaseNetwork (explicit --subnet with bounded retry)." }
}
if ((Get-Content -Raw -LiteralPath (Join-Path $root 'eng/release/ReleaseCommon.ps1')) -notmatch [regex]::Escape("'--subnet', `$subnet, '--label', `$Label")) { throw 'New-ReleaseNetwork lost its explicit --subnet or label.' }
$count++

# New-ReleaseNetwork with a stubbed Docker client: overlap -> retry -> success; other error -> immediate throw; exhaustion -> throw.
$script:networkCalls = [Collections.Generic.List[string]]::new(); $script:networkPlan = @()
function Invoke-ReleaseDockerClient([string[]] $DockerArguments, [string] $InputText = '', [double] $CallLimit = 0, [switch] $CaptureOutput, [IO.TextWriter] $StandardOutputWriter, [IO.TextWriter] $StandardErrorWriter) {
    $script:networkCalls.Add($DockerArguments -join ' ')
    $step = $script:networkPlan[[Math]::Min($script:networkCalls.Count, $script:networkPlan.Count) - 1]
    if ($step.ContainsKey('Error')) { $StandardErrorWriter.Write($step.Error) }
    [pscustomobject]@{ ExitCode = $step.Exit; Output = '' }
}
$overlap = @{ Exit = 1; Error = 'Error response from daemon: Pool overlaps with other one on this address space' }
$script:networkPlan = @($overlap, $overlap, @{ Exit = 0 })
$used = New-ReleaseNetwork -Context $subnetContext -Offset 1 -Label 'l=x' -Name 'net' -Internal 6>$null
if ($script:networkCalls.Count -ne 3 -or $used -ne $candidates[2] -or @($script:networkCalls | Where-Object { $_ -notmatch '--internal --subnet 10\.139\.\d+\.0/24 --label l=x net$' }).Count) { throw "Overlap retry misbehaved: $($script:networkCalls -join ' | ')" }
$script:networkCalls.Clear(); $script:networkPlan = @(@{ Exit = 1; Error = 'network with name net already exists' })
$threw = $false; try { New-ReleaseNetwork -Context $subnetContext -Offset 1 -Label 'l=x' -Name 'net' 6>$null 2>$null | Out-Null } catch { $threw = $true }
if (-not $threw -or $script:networkCalls.Count -ne 1) { throw 'A non-overlap error must fail immediately without retry.' }
$script:networkCalls.Clear(); $script:networkPlan = @($overlap)
$threw = $false; try { New-ReleaseNetwork -Context $subnetContext -Offset 1 -Label 'l=x' -Name 'net' 6>$null 2>$null | Out-Null } catch { $threw = $_.Exception.Message -match 'No free explicit' }
if (-not $threw -or $script:networkCalls.Count -ne 6) { throw "Exhaustion must throw after exactly 6 attempts (got $($script:networkCalls.Count))." }
$count++

Write-Host "$count release Docker argv checks passed across the native process boundary; no WSL/Docker call."
