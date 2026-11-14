#Requires -Version 7.0
<#
.SYNOPSIS
Checks that the repository's documentation and scripts point at things that exist.

.DESCRIPTION
Static, read-only check that works from any folder (everything is derived from the script location, so it also
works inside a git worktree). It reports an error for:

  * a relative Markdown link or image whose target file does not exist, or whose #fragment matches no heading in
    a Markdown target (all Markdown: README.md, AGENTS.md, docs/**, including historical release notes);
  * an absolute path under D:\Projects\PremiereCalendar\ (as written in documented commands) whose file does not
    exist in this checkout;
  * a backticked repository path (for example `eng/Foo.ps1`) that does not exist, in the maintained documents;
  * a script reference of the form PSScriptRoot-relative file name that does not exist, in eng/**;
  * stale statements in the maintained documents: SQLite or the Windows service/installer/updater described as
    usable, GitHub Actions presented as the release path, `dotnet test` run on the Windows host (it is allowed only when the line says it runs in the container).

Historical evidence (docs/releases/**, dated documents such as *-20261107.md, and the validation records listed in
$EvidenceDocuments) is only checked for broken links and absolute D:\Projects\PremiereCalendar paths; the
statements in them describe the past.

.EXAMPLE
pwsh -File D:\Projects\PremiereCalendar\eng\Test-Docs.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$rootPrefix = $root.TrimEnd('\') + '\'
# Git-ignored evidence (artifacts/**, sibling build folders) lives only in the primary checkout, not in a worktree.
$mainRoot = if ($root -match '^(.*)\\\.worktrees\\[^\\]+$') { $Matches[1] } else { $root }

# Undated documents that record past validation runs or designs; they are checked for links only.
$EvidenceDocuments = @(
    'DiscoveryReuseImprovements.md', 'FollowUpPerformanceValidation.md', 'MovieLoadDiagnostics.md',
    'MovieWeekSpeedValidation.md', 'PerformanceImprovements-20261105.md', 'PerformanceReview.md',
    'ProviderClientImprovements.md', 'ProviderRetrievalProfile.md', 'RetrievalImprovements.md',
    'SchedulerRecoveryImprovements.md', 'SpeedImprovements.md', 'SpeedReliabilityValidation.md',
    'TraceImageImprovements.md')

$errors = [Collections.Generic.List[string]]::new()
function Add-Problem([string] $File, [int] $Line, [string] $Message) {
    $relative = $File.Substring($rootPrefix.Length).Replace('\', '/')
    $errors.Add("${relative}:${Line}: $Message")
}

function Get-RelativePath([string] $File) { $File.Substring($rootPrefix.Length).Replace('\', '/') }

function Get-HeadingAnchors([string] $MarkdownFile) {
    $anchors = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $inFence = $false
    foreach ($line in [IO.File]::ReadAllLines($MarkdownFile)) {
        if ($line -match '^\s*(```|~~~)') { $inFence = -not $inFence; continue }
        if ($inFence -or $line -notmatch '^#{1,6}\s+(.+?)\s*#*\s*$') { continue }
        $slug = $Matches[1].ToLowerInvariant()
        $slug = [regex]::Replace($slug, '[`*_~]', '')
        $slug = [regex]::Replace($slug, '[^\p{L}\p{N}\s-]', '')
        $slug = ($slug -replace '\s', '-')
        [void]$anchors.Add($slug)
    }
    $anchors
}
$anchorCache = @{}

$markdown = @(Get-Item -LiteralPath (Join-Path $root 'README.md'), (Join-Path $root 'AGENTS.md') -ErrorAction SilentlyContinue)
$markdown += @(Get-ChildItem -LiteralPath (Join-Path $root 'docs') -Filter '*.md' -File -Recurse)
$markdown += @(Get-ChildItem -LiteralPath (Join-Path $root 'eng') -Filter '*.md' -File -Recurse -ErrorAction SilentlyContinue)

foreach ($file in $markdown) {
    $relative = Get-RelativePath $file.FullName
    $isEvidence = $relative -like 'docs/releases/*' -or $file.Name -match '-\d{8}' -or ($relative -like 'docs/*' -and $EvidenceDocuments -contains $file.Name)
    $lines = [IO.File]::ReadAllLines($file.FullName)
    $directory = Split-Path -Parent $file.FullName
    $inFence = $false
    for ($i = 0; $i -lt $lines.Length; $i++) {
        $line = $lines[$i]
        $number = $i + 1
        if ($line -match '^\s*(```|~~~)') { $inFence = -not $inFence }

        # Links and images: [text](target) and ![alt](target); skip external, mail and in-page links handled below.
        foreach ($m in [regex]::Matches($line, '!?\[[^\]]*\]\(([^)\s]+)(?:\s+"[^"]*")?\)')) {
            $target = $m.Groups[1].Value
            if ($target -match '^(https?:|mailto:|tel:|data:)') { continue }
            $path, $fragment = $target, $null
            if ($target.Contains('#')) { $path, $fragment = $target.Split('#', 2) }
            $resolved = if ([string]::IsNullOrEmpty($path)) { $file.FullName }
                        elseif ($path -match '^[A-Za-z]:[\\/]') { [IO.Path]::GetFullPath($path) }
                        else { [IO.Path]::GetFullPath((Join-Path $directory ([Uri]::UnescapeDataString($path)))) }
            if (-not (Test-Path -LiteralPath $resolved) -and $mainRoot -ne $root -and $resolved.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                $resolved = Join-Path $mainRoot $resolved.Substring($rootPrefix.Length)
            }
            if (-not (Test-Path -LiteralPath $resolved) -and $mainRoot -ne $root) {
                # '../../PremiereCalendar.Build.*' from docs/ leaves the worktree; retry relative to the primary checkout.
                $mainDirectory = Join-Path $mainRoot (Split-Path -Parent $relative)
                $resolved = [IO.Path]::GetFullPath((Join-Path $mainDirectory ([Uri]::UnescapeDataString($path))))
            }
            if (-not (Test-Path -LiteralPath $resolved)) {
                Add-Problem $file.FullName $number "broken link target '$target'"
                continue
            }
            if ($fragment -and $resolved -like '*.md') {
                if (-not $anchorCache.ContainsKey($resolved)) { $anchorCache[$resolved] = Get-HeadingAnchors $resolved }
                if (-not $anchorCache[$resolved].Contains($fragment.ToLowerInvariant())) {
                    Add-Problem $file.FullName $number "no heading matches the fragment '#$fragment' in '$path'"
                }
            }
        }

        # Absolute paths written for the owner (full-path commands) must exist in this checkout.
        foreach ($m in [regex]::Matches($line, 'D:\\Projects\\PremiereCalendar\\([^\s''"`<>|*?){}]+)')) {
            $candidate = $m.Groups[1].Value.TrimEnd('.', ',', ';', ':')
            if ($candidate -match '^(\.worktrees|artifacts|PremiereCalendar\.)') { continue }
            if ($candidate -notmatch '\.[A-Za-z0-9]{1,8}$') { continue }
            if (-not (Test-Path -LiteralPath (Join-Path $root $candidate))) {
                Add-Problem $file.FullName $number "documented path does not exist: D:\Projects\PremiereCalendar\$candidate"
            }
        }

        if ($isEvidence) { continue }

        # Backticked repository paths in maintained documents.
        foreach ($m in [regex]::Matches($line, '`([^`\r\n]+)`')) {
            $span = $m.Groups[1].Value.Trim()
            if ($span -notmatch '^[A-Za-z0-9_.\-/\\]+\.(ps1|py|sh|md|yaml|yml|json|csproj|cs|razor|sql|props|slnx)$') { continue }
            # Runtime or evidence locations (UNC shares, other drives, release-bundle files, container paths) are not repository paths.
            if ($span -match '^(App_Data|\.secrets|appsettings|launchSettings|release-config|source-|build-metadata|installed[\\/]|operations[\\/]|nas-docs[\\/])' -or $span -match '^[A-Za-z]:' -or $span.StartsWith('/') -or $span.StartsWith('\\')) { continue }
            if ($span -notmatch '[/\\]') {
                # A bare file name is only checked when it names a top-level repository file.
                if ($span -match '^(README\.md|AGENTS\.md|compose[A-Za-z.]*\.yaml|global\.json|Directory\.Build\.props|PremiereCalendar\.slnx)$' -and
                    -not (Test-Path -LiteralPath (Join-Path $root $span))) {
                    Add-Problem $file.FullName $number "referenced file does not exist: $span"
                }
                continue
            }
            $normalized = $span.Replace('\', '/')
            if (-not (Test-Path -LiteralPath (Join-Path $root $normalized)) -and
                -not (Test-Path -LiteralPath (Join-Path $directory $normalized)) -and
                -not (Test-Path -LiteralPath (Join-Path (Join-Path $root 'PremiereCalendar') $normalized)) -and
                -not (Test-Path -LiteralPath (Join-Path (Join-Path $root 'docs') $normalized))) {
                Add-Problem $file.FullName $number "referenced path does not exist: $span"
            }
        }

        # Stale statements (maintained documents only).
        $allowed = '\bno\b|removed|no longer|not supported|unsupported|never|disabled|last release|must not|migrate|older|former|previous|legacy|historical|old '
        if ($line -match '(?i)sqlite' -and $line -notmatch "(?i)$allowed") { Add-Problem $file.FullName $number 'SQLite is mentioned without saying it was removed' }
        if ($line -match '(?i)windows service|self-?updat|installer|update manifest' -and $line -notmatch "(?i)$allowed") { Add-Problem $file.FullName $number 'removed Windows-service/installer/updater material is described as usable' }
        if ($line -match '(?i)github actions|workflow_dispatch|\.github/workflows' -and $line -notmatch "(?i)$allowed|do not use|not be used|without running") { Add-Problem $file.FullName $number 'GitHub Actions presented as part of the build or release path' }
        if ($line -match 'dotnet test' -and $line -notmatch '(?i)container|global\.json|Microsoft\.Testing|--filter-class|never|inside|not' -and $relative -ne 'docs/Releasing.md') { Add-Problem $file.FullName $number "'dotnet test' without saying it runs in the container" }
    }
}

# Scripts: references to sibling files must exist.
$scripts = @(Get-ChildItem -LiteralPath (Join-Path $root 'eng') -Include '*.ps1', '*.py' -File -Recurse)
foreach ($script in $scripts) {
    $lines = [IO.File]::ReadAllLines($script.FullName)
    for ($i = 0; $i -lt $lines.Length; $i++) {
        foreach ($m in [regex]::Matches($lines[$i], '\$PSScriptRoot[\\/]+([A-Za-z0-9_.\-\\/]+?\.(?:ps1|py|sql|json|yaml))')) {
            $target = Join-Path $script.DirectoryName $m.Groups[1].Value
            if (-not (Test-Path -LiteralPath $target)) { Add-Problem $script.FullName ($i + 1) "referenced script file does not exist: $($m.Groups[1].Value)" }
        }
        foreach ($m in [regex]::Matches($lines[$i], "Join-Path \`$PSScriptRoot '([^']+\.(?:ps1|py|sql|json|yaml))'")) {
            $target = Join-Path $script.DirectoryName $m.Groups[1].Value
            if (-not (Test-Path -LiteralPath $target)) { Add-Problem $script.FullName ($i + 1) "referenced script file does not exist: $($m.Groups[1].Value)" }
        }
    }
}

if ($errors.Count -gt 0) {
    $errors | ForEach-Object { Write-Host $_ -ForegroundColor Red }
    throw "$($errors.Count) documentation problem(s) found."
}
Write-Host "Documentation and script references are consistent ($($markdown.Count) Markdown files, $($scripts.Count) scripts checked)."
