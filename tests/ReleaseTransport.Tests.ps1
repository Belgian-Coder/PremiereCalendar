#Requires -Version 7.0
# Real native child I/O; WSL/Docker construction is replaced, never executed.
[CmdletBinding()]
param([string] $PythonExecutable)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $root 'eng/release/ReleaseCommon.ps1')
. (Join-Path $root 'eng/Python.Common.ps1')
$python = Resolve-PythonExecutable $PythonExecutable
if (-not $python) { throw 'Python 3.10 or newer is required; pass -PythonExecutable.' }
$fixture = Join-Path $PSScriptRoot 'fixtures/harness_client.py'
$folder = Join-Path $root ('artifacts/release-transport-tests/' + [guid]::NewGuid().ToString('N'))
[void] (New-Item -ItemType Directory -Path $folder)
$script:FixtureMode = 'json_stderr'; $script:FixtureDeadline = 0; $script:FixtureExtra = @()
$script:NativeStarts = 0; $script:LastTransportArguments = @()
$script:ActualClient = (Get-Command Invoke-HarnessWslClient).ScriptBlock
# Test-only barrier/recorder around the real shared UTF8 writer. Native child
# pipes and the production process pump remain real; no read is mocked.
if (-not ('WslBoundaryWriter' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Diagnostics;
using System.Collections.Generic;
public sealed class WslBoundaryWriter : TextWriter {
    readonly TextWriter inner;
    readonly string side, target, signal, ack;
    readonly char prefix;
    readonly List<string> events;
    int seen;
    bool signalled;
    public bool SawTrailingHigh { get; private set; }
    public override Encoding Encoding => inner.Encoding;
    public WslBoundaryWriter(TextWriter inner, string side, string target, string signal, string ack, List<string> events) {
        this.inner=inner; this.side=side; this.target=target; this.signal=signal; this.ack=ack; this.events=events;
        prefix=target=="stdout" ? 'A' : 'B';
    }
    public override void Write(string value) {
        bool high=value.Length>0 && char.IsHighSurrogate(value[value.Length-1]);
        SawTrailingHigh |= high;
        events.Add(side+":"+value.Length+":"+high);
        inner.Write(value);
        if (side!=target || signalled) return;
        foreach(char c in value) if(c==prefix) seen++;
        if(seen<8191) return;
        signalled=true;
        File.WriteAllText(signal,"ready");
        var clock=Stopwatch.StartNew();
        while(!File.Exists(ack) && clock.Elapsed.TotalSeconds<2) Thread.Sleep(1);
        if(!File.Exists(ack)) throw new IOException("Native Unicode barrier did not acknowledge");
        Thread.Sleep(50); // Let the real other-pipe read complete before forwarding the low surrogate.
    }
}
'@
}
function Assert-HarnessWslRunning { }
function New-HarnessWslStartInfo([string[]] $Arguments) {
    $script:NativeStarts++
    $script:LastTransportArguments = $Arguments
    $dockerAt = [array]::IndexOf($Arguments, 'docker')
    if ($dockerAt -ge 0) {
        $first = $dockerAt + 3
        if ($Arguments[$first - 1] -ne 'unix:///var/run/docker.sock') { throw 'Explicit local socket missing.' }
        $forward = $Arguments[$first..($Arguments.Length - 1)]
    } else {
        if ($Arguments[-1] -ne '-s' -or $Arguments -notcontains 'bash' -or $Arguments -notcontains 'timeout') { throw 'Unexpected shell transport.' }
        $forward = @()
    }
    $start = [Diagnostics.ProcessStartInfo]::new($python)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    foreach ($word in (@('-I', '-B', $fixture, $script:FixtureMode) + $script:FixtureExtra + $forward)) { $start.ArgumentList.Add($word) }
    $start
}
function Invoke-HarnessWslClient($StartInfo, [string] $InputText = '', [double] $TimeoutSeconds,
    [switch] $CaptureOutput, [IO.TextWriter] $StandardOutputWriter, [IO.TextWriter] $StandardErrorWriter) {
    $script:LastClientBudget = $TimeoutSeconds
    if ($script:FixtureDeadline -gt 0) { $TimeoutSeconds = [Math]::Min($TimeoutSeconds, $script:FixtureDeadline) }
    if ($script:FixtureMode -in 'unicode_stdout', 'unicode_stderr') {
        $target = $script:FixtureMode.Replace('unicode_', '')
        $script:BoundaryEvents = [Collections.Generic.List[string]]::new()
        $script:BoundaryOut = [WslBoundaryWriter]::new($StandardOutputWriter, 'stdout', $target, $script:FixtureExtra[0], $script:FixtureExtra[1], $script:BoundaryEvents)
        $script:BoundaryError = [WslBoundaryWriter]::new($StandardErrorWriter, 'stderr', $target, $script:FixtureExtra[0], $script:FixtureExtra[1], $script:BoundaryEvents)
        $StandardOutputWriter = $script:BoundaryOut; $StandardErrorWriter = $script:BoundaryError
    }
    & $script:ActualClient -StartInfo $StartInfo -InputText $InputText -TimeoutSeconds $TimeoutSeconds -CaptureOutput:$CaptureOutput -StandardOutputWriter $StandardOutputWriter -StandardErrorWriter $StandardErrorWriter
}
$count = 0
function Assert-ReleaseTransport([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
    $script:count++
}
$oldError = [Console]::Error; $errors = [IO.StringWriter]::new()
try {
    [Console]::SetError($errors)
    $json = Invoke-LocalDockerJson image inspect 'name with spaces,"quotes"' --format '{{json .Config.Labels}}'
    Assert-ReleaseTransport ($json.ok -and $json.values.Count -eq 10000 -and $json.argv[2] -ceq 'name with spaces,"quotes"') 'JSON/argv output was truncated or polluted.'
    Assert-ReleaseTransport ($errors.ToString() -ceq (('E' * 262144) + "`nJSON_STDERR_END`n")) 'JSON stderr was lost or mixed into stdout.'
    Assert-ReleaseTransport ($LASTEXITCODE -eq 0) 'Successful native LASTEXITCODE changed.'
    $secret = [guid]::NewGuid().ToString('N')
    $sql = $secret + ('x' * 524288)
    $text = @(Invoke-LocalDockerInput -InputText $sql -DockerArguments @('exec', '-i', 'owned-id', 'psql', '-F', '|')) -join "`n"
    $state = $text | ConvertFrom-Json
    Assert-ReleaseTransport ($state.inputBytes -eq [Text.Encoding]::UTF8.GetByteCount($sql) -and $state.argv[1] -eq '-i' -and $state.argv[-1] -eq '|') 'Text stdin/SQL argv was changed.'
    Assert-ReleaseTransport (-not (($script:LastTransportArguments -join ' ') + $text + $errors.ToString()).Contains($secret)) 'Input secret escaped into argv/output.'
} finally { [Console]::SetError($oldError); $errors.Dispose() }

$script:FixtureMode = 'duplex'
$combined = Join-Path $folder 'combined.log'
$code = Invoke-LocalDockerToFile $combined run fixture
$raw = [IO.File]::ReadAllBytes($combined); $log = [Text.Encoding]::UTF8.GetString($raw)
Assert-ReleaseTransport ($code -eq 0 -and $LASTEXITCODE -eq 0) 'Combined log exit changed.'
$markers = @("`nSTDOUT_END`n", "`nSTDERR_END`n", "INPUT_BYTES=0`n")
$payloadOnly = $log
foreach ($marker in $markers) {
    Assert-ReleaseTransport ([regex]::Matches($log, [regex]::Escape($marker)).Count -eq 1) 'Combined stream marker missing/duplicated.'
    $payloadOnly = $payloadOnly.Replace($marker, '')
}
Assert-ReleaseTransport (($payloadOnly -replace 'E', '').Length -eq 262144 -and ($payloadOnly -replace 'O', '').Length -eq 262144 -and $payloadOnly -cmatch '^[OE]+$') 'Concurrent combined bytes were lost or duplicated.'
$expectedLength = 524288 + [Text.Encoding]::UTF8.GetByteCount(($markers -join ''))
Assert-ReleaseTransport ($raw.Length -eq $expectedLength -and -not ($raw[0] -eq 239 -and $raw[1] -eq 187 -and $raw[2] -eq 191)) 'Combined UTF8 bytes/BOM changed.'
$before = (Get-FileHash -LiteralPath $combined -Algorithm SHA256).Hash; $starts = $script:NativeStarts
$refused = $false
try { Invoke-LocalDockerToFile $combined run fixture | Out-Null } catch { $refused = $true }
Assert-ReleaseTransport ($refused -and $script:NativeStarts -eq $starts -and (Get-FileHash -LiteralPath $combined -Algorithm SHA256).Hash -eq $before) 'CreateNew log collision started a process or overwrote evidence.'

foreach ($target in 'stdout', 'stderr') {
    $script:FixtureMode = 'unicode_' + $target
    $unicode = Join-Path $folder ($script:FixtureMode + '.log')
    $script:FixtureExtra = @((Join-Path $folder ($target + '.signal')), (Join-Path $folder ($target + '.ack')))
    $unicodeCode = Invoke-LocalDockerToFile $unicode run fixture
    $unicodeText = [Text.UTF8Encoding]::new($false, $true).GetString([IO.File]::ReadAllBytes($unicode))
    $prefix = if ($target -eq 'stdout') { 'A' } else { 'B' }
    $expected = ($prefix * 8191) + "BETWEEN_OTHER`n" + [char]::ConvertFromUtf32(0x1f600) + "TARGET_END`n"
    [ordered]@{ events = $script:BoundaryEvents.ToArray(); stdoutTrailingHigh = $script:BoundaryOut.SawTrailingHigh; stderrTrailingHigh = $script:BoundaryError.SawTrailingHigh } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $folder ($target + '-unicode-events.json'))
    Assert-ReleaseTransport ($unicodeCode -eq 0 -and $unicodeText -ceq $expected) 'Supplementary Unicode was corrupted across interleaved8192-char pipe reads.'
    Assert-ReleaseTransport (-not $script:BoundaryOut.SawTrailingHigh -and -not $script:BoundaryError.SawTrailingHigh) 'An incomplete scalar reached the shared UTF8 writer.'
}
$script:FixtureExtra = @()

$script:FixtureMode = 'early7'
$found = Test-LocalDocker container inspect absent
Assert-ReleaseTransport (-not $found -and $LASTEXITCODE -eq 7) 'Ordinary nonzero existence result changed.'
$script:FixtureMode = 'exit124'
$refused = $false
try { Test-LocalDocker container inspect unknown | Out-Null } catch { $refused = $_.Exception.Message.Contains('deadline') }
Assert-ReleaseTransport ($refused -and $LASTEXITCODE -eq 124) 'Guest timeout was treated as a free resource name.'
foreach ($wrapperCode in 125, 126, 127) {
    $script:FixtureMode = 'exit' + $wrapperCode
    $refused = $false
    try { Assert-DockerNamesFree -Container @('unknown') } catch { $refused = $_.Exception.Message.Contains('transport') }
    Assert-ReleaseTransport ($refused -and $LASTEXITCODE -eq $wrapperCode) "GNU wrapper exit $wrapperCode was treated as a free name or lost its native exit."
}

$script:FixtureMode = 'early7'; $errors = [IO.StringWriter]::new(); $oldError = [Console]::Error
try {
    [Console]::SetError($errors); $message = ''
    try { Invoke-LocalDockerInput -InputText $sql -DockerArguments @('exec', '-i', 'owned-id', 'psql') | Out-Null } catch { $message = $_.Exception.Message }
    Assert-ReleaseTransport ($message.Contains('transport') -and $LASTEXITCODE -eq 7 -and -not ($message + $errors.ToString()).Contains($secret)) 'Early input rejection lost native exit or exposed input.'
} finally { [Console]::SetError($oldError); $errors.Dispose() }

$script:FixtureMode = 'duplex'
$oldOut = [Console]::Out; $oldError = [Console]::Error; $shellOut = [IO.StringWriter]::new(); $shellError = [IO.StringWriter]::new()
try {
    [Console]::SetOut($shellOut); [Console]::SetError($shellError)
    $shellCode = Invoke-LocalShell -Script $secret
    Assert-ReleaseTransport ($shellCode -is [int] -and $shellCode -eq 0 -and $LASTEXITCODE -eq 0) 'Release shell stdout entered its exit-code return.'
    Assert-ReleaseTransport ($shellOut.ToString().Contains("STDOUT_END`n") -and $shellError.ToString().Contains("STDERR_END`n") -and -not (($script:LastTransportArguments -join ' ') + $shellOut.ToString() + $shellError.ToString()).Contains($secret)) 'Release shell input/stream separation changed.'
} finally { [Console]::SetOut($oldOut); [Console]::SetError($oldError); $shellOut.Dispose(); $shellError.Dispose() }

$script:FixtureMode = 'delay'; $script:FixtureDeadline = 0.4
$clock = [Diagnostics.Stopwatch]::StartNew(); $refused = $false
try { Test-LocalDocker container inspect unknown | Out-Null } catch { $refused = $_.Exception.Message.Contains('unconfirmed') }
Assert-ReleaseTransport ($refused -and $clock.Elapsed.TotalSeconds -lt 2) 'Owned-client timeout became false or exceeded its bound.'
# The child must print before the deadline; allow for interpreter start-up on a loaded host (the fixture then waits 3 s).
$script:FixtureMode = 'partial_delay'; $script:FixtureDeadline = 2; $partial = Join-Path $folder 'partial.log'; $refused = $false
try { Invoke-LocalDockerToFile $partial run fixture | Out-Null } catch { $refused = $_.Exception.Message.Contains('unconfirmed') }
$script:FixtureDeadline = 0.4
$partialText = Get-Content -LiteralPath $partial -Raw
Assert-ReleaseTransport ($refused -and $partialText.Contains("OUT_BEFORE_TIMEOUT`n") -and $partialText.Contains("ERR_BEFORE_TIMEOUT`n")) 'Failed transport lost its available combined output.'
$script:FixtureMode = 'delay'
$clock.Restart(); $refused = $false
try { Wait-LocalCondition -Attempts 10 -DelaySeconds 0 -Message 'fixture wait' -Condition { Test-LocalDocker container inspect unknown } } catch { $refused = $_.Exception.Message.Contains('unconfirmed') }
Assert-ReleaseTransport ($refused -and $script:LastClientBudget -le 10 -and $clock.Elapsed.TotalSeconds -lt 2 -and $script:ReleaseLoopBudget -eq $null) 'Loop budget did not cap native calls or restore caller state.'

# Deadline covers two interpreter starts inside a fully parallel static lane; the descendant holds the pipe for 15 s,
# so a drain that waited for it would take far longer than the 10 s bound below.
$script:FixtureMode = 'inherited_pipe'; $script:FixtureDeadline = 4; $script:FixtureExtra = @((Join-Path $folder 'retained.pid'))
$clock.Restart(); $refused = $false
try { Test-LocalDocker container inspect unknown | Out-Null } catch { $refused = $_.Exception.Message.Contains('unconfirmed') }
Assert-ReleaseTransport ($refused -and $clock.Elapsed.TotalSeconds -lt 10) "Inherited drain timeout was treated as absence (refused $refused, $([math]::Round($clock.Elapsed.TotalSeconds, 1)) s)."
# The fixture can still be starting under host load when the bounded drain gives up; wait for its pid file.
$clock.Restart()
while (-not (Test-Path -LiteralPath $script:FixtureExtra[0]) -and $clock.Elapsed.TotalSeconds -lt 10) { Start-Sleep -Milliseconds 25 }
$childId = [int] (Get-Content -LiteralPath $script:FixtureExtra[0] -Raw)
Assert-ReleaseTransport ([bool] (Get-Process -Id $childId -ErrorAction SilentlyContinue)) 'A descendant tree was killed.'
# Fixture cleanup: stop only the descendant this test started (it would self-exit after its 15 s hold).
Stop-Process -Id $childId -Force -ErrorAction SilentlyContinue
$script:FixtureExtra = @(); $script:FixtureDeadline = 0; $script:FixtureMode = 'early7'
$oldSeconds = $script:ReleaseStepSeconds; $oldClock = $script:ReleaseStepClock
try {
    $script:ReleaseStepSeconds = 0.01; $script:ReleaseStepClock = [Diagnostics.Stopwatch]::StartNew()
    Start-Sleep -Milliseconds 20; $starts = $script:NativeStarts; $refused = $false
    try { Test-LocalDocker container inspect unknown | Out-Null } catch { $refused = $_.Exception.Message.Contains('deadline') }
    Assert-ReleaseTransport ($refused -and $script:NativeStarts -eq $starts) 'Expired total step started another native call.'
} finally { $script:ReleaseStepSeconds = $oldSeconds; $script:ReleaseStepClock = $oldClock }
Assert-ReleaseTransport ($script:ReleaseLoopBudget -eq $null) 'Loop deadline leaked into caller state.'
[ordered]@{ state = 'passed'; checks = $count; noWslDocker = $true; secretExposed = $false } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $folder 'summary.json')
Write-Host "$count native release transport checks passed. Evidence: $folder"
