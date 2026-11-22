# Deterministic Python 3 discovery for the portable static checks (Windows and macOS).
# Dot-source this file; it has no WSL/Docker dependency and never installs anything.
#
# Resolve-PythonExecutable: an explicit path/name wins; otherwise the first python3, then python, on PATH
# that is a real Python >= 3.11. The Windows Store alias stub (WindowsApps\python.exe) is skipped. Always
# returns one string (or $null when nothing qualifies and no explicit value was given).

$script:PythonMinimumMajor = 3
$script:PythonMinimumMinor = 11  # audit_image_archive.py uses hashlib.file_digest (3.11)

function Test-PythonStoreStub([string] $Path) {
    $Path -match '(?i)[\\/]WindowsApps[\\/]python3?(\.\d+)?\.exe$'
}

function Test-PythonInterpreter([string] $Path) {
    try {
        $null = & $Path -I -B -c "import sys; sys.exit(0 if sys.version_info >= ($script:PythonMinimumMajor, $script:PythonMinimumMinor) else 1)" 2>&1
        $LASTEXITCODE -eq 0
    } catch { $false }
}

# Pure selection step (testable with a fake probe): order is preserved, stubs and failing probes are skipped.
function Select-PythonCandidate([string[]] $Candidates, [scriptblock] $Probe = { param($p) Test-PythonInterpreter $p }) {
    foreach ($candidate in $Candidates) {
        if (-not $candidate -or (Test-PythonStoreStub $candidate)) { continue }
        if (& $Probe $candidate) { return [string]$candidate }
    }
    $null
}

function Resolve-PythonExecutable([string] $Requested) {
    if ($Requested) {
        $command = Get-Command $Requested -CommandType Application -ErrorAction Stop | Select-Object -First 1
        $path = [string]$command.Source
        if (-not (Test-PythonInterpreter $path)) { throw "'$Requested' is not a working Python $script:PythonMinimumMajor.$script:PythonMinimumMinor or newer." }
        return $path
    }
    $paths = foreach ($name in 'python3', 'python') {
        Get-Command $name -All -CommandType Application -ErrorAction SilentlyContinue | ForEach-Object { [string]$_.Source }
    }
    Select-PythonCandidate @($paths)
}
