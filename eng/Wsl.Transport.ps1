# Shared text-only WSL transport. No Git/cache/path helpers or binary restore.
function New-HarnessWslStartInfo([string[]] $Arguments, [switch] $UnicodeOutput) {
    $command = Get-Command wsl.exe -CommandType Application -ErrorAction Stop | Select-Object -First 1
    $start = [Diagnostics.ProcessStartInfo]::new($command.Source)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardInput = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
    $start.StandardOutputEncoding = if ($UnicodeOutput) { [Text.Encoding]::Unicode } else { [Text.UTF8Encoding]::new($false) }
    $start.StandardErrorEncoding = [Text.UTF8Encoding]::new($false)
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $start
}

# One owned Windows client, not a WSL process-tree or distro owner. Read both
# pipes before writing stdin; forward chunks only on this PowerShell thread.
function Invoke-HarnessWslClient([Diagnostics.ProcessStartInfo] $StartInfo, [string] $InputText = '',
    [double] $TimeoutSeconds = 5, [switch] $CaptureOutput,
    [IO.TextWriter] $StandardOutputWriter, [IO.TextWriter] $StandardErrorWriter) {
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $StartInfo
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $output = [Text.StringBuilder]::new()
    $stdoutBuffer = [char[]]::new(8192)
    $stderrBuffer = [char[]]::new(8192)
    $pendingHigh = @{ stdout = ''; stderr = '' }
    $stdoutDone = $false; $stderrDone = $false; $inputClosed = $false
    $inputFailed = $false; $timedOut = $false; $started = $false
    try {
        if (-not $process.Start()) { throw 'Could not start the owned WSL client.' }
        $started = $true
        $stdout = $process.StandardOutput.ReadAsync($stdoutBuffer, 0, $stdoutBuffer.Length)
        $stderr = $process.StandardError.ReadAsync($stderrBuffer, 0, $stderrBuffer.Length)
        $inputBytes = [Text.Encoding]::UTF8.GetBytes($InputText)
        $inputTask = $process.StandardInput.BaseStream.WriteAsync($inputBytes, 0, $inputBytes.Length)
        while ($true) {
            if (-not $inputClosed -and $inputTask.IsCompleted) {
                try { $null = $inputTask.GetAwaiter().GetResult() }
                catch { $inputFailed = $true }
                finally { $process.StandardInput.BaseStream.Dispose(); $inputClosed = $true }
            }
            foreach ($side in 'stdout', 'stderr') {
                $done = if ($side -eq 'stdout') { $stdoutDone } else { $stderrDone }
                $task = if ($side -eq 'stdout') { $stdout } else { $stderr }
                if ($done -or -not $task.IsCompleted) { continue }
                $length = $task.GetAwaiter().GetResult()
                if ($length -eq 0) {
                    if ($side -eq 'stdout') { $stdoutDone = $true } else { $stderrDone = $true }
                    if ($pendingHigh[$side].Length -eq 0) { continue }
                    $chunk = [string][char]0xfffd
                    $pendingHigh[$side] = ''
                } else {
                    $buffer = if ($side -eq 'stdout') { $stdoutBuffer } else { $stderrBuffer }
                    $chunk = [string]::new($buffer, 0, $length)
                    # ReadAsync counts UTF16 chars, not scalars. Keep a trailing
                    # high surrogate on its own pipe, never in a shared writer's
                    # encoder where the other pipe could interrupt its pair.
                    if ($pendingHigh[$side].Length -gt 0) {
                        $leading = if ([char]::IsLowSurrogate($chunk[0])) { $pendingHigh[$side] } else { [string][char]0xfffd }
                        $chunk = $leading + $chunk
                        $pendingHigh[$side] = ''
                    }
                    if ([char]::IsHighSurrogate($chunk[$chunk.Length - 1])) {
                        $pendingHigh[$side] = [string]$chunk[$chunk.Length - 1]
                        $chunk = $chunk.Substring(0, $chunk.Length - 1)
                    }
                }
                if ($side -eq 'stdout') {
                    if ($CaptureOutput) { [void] $output.Append($chunk) }
                    if ($null -ne $StandardOutputWriter) { $StandardOutputWriter.Write($chunk) }
                    elseif (-not $CaptureOutput) { [Console]::Out.Write($chunk) }
                } elseif ($null -ne $StandardErrorWriter) { $StandardErrorWriter.Write($chunk) }
                elseif (-not $CaptureOutput) { [Console]::Error.Write($chunk) }
                if ($length -gt 0) {
                    if ($side -eq 'stdout') { $stdout = $process.StandardOutput.ReadAsync($stdoutBuffer, 0, $stdoutBuffer.Length) }
                    else { $stderr = $process.StandardError.ReadAsync($stderrBuffer, 0, $stderrBuffer.Length) }
                }
            }
            if ($process.HasExited -and $stdoutDone -and $stderrDone -and $inputClosed) { break }
            if ($clock.Elapsed.TotalSeconds -ge $TimeoutSeconds) { $timedOut = $true; break }
            [Threading.Thread]::Sleep(10)
        }
        $exitCode = if ($process.HasExited) { $process.ExitCode } else { $null }
        [pscustomobject]@{
            ExitCode = $exitCode; Output = $output.ToString(); TimedOut = $timedOut
            InputFailed = $inputFailed; OutputDrained = $stdoutDone -and $stderrDone
            ClientPid = $process.Id; ClientExited = $process.HasExited
        }
    } finally {
        if ($started) {
            # Closing the pipe must not synchronously flush a pending write.
            if (-not $inputClosed) { try { $process.StandardInput.BaseStream.Dispose() } catch { } }
            if (-not $process.HasExited) {
                try { $process.Kill() }
                catch { [Console]::Error.WriteLine("Owned WSL client $($process.Id) could not be stopped.") }
                [Console]::Error.WriteLine("WSL client $($process.Id) did not finish normally. Windows descendants and Linux resources may remain; cleanup is unconfirmed.")
            }
            # Kill only this verified Process handle. wslhost/conhost and Linux
            # descendants may be shared or retained; never Kill(entireTree).
            try { $process.StandardOutput.Dispose() } catch { }
            try { $process.StandardError.Dispose() } catch { }
        }
        $process.Dispose()
    }
}

function Assert-WslTransportCompletion($Result, [double] $TimeoutSeconds) {
    if ($Result.TimedOut -or -not $Result.OutputDrained) {
        throw "WSL client $($Result.ClientPid) did not complete input/process/output within ${TimeoutSeconds}s. Windows descendants and Linux resources may remain; cleanup is unconfirmed."
    }
    if ($Result.InputFailed -and $Result.ExitCode -eq 0) { throw "WSL client $($Result.ClientPid) did not consume its input; a zero exit is not accepted." }
    if ($Result.InputFailed) { [Console]::Error.WriteLine("WSL client $($Result.ClientPid) exited $($Result.ExitCode) before consuming its input; guest cleanup is unconfirmed.") }
    $global:LASTEXITCODE = [int] $Result.ExitCode
}

function Assert-HarnessWslRunning([string] $WslDistribution, [double] $TimeoutSeconds = 5) {
    # Listing does not start a distribution. This observed precondition avoids
    # intentionally booting a stopped environment; it is not an atomic WSL lock.
    $result = Invoke-HarnessWslClient (New-HarnessWslStartInfo @('--list', '--running', '--quiet') -UnicodeOutput) -TimeoutSeconds $TimeoutSeconds -CaptureOutput
    if ($result.TimedOut -or -not $result.OutputDrained -or $result.InputFailed -or $result.ExitCode -ne 0) { throw 'Could not inspect running WSL distributions within its bound. Check wsl.exe --list --running --quiet before retrying.' }
    $names = @($result.Output -split '\r?\n' | ForEach-Object { ($_ -replace "`0", '').Trim() })
    if ($names -notcontains $WslDistribution) {
        throw "WSL distribution '$WslDistribution' must already be running; helpers never start it."
    }
}

function Invoke-HarnessWsl([string] $Script, [switch] $ReturnExitCode,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')][string] $WslDistribution = 'Ubuntu-24.04',
    [ValidateRange(26, 86400)][int] $TimeoutSeconds = 1800,
    [ValidateRange(15, 120)][int] $CleanupGraceSeconds = 15) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    Assert-HarnessWslRunning $WslDistribution -TimeoutSeconds ([Math]::Min(5, $TimeoutSeconds))
    # stdin avoids the Windows -> bash command-line quoting layer.
    # An inherited Docker context must never redirect local checks to the NAS.
    $localEngine = "unset DOCKER_CONTEXT; export DOCKER_HOST=unix:///var/run/docker.sock`n"
    # Guest execution, the selected TERM cleanup grace, and final pipe drain (5s) fit
    # inside the caller's total; a killed Windows client proves no guest cleanup.
    $remaining = $TimeoutSeconds - $clock.Elapsed.TotalSeconds
    $cleanupAndDrainSeconds = $CleanupGraceSeconds + 5
    if ($remaining -lt ($cleanupAndDrainSeconds + 1)) { throw 'Insufficient total deadline remains for guest execution, cleanup and drain.' }
    $guestSeconds = [Math]::Floor($remaining) - $cleanupAndDrainSeconds
    $wslArguments = @('-d', $WslDistribution, '--exec', 'timeout', '--signal=TERM', "--kill-after=${CleanupGraceSeconds}s", "${guestSeconds}s", 'bash', '-s')
    $inputText = (($localEngine + $Script -replace "`r`n", "`n").TrimEnd() + "`n# end of script`n")
    $result = Invoke-HarnessWslClient (New-HarnessWslStartInfo $wslArguments) -InputText $inputText -TimeoutSeconds $remaining
    Assert-WslTransportCompletion $result $TimeoutSeconds
    # -ReturnExitCode lets a caller report its own failure (for example failed tests) after printing a summary.
    if ($ReturnExitCode) { return [int] $result.ExitCode }
    if ($result.ExitCode -ne 0) { throw "WSL command failed (exit $($result.ExitCode)); inspect resource cleanup evidence. Docker must already be running." }
}
