#Requires -Version 7.0
# Native pipe/deadline checks; Python is a controlled child, never a WSL/SDK fallback.
[CmdletBinding()]
param([string] $PythonExecutable)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $root 'eng/Harness.Common.ps1')
. (Join-Path $root 'eng/Python.Common.ps1')
$python = Resolve-PythonExecutable $PythonExecutable
if (-not $python) { throw 'Python 3.10 or newer is required; pass -PythonExecutable.' }
$fixture = Join-Path $PSScriptRoot 'fixtures/harness_client.py'
$folder = Join-Path $root ('artifacts/harness-process-tests/' + [guid]::NewGuid().ToString('N'))
[void] (New-Item -ItemType Directory -Path $folder)
$count = 0
function Assert-Process([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
    $script:count++
}
function New-FixtureStart([string] $Mode, [string[]] $Extra = @()) {
    $start = [Diagnostics.ProcessStartInfo]::new($python)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    foreach ($word in (@('-I', '-B', $fixture, $Mode) + $Extra)) { $start.ArgumentList.Add($word) }
    $start
}
function Invoke-CapturedFixture($Start, [string] $Text, [double] $Seconds) {
    $oldOut = [Console]::Out; $oldError = [Console]::Error
    $out = [IO.StringWriter]::new(); $errorText = [IO.StringWriter]::new()
    $clock = [Diagnostics.Stopwatch]::StartNew()
    try {
        [Console]::SetOut($out); [Console]::SetError($errorText)
        $result = Invoke-HarnessWslClient -StartInfo $Start -InputText $Text -TimeoutSeconds $Seconds
        [pscustomobject]@{ Result = $result; Out = $out.ToString(); Error = $errorText.ToString(); Seconds = $clock.Elapsed.TotalSeconds }
    } finally { [Console]::SetOut($oldOut); [Console]::SetError($oldError); $out.Dispose(); $errorText.Dispose() }
}
$secret = [guid]::NewGuid().ToString('N')
$inputText = $secret + ('x' * 524288)
$start = New-FixtureStart duplex
$run = Invoke-CapturedFixture $start $inputText 30  # success-only check; generous for a fully loaded parallel lane
Assert-Process ($run.Result.ExitCode -eq 0 -and -not $run.Result.TimedOut -and -not $run.Result.InputFailed -and $run.Result.OutputDrained) 'Concurrent pipe/slow-input fixture failed.'
Assert-Process ($run.Out -eq (('O' * 262144) + "`nSTDOUT_END`nINPUT_BYTES=$([Text.Encoding]::UTF8.GetByteCount($inputText))`n")) 'Stdout was lost, duplicated or changed.'
Assert-Process ($run.Error -eq (('E' * 262144) + "`nSTDERR_END`n")) 'Stderr was lost, duplicated or changed.'
Assert-Process (-not ($run.Out + $run.Error + ($start.ArgumentList -join ' ') + ($run.Result | ConvertTo-Json)).Contains($secret)) 'Input secret escaped into output, arguments or receipt.'

$start = New-FixtureStart delay
$run = Invoke-CapturedFixture $start $inputText 0.4
Assert-Process ($run.Result.TimedOut -and -not $run.Result.OutputDrained -and $run.Seconds -lt 2) 'Blocked input/process did not respect its deadline.'
Assert-Process (-not (Get-Process -Id $run.Result.ClientPid -ErrorAction SilentlyContinue)) 'Owned timed-out client remains live.'

$pidFile = Join-Path $folder 'child.pid'
$run = Invoke-CapturedFixture (New-FixtureStart inherited_pipe @($pidFile)) '' 4
# Deadline covers two interpreter starts inside a fully parallel static lane; the descendant holds the pipe for 15 s, so a
# bypass would take far longer than 10 s.
Assert-Process ($run.Result.ExitCode -eq 0 -and $run.Result.TimedOut -and -not $run.Result.OutputDrained -and $run.Seconds -lt 10) "An inherited pipe handle bypassed the drain deadline (exit $($run.Result.ExitCode), timedOut $($run.Result.TimedOut), drained $($run.Result.OutputDrained), $([math]::Round($run.Seconds, 1)) s)."
Assert-Process ($run.Out -eq "PARENT_END`n") 'Parent output was not preserved before drain timeout.'
$childId = [int] (Get-Content -LiteralPath $pidFile -Raw)
Assert-Process ([bool] (Get-Process -Id $childId -ErrorAction SilentlyContinue)) 'Client ownership killed an unrelated descendant tree.'
# Fixture cleanup: stop only the descendant this test started (it would self-exit after its 15 s hold).
Stop-Process -Id $childId -Force -ErrorAction SilentlyContinue

$run = Invoke-CapturedFixture (New-FixtureStart exit7) $secret 15  # behaviour-only cases: generous for a loaded parallel lane
Assert-Process ($run.Result.ExitCode -eq 7 -and -not $run.Result.TimedOut -and $run.Result.OutputDrained) 'Native failure exit code changed.'
Assert-Process ($run.Out -eq "EXPECTED_FAILURE`n" -and -not ($run | ConvertTo-Json -Depth 5).Contains($secret)) 'Failure output exposed input or was incomplete.'
$run = Invoke-CapturedFixture (New-FixtureStart early7) $inputText 15
Assert-Process ($run.Result.ExitCode -eq 7 -and $run.Result.InputFailed -and $run.Result.OutputDrained -and -not $run.Result.TimedOut) "Early native failure lost its exit/input/drain distinction (exit $($run.Result.ExitCode), inputFailed $($run.Result.InputFailed), drained $($run.Result.OutputDrained), timedOut $($run.Result.TimedOut))."
$code = & {
    function Assert-HarnessWslRunning { }
    function New-HarnessWslStartInfo { New-FixtureStart exit7 }
    Invoke-HarnessWsl -Script $secret -ReturnExitCode -TimeoutSeconds 30
}
Assert-Process ($code -eq 7) 'ReturnExitCode no longer preserves a native failure.'
$code = & {
    function Assert-HarnessWslRunning { }
    function New-HarnessWslStartInfo { New-FixtureStart early7 }
    Invoke-HarnessWsl -Script $inputText -ReturnExitCode -TimeoutSeconds 30
}
Assert-Process ($code -eq 7) 'An early input rejection replaced the original nonzero native exit.'
$failure = & {
    function Assert-HarnessWslRunning { }
    function New-HarnessWslStartInfo { New-FixtureStart exit7 }
    try { Invoke-HarnessWsl -Script $secret -TimeoutSeconds 30; '' } catch { $_.Exception.Message }
}
Assert-Process ($failure -match 'exit 7' -and -not $failure.Contains($secret)) 'Default failure behavior or secret-safe diagnostic changed.'
foreach ($grace in @(15, 40)) {
    $budget = & {
        param($grace)
        function Assert-HarnessWslRunning { }
        function New-HarnessWslStartInfo([string[]] $Arguments) { [pscustomobject]@{ Arguments = $Arguments } }
        function Invoke-HarnessWslClient($StartInfo, $InputText, $TimeoutSeconds) {
            $script:budgetArguments = $StartInfo.Arguments
            $script:budgetTotal = $TimeoutSeconds
            [pscustomobject]@{ ExitCode = 0; TimedOut = $false; InputFailed = $false; OutputDrained = $true }
        }
        $code = Invoke-HarnessWsl -Script 'true' -ReturnExitCode -TimeoutSeconds 60 -CleanupGraceSeconds $grace
        [pscustomobject]@{ Code = $code; Arguments = $script:budgetArguments; Total = $script:budgetTotal }
    } $grace
    $guestSeconds = [int] $budget.Arguments[6].TrimEnd('s')
    Assert-Process ($budget.Code -eq 0 -and $budget.Arguments[5] -eq "--kill-after=${grace}s") 'Selected cleanup reserve did not reach the guest timeout.'
    Assert-Process ($guestSeconds -gt 0 -and ($guestSeconds + $grace + 5) -le $budget.Total -and $budget.Total -le 60) 'Cleanup or final five-second drain escaped the same total deadline.'
}
$failure = & {
    function Assert-HarnessWslRunning { }
    function New-HarnessWslStartInfo { throw 'A guest was launched without its cleanup reserve.' }
    try { Invoke-HarnessWsl -Script 'true' -TimeoutSeconds 30 -CleanupGraceSeconds 40; '' } catch { $_.Exception.Message }
}
Assert-Process ($failure -match 'Insufficient total deadline') 'Insufficient execution/cleanup/drain budget reached the guest.'
[ordered]@{ checks = $count; state = 'passed'; noWslOrDocker = $true; secretExposed = $false } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $folder 'summary.json')
Write-Host "$count native harness process checks passed. Evidence: $folder"
