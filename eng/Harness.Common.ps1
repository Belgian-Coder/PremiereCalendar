# Shared only by the local development helpers; dot-source without running WSL.
function ConvertTo-BashLiteral([string] $Value) {
    "'" + $Value.Replace("'", "'\''") + "'"
}

function Get-HarnessContext([string] $Root) {
    $rootPath = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    $common = & git -C $rootPath rev-parse --git-common-dir
    if ($LASTEXITCODE -ne 0) { throw 'The harness must run from a Git checkout.' }
    $commonPath = if ([IO.Path]::IsPathRooted($common)) { [IO.Path]::GetFullPath($common) } else { [IO.Path]::GetFullPath((Join-Path $rootPath $common)) }
    $primaryRoot = Split-Path -Parent $commonPath
    $suffix = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes($rootPath.ToLowerInvariant()))).Substring(0, 10).ToLowerInvariant()
    [pscustomobject]@{
        Root = $rootPath
        BuildRoot = Join-Path $rootPath 'artifacts/build/linux'
        PackageRoot = Join-Path $primaryRoot 'artifacts/packages/linux'
        ComposeProject = if ($rootPath -eq $primaryRoot) { 'premierecalendar-dev' } else { "premierecalendar-dev-$suffix" }
    }
}

function ConvertTo-WslPath([string] $Path) {
    $result = & wsl.exe -d Ubuntu-24.04 -- wslpath -a ($Path -replace '\\', '/')
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($result)) { throw "Could not resolve WSL path: $Path" }
    $result.Trim()
}

function Invoke-HarnessWsl([string] $Script) {
    # stdin avoids the Windows -> bash command-line quoting layer.
    (($Script -replace "`r`n", "`n").TrimEnd() + "`n# end of script") |
        & wsl.exe -d Ubuntu-24.04 -- bash -s | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "WSL command failed (exit $LASTEXITCODE). Docker must already be running." }
}
