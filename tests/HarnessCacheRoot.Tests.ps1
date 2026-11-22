#Requires -Version 7.0
# Real local Git fixtures only: no WSL, Docker, network, SDK or user credentials.
[CmdletBinding()]
param([ValidateSet('All', 'Marker', 'Origin', 'Environment')][string] $Section = 'All')
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ($Section -eq 'All') {
    # Each section builds its own disposable Git fixtures (unique directory) and owns its process environment
    # (BUILD_RELEASE_PROJECT_ROOT/GIT_*), so the three sections run as concurrent child processes. A Git process
    # costs several hundred milliseconds on Windows and every rejection case needs a few of them.
    $self = $PSCommandPath
    $pwshPath = [Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
    $results = @('Marker', 'Origin', 'Environment' | ForEach-Object -ThrottleLimit 3 -Parallel {
        $output = & $using:pwshPath -NoProfile -File $using:self -Section $_ 2>&1
        [pscustomobject]@{ Section = $_; ExitCode = $LASTEXITCODE; Output = @($output | ForEach-Object { "$_" }) }
    })
    $total = 0
    foreach ($name in 'Marker', 'Origin', 'Environment') {
        $result = @($results | Where-Object Section -eq $name)
        if ($result.Count -ne 1 -or $result[0].ExitCode -ne 0 -or $result[0].Output.Count -eq 0 -or $result[0].Output[-1] -notmatch '^(\d+) cache-root checks passed') {
            $result | ForEach-Object { $_.Output } | Write-Host
            throw "Cache-root section failed: $name"
        }
        $total += [int] $Matches[1]
    }
    Write-Host "$total cache-root checks passed with real independent/linked Git fixtures; environment restored."
    return
}
. (Join-Path $root 'eng/Harness.Common.ps1')
$savedHint = [Environment]::GetEnvironmentVariable('BUILD_RELEASE_PROJECT_ROOT')
$hadHint = Test-Path Env:BUILD_RELEASE_PROJECT_ROOT
$savedGitEnvironment = @{}
Get-ChildItem Env: | Where-Object Name -like 'GIT_*' | ForEach-Object { $savedGitEnvironment[$_.Name] = $_.Value }
$sandbox = Join-Path $root ('artifacts/harness-tests/cache-root-' + $Section.ToLowerInvariant() + '-' + [guid]::NewGuid().ToString('N'))
$templates = Join-Path $sandbox 'empty-templates'
$hooks = Join-Path $sandbox 'empty-hooks'
$count = 0
function Assert-Cache([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
    $script:count++
}
function Fixture-Git([string] $Path, [string[]] $Arguments) {
    $output = & git --no-optional-locks -c "core.hooksPath=$hooks" -c 'filter.lfs.required=false' -c 'filter.lfs.process=' -c 'filter.lfs.smudge=' -c 'filter.lfs.clean=' -c 'protocol.file.allow=always' -C $Path @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) { throw 'Disposable Git fixture operation failed.' }
    $output
}
function Reject-Hint([scriptblock] $Action, [string] $Message) {
    $rejected = $false
    try { & $Action | Out-Null } catch { $rejected = $true }
    Assert-Cache $rejected $Message
}
try {
    Get-ChildItem Env: | Where-Object Name -like 'GIT_*' | ForEach-Object { Remove-Item -LiteralPath ('Env:' + $_.Name) }
    Remove-Item Env:BUILD_RELEASE_PROJECT_ROOT -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Path $templates, $hooks -Force | Out-Null
    $primary = Join-Path $sandbox 'primary'
    New-Item -ItemType Directory -Path $primary | Out-Null
    Fixture-Git $primary @('init', '--quiet', "--template=$templates") | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $primary 'eng') | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'eng/release-targets.json') -Destination (Join-Path $primary 'eng/release-targets.json')
    [IO.File]::WriteAllText((Join-Path $primary 'README.txt'), "committed fixture`n")
    Fixture-Git $primary @('add', 'README.txt', 'eng/release-targets.json') | Out-Null
    Fixture-Git $primary @('-c', 'user.name=Harness Fixture', '-c', 'user.email=harness@example.invalid', 'commit', '--quiet', '-m', 'fixture') | Out-Null
    $origin = 'https://github.com/Belgian-Coder/PremiereCalendar.git'
    Fixture-Git $primary @('remote', 'add', 'origin', $origin) | Out-Null
    $manifest = Get-Content -LiteralPath (Join-Path $primary 'eng/release-targets.json') -Raw | ConvertFrom-Json -AsHashtable
    $target = @($manifest.targets.Keys | Sort-Object)[0]
    $clone = Join-Path (Join-Path $primary $manifest.worktreeDirectory) ('release-' + $target)
    New-Item -ItemType Directory -Path (Split-Path -Parent $clone) | Out-Null
    Fixture-Git $primary @('clone', '--quiet', '--no-hardlinks', "--template=$templates", $primary, $clone) | Out-Null
    Fixture-Git $clone @('remote', 'set-url', 'origin', $origin) | Out-Null
    $linked = Join-Path $sandbox 'linked'
    Fixture-Git $primary @('worktree', 'add', '--quiet', '--detach', $linked, 'HEAD') | Out-Null
    [IO.File]::WriteAllText((Join-Path $primary 'README.txt'), "preserve dirty primary WIP`n")
    $indexHash = (Get-FileHash -LiteralPath (Join-Path $primary '.git/index')).Hash
    $wipHash = (Get-FileHash -LiteralPath (Join-Path $primary 'README.txt')).Hash
    $normal = Get-HarnessContext $primary
    $markerPath = Join-Path $clone '.request-release-owner.json'
    $marker = @{ owner = 'request-release'; version = 1; project = $manifest.project; target = $target; root = $primary }
    function Write-Marker { [IO.File]::WriteAllText($markerPath, ($marker | ConvertTo-Json -Compress)) }
    function New-ForeignFixtures {
        $script:bare = Join-Path $sandbox 'bare.git'
        New-Item -ItemType Directory -Path $script:bare | Out-Null
        Fixture-Git $script:bare @('init', '--quiet', '--bare', "--template=$templates") | Out-Null
        $script:wrongClone = Join-Path $primary '.wt/wrong-path'
        Fixture-Git $primary @('clone', '--quiet', '--no-hardlinks', "--template=$templates", $primary, $script:wrongClone) | Out-Null
        Fixture-Git $script:wrongClone @('remote', 'set-url', 'origin', $origin) | Out-Null
    }
    Write-Marker
    $primaryStorage = Get-HarnessStorage $normal

    if ($Section -eq 'Marker') {
        # Routing identity without a hint, then every invalid/ambiguous owner-marker or root-hint shape.
        $linkedContext = Get-HarnessContext $linked
        $independent = Get-HarnessContext $clone
        Assert-Cache ($normal.PrimaryRoot -eq $primary -and $linkedContext.PrimaryRoot -eq $primary) 'Normal primary/linked Git routing changed.'
        Assert-Cache ($independent.PrimaryRoot -eq $clone) 'An unhinted independent clone was silently adopted.'
        Assert-Cache ($normal.ProjectNamespace -eq $linkedContext.ProjectNamespace -and $normal.WorktreeNamespace -ne $linkedContext.WorktreeNamespace) 'Linked worktree cache/build isolation changed.'
        [Environment]::SetEnvironmentVariable('BUILD_RELEASE_PROJECT_ROOT', $primary)
        $hinted = Get-HarnessContext $clone
        $hintedStorage = Get-HarnessStorage $hinted
        Assert-Cache ($hinted.PrimaryRoot -eq $primary -and $hinted.ProjectNamespace -eq $normal.ProjectNamespace) 'Validated transport clone did not select the canonical package namespace.'
        Assert-Cache ($hintedStorage.NugetVolume -eq $primaryStorage.NugetVolume) 'Independent transport clone misses canonical NuGet/HTTP/SCRATCH volume.'
        Assert-Cache ($hinted.WorktreeNamespace -eq $independent.WorktreeNamespace -and $hintedStorage.BuildVolume -ne $primaryStorage.BuildVolume) 'Transport hint mixed mutable checkout build state.'
        Assert-Cache ((Get-HarnessContext $linked).PrimaryRoot -eq $primary) 'Explicit normal primary hint requires an unrelated protocol marker.'
        foreach ($change in @(@('owner','foreign'), @('version','1'), @('project','foreign'), @('target','not-a-target'), @('root',$sandbox))) {
            $oldValue = $marker[$change[0]]
            $marker[$change[0]] = $change[1]; Write-Marker
            Reject-Hint { Get-HarnessContext $clone } 'Invalid owner/project/target/root/version was accepted.'
            $marker[$change[0]] = $oldValue; Write-Marker
        }
        $marker.extra = 'unexpected'; Write-Marker
        Reject-Hint { Get-HarnessContext $clone } 'Unknown marker field was accepted.'
        $marker.Remove('extra'); Write-Marker
        foreach ($key in @('owner', 'project', 'root', 'target', 'version')) {
            $oldValue = $marker[$key]
            $marker[$key] = @($oldValue); Write-Marker
            Reject-Hint { Get-HarnessContext $clone } 'Array-valued protocol identity was accepted as a string.'
            $marker[$key] = @(); Write-Marker
            Reject-Hint { Get-HarnessContext $clone } 'Empty-array protocol identity was accepted as a scalar.'
            $marker[$key] = $oldValue; Write-Marker
        }
        Remove-Item -LiteralPath $markerPath -Force
        Reject-Hint { Get-HarnessContext $clone } 'Missing protocol owner marker was accepted.'
        Write-Marker
        [Environment]::SetEnvironmentVariable('BUILD_RELEASE_PROJECT_ROOT', $sandbox)
        Reject-Hint { Get-HarnessContext $clone } 'A containing non-checkout root was accepted.'
        [Environment]::SetEnvironmentVariable('BUILD_RELEASE_PROJECT_ROOT', ($primary + [IO.Path]::DirectorySeparatorChar + '..' + [IO.Path]::DirectorySeparatorChar + 'primary'))
        Reject-Hint { Get-HarnessContext $clone } 'Noncanonical root traversal was accepted.'
        [Environment]::SetEnvironmentVariable('BUILD_RELEASE_PROJECT_ROOT', 'relative-primary')
        Reject-Hint { Get-HarnessContext $clone } 'Relative root hint was accepted.'
        [Environment]::SetEnvironmentVariable('BUILD_RELEASE_PROJECT_ROOT', $linked)
        Reject-Hint { Get-HarnessContext $clone } 'A linked checkout was accepted as the canonical primary.'
        [Environment]::SetEnvironmentVariable('BUILD_RELEASE_PROJECT_ROOT', '')
        Reject-Hint { Get-HarnessContext $clone } 'An explicitly empty root hint was silently ignored.'
        [Environment]::SetEnvironmentVariable('BUILD_RELEASE_PROJECT_ROOT', $primary)
        Assert-Cache ((Get-HarnessContext $clone).ProjectNamespace -eq $normal.ProjectNamespace) 'Refused hints contaminated subsequent valid routing.'
    }

    if ($Section -eq 'Origin') {
        # Origin/config/path/link refusals around a valid transport marker.
        [Environment]::SetEnvironmentVariable('BUILD_RELEASE_PROJECT_ROOT', $primary)
        Fixture-Git $clone @('remote', 'set-url', 'origin', 'https://github.com/example/foreign.git') | Out-Null
        Reject-Hint { Get-HarnessContext $clone } 'Foreign origin was accepted.'
        Fixture-Git $clone @('remote', 'set-url', 'origin', 'https://fixture-user@github.com/Belgian-Coder/PremiereCalendar.git') | Out-Null
        Reject-Hint { Get-HarnessContext $clone } 'Credential-bearing origin was accepted.'
        Fixture-Git $clone @('remote', 'set-url', 'origin', $origin) | Out-Null
        Fixture-Git $clone @('config', '--local', '--add', 'remote.origin.url', $origin) | Out-Null
        Reject-Hint { Get-HarnessContext $clone } 'Multiple identical local origin values were accepted.'
        Fixture-Git $clone @('config', '--local', '--unset-all', 'remote.origin.url') | Out-Null
        Fixture-Git $clone @('config', '--local', '--add', 'remote.origin.url', $origin) | Out-Null
        Fixture-Git $clone @('config', '--local', 'remote.origin.url', ($origin + "`n" + $origin)) | Out-Null
        Reject-Hint { Get-HarnessContext $clone } 'A multiline local origin value was accepted.'
        Fixture-Git $clone @('config', '--local', 'remote.origin.url', $origin) | Out-Null
        Fixture-Git $primary @('config', '--local', '--add', 'remote.origin.url', 'https://github.com/example/foreign.git') | Out-Null
        Reject-Hint { Get-HarnessContext $clone } 'Ambiguous primary origin values were accepted.'
        Fixture-Git $primary @('config', '--local', '--unset-all', 'remote.origin.url') | Out-Null
        Fixture-Git $primary @('config', '--local', '--add', 'remote.origin.url', $origin) | Out-Null
        Fixture-Git $clone @('config', '--local', 'core.worktree', $linked) | Out-Null
        Reject-Hint { Get-HarnessContext $clone } 'A redirected execution checkout was accepted.'
        Fixture-Git $clone @('config', '--local', '--unset-all', 'core.worktree') | Out-Null
        Fixture-Git $primary @('config', '--local', 'core.worktree', $linked) | Out-Null
        Reject-Hint { Get-HarnessContext $clone } 'A redirected canonical primary was accepted.'
        Fixture-Git $primary @('config', '--local', '--unset-all', 'core.worktree') | Out-Null
        New-ForeignFixtures
        [Environment]::SetEnvironmentVariable('BUILD_RELEASE_PROJECT_ROOT', $bare)
        Reject-Hint { Get-HarnessContext $clone } 'A bare repository was accepted as canonical primary.'
        [Environment]::SetEnvironmentVariable('BUILD_RELEASE_PROJECT_ROOT', $primary)
        [IO.File]::WriteAllText((Join-Path $wrongClone '.request-release-owner.json'), ($marker | ConvertTo-Json -Compress))
        Reject-Hint { Get-HarnessContext $wrongClone } 'A clone outside exact release-target path was accepted.'
        $link = Join-Path $sandbox 'linked-primary'
        New-Item -ItemType SymbolicLink -Path $link -Target $primary | Out-Null
        try {
            [Environment]::SetEnvironmentVariable('BUILD_RELEASE_PROJECT_ROOT', $link)
            Reject-Hint { Get-HarnessContext $clone } 'Linked primary root was accepted.'
        } finally { Remove-Item -LiteralPath $link }
        $markerLink = Join-Path $sandbox 'marker.json'
        Copy-Item -LiteralPath $markerPath -Destination $markerLink -Force
        Remove-Item -LiteralPath $markerPath -Force
        New-Item -ItemType SymbolicLink -Path $markerPath -Target $markerLink | Out-Null
        try {
            [Environment]::SetEnvironmentVariable('BUILD_RELEASE_PROJECT_ROOT', $primary)
            Reject-Hint { Get-HarnessContext $clone } 'Linked owner marker was accepted.'
        } finally { Remove-Item -LiteralPath $markerPath -Force; Write-Marker }
        [Environment]::SetEnvironmentVariable('BUILD_RELEASE_PROJECT_ROOT', $primary)
        Assert-Cache ((Get-HarnessContext $clone).ProjectNamespace -eq $normal.ProjectNamespace) 'Refused hints contaminated subsequent valid routing.'
    }

    if ($Section -eq 'Environment') {
        # Inherited Git state must not redirect routing, and the actual adapter must reject a bad hint before any output.
        New-ForeignFixtures
        $poison = @{
            GIT_DIR = (Join-Path $wrongClone '.git'); GIT_WORK_TREE = $wrongClone
            GIT_CONFIG_COUNT = '2'; GIT_CONFIG_KEY_0 = 'remote.origin.url'; GIT_CONFIG_VALUE_0 = 'https://example.invalid/injected.git'
            GIT_CONFIG_KEY_1 = 'core.worktree'; GIT_CONFIG_VALUE_1 = $wrongClone
            GIT_CONFIG_PARAMETERS = "'remote.origin.url=https://example.invalid/parameters.git'"
            GIT_OBJECT_DIRECTORY = (Join-Path $wrongClone '.git/objects'); GIT_ALTERNATE_OBJECT_DIRECTORIES = (Join-Path $bare 'objects')
            GIT_OPTIONAL_LOCKS = '1'; GIT_TERMINAL_PROMPT = '1'
        }
        try {
            foreach ($key in $poison.Keys) { [Environment]::SetEnvironmentVariable($key, $poison[$key]) }
            Remove-Item Env:BUILD_RELEASE_PROJECT_ROOT -ErrorAction SilentlyContinue
            Assert-Cache ((Get-HarnessContext $primary).PrimaryRoot -eq $primary) 'Inherited Git state redirected initial no-hint/common-dir discovery.'
            Assert-Cache ((Get-HarnessContext $linked).PrimaryRoot -eq $primary) 'Inherited Git state redirected the linked warm route.'
            [Environment]::SetEnvironmentVariable('BUILD_RELEASE_PROJECT_ROOT', $primary)
            Assert-Cache ((Get-HarnessStorage (Get-HarnessContext $clone)).NugetVolume -eq $primaryStorage.NugetVolume) 'Inherited Git state redirected a validated independent clone cache identity.'
            Assert-Cache ((Get-HarnessGitScalar $primary @('config', '--local', '--get', 'remote.origin.url')) -ceq $origin) 'Inherited config parameters replaced the stored project origin.'
            Assert-Cache (@($poison.Keys | Where-Object { [Environment]::GetEnvironmentVariable($_) -cne $poison[$_] }).Count -eq 0) 'Metadata subprocess setup changed caller Git environment.'
            Assert-Cache (-not (Test-Path -LiteralPath (Join-Path $primary 'artifacts')) -and -not (Test-Path -LiteralPath (Join-Path $wrongClone 'artifacts'))) 'Poisoned Git state caused a wrong-root cache/output write.'
        } finally {
            foreach ($key in $poison.Keys) { Remove-Item -LiteralPath ('Env:' + $key) -ErrorAction SilentlyContinue }
        }
        # Invoke the ACTUAL adapter entry point. A selector-only test would miss
        # output or lock creation before the helper is reached.
        $adapterOutput = Join-Path $sandbox 'adapter-must-not-exist'
        $adapter = Join-Path $root 'eng/release/Invoke-Candidate.ps1'
        $revision = (& git --no-optional-locks -C $root rev-parse HEAD).Trim()
        [Environment]::SetEnvironmentVariable('BUILD_RELEASE_PROJECT_ROOT', 'relative-primary')
        $adapterError = $null
        try {
            & $adapter -Target linux-amd64 -Mode validate -Revision $revision -Output $adapterOutput -Version 0.0.2 -ExpectedOldVersion 0.0.1 -ExpectedOldDigest ('sha256:' + ('b' * 64)) -ExpectedStackRevision 1 | Out-Null
        } catch { $adapterError = $_.Exception.Message }
        Assert-Cache ($adapterError -like '*absolute native local path*') 'Actual candidate adapter did not reject the invalid hint before later guards or execution.'
        Assert-Cache (-not (Test-Path -LiteralPath $adapterOutput)) 'Actual candidate adapter created output/lock state before rejecting the hint.'
        Remove-Item Env:BUILD_RELEASE_PROJECT_ROOT
        $adapterPlan = & $adapter -Target linux-amd64 -Mode plan -Revision $revision -Output $adapterOutput
        Assert-Cache ($adapterPlan.Target -eq 'linux-amd64' -and -not (Test-Path -LiteralPath $adapterOutput)) 'Normal no-hint adapter plan changed or created runtime/output state.'
    }
    # Every section: read-only routing must not touch the dirty primary or install hooks.
    Assert-Cache ((Get-FileHash -LiteralPath (Join-Path $primary '.git/index')).Hash -eq $indexHash -and (Get-FileHash -LiteralPath (Join-Path $primary 'README.txt')).Hash -eq $wipHash) 'Read-only routing modified primary WIP/index.'
    Assert-Cache (-not (Test-Path -LiteralPath (Join-Path $primary '.git/hooks')) -and -not (Test-Path -LiteralPath (Join-Path $clone '.git/hooks'))) 'Git fixtures installed hooks.'
    Fixture-Git $primary @('worktree', 'remove', $linked) | Out-Null
} finally {
    Get-ChildItem Env: | Where-Object Name -like 'GIT_*' | ForEach-Object { Remove-Item -LiteralPath ('Env:' + $_.Name) }
    foreach ($key in $savedGitEnvironment.Keys) { [Environment]::SetEnvironmentVariable($key, $savedGitEnvironment[$key]) }
    if ($hadHint) { [Environment]::SetEnvironmentVariable('BUILD_RELEASE_PROJECT_ROOT', $savedHint) }
    else { Remove-Item Env:BUILD_RELEASE_PROJECT_ROOT -ErrorAction SilentlyContinue }
    if (Test-Path -LiteralPath $sandbox) {
        $full = [IO.Path]::GetFullPath($sandbox)
        $parent = [IO.Path]::GetFullPath((Join-Path $root 'artifacts/harness-tests')) + [IO.Path]::DirectorySeparatorChar
        if (-not $full.StartsWith($parent) -or (Get-Item -LiteralPath $full).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Preserving an unexpected cache fixture path.' }
        $links = @(Get-ChildItem -LiteralPath $full -Recurse -Force -Attributes ReparsePoint)
        if ($links.Count -gt 0) { throw 'Preserving fixture containing an unexpected link.' }
        Remove-Item -LiteralPath $full -Recurse -Force
    }
}
Write-Host "$count cache-root checks passed ($Section section) with real independent/linked Git fixtures; environment restored."
