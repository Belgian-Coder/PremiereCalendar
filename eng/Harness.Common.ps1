# Shared only by the local development helpers; dot-source without running WSL.
. (Join-Path $PSScriptRoot 'Wsl.Transport.ps1')
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
        SdkCacheSubdirectory = '.sdk-cache'
        DotnetCliHome = '/build/.sdk-cache/dotnet'
        NugetPluginsCache = '/build/.sdk-cache/nuget-plugins'
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

function ConvertTo-WslPath([string] $Path,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]*$')][string] $WslDistribution = 'Ubuntu-24.04') {
    Assert-HarnessWslRunning $WslDistribution
    $wslArguments = @('-d', $WslDistribution, '--', 'wslpath', '-a', ($Path -replace '\\', '/'))
    $result = Invoke-HarnessWslClient (New-HarnessWslStartInfo $wslArguments) -CaptureOutput
    if ($result.TimedOut -or -not $result.OutputDrained -or $result.InputFailed -or $result.ExitCode -ne 0 -or [string]::IsNullOrWhiteSpace($result.Output)) { throw "Could not resolve WSL path within its bound: $Path" }
    $result.Output.Trim()
}
