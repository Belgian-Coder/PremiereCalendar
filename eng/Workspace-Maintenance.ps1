#Requires -Version 7.0
[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateSet('Check', 'Clean')]
    [string] $Mode = 'Check',
    [Alias('MaxExternalBuildMegabytes')]
    [ValidateRange(1, 102400)]
    [int] $MaxBuildMegabytes = 1536,
    [ValidateRange(1, 102400)]
    [int] $MaxDevelopmentDataMegabytes = 512,
    [switch] $RemoveDevelopmentData
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\', '/')
$projectPrefix = $projectRoot + [IO.Path]::DirectorySeparatorChar
$buildRoot = Join-Path $projectRoot 'artifacts/build'
$resultsRoot = Join-Path $projectRoot 'artifacts/test-results'
$developmentDataRoot = Join-Path $projectRoot '.local-data'

function Get-TreeSize([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return 0L }
    [long]((Get-ChildItem -LiteralPath $Path -File -Force -Recurse -ErrorAction SilentlyContinue |
        Measure-Object -Property Length -Sum).Sum)
}

# Prune other worktrees, secrets, tools and expected caches rather than recursively scanning them.
$pending = [Collections.Generic.Stack[string]]::new()
$pending.Push($projectRoot)
$leaked = [Collections.Generic.List[string]]::new()
while ($pending.Count -gt 0) {
    foreach ($directory in Get-ChildItem -LiteralPath $pending.Pop() -Directory -Force) {
        if ($directory.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
        if ($directory.Name -in @('.git', '.worktrees', '.secrets', '.local-data', 'artifacts', '.dotnet', '.vs', '.idea', 'node_modules', '.scratch', '.bench')) { continue }
        if ($directory.Name -in @('bin', 'obj', 'TestResults')) { $leaked.Add($directory.FullName) } else { $pending.Push($directory.FullName) }
    }
}
$buildMegabytes = [math]::Round((Get-TreeSize $buildRoot) / 1MB, 1)
$dataMegabytes = [math]::Round((Get-TreeSize $developmentDataRoot) / 1MB, 1)
Write-Host "This checkout's build output: $buildMegabytes MB"
Write-Host "This checkout's Development data: $dataMegabytes MB"
if ($Mode -eq 'Check') {
    if ($leaked.Count) { throw "Unexpected generated directories: $($leaked -join ', '). Review before cleaning." }
    if ($buildMegabytes -gt $MaxBuildMegabytes) { throw "Build output exceeds $MaxBuildMegabytes MB; review before cleaning." }
    if ($dataMegabytes -gt $MaxDevelopmentDataMegabytes) { throw "Development data exceeds $MaxDevelopmentDataMegabytes MB; review before resetting it." }
    return
}
$targets = @($buildRoot, $resultsRoot) + @($leaked)
if ($RemoveDevelopmentData) { $targets += $developmentDataRoot }
foreach ($target in $targets) {
    $absolute = [IO.Path]::GetFullPath($target)
    if (-not $absolute.StartsWith($projectPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing a path outside this checkout: $absolute" }
    if (-not (Test-Path -LiteralPath $absolute)) { continue }
    if (((Get-Item -LiteralPath $absolute -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -or
        @(Get-ChildItem -LiteralPath $absolute -Directory -Force -Recurse | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
        throw "Refusing to clean a tree containing a link: $absolute"
    }
    if ($PSCmdlet.ShouldProcess($absolute, 'Remove generated content from this checkout')) {
        Remove-Item -LiteralPath $absolute -Recurse -Force
    }
}
# Package caches, SDKs, credentials, sibling paths and other worktrees are always preserved.
