[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [ValidateSet('Check', 'Clean')]
    [string] $Mode = 'Check',

    [ValidateRange(1, 10240)]
    [int] $MaxExternalBuildMegabytes = 1536,

    [ValidateRange(1, 10240)]
    [int] $MaxDevelopmentDataMegabytes = 512,

    [switch] $RemoveDevelopmentData
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd([IO.Path]::DirectorySeparatorChar)
$projectPrefix = $projectRoot + [IO.Path]::DirectorySeparatorChar
$parentRoot = [IO.Path]::GetFullPath((Split-Path -Parent $projectRoot)).TrimEnd([IO.Path]::DirectorySeparatorChar)
$externalBuildRoot = [IO.Path]::GetFullPath((Join-Path $parentRoot 'PremiereCalendar.Build'))
$developmentDataRoot = [IO.Path]::GetFullPath((Join-Path $parentRoot 'PremiereCalendar.LocalData'))

function Assert-ProjectPath([string] $Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($projectPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to operate outside the project: $fullPath"
    }
    $fullPath
}

function Get-TreeSize([string] $Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return 0L }
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        return [long](Get-Item -LiteralPath $Path -Force).Length
    }
    [long]((Get-ChildItem -LiteralPath $Path -File -Force -Recurse -ErrorAction SilentlyContinue |
        Measure-Object -Property Length -Sum).Sum)
}

$fixedRelativePaths = @(
    '.dotnet',
    '.playwright-cli',
    '.playwright-mcp',
    '.run',
    'artifacts',
    'output',
    'PremiereCalendar/App_Data'
)

$targets = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($relativePath in $fixedRelativePaths) {
    $candidate = Assert-ProjectPath (Join-Path $projectRoot $relativePath)
    if (Test-Path -LiteralPath $candidate) { $null = $targets.Add($candidate) }
}

$gitPrefix = (Join-Path $projectRoot '.git') + [IO.Path]::DirectorySeparatorChar
$secretsPrefix = (Join-Path $projectRoot '.secrets') + [IO.Path]::DirectorySeparatorChar
Get-ChildItem -LiteralPath $projectRoot -Directory -Force -Recurse -ErrorAction SilentlyContinue |
    Where-Object {
        $_.Name -in @('bin', 'obj', 'TestResults') -and
        -not $_.FullName.StartsWith($gitPrefix, [StringComparison]::OrdinalIgnoreCase) -and
        -not $_.FullName.StartsWith($secretsPrefix, [StringComparison]::OrdinalIgnoreCase)
    } |
    ForEach-Object { $null = $targets.Add((Assert-ProjectPath $_.FullName)) }

Get-ChildItem -LiteralPath $projectRoot -File -Force -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -like '*.png' -or $_.Name -like '*-snapshot.md' } |
    ForEach-Object { $null = $targets.Add((Assert-ProjectPath $_.FullName)) }

$rows = @($targets | Sort-Object | ForEach-Object {
    [pscustomobject]@{ Path = $_; Megabytes = [math]::Round((Get-TreeSize $_) / 1MB, 1) }
})
$repositoryMegabytes = [math]::Round((($rows | Measure-Object -Property Megabytes -Sum).Sum), 1)
$externalBuildMegabytes = [math]::Round((Get-TreeSize $externalBuildRoot) / 1MB, 1)
$developmentDataMegabytes = [math]::Round((Get-TreeSize $developmentDataRoot) / 1MB, 1)

if ($Mode -eq 'Check') {
    if ($rows.Count -gt 0) { $rows | Format-Table -AutoSize }
    Write-Host "Generated data inside checkout: $repositoryMegabytes MB"
    Write-Host "Shared external build output: $externalBuildMegabytes MB"
    Write-Host "External Development data: $developmentDataMegabytes MB"

    if ($rows.Count -gt 0) {
        throw 'Generated files are present inside the source checkout. Run this script with -Mode Clean.'
    }
    if ($externalBuildMegabytes -gt $MaxExternalBuildMegabytes) {
        throw "The shared build output exceeds $MaxExternalBuildMegabytes MB. Run this script with -Mode Clean."
    }
    if ($developmentDataMegabytes -gt $MaxDevelopmentDataMegabytes) {
        throw "Development data exceeds $MaxDevelopmentDataMegabytes MB. Review it, then use -Mode Clean -RemoveDevelopmentData when a local reset is safe."
    }
    return
}

foreach ($target in $targets | Sort-Object -Descending) {
    if ($PSCmdlet.ShouldProcess($target, 'Remove generated workspace content')) {
        Remove-Item -LiteralPath $target -Recurse -Force
    }
}

if (Test-Path -LiteralPath $externalBuildRoot) {
    if (-not $externalBuildRoot.StartsWith($parentRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $externalBuildRoot) -ne 'PremiereCalendar.Build') {
        throw "Refusing to remove unexpected external build path: $externalBuildRoot"
    }
    if ($PSCmdlet.ShouldProcess($externalBuildRoot, 'Remove shared PremiereCalendar build output')) {
        Remove-Item -LiteralPath $externalBuildRoot -Recurse -Force
    }
}

if ($RemoveDevelopmentData -and (Test-Path -LiteralPath $developmentDataRoot)) {
    if (-not $developmentDataRoot.StartsWith($parentRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $developmentDataRoot) -ne 'PremiereCalendar.LocalData') {
        throw "Refusing to remove unexpected Development data path: $developmentDataRoot"
    }
    if ($PSCmdlet.ShouldProcess($developmentDataRoot, 'Remove local PremiereCalendar Development data')) {
        Remove-Item -LiteralPath $developmentDataRoot -Recurse -Force
    }
}

Write-Host 'Workspace cleanup completed.'
