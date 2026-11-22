# Shared only by the local development helpers; dot-source without running WSL.
. (Join-Path $PSScriptRoot 'Wsl.Transport.ps1')
function ConvertTo-BashLiteral([string] $Value) {
    "'" + $Value.Replace("'", "'\''") + "'"
}

function Invoke-HarnessGitMetadata([string] $Path, [string[]] $Arguments, [string] $InputText) {
    # Resolve the executable once per process: Get-Command costs ~65 ms and this runs for every metadata read.
    if (-not (Test-Path Variable:global:HarnessGitExecutable) -or -not [IO.File]::Exists($global:HarnessGitExecutable)) {
        $global:HarnessGitExecutable = (Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
    }
    $start = [Diagnostics.ProcessStartInfo]::new($global:HarnessGitExecutable)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    if ($PSBoundParameters.ContainsKey('InputText')) {
        $start.RedirectStandardInput = $true
        $start.StandardInputEncoding = [Text.UTF8Encoding]::new($false)
    }
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
        if ($PSBoundParameters.ContainsKey('InputText')) {
            # Output is already being drained, so a large input cannot deadlock against a full output pipe.
            try { $process.StandardInput.Write($InputText); $process.StandardInput.Close() } catch { }
        }
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
    # One Git read answers both questions (top level, then absolute common directory), one line each.
    $answers = Invoke-HarnessGitMetadata $primary @('rev-parse', '--show-toplevel', '--path-format=absolute', '--git-common-dir')
    $lines = @($answers.Output -replace '\r?\n$', '' -split '\r?\n')
    if ($answers.ExitCode -ne 0 -or $lines.Count -ne 2 -or @($lines | Where-Object { [string]::IsNullOrWhiteSpace($_) -or $_ -match '\x00' }).Count -gt 0) {
        throw 'Expected the top level and common directory from the selected checkout.'
    }
    $top = $lines[0]; $primaryCommon = $lines[1]
    if (-not [string]::Equals([IO.Path]::GetFullPath($top), $primary, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Cache-root hint is not the exact native Git primary checkout.'
    }
    $null = Assert-HarnessCanonicalPath (Join-Path $primary '.git') -Directory
    if (-not [string]::Equals([IO.Path]::GetFullPath($primaryCommon), (Join-Path $primary '.git'), [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Cache-root hint must name the canonical primary, not another linked checkout.'
    }
    if ([string]::Equals($primary, $GitPrimaryRoot, [StringComparison]::OrdinalIgnoreCase)) { return $primary }

    # An independent transport clone may share dependencies only through its
    # exact protocol owner/path/origin binding. Never adopt or silently fall back.
    $checkout = Assert-HarnessCanonicalPath $Root -Directory
    $null = Assert-HarnessCanonicalPath (Join-Path $checkout '.git') -Directory
    $markerPath = Assert-HarnessCanonicalPath (Join-Path $checkout '.request-release-owner.json')
    if ((Get-Item -LiteralPath $markerPath -Force).PSIsContainer) { throw 'Transport owner marker must be a file.' }
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
    # Git is consulted for the clone only after the file-based owner/path binding passed (cheaper rejection, same acceptance).
    $cloneTop = Get-HarnessGitScalar $checkout @('rev-parse', '--show-toplevel')
    if (-not [string]::Equals([IO.Path]::GetFullPath($cloneTop), $checkout, [StringComparison]::OrdinalIgnoreCase)) { throw 'Transport clone is not an exact Git checkout.' }
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
        # Per-checkout ext4 source mirror inside the WSL distribution (relative to the guest $HOME); never shared between checkouts.
        SourceMirror = ".cache/premierecalendar/mirror/$($Context.WorktreeNamespace)"
    }
}

# Emitted into the same shell as resource creation. Fail closed before creating
# volumes/networks/containers; use the inspected immutable image ID thereafter.
# -SkipEngine omits the engine check when an earlier preflight in the same shell already passed it (each docker call costs
# seconds on a loaded shared engine).
function Get-HarnessPreflight([string] $Image, [string] $ImageVariable = 'dev_image', [switch] $SkipEngine) {
    $engine = @'
engine=$(docker info --format '{{.OSType}}/{{.Architecture}}') || { echo 'Local Docker must already be running; helpers never start it.' >&2; exit 25; }
case "$engine" in linux/amd64|linux/x86_64) ;; *) echo "Expected local linux/amd64 Docker, got $engine" >&2; exit 26 ;; esac
'@
    if (-not $Image) { return $engine }
    $engine = if ($SkipEngine) { '' } else { $engine + "`n" }
    $engine + @'
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

# ---------------------------------------------------------------------------
# Source mirror: WSL runs build and test containers from a per-checkout copy of the checkout's source on the
# distribution's ext4 disk instead of the Windows drive mounted into WSL (about 100x faster file metadata and reads).
# The file set is what a build can read: tracked plus untracked-not-ignored files. The copy is content addressed
# (path -> Git blob id) and VERIFIED in the guest against the Windows manifest before any container uses it.
# ---------------------------------------------------------------------------
function Get-HarnessSourceManifest([string] $Root) {
    $listed = Invoke-HarnessGitMetadata $Root @('ls-files', '-z', '--cached', '--others', '--exclude-standard')
    if ($listed.ExitCode -ne 0) { throw 'Could not list the source files of this checkout.' }
    $set = [Collections.Generic.SortedSet[string]]::new([StringComparer]::Ordinal)
    foreach ($path in $listed.Output.Split([char]0, [StringSplitOptions]::RemoveEmptyEntries)) {
        # Tracked files deleted from the working tree are still listed from the index; a build cannot read them.
        if (-not [IO.File]::Exists([IO.Path]::Combine($Root, $path.Replace('/', [IO.Path]::DirectorySeparatorChar)))) { continue }
        if ($path -match '[\r\n]') { throw 'A source path contains a line break; the mirror refuses it.' }
        [void] $set.Add($path)
    }
    $manifest = [Collections.Generic.SortedDictionary[string, string]]::new([StringComparer]::Ordinal)
    if ($set.Count -gt 0) {
        $hashed = Invoke-HarnessGitMetadata $Root @('hash-object', '--no-filters', '--stdin-paths') -InputText ((@($set) -join "`n") + "`n")
        $blobs = @($hashed.Output.Split("`n", [StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { $_.Trim() })
        if ($hashed.ExitCode -ne 0 -or $blobs.Count -ne $set.Count -or @($blobs | Where-Object { $_ -notmatch '^[0-9a-f]{40}$' }).Count -gt 0) {
            throw 'Could not hash the source files of this checkout.'
        }
        $index = 0
        foreach ($path in $set) { $manifest[$path] = $blobs[$index]; $index++ }
    }
    , $manifest
}

function ConvertTo-HarnessManifestText($Manifest) {
    $builder = [Text.StringBuilder]::new()
    foreach ($entry in $Manifest.GetEnumerator()) { [void] $builder.Append($entry.Value).Append(' ').Append($entry.Key).Append("`n") }
    $builder.ToString()
}

function ConvertFrom-HarnessManifestText([string] $Text) {
    $manifest = [Collections.Generic.SortedDictionary[string, string]]::new([StringComparer]::Ordinal)
    foreach ($line in $Text.Split("`n", [StringSplitOptions]::RemoveEmptyEntries)) {
        if ($line.Length -lt 42 -or $line[40] -ne ' ' -or $line.Substring(0, 40) -notmatch '^[0-9a-f]{40}$') { return $null }
        $manifest[$line.Substring(41)] = $line.Substring(0, 40)
    }
    , $manifest
}

function Get-HarnessTextSha256([string] $Text) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text))).ToLowerInvariant()
}

# Changed = added or content-modified paths (what must be shipped); Deleted = paths that vanished. $Old may be $null.
function Compare-HarnessManifest($Old, $New) {
    $changed = [Collections.Generic.List[string]]::new()
    foreach ($entry in $New.GetEnumerator()) {
        if ($null -eq $Old -or -not $Old.ContainsKey($entry.Key) -or $Old[$entry.Key] -cne $entry.Value) { $changed.Add($entry.Key) }
    }
    $deleted = [Collections.Generic.List[string]]::new()
    if ($null -ne $Old) { foreach ($path in $Old.Keys) { if (-not $New.ContainsKey($path)) { $deleted.Add($path) } } }
    [pscustomobject]@{ Changed = $changed.ToArray(); Deleted = $deleted.ToArray(); Unchanged = $New.Count - $changed.Count }
}

# One uncompressed tar of exactly the given files (raw bytes, no line-ending conversion). Shell scripts stay executable.
function New-HarnessSourceTar([string] $Root, [string] $TarPath, [string[]] $Paths) {
    $stream = [IO.File]::Create($TarPath)
    try {
        $writer = [Formats.Tar.TarWriter]::new($stream, [Formats.Tar.TarEntryFormat]::Ustar, $false)
        try {
            foreach ($path in $Paths) {
                $entry = [Formats.Tar.UstarTarEntry]::new([Formats.Tar.TarEntryType]::RegularFile, $path)
                $entry.Mode = if ($path.EndsWith('.sh', [StringComparison]::Ordinal)) { [IO.UnixFileMode] 493 } else { [IO.UnixFileMode] 420 }
                $full = [IO.Path]::Combine($Root, $path.Replace('/', [IO.Path]::DirectorySeparatorChar))
                # The mirror keeps the Windows modification times, so incremental builds see exactly what they would on the
                # live checkout: a full resync does not make every file look new, an edited file is newer than its outputs.
                $entry.ModificationTime = [DateTimeOffset]::new([IO.File]::GetLastWriteTimeUtc($full), [TimeSpan]::Zero)
                $source = [IO.File]::OpenRead($full)
                try { $entry.DataStream = $source; $writer.WriteEntry($entry) } finally { $source.Dispose() }
            }
        } finally { $writer.Dispose() }
    } finally { $stream.Dispose() }
}

function Get-HarnessMirrorStateDirectory([string] $Root) { Join-Path $Root 'artifacts/mirror' }

function Read-HarnessMirrorBase([string] $StateDirectory) {
    $file = Join-Path $StateDirectory 'manifest.txt'
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { return $null }
    ConvertFrom-HarnessManifestText ([IO.File]::ReadAllText($file))
}

# Computes the manifest and (with -Pack) writes this run's delta tar plus the expected path/blob lists under
# artifacts/mirror/run-<id>. Everything lives in the checkout's ignored artifacts folder, one folder per run.
function New-HarnessMirrorPlan([string] $Root, [string] $RunId, [switch] $Pack, [switch] $Full) {
    $stateDirectory = Get-HarnessMirrorStateDirectory $Root
    $runDirectory = Join-Path $stateDirectory "run-$RunId"
    [void] (New-Item -ItemType Directory -Path $runDirectory -Force)
    $manifest = Get-HarnessSourceManifest $Root
    $text = ConvertTo-HarnessManifestText $manifest
    $base = if ($Full -or -not $Pack) { $null } else { Read-HarnessMirrorBase $stateDirectory }
    $difference = Compare-HarnessManifest $base $manifest
    $tar = $null
    if ($Pack) {
        [IO.File]::WriteAllText((Join-Path $runDirectory 'paths.txt'), ((@($manifest.Keys) -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText((Join-Path $runDirectory 'blobs.txt'), ((@($manifest.Values) -join "`n") + "`n"), [Text.UTF8Encoding]::new($false))
        if ($difference.Changed.Count -gt 0) {
            $tar = Join-Path $runDirectory 'delta.tar'
            New-HarnessSourceTar $Root $tar $difference.Changed
        }
    }
    [pscustomobject]@{
        Manifest = $manifest; ManifestText = $text; ManifestSha256 = (Get-HarnessTextSha256 $text)
        Changed = $difference.Changed; Deleted = $difference.Deleted; Full = ($null -eq $base)
        RunDirectory = $runDirectory; StateDirectory = $stateDirectory; Tar = $tar
    }
}

# Bash for the guest: ship the delta, drop files that are not in the manifest, VERIFY every path's blob id against the
# Windows manifest and fail closed (51 = a delta did not verify: retry in full; 52 = a full copy did not verify).
# Leaves $pc_src (the directory to mount as /src) and writes <run>/verified only after the verification passed.
function Get-HarnessMirrorSyncScript($Plan, $Storage, [string] $WslRoot, [string] $RunId) {
    if ($Storage.SourceMirror -notmatch '^\.cache/premierecalendar/mirror/[0-9a-f]{10}$') { throw 'Unexpected source mirror location.' }
    $run = "$WslRoot/artifacts/mirror/run-$RunId"
    $q = { param($value) ConvertTo-BashLiteral $value }
    $clear = if ($Plan.Full) { 'find "$pc_src" -mindepth 1 -maxdepth 1 -exec rm -rf -- {} +' } else { ':' }
    $tarStep = if ($Plan.Tar) { "tar -x --no-same-owner -f $(& $q "$run/delta.tar") -C `"`$pc_src`"" } else { ':' }
    $failure = if ($Plan.Full) { 52 } else { 51 }
    $mode = if ($Plan.Full) { 'full' } else { 'delta' }
    @"
pc_src="`$HOME/$($Storage.SourceMirror)"
pc_sync_t0=`$(date +%s%N)
mkdir -p "`$pc_src"
$clear
$tarStep
mkdir -p "`$pc_src/artifacts"
pc_mirror_sync() {
  cd "`$pc_src" || return 1
  LC_ALL=C comm -23 <(find . -path ./artifacts -prune -o \( -type f -o -type l \) -printf '%P\n' | LC_ALL=C sort) <(LC_ALL=C sort $(& $q "$run/paths.txt")) | while IFS= read -r pc_extra; do rm -f -- "`$pc_extra"; done
  # The mirror is not a repository; the ceiling stops Git from adopting an enclosing one (it would resolve the paths against it).
  GIT_CEILING_DIRECTORIES="`$(dirname "`$pc_src")" git hash-object --no-filters --stdin-paths < $(& $q "$run/paths.txt") 2>/dev/null | cmp -s - $(& $q "$run/blobs.txt")
}
if ! ( pc_mirror_sync ); then echo "PC_SOURCE_MIRROR state=verification-failed mode=$mode path=`$pc_src" >&2; exit $failure; fi
: > $(& $q "$run/verified")
echo "PC_SOURCE_MIRROR mode=$mode files=$($Plan.Manifest.Count) changed=$($Plan.Changed.Count) ms=`$(( (`$(date +%s%N) - pc_sync_t0) / 1000000 )) path=`$pc_src"
"@
}

# Runs one guest script built by $BuildScript (given the sync object: SyncScript, RunDirectoryWsl, ManifestSha256) with the
# source either mirrored ('Mirror') or the live Windows mount ('Windows'). A delta that does not verify (exit 51: the
# mirror was wiped or edited) is repeated once with a full copy. The Windows copy of the manifest is replaced only when the
# guest wrote its verified marker, so it never claims more than was proven. Returns ExitCode, ImageId and ReuseFolder.
function Invoke-HarnessMirroredWsl([scriptblock] $BuildScript, $Context, $Storage, [string] $WslRoot, [string] $RunId,
    [ValidateSet('Mirror', 'Windows')][string] $SourceMount, [string] $WslDistribution, [scriptblock] $RemainingSeconds,
    [int] $CleanupGraceSeconds = 15) {
    $mirror = $SourceMount -eq 'Mirror'
    $forceFull = $false
    for ($attempt = 0; $attempt -lt 2; $attempt++) {
        $plan = New-HarnessMirrorPlan $Context.Root $RunId -Pack:$mirror -Full:$forceFull
        $verifiedMarker = Join-Path $plan.RunDirectory 'verified'
        try {
            $sync = [pscustomobject]@{
                SyncScript = if ($mirror) { Get-HarnessMirrorSyncScript $plan $Storage $WslRoot $RunId } else { "pc_src=$(ConvertTo-BashLiteral $WslRoot)" }
                RunDirectoryWsl = "$WslRoot/artifacts/mirror/run-$RunId"; ManifestSha256 = $plan.ManifestSha256
                FileCount = $plan.Manifest.Count; Changed = $plan.Changed.Count; Full = $plan.Full
            }
            $script = & $BuildScript $sync
            $code = Invoke-HarnessWsl $script -ReturnExitCode -WslDistribution $WslDistribution -TimeoutSeconds (& $RemainingSeconds) -CleanupGraceSeconds $CleanupGraceSeconds
            $verified = $mirror -and (Test-Path -LiteralPath $verifiedMarker)
            $stateFile = Join-Path $plan.StateDirectory 'manifest.txt'
            if ($verified) {
                $temporary = "$stateFile.$RunId.tmp"
                [IO.File]::WriteAllText($temporary, $plan.ManifestText, [Text.UTF8Encoding]::new($false))
                Move-Item -LiteralPath $temporary -Destination $stateFile -Force
            } elseif ($mirror -and $code -in 51, 52) {
                Remove-Item -LiteralPath $stateFile -Force -ErrorAction SilentlyContinue
            }
            $imageFile = Join-Path $plan.RunDirectory 'image-id.txt'
            $reuseFile = Join-Path $plan.RunDirectory 'reuse.txt'
            $result = [pscustomobject]@{
                ExitCode = $code
                ImageId = if (Test-Path -LiteralPath $imageFile) { ([IO.File]::ReadAllText($imageFile)).Trim() } else { $null }
                ReuseFolder = if (Test-Path -LiteralPath $reuseFile) { ([IO.File]::ReadAllText($reuseFile)).Trim() } else { $null }
                Mirror = $mirror; SyncChanged = $plan.Changed.Count; SyncFiles = $plan.Manifest.Count
            }
        } catch {
            # The guest may have changed the mirror part-way: forget the recorded state so the next run copies in full.
            if ($mirror) { Remove-Item -LiteralPath (Join-Path $plan.StateDirectory 'manifest.txt') -Force -ErrorAction SilentlyContinue }
            throw
        } finally {
            $resolved = [IO.Path]::GetFullPath($plan.RunDirectory)
            if ($resolved.StartsWith([IO.Path]::GetFullPath($plan.StateDirectory) + [IO.Path]::DirectorySeparatorChar) -and (Split-Path -Leaf $resolved) -eq "run-$RunId") {
                Remove-Item -LiteralPath $resolved -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
        if ($mirror -and $result.ExitCode -eq 51 -and -not $plan.Full) {
            Write-Host 'The source mirror did not match its record; copying the source in full.'
            $forceFull = $true
            continue
        }
        return $result
    }
}

# ---------------------------------------------------------------------------
# Test result cache: a run whose inputs (verified source manifest, dev image identity, selection, configuration) were
# already seen passing is not run again. Only a complete pass is recorded; failed or partial runs are never reused.
# ---------------------------------------------------------------------------
function Get-HarnessResultInputsHash([hashtable] $Inputs) {
    $canonical = [ordered]@{}
    foreach ($name in ($Inputs.Keys | Sort-Object { $_ } -CaseSensitive)) { $canonical[$name] = $Inputs[$name] }
    Get-HarnessTextSha256 ($canonical | ConvertTo-Json -Compress -Depth 5)
}

function Get-HarnessResultKey([string] $InputsHash, [string] $ImageId) { Get-HarnessTextSha256 "$InputsHash`nimage=$ImageId" }

# True only when every suite has a CTRF report with at least one test and no failed, errored or pending test.
function Test-HarnessResultsPassed([string] $Folder, [string[]] $Suites) {
    if (-not (Test-Path -LiteralPath $Folder -PathType Container) -or $Suites.Count -eq 0) { return $false }
    foreach ($suite in $Suites) {
        $report = Join-Path $Folder "$suite.ctrf.json"
        if (-not (Test-Path -LiteralPath $report -PathType Leaf)) { return $false }
        try { $summary = (Get-Content -LiteralPath $report -Raw | ConvertFrom-Json).results.summary } catch { return $false }
        if ($null -eq $summary -or [int] $summary.tests -le 0 -or ([int] $summary.failed + [int] $summary.other + [int] $summary.pending) -ne 0) { return $false }
    }
    $true
}

function Write-HarnessResultKey([string] $Folder, [hashtable] $Inputs, [string] $InputsHash, [string] $ImageId, [string[]] $Suites) {
    $record = [ordered]@{
        schema = 1; key = (Get-HarnessResultKey $InputsHash $ImageId); inputsHash = $InputsHash; imageId = $ImageId
        passed = $true; suites = @($Suites); inputs = $Inputs; writtenUtc = [DateTime]::UtcNow.ToString('o')
    }
    [IO.File]::WriteAllText((Join-Path $Folder 'result-key.json'), ($record | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
}

# Passing runs recorded for the same inputs (newest first). The image identity is compared where Docker answers.
function Find-HarnessReusableResults([string] $ResultsRoot, [string] $InputsHash, [string[]] $Suites, [int] $Limit = 8) {
    if (-not (Test-Path -LiteralPath $ResultsRoot -PathType Container)) { return @() }
    $found = [Collections.Generic.List[object]]::new()
    foreach ($directory in (Get-ChildItem -LiteralPath $ResultsRoot -Directory | Where-Object { $_.Name -match '^[0-9]{8}-[0-9]{6}$' } | Sort-Object Name -Descending)) {
        $file = Join-Path $directory.FullName 'result-key.json'
        if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { continue }
        try { $record = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json } catch { continue }
        if ($record.schema -ne 1 -or $record.passed -ne $true -or $record.inputsHash -cne $InputsHash -or $record.imageId -notmatch '^sha256:[0-9a-f]{64}$') { continue }
        if ($record.key -cne (Get-HarnessResultKey $InputsHash $record.imageId)) { continue }
        if (-not (Test-HarnessResultsPassed $directory.FullName $Suites)) { continue }
        $found.Add([pscustomobject]@{ Folder = $directory.FullName; Name = $directory.Name; ImageId = [string] $record.imageId; Key = [string] $record.key })
        if ($found.Count -ge $Limit) { break }
    }
    $found.ToArray()
}

# ---------------------------------------------------------------------------
# Native feedback lane (macOS, Linux): the same suites, runner arguments, results and passing-result cache as the container
# lane, but built and run with the host's own .NET SDK against a throwaway PostgreSQL started from locally installed
# binaries. It is FEEDBACK ONLY: release acceptance stays on the linux/amd64 container builder (see AGENTS.md).
# Nothing here installs software, starts a service, or edits PATH/profile files.
# ---------------------------------------------------------------------------

# The OS decision lives in one place so tests can force it. Auto: Windows uses WSL/Docker, every other OS runs natively.
# PC_HARNESS_LANE=Wsl|Native is a test hook that overrides Auto (the Harness tests drive the WSL lane's plans and intercepted
# fixtures on any host); explicit -Lane values always win. Release scripts never use the native lane (HarnessPolicy.Tests.ps1).
function Resolve-HarnessHostLane([ValidateSet('Auto', 'Native', 'Wsl')][string] $Requested = 'Auto', [Nullable[bool]] $IsWindowsHost = $null) {
    if ($Requested -ne 'Auto') { return $Requested }
    $override = [Environment]::GetEnvironmentVariable('PC_HARNESS_LANE')
    if ($override -in 'Native', 'Wsl') { return $override }
    $windowsHost = if ($null -ne $IsWindowsHost) { [bool] $IsWindowsHost } else { [bool] $IsWindows }
    if ($windowsHost) { 'Wsl' } else { 'Native' }
}

function Get-HarnessNativePlatform([string] $Os, [string] $Architecture) {
    if (-not $Os) { $Os = if ($IsMacOS) { 'macos' } elseif ($IsLinux) { 'linux' } else { 'windows' } }
    if (-not $Architecture) { $Architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant() }
    $ridOs = switch ($Os) { 'macos' { 'osx' } 'linux' { 'linux' } 'windows' { 'win' } default { throw "Unsupported native OS '$Os'." } }
    [pscustomobject]@{ Os = $Os; Architecture = $Architecture; Rid = "$ridOs-$Architecture"; Name = "$Os-$Architecture" }
}

# Isolated per OS/architecture (and runtime-async mode) so Linux, Windows and macOS outputs never mix.
function Get-HarnessNativeStorage($Context, $Platform, [switch] $NoRuntimeAsync) {
    $suffix = if ($NoRuntimeAsync) { '-sync' } else { '' }
    [pscustomobject]@{
        BuildDirectory = Join-Path $Context.Root "artifacts/build/native-$($Platform.Name)$suffix"
        PackageDirectory = Join-Path $Context.PrimaryRoot "artifacts/packages/native-$($Platform.Name)"
    }
}

# auto = half of the CPUs (at least 2, at most 64); explicit numbers pass through.
function Get-HarnessNativeBudget([string] $Value, [int] $CpuCount = [Environment]::ProcessorCount) {
    if ($Value -ne 'auto') { return [int] $Value }
    [Math]::Min(64, [Math]::Max(2, [int][Math]::Floor($CpuCount / 2)))
}

function Get-HarnessRequiredSdk([string] $Root) {
    $global = Get-Content -LiteralPath (Join-Path $Root 'global.json') -Raw | ConvertFrom-Json
    [pscustomobject]@{ Version = [string] $global.sdk.version; RollForward = if ($global.sdk.PSObject.Properties['rollForward']) { [string] $global.sdk.rollForward } else { 'patch' } }
}

# Highest installed SDK that satisfies global.json: same major.minor, not older, no prerelease ('disable' = exact).
function Select-HarnessSdk([string[]] $Installed, $Required) {
    $need = [version] $Required.Version
    $best = $null
    foreach ($text in $Installed) {
        if ($text -notmatch '^\d+\.\d+\.\d+$') { continue }
        $candidate = [version] $text
        if ($candidate.Major -ne $need.Major -or $candidate.Minor -ne $need.Minor) { continue }
        if ($Required.RollForward -eq 'disable') { if ($candidate -ne $need) { continue } } elseif ($candidate -lt $need) { continue }
        if ($null -eq $best -or $candidate -gt [version] $best) { $best = $text }
    }
    $best
}

function Get-HarnessDotnetCandidates {
    $list = [Collections.Generic.List[string]]::new()
    if ($env:DOTNET_ROOT) { $list.Add((Join-Path $env:DOTNET_ROOT 'dotnet')) }
    $onPath = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($onPath) { $list.Add($onPath.Source) }
    foreach ($fixed in '/usr/local/share/dotnet/dotnet', '/usr/share/dotnet/dotnet', '/usr/lib/dotnet/dotnet') { $list.Add($fixed) }
    @($list | Select-Object -Unique)
}

# -Probe runs `dotnet --list-sdks` (execution); without it the SDK folders next to the executable are listed instead, so a
# plan never starts a process. Returns Ok=$false with an explanation instead of throwing.
function Resolve-HarnessDotnet([string] $Root, [string[]] $Candidates = (Get-HarnessDotnetCandidates), [switch] $Probe, [scriptblock] $ListSdks) {
    $required = Get-HarnessRequiredSdk $Root
    $tried = [Collections.Generic.List[string]]::new()
    foreach ($candidate in $Candidates) {
        if (-not [IO.File]::Exists($candidate)) { $tried.Add("$candidate (missing)"); continue }
        $resolved = $candidate
        try { $link = (Get-Item -LiteralPath $candidate -Force).ResolveLinkTarget($true); if ($link) { $resolved = $link.FullName } } catch { }
        $installed = if ($Probe) {
            if ($ListSdks) { @(& $ListSdks $candidate) } else {
                $listed = Invoke-HarnessNativeCommand $candidate @('--list-sdks') -TimeoutSeconds 60
                if ($listed.ExitCode -ne 0) { $tried.Add("$candidate (--list-sdks failed)"); continue }
                @($listed.Output -split '\r?\n' | Where-Object { $_ -match '^\s*\d' } | ForEach-Object { ($_.Trim() -split '\s+')[0] })
            }
        } else {
            $sdkDirectory = Join-Path ([IO.Path]::GetDirectoryName($resolved)) 'sdk'
            if (Test-Path -LiteralPath $sdkDirectory -PathType Container) { @(Get-ChildItem -LiteralPath $sdkDirectory -Directory | ForEach-Object Name) } else { @() }
        }
        $sdk = Select-HarnessSdk $installed $required
        if ($sdk) {
            return [pscustomobject]@{ Ok = $true; Path = $candidate; Root = [IO.Path]::GetDirectoryName($resolved); Sdk = $sdk; Required = $required.Version; Error = $null; Tried = $tried.ToArray() }
        }
        $tried.Add("$candidate (SDKs: $((@($installed) -join ', ')))")
    }
    $message = "No .NET SDK matching global.json ($($required.Version), $($required.RollForward)) was found. Tried: $(if ($tried.Count) { $tried -join '; ' } else { 'nothing' }). Nothing is installed by the helpers; install the SDK yourself or set DOTNET_ROOT."
    [pscustomobject]@{ Ok = $false; Path = $null; Root = $null; Sdk = $null; Required = $required.Version; Error = $message; Tried = $tried.ToArray() }
}

function Get-HarnessPostgresCandidates {
    $list = [Collections.Generic.List[string]]::new()
    foreach ($fixed in '/opt/homebrew/opt/postgresql@18/bin', '/usr/local/opt/postgresql@18/bin') { $list.Add($fixed) }
    $onPath = Get-Command pg_ctl -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($onPath) { $list.Add([IO.Path]::GetDirectoryName($onPath.Source)) }
    $list.ToArray()
}

# A usable PostgreSQL binary directory holds initdb, pg_ctl, postgres and psql. -Requested (a bin folder or an install root
# with bin/) is the only candidate when given, so a wrong explicit path never silently falls back.
function Resolve-HarnessPostgresBin([string] $Requested, [string[]] $Candidates) {
    $tools = 'initdb', 'pg_ctl', 'postgres', 'psql'
    $list = if ($Requested) {
        @($Requested, (Join-Path $Requested 'bin'))
    } elseif ($PSBoundParameters.ContainsKey('Candidates')) { @($Candidates) } else { @(Get-HarnessPostgresCandidates) }
    $tried = [Collections.Generic.List[string]]::new()
    foreach ($directory in $list) {
        if (-not (Test-Path -LiteralPath $directory -PathType Container)) { $tried.Add("$directory (missing)"); continue }
        $missing = @($tools | Where-Object { -not [IO.File]::Exists((Join-Path $directory $_)) -and -not [IO.File]::Exists((Join-Path $directory "$_.exe")) })
        if ($missing.Count -eq 0) { return [pscustomobject]@{ Ok = $true; Bin = [IO.Path]::GetFullPath($directory); Error = $null; Tried = $tried.ToArray() } }
        $tried.Add("$directory (missing $($missing -join ', '))")
    }
    $message = "No PostgreSQL binaries (initdb, pg_ctl, postgres, psql) were found. Tried: $(if ($tried.Count) { $tried -join '; ' } else { 'nothing' }). Nothing is installed by the helpers; pass -PostgresBin <folder> or install PostgreSQL yourself."
    [pscustomobject]@{ Ok = $false; Bin = $null; Error = $message; Tried = $tried.ToArray() }
}

function Get-HarnessPostgresTool([string] $Bin, [string] $Name) {
    $plain = Join-Path $Bin $Name
    if ([IO.File]::Exists($plain)) { $plain } else { "$plain.exe" }
}

# Cache identity of the native environment, shaped like a container image id so the shared cache helpers apply unchanged:
# SDK version, runtime identifier and PostgreSQL version (the container lane uses the immutable dev image id instead).
function Get-HarnessNativeEnvironmentId([string] $Sdk, [string] $Rid, [string] $PostgresVersion) {
    'sha256:' + (Get-HarnessTextSha256 "native-environment`nsdk=$Sdk`nrid=$Rid`npostgres=$PostgresVersion")
}

function New-HarnessNativeCacheInputs([string] $ManifestSha256, [string[]] $Suites, [string[]] $Filter, [string[]] $QueryFilter,
    [string] $Parallel, [string[]] $RunnerArguments, [string] $Configuration, [bool] $RuntimeAsync, [string] $Sdk, [string] $Rid) {
    @{
        schema = 1; lane = 'native'; manifest = $ManifestSha256; suites = @($Suites | Sort-Object); filter = @($Filter | Sort-Object)
        queryFilter = @($QueryFilter | Sort-Object); parallel = [string] $Parallel; runnerArguments = @($RunnerArguments)
        configuration = $Configuration; runtimeAsync = $RuntimeAsync; sdk = $Sdk; rid = $Rid
    }
}

# Runs one process (through `sh -c 'exec "$@" 2>&1'` so stdout and stderr arrive as one ordered stream), bounded by a
# deadline. -LogPath tees every line to a file, -Echo to the console; without -Echo the output is returned (capped).
# The single process boundary of the native lane: tests replace this function with a stub.
function Invoke-HarnessNativeCommand([string] $File, [string[]] $Arguments = @(), [hashtable] $Environment = @{}, [int] $TimeoutSeconds = 60,
    [string] $LogPath, [switch] $Echo, [string] $WorkingDirectory) {
    if ($IsWindows) { throw 'The native lane runs on macOS or Linux.' }
    $start = [Diagnostics.ProcessStartInfo]::new('/bin/sh')
    foreach ($argument in (@('-c', 'exec "$@" 2>&1', 'sh', $File) + $Arguments)) { $start.ArgumentList.Add($argument) }
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardInput = $true
    $start.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    if ($WorkingDirectory) { $start.WorkingDirectory = $WorkingDirectory }
    foreach ($name in $Environment.Keys) { $start.Environment[$name] = [string] $Environment[$name] }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $writer = $null
    $captured = [Text.StringBuilder]::new()
    $timedOut = $false
    try {
        if ($LogPath) { $writer = [IO.StreamWriter]::new($LogPath, $false, [Text.UTF8Encoding]::new($false)); $writer.AutoFlush = $true }
        if (-not $process.Start()) { throw "Could not start $File." }
        $process.StandardInput.Close()
        $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
        $reader = $process.StandardOutput
        while ($true) {
            $pending = $reader.ReadLineAsync()
            while (-not $pending.Wait(250)) {
                if ([DateTime]::UtcNow -gt $deadline) { $timedOut = $true; break }
            }
            if ($timedOut) { break }
            $line = $pending.Result
            if ($null -eq $line) { break }
            if ($Echo) { [Console]::Out.WriteLine($line) } elseif ($captured.Length -lt 1MB) { [void] $captured.AppendLine($line) }
            if ($writer) { $writer.WriteLine($line) }
        }
        if ($timedOut) {
            try { $process.Kill($true) } catch { }
            [void] $process.WaitForExit(10000)
            $message = "TIMEOUT: $File exceeded $TimeoutSeconds s and was stopped."
            if ($Echo) { [Console]::Out.WriteLine($message) }
            if ($writer) { $writer.WriteLine($message) }
            return [pscustomobject]@{ ExitCode = 124; Output = $captured.ToString(); TimedOut = $true }
        }
        $process.WaitForExit()
        [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $captured.ToString(); TimedOut = $false }
    } finally {
        if ($writer) { $writer.Dispose() }
        $process.Dispose()
    }
}

function Get-HarnessFreeTcpPort {
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    try { $listener.Start(); ([Net.IPEndPoint] $listener.LocalEndpoint).Port } finally { $listener.Stop() }
}

function Set-HarnessPrivateFile([string] $Path, [string] $Content) {
    [IO.File]::WriteAllText($Path, '')
    if (-not $IsWindows) { [IO.File]::SetUnixFileMode($Path, [IO.UnixFileMode]'UserRead,UserWrite') }
    [IO.File]::WriteAllText($Path, $Content, [Text.UTF8Encoding]::new($false))
}

# Removes the throwaway server folder only when it is exactly <temp>/pcpg-<12 hex> (never anything else).
function Remove-HarnessNativePostgresDirectory([string] $Directory, [string] $TempRoot) {
    $full = [IO.Path]::GetFullPath($Directory).TrimEnd('/', '\')
    $parent = [IO.Path]::GetFullPath($TempRoot).TrimEnd('/', '\')
    if ([IO.Path]::GetDirectoryName($full) -cne $parent -or [IO.Path]::GetFileName($full) -cnotmatch '^pcpg-[0-9a-f]{12}$') { throw "Refusing to remove unexpected path '$full'." }
    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        if (-not (Test-Path -LiteralPath $full)) { return $true }
        try { Remove-Item -LiteralPath $full -Recurse -Force -ErrorAction Stop } catch { Start-Sleep -Milliseconds 200 }
    }
    -not (Test-Path -LiteralPath $full)
}

# Starts a throwaway PostgreSQL (own data directory, unix socket inside it, 127.0.0.1 on a free port, SCRAM password from a
# per-run password file), runs $Body with the environment variables the test projects read, and ALWAYS stops the server and
# deletes the folder afterwards (also when the body fails or is interrupted). The server is never reachable without the
# password over TCP, matches the container lane's settings (max_connections=300) and uses no fsync because it is disposable.
function Invoke-HarnessWithNativePostgres([string] $Bin, [scriptblock] $Body, [int] $ReadySeconds = 60, [string] $TempRoot) {
    if (-not $TempRoot) { $TempRoot = [IO.Path]::GetTempPath() }
    $TempRoot = [IO.Path]::GetFullPath($TempRoot).TrimEnd('/', '\')
    # Unix socket paths are limited to about 100 characters on macOS.
    if (($TempRoot.Length + 40) -gt 100 -and (Test-Path -LiteralPath '/tmp' -PathType Container)) { $TempRoot = '/tmp' }
    $directory = Join-Path $TempRoot ('pcpg-' + [Guid]::NewGuid().ToString('N').Substring(0, 12))
    $data = Join-Path $directory 'data'
    $socket = Join-Path $directory 'sock'
    $passwordFile = Join-Path $directory 'password'
    $log = Join-Path $directory 'server.log'
    $password = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(16)).ToLowerInvariant()
    $startAttempted = $false
    $exitHandler = $null
    try {
        [void] (New-Item -ItemType Directory -Path $directory)
        if (-not $IsWindows) { [IO.File]::SetUnixFileMode($directory, [IO.UnixFileMode]'UserRead,UserWrite,UserExecute') }
        [void] (New-Item -ItemType Directory -Path $socket)
        Set-HarnessPrivateFile $passwordFile $password
        # Best effort for SIGTERM/exit paths that do not run `finally`: stop the server and remove the folder.
        $exitHandler = Register-EngineEvent -SourceIdentifier PowerShell.Exiting -MessageData @{ Bin = $Bin; Data = $data; Directory = $directory; Root = $TempRoot } -Action {
            $m = $Event.MessageData
            try { & (Join-Path $m.Bin 'pg_ctl') stop -D $m.Data -m immediate -t 10 *> $null } catch { }
            try { if ((Split-Path -Leaf $m.Directory) -match '^pcpg-[0-9a-f]{12}$') { Remove-Item -LiteralPath $m.Directory -Recurse -Force -ErrorAction SilentlyContinue } } catch { }
        }
        $init = Invoke-HarnessNativeCommand (Get-HarnessPostgresTool $Bin 'initdb') @('-D', $data, '-U', 'premierecalendar', '--auth-local=trust', '--auth-host=scram-sha-256', "--pwfile=$passwordFile", '-E', 'UTF8', '--no-sync') -TimeoutSeconds 120
        if ($init.ExitCode -ne 0) { throw "initdb failed (exit $($init.ExitCode)): $($init.Output.Trim())" }
        $ready = $false
        for ($attempt = 1; $attempt -le 3 -and -not $ready; $attempt++) {
            $port = Get-HarnessFreeTcpPort
            $options = "-c port=$port -c listen_addresses=127.0.0.1 -c unix_socket_directories=$socket -c max_connections=300 -c fsync=off -c synchronous_commit=off -c full_page_writes=off"
            $startAttempted = $true
            $started = Invoke-HarnessNativeCommand (Get-HarnessPostgresTool $Bin 'pg_ctl') @('start', '-D', $data, '-l', $log, '-o', $options) -TimeoutSeconds 30
            if ($started.ExitCode -ne 0) { continue }
            $deadline = [DateTime]::UtcNow.AddSeconds($ReadySeconds)
            while ([DateTime]::UtcNow -lt $deadline) {
                $probe = Invoke-HarnessNativeCommand (Get-HarnessPostgresTool $Bin 'psql') @('-h', $socket, '-p', "$port", '-U', 'premierecalendar', '-d', 'postgres', '-Atc', 'select 1') -Environment @{ PGCONNECT_TIMEOUT = '2' } -TimeoutSeconds 15
                if ($probe.ExitCode -eq 0 -and $probe.Output.Trim() -eq '1') { $ready = $true; break }
                Start-Sleep -Milliseconds 100
            }
            if (-not $ready) { break }
        }
        if (-not $ready) {
            $tail = if (Test-Path -LiteralPath $log) { (Get-Content -LiteralPath $log -Tail 15) -join "`n" } else { '' }
            throw "The disposable PostgreSQL did not become ready.`n$tail"
        }
        $variables = @{
            PREMIERECALENDAR_TEST_POSTGRES = "Host=127.0.0.1;Port=$port;Database=postgres;Username=premierecalendar;Timeout=60;Command Timeout=120"
            PREMIERECALENDAR_TEST_POSTGRES_PASSWORD_FILE = $passwordFile
        }
        & $Body $variables
    } finally {
        if ($startAttempted) {
            $stopped = Invoke-HarnessNativeCommand (Get-HarnessPostgresTool $Bin 'pg_ctl') @('stop', '-D', $data, '-m', 'immediate', '-t', '20') -TimeoutSeconds 40
            $pidFile = Join-Path $data 'postmaster.pid'
            if ($stopped.ExitCode -ne 0 -and (Test-Path -LiteralPath $pidFile)) {
                # pg_ctl could not stop it: signal the recorded postmaster directly.
                try { $postmaster = [int] ((Get-Content -LiteralPath $pidFile -TotalCount 1).Trim()); Stop-Process -Id $postmaster -Force -ErrorAction SilentlyContinue } catch { }
            }
        }
        if (Test-Path -LiteralPath $directory) {
            if (-not (Remove-HarnessNativePostgresDirectory $directory $TempRoot)) { [Console]::Error.WriteLine("The disposable PostgreSQL folder could not be removed: $directory") }
        }
        if ($exitHandler) { Unregister-Event -SourceIdentifier PowerShell.Exiting -ErrorAction SilentlyContinue; Remove-Job -Id $exitHandler.Id -Force -ErrorAction SilentlyContinue }
    }
}
