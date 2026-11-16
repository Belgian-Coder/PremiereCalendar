# Shared only by the local development helpers; dot-source without running WSL.
function ConvertTo-BashLiteral([string] $Value) {
    "'" + $Value.Replace("'", "'\''") + "'"
}

function Invoke-HarnessGitMetadata([string] $Path, [string[]] $Arguments) {
    $gitCommand = Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1
    $start = [Diagnostics.ProcessStartInfo]::new($gitCommand.Source)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($name in @($start.Environment.Keys)) {
        if ($name -like 'GIT_*') { $null = $start.Environment.Remove($name) }
    }
    $start.Environment['GIT_OPTIONAL_LOCKS'] = '0'
    $start.Environment['GIT_TERMINAL_PROMPT'] = '0'
    foreach ($argument in (@('--no-optional-locks', '-C', $Path) + $Arguments)) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw 'Could not read Git metadata.' }
        $output = $process.StandardOutput.ReadToEndAsync()
        $errors = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(30000)) { $process.Kill(); throw 'Git metadata read exceeded its bound.' }
        $text = $output.GetAwaiter().GetResult()
        $null = $errors.GetAwaiter().GetResult()
        [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $text }
    } finally { $process.Dispose() }
}

function Get-HarnessGitScalar([string] $Path, [string[]] $Arguments) {
    $result = Invoke-HarnessGitMetadata $Path $Arguments
    $text = $result.Output -replace '\r?\n$', ''
    if ($result.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($text) -or $text -match '[\r\n\x00]') { throw 'Expected one scalar Git metadata value from the selected checkout.' }
    $text
}

function Assert-HarnessCanonicalPath([string] $Path, [switch] $Directory) {
    if (-not [IO.Path]::IsPathFullyQualified($Path) -or ($IsWindows -and $Path -notmatch '^[A-Za-z]:[\\/]')) {
        throw 'The cache-root route requires an absolute native local path.'
    }
    $full = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    $native = $Path.Replace([IO.Path]::AltDirectorySeparatorChar, [IO.Path]::DirectorySeparatorChar).TrimEnd('\', '/')
    if (-not [string]::Equals($native, $full, [StringComparison]::OrdinalIgnoreCase)) { throw 'Noncanonical cache-root path refused.' }
    $item = Get-Item -LiteralPath $full -Force -ErrorAction Stop
    if ($Directory -and -not $item.PSIsContainer) { throw 'The cache-root path must be an existing directory.' }
    $cursor = $full
    while ($cursor) {
        if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'Linked cache-root paths are refused.'
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
    $full
}

function Get-HarnessPrimaryRoot([string] $Root, [string] $GitPrimaryRoot) {
    $hint = [Environment]::GetEnvironmentVariable('BUILD_RELEASE_PROJECT_ROOT')
    if ($null -eq $hint) { return $GitPrimaryRoot }
    $primary = Assert-HarnessCanonicalPath $hint -Directory
    $top = Get-HarnessGitScalar $primary @('rev-parse', '--show-toplevel')
    if (-not [string]::Equals([IO.Path]::GetFullPath($top), $primary, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Cache-root hint is not the exact native Git primary checkout.'
    }
    $null = Assert-HarnessCanonicalPath (Join-Path $primary '.git') -Directory
    $primaryCommon = Get-HarnessGitScalar $primary @('rev-parse', '--path-format=absolute', '--git-common-dir')
    if (-not [string]::Equals([IO.Path]::GetFullPath($primaryCommon), (Join-Path $primary '.git'), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Cache-root hint must name the canonical primary, not another linked checkout.'
    }
    if ([string]::Equals($primary, $GitPrimaryRoot, [StringComparison]::OrdinalIgnoreCase)) { return $primary }

    # An independent transport clone may share dependencies only through its
    # exact protocol owner/path/origin binding. Never adopt or silently fall back.
    $checkout = Assert-HarnessCanonicalPath $Root -Directory
    $null = Assert-HarnessCanonicalPath (Join-Path $checkout '.git') -Directory
    $cloneTop = Get-HarnessGitScalar $checkout @('rev-parse', '--show-toplevel')
    if (-not [string]::Equals([IO.Path]::GetFullPath($cloneTop), $checkout, [StringComparison]::OrdinalIgnoreCase)) { throw 'Transport clone is not an exact Git checkout.' }
    $markerPath = Assert-HarnessCanonicalPath (Join-Path $checkout '.request-release-owner.json')
    if ((Get-Item -LiteralPath $markerPath).PSIsContainer) { throw 'Transport owner marker must be a file.' }
    $marker = Get-Content -LiteralPath $markerPath -Raw | ConvertFrom-Json -AsHashtable
    $manifestPath = Assert-HarnessCanonicalPath (Join-Path $checkout 'eng/release-targets.json')
    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json -AsHashtable
    if (($marker.Keys | Sort-Object) -join ',' -cne 'owner,project,root,target,version' -or
        $marker.owner -isnot [string] -or $marker.project -isnot [string] -or $marker.root -isnot [string] -or
        $marker.owner -cne 'request-release' -or $marker.version.GetType() -notin @([int], [long]) -or $marker.version -ne 1 -or
        $marker.project -cne $manifest.project -or $marker.root -cne $primary -or
        $marker.target -isnot [string] -or -not $manifest.targets.ContainsKey($marker.target)) { throw 'Invalid or foreign transport owner marker.' }
    $worktrees = $manifest.worktreeDirectory
    if ($worktrees -isnot [string] -or $worktrees -notmatch '^[A-Za-z0-9_./-]+$' -or
        @($worktrees.Split('/') | Where-Object { $_ -in '', '.', '..' }).Count -gt 0 -or [IO.Path]::IsPathRooted($worktrees)) { throw 'Invalid transport worktree directory.' }
    $expected = [IO.Path]::GetFullPath((Join-Path (Join-Path $primary $worktrees) ('release-' + $marker.target)))
    if (-not $expected.StartsWith($primary + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        -not [string]::Equals($checkout, $expected, [StringComparison]::OrdinalIgnoreCase)) { throw 'Transport clone is outside its exact protocol path.' }
    $origins = foreach ($path in @($primary, $checkout)) {
        $result = Invoke-HarnessGitMetadata $path @('config', '--local', '--null', '--get-all', 'remote.origin.url')
        if ($result.ExitCode -ne 0 -or -not $result.Output.EndsWith("`0")) { throw 'A local project origin is required.' }
        $values = @($result.Output.Substring(0, $result.Output.Length - 1).Split([char]0))
        if ($values.Count -ne 1 -or $values[0] -isnot [string] -or [string]::IsNullOrWhiteSpace($values[0]) -or
            $values[0] -match '[\x00-\x20]' -or $values[0] -match '://[^/]*@') { throw 'Exactly one credential-free local project origin is required.' }
        $values[0]
    }
    if ($origins[0] -cne $origins[1]) { throw 'Transport clone and canonical primary origins differ.' }
    $primary
}

function Get-HarnessContext([string] $Root) {
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $common = Get-HarnessGitScalar $rootPath @('rev-parse', '--git-common-dir')
    $commonPath = if ([IO.Path]::IsPathRooted($common)) { [IO.Path]::GetFullPath($common) } else { [IO.Path]::GetFullPath((Join-Path $rootPath $common)) }
    $primaryRoot = Get-HarnessPrimaryRoot $rootPath (Split-Path -Parent $commonPath)
    $suffix = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes($rootPath.ToLowerInvariant()))).Substring(0, 10).ToLowerInvariant()
    $projectSuffix = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes($primaryRoot.ToLowerInvariant()))).Substring(0, 10).ToLowerInvariant()
    [pscustomobject]@{
        Root = $rootPath
        PrimaryRoot = $primaryRoot
        ProjectNamespace = "pc-$projectSuffix"
        WorktreeNamespace = $suffix
        BuildRoot = Join-Path $rootPath 'artifacts/build/linux'
        PackageRoot = Join-Path $primaryRoot 'artifacts/packages/linux'
        ComposeProject = if ($rootPath -eq $primaryRoot) { 'premierecalendar-dev' } else { "premierecalendar-dev-$suffix" }
    }
}

function Get-HarnessStorage($Context, [ValidateSet('musl', 'glibc')][string] $CacheFlavor = 'musl',
    [switch] $NoRuntimeAsync, [switch] $Watcher, [string] $BuildVolume, [string] $NugetVolume) {
    $mode = if ($NoRuntimeAsync) { 'sync' } else { 'async' }
    $lane = if ($Watcher) { 'watch' } else { 'check' }
    [pscustomobject]@{
        BuildVolume = if ($BuildVolume) { $BuildVolume } else { "$($Context.ProjectNamespace)-$($Context.WorktreeNamespace)-linux-amd64-$CacheFlavor-$mode-$lane" }
        NugetVolume = if ($NugetVolume) { $NugetVolume } else { "$($Context.ProjectNamespace)-linux-amd64-nuget" }
    }
}

# Emitted into the same shell as resource creation. Fail closed before creating
# volumes/networks/containers; use the inspected immutable image ID thereafter.
function Get-HarnessPreflight([string] $Image, [string] $ImageVariable = 'dev_image') {
    $engine = @'
engine=$(docker info --format '{{.OSType}}/{{.Architecture}}') || { echo 'Local Docker must already be running; helpers never start it.' >&2; exit 25; }
case "$engine" in linux/amd64|linux/x86_64) ;; *) echo "Expected local linux/amd64 Docker, got $engine" >&2; exit 26 ;; esac
'@
    if (-not $Image) { return $engine }
    $engine + "`n" + @'
image_record=$(docker image inspect --format '{{.Os}}/{{.Architecture}} {{.Id}}' @@IMAGE@@) || { echo 'Required local image is unavailable; helpers never pull it.' >&2; exit 20; }
case "$image_record" in linux/amd64\ sha256:*) ;; *) echo "Expected a local linux/amd64 image identity, got $image_record" >&2; exit 27 ;; esac
@@VARIABLE@@=${image_record#* }
case "$@@VARIABLE@@" in sha256:????????????????????????????????????????????????????????????????) ;; *) echo 'Invalid local image identity.' >&2; exit 27 ;; esac
image_hash=${@@VARIABLE@@#sha256:}
case "$image_hash" in *[!0-9a-f]*) echo 'Invalid local image identity.' >&2; exit 27 ;; esac
'@.Replace('@@IMAGE@@', (ConvertTo-BashLiteral $Image)).Replace('@@VARIABLE@@', $ImageVariable)
}

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
    [double] $TimeoutSeconds = 5, [switch] $CaptureOutput) {
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $StartInfo
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $output = [Text.StringBuilder]::new()
    $stdoutBuffer = [char[]]::new(8192)
    $stderrBuffer = [char[]]::new(8192)
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
                    continue
                }
                $buffer = if ($side -eq 'stdout') { $stdoutBuffer } else { $stderrBuffer }
                $chunk = [string]::new($buffer, 0, $length)
                if ($CaptureOutput) {
                    if ($side -eq 'stdout') { [void] $output.Append($chunk) }
                } elseif ($side -eq 'stdout') { [Console]::Out.Write($chunk) }
                else { [Console]::Error.Write($chunk) }
                if ($side -eq 'stdout') { $stdout = $process.StandardOutput.ReadAsync($stdoutBuffer, 0, $stdoutBuffer.Length) }
                else { $stderr = $process.StandardError.ReadAsync($stderrBuffer, 0, $stderrBuffer.Length) }
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

function ConvertTo-WslPath([string] $Path,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')][string] $WslDistribution = 'Ubuntu-24.04') {
    Assert-HarnessWslRunning $WslDistribution
    $wslArguments = @('-d', $WslDistribution, '--', 'wslpath', '-a', ($Path -replace '\\', '/'))
    $result = Invoke-HarnessWslClient (New-HarnessWslStartInfo $wslArguments) -CaptureOutput
    if ($result.TimedOut -or -not $result.OutputDrained -or $result.InputFailed -or $result.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($result.Output)) { throw "Could not resolve WSL path within its bound: $Path" }
    $result.Output.Trim()
}

function Invoke-HarnessWsl([string] $Script, [switch] $ReturnExitCode,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')][string] $WslDistribution = 'Ubuntu-24.04',
    [ValidateRange(26, 86400)][int] $TimeoutSeconds = 1800) {
    $clock = [Diagnostics.Stopwatch]::StartNew()
    Assert-HarnessWslRunning $WslDistribution -TimeoutSeconds ([Math]::Min(5, $TimeoutSeconds))
    # stdin avoids the Windows -> bash command-line quoting layer.
    # An inherited Docker context must never redirect local checks to the NAS.
    $localEngine = "unset DOCKER_CONTEXT; export DOCKER_HOST=unix:///var/run/docker.sock`n"
    # Guest execution, TERM cleanup (15s), and final pipe drain (5s) all fit
    # inside the caller's total; a killed Windows client proves no guest cleanup.
    $remaining = $TimeoutSeconds - $clock.Elapsed.TotalSeconds
    if ($remaining -lt 21) { throw 'Insufficient total deadline remains for guest execution, cleanup and drain.' }
    $guestSeconds = [Math]::Floor($remaining) - 20
    $wslArguments = @('-d', $WslDistribution, '--exec', 'timeout', '--signal=TERM', '--kill-after=15s', "${guestSeconds}s", 'bash', '-s')
    $inputText = (($localEngine + $Script -replace "`r`n", "`n").TrimEnd() + "`n# end of script`n")
    $result = Invoke-HarnessWslClient (New-HarnessWslStartInfo $wslArguments) -InputText $inputText -TimeoutSeconds $remaining
    if ($result.TimedOut -or -not $result.OutputDrained) {
        throw "WSL client $($result.ClientPid) did not complete input/process/output within ${TimeoutSeconds}s. Windows descendants and Linux resources may remain; cleanup is unconfirmed."
    }
    if ($result.InputFailed -and $result.ExitCode -eq 0) { throw "WSL client $($result.ClientPid) did not consume its input; a zero exit is not accepted." }
    if ($result.InputFailed) { [Console]::Error.WriteLine("WSL client $($result.ClientPid) exited $($result.ExitCode) before consuming its input; guest cleanup is unconfirmed.") }
    $global:LASTEXITCODE = [int] $result.ExitCode
    # -ReturnExitCode lets a caller report its own failure (for example failed tests) after printing a summary.
    if ($ReturnExitCode) { return [int] $result.ExitCode }
    if ($result.ExitCode -ne 0) { throw "WSL command failed (exit $($result.ExitCode)); inspect resource cleanup evidence. Docker must already be running." }
}
