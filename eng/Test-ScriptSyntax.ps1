#Requires -Version 7.0
<#
.SYNOPSIS
Parses every PowerShell and Python script in the repository without running any of them.

.DESCRIPTION
Static check that works from any folder. PowerShell files are parsed with the PowerShell language parser;
Python files are compiled in memory (nothing is written next to the sources). Also fails when a script that
must stay LF (*.py, *.sh, *.sql) contains CRLF, because those files run inside Linux containers and WSL.
Files ignored by git (scratch folders, artifacts, build output) are skipped.

.EXAMPLE
pwsh -File D:\Projects\PremiereCalendar\eng\Test-ScriptSyntax.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
# Tracked plus untracked-but-not-ignored files: fast, and it skips ignored scratch folders, artifacts and build output.
$names = (& git -C $root ls-files -z --cached --others --exclude-standard) -split "`0" | Where-Object { $_ -match '\.(ps1|py|sh|sql)$' }
if ($LASTEXITCODE -ne 0) { throw 'git ls-files failed.' }
$files = @($names | ForEach-Object { Join-Path $root $_ } | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Get-Item)
$problems = [Collections.Generic.List[string]]::new()
$python = Get-Command python -ErrorAction SilentlyContinue
if (-not $python) { $python = Get-Command python3 -ErrorAction SilentlyContinue }
$pyChecked = 0
$psChecked = 0

foreach ($file in $files) {
    $relative = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
    switch ($file.Extension.ToLowerInvariant()) {
        '.ps1' {
            $psChecked++
            $tokens = $null; $parseErrors = $null
            [void][System.Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$parseErrors)
            foreach ($e in $parseErrors) { $problems.Add("${relative}:$($e.Extent.StartLineNumber): $($e.Message)") }
        }
        '.py' {
            if (-not $python) { $problems.Add("${relative}: python is not available to check this file"); break }
            $pyChecked++
            $code = 'import sys; compile(open(sys.argv[1], encoding="utf-8").read(), sys.argv[1], "exec")'
            $output = & $python.Source -I -c $code $file.FullName 2>&1
            if ($LASTEXITCODE -ne 0) { $problems.Add("${relative}: $($output -join ' ')") }
        }
    }
    if ($file.Extension -in '.py', '.sh', '.sql') {
        if ([IO.File]::ReadAllText($file.FullName).Contains("`r")) { $problems.Add("${relative}: contains CR characters; it must be LF") }
    }
}

if ($problems.Count -gt 0) {
    $problems | ForEach-Object { Write-Host $_ -ForegroundColor Red }
    throw "$($problems.Count) script problem(s) found."
}
Write-Host "All scripts parse: $psChecked PowerShell and $pyChecked Python files ($($files.Count) script files in total)."
