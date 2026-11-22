# Shared table, runner and change classifier for eng/Invoke-StaticChecks.ps1 and eng/Invoke-Validation.ps1.
# Dot-source only; no WSL, Docker, SDK or network. Works with PowerShell 7 on Windows and macOS.
. (Join-Path $PSScriptRoot 'Python.Common.ps1')

# One row per portable static check. Paths are repo-relative with '/'.
#   Kind    pwsh (script via pwsh -File) | python (script via the resolved interpreter) | git (git arguments)
#   UsesPython  pwsh script accepts -PythonExecutable and receives the resolved interpreter
#   Weight  rough serial seconds on a warm host; the runner starts heavy checks first
#   Group   checks with the same non-empty Group never run at the same time. Fixtures never collide (each lives in its own
#           GUID-named sandbox); 'timing' groups the checks with sub-second child-process deadlines so they do not starve each other.
#   Covers  regexes over changed paths; a changed file that no check covers is "unsure" and runs all checks.
#           Every check also covers its own script.
function Get-StaticCheckTable {
    $common = '^eng/(Harness\.Common\.ps1|Wsl\.Transport\.ps1|Python\.Common\.ps1)$'
    $fixtures = '^tests/fixtures/'
    @(
        [pscustomobject]@{ Name = 'Harness';                 Kind = 'pwsh';   Path = 'tests/Harness.Tests.ps1';                 Args = @(); UsesPython = $false; Weight = 99;  Group = ''; Covers = @('^eng/(wsl-docker\.ps1|Test-WithPostgres\.ps1|Harness\.Common\.ps1|Wsl\.Transport\.ps1|run-with-sdk-cache\.sh|release/Invoke-ReleaseTests\.ps1)$', '^(compose\.dev\.yaml|Directory\.Build\.props)$', $fixtures) }
        [pscustomobject]@{ Name = 'HarnessProcess';          Kind = 'pwsh';   Path = 'tests/HarnessProcess.Tests.ps1';          Args = @(); UsesPython = $true;  Weight = 5;   Group = 'timing'; Covers = @($common, $fixtures) }
        [pscustomobject]@{ Name = 'ReleaseTransport';        Kind = 'pwsh';   Path = 'tests/ReleaseTransport.Tests.ps1';        Args = @(); UsesPython = $true;  Weight = 10;  Group = 'timing'; Covers = @($common, '^eng/release/', $fixtures) }
        [pscustomobject]@{ Name = 'HarnessCacheRoot';        Kind = 'pwsh';   Path = 'tests/HarnessCacheRoot.Tests.ps1';        Args = @(); UsesPython = $false; Weight = 111; Group = ''; Covers = @('^eng/(Harness\.Common\.ps1|release-targets\.json|release/)') }
        [pscustomobject]@{ Name = 'ExistingRestoreFixture';  Kind = 'pwsh';   Path = 'tests/ExistingRestoreFixture.Tests.ps1';  Args = @(); UsesPython = $false; Weight = 28;  Group = ''; Covers = @('^eng/release/') }
        [pscustomobject]@{ Name = 'ReleaseDockerArguments';  Kind = 'pwsh';   Path = 'tests/ReleaseDockerArguments.Tests.ps1';  Args = @(); UsesPython = $true;  Weight = 3;   Group = 'timing'; Covers = @($common, '^eng/release/', $fixtures) }
        [pscustomobject]@{ Name = 'ValidationTools';         Kind = 'pwsh';   Path = 'tests/ValidationTools.Tests.ps1';         Args = @(); UsesPython = $true;  Weight = 30;  Group = ''; Covers = @('^eng/(Test-Docs|Test-ScriptSyntax|Python\.Common)\.ps1$', '^eng/release/Invoke-Candidate\.ps1$') }
        [pscustomobject]@{ Name = 'SdkCache';                Kind = 'pwsh';   Path = 'tests/SdkCache.Tests.ps1';                Args = @(); UsesPython = $false; Weight = 10;  Group = ''; Covers = @('^eng/run-with-sdk-cache\.sh$') }
        [pscustomobject]@{ Name = 'PerformanceLogAudit';     Kind = 'pwsh';   Path = 'tests/PerformanceLogAudit.Tests.ps1';     Args = @(); UsesPython = $false; Weight = 7;   Group = ''; Covers = @('^eng/Read-CalendarPerformance\.ps1$') }
        [pscustomobject]@{ Name = 'ReleaseOrchestrator';     Kind = 'pwsh';   Path = 'tests/ReleaseOrchestrator.Tests.ps1';     Args = @(); UsesPython = $false; Weight = 45;  Group = ''; Covers = @('^eng/release/') }
        [pscustomobject]@{ Name = 'StaticChecks';           Kind = 'pwsh';   Path = 'tests/StaticChecks.Tests.ps1';            Args = @(); UsesPython = $true;  Weight = 8;   Group = ''; Covers = @('^eng/(StaticChecks\.Common|Invoke-StaticChecks|Invoke-Validation|Python\.Common)\.ps1$') }
        [pscustomobject]@{ Name = 'ScriptSyntax';            Kind = 'pwsh';   Path = 'eng/Test-ScriptSyntax.ps1';               Args = @(); UsesPython = $true;  Weight = 5;   Group = ''; Covers = @('\.(ps1|py|sh|sql)$', '^\.gitattributes$') }
        [pscustomobject]@{ Name = 'Docs';                    Kind = 'pwsh';   Path = 'eng/Test-Docs.ps1';                       Args = @('-SourceOnly'); UsesPython = $false; Weight = 7; Group = ''; Covers = @('\.md$', '^docs/', '^eng/.*\.ps1$', '^LICENSE$') }
        [pscustomobject]@{ Name = 'SourceFingerprints';      Kind = 'python'; Path = 'tests/test_source_fingerprints.py';       Args = @('-B'); UsesPython = $true;  Weight = 38;  Group = ''; Covers = @('^eng/release/') }
        [pscustomobject]@{ Name = 'ReviewInputs';            Kind = 'python'; Path = 'tests/test_review_inputs.py';             Args = @('-B'); UsesPython = $true;  Weight = 9;   Group = ''; Covers = @('^eng/release/') }
        [pscustomobject]@{ Name = 'GitDiffCheck';            Kind = 'git';    Path = '';                                        Args = @('diff', '--check', 'HEAD'); UsesPython = $false; Weight = 1; Group = ''; Covers = @() }
    )
}

function Get-PwshExecutable { Join-Path $PSHOME ($(if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' })) }

# Turn a table row into a runnable command {Name, FileName, Arguments, WorkingDirectory, Weight, Group}.
function ConvertTo-StaticCheckCommand($Check, [string] $Root, [string] $Python) {
    switch ($Check.Kind) {
        'pwsh' {
            $arguments = @('-NoProfile', '-NonInteractive', '-File', (Join-Path $Root $Check.Path)) + @($Check.Args)
            if ($Check.UsesPython -and $Python) { $arguments += @('-PythonExecutable', $Python) }
            $file = Get-PwshExecutable
        }
        'python' {
            if (-not $Python) { throw "Check '$($Check.Name)' needs Python 3.10 or newer; pass -PythonExecutable." }
            $file = $Python
            $arguments = @($Check.Args) + @((Join-Path $Root $Check.Path))
        }
        'git' { $file = 'git'; $arguments = @('-C', $Root) + @($Check.Args) }
        default { throw "Unknown check kind '$($Check.Kind)'." }
    }
    [pscustomobject]@{ Name = $Check.Name; FileName = $file; Arguments = [string[]]$arguments; WorkingDirectory = $Root; Weight = [double]$Check.Weight; Group = [string]$Check.Group }
}

function Select-StaticChecks($Table, [string[]] $Only, [string[]] $Exclude) {
    $selected = @($Table)
    if ($Only) {
        foreach ($pattern in $Only) { if (-not @($Table | Where-Object { $_.Name -like $pattern })) { throw "-Only '$pattern' matches no static check. Known: $(($Table.Name) -join ', ')." } }
        $selected = @($selected | Where-Object { $name = $_.Name; @($Only | Where-Object { $name -like $_ }).Count -gt 0 })
    }
    if ($Exclude) {
        foreach ($pattern in $Exclude) { if (-not @($Table | Where-Object { $_.Name -like $pattern })) { throw "-Exclude '$pattern' matches no static check. Known: $(($Table.Name) -join ', ')." } }
        $selected = @($selected | Where-Object { $name = $_.Name; @($Exclude | Where-Object { $name -like $_ }).Count -eq 0 })
    }
    , @($selected)
}

# Runs one child process with merged-by-section output captured to a log; used inside parallel runspaces.
$script:StaticCheckProcessRunner = @'
param($Command, [string] $LogDirectory, [int] $TimeoutSeconds)
$log = Join-Path $LogDirectory ($Command.Name + '.log')
$clock = [Diagnostics.Stopwatch]::StartNew()
$exit = -1; $status = 'FAIL'; $text = ''
try {
    $start = [Diagnostics.ProcessStartInfo]::new($Command.FileName)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.WorkingDirectory = $Command.WorkingDirectory
    $start.Environment['GIT_OPTIONAL_LOCKS'] = '0'
    $start.Environment['GIT_TERMINAL_PROMPT'] = '0'
    foreach ($argument in $Command.Arguments) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $start
    try {
        [void]$process.Start()
        $out = $process.StandardOutput.ReadToEndAsync(); $err = $process.StandardError.ReadToEndAsync()
        if ($process.WaitForExit($TimeoutSeconds * 1000)) {
            $process.WaitForExit()
            $exit = $process.ExitCode
            $status = if ($exit -eq 0) { 'PASS' } else { 'FAIL' }
            $text = $out.GetAwaiter().GetResult()
            $errText = $err.GetAwaiter().GetResult()
            if ($errText) { $text += "`n[stderr]`n" + $errText }
        } else {
            try { $process.Kill($true) } catch { }
            $status = 'TIMEOUT'
            $text = "Exceeded $TimeoutSeconds seconds; process tree killed."
        }
    } finally { $process.Dispose() }
} catch { $text = "Could not run: $($_.Exception.Message)" }
$clock.Stop()
[IO.File]::WriteAllText($log, "# $($Command.FileName) $($Command.Arguments -join ' ')`n# exit=$exit status=$status seconds=$([math]::Round($clock.Elapsed.TotalSeconds, 1))`n$text")
[pscustomobject]@{ Name = $Command.Name; Status = $status; ExitCode = $exit; Seconds = [math]::Round($clock.Elapsed.TotalSeconds, 1); Log = $log }
'@

# Runs commands concurrently (heaviest first). Commands sharing a Group run one after another inside one slot.
function Invoke-StaticCheckSet($Commands, [string] $LogDirectory, [int] $ThrottleLimit, [int] $TimeoutSeconds = 1200) {
    [void][IO.Directory]::CreateDirectory($LogDirectory)
    $units = @($Commands | Group-Object { if ($_.Group) { 'g:' + $_.Group } else { 'n:' + $_.Name } } |
        ForEach-Object { , @($_.Group) } |
        Sort-Object { -(($_ | Measure-Object Weight -Sum).Sum) })
    $runner = $script:StaticCheckProcessRunner
    @($units | ForEach-Object -ThrottleLimit ([math]::Max(1, $ThrottleLimit)) -Parallel {
        $run = [scriptblock]::Create($using:runner)
        foreach ($command in $_) { & $run $command $using:LogDirectory $using:TimeoutSeconds }
    })
}

function Write-StaticCheckSummary($Results, [double] $WallSeconds, [int] $TailLines = 30) {
    $rows = @($Results | Sort-Object Name)
    $width = [math]::Max(4, ($rows | ForEach-Object { $_.Name.Length } | Measure-Object -Maximum).Maximum)
    Write-Host ''
    Write-Host ('{0}  {1,-7}  {2,8}  {3}' -f 'Check'.PadRight($width), 'Status', 'Seconds', 'Log')
    foreach ($row in $rows) {
        $color = if ($row.Status -eq 'PASS') { 'Green' } else { 'Red' }
        Write-Host ('{0}  {1,-7}  {2,8:N1}  {3}' -f $row.Name.PadRight($width), $row.Status, $row.Seconds, $row.Log) -ForegroundColor $color
    }
    $failed = @($rows | Where-Object Status -ne 'PASS')
    $sum = ($rows | Measure-Object Seconds -Sum).Sum
    Write-Host ('Total wall time {0:N1} s for {1} check(s) (sum of check times {2:N1} s); {3} failed.' -f $WallSeconds, $rows.Count, $sum, $failed.Count)
    foreach ($row in $failed) {
        Write-Host ''
        Write-Host "--- $($row.Name) ($($row.Status), exit $($row.ExitCode)): last $TailLines lines of $($row.Log)" -ForegroundColor Red
        if (Test-Path -LiteralPath $row.Log) { Get-Content -LiteralPath $row.Log -Tail $TailLines | ForEach-Object { Write-Host $_ } }
    }
}

# Resolves the interpreter once; checks that need none still run when Python is absent.
function Resolve-StaticPython([string] $Requested, $Selected) {
    $needs = @($Selected | Where-Object { $_.UsesPython }).Count -gt 0
    if ($Requested -or $needs) {
        $python = Resolve-PythonExecutable $Requested
        if (-not $python -and $needs) { throw 'Python 3.10 or newer is required by the selected checks. Put python3 on PATH or pass -PythonExecutable <path>.' }
        return $python
    }
    $null
}

# ---------------------------------------------------------------- change classification

function Get-UiPathPattern { '\.(razor|css|scss|js|cshtml)$' }

# Classify repo-relative changed paths for docs/Testing.md "Scoped application checks".
# Returns: DocsOnly, RunAll (unsure), StaticNames, Suites, BrowserNeeded, BrowserProject, BuildApp, Reasons.
function Get-ChangeClassification([string[]] $Paths, $Table = (Get-StaticCheckTable)) {
    $paths = @($Paths | Where-Object { $_ } | ForEach-Object { $_.Replace('\', '/') } | Sort-Object -Unique)
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $suites = [Collections.Generic.HashSet[string]]::new()
    $reasons = [Collections.Generic.List[string]]::new()
    $runAll = $false; $app = $false; $browser = $false; $browserProject = $false; $nonDoc = $false
    $docPattern = '(\.md$|^docs/|^LICENSE$)'
    foreach ($path in $paths) {
        $isDoc = $path -match $docPattern
        if (-not $isDoc) { $nonDoc = $true }
        $covered = @($Table | Where-Object {
            $check = $_
            ($check.Path -and $check.Path -eq $path) -or @($check.Covers | Where-Object { $path -match $_ }).Count -gt 0 })
        if ($isDoc) { $covered = @($Table | Where-Object Name -eq 'Docs') }
        foreach ($check in $covered) { [void]$names.Add($check.Name) }
        $isApp = $false
        if ($path -match '^PremiereCalendar/') {
            $isApp = $true
            if ($path -match (Get-UiPathPattern)) { [void]$suites.Add('Component'); $browser = $true; $reasons.Add("${path}: UI file -> Component; Browser checks needed before release") }
            else { foreach ($s in 'Unit', 'Component', 'Integration') { [void]$suites.Add($s) }; $reasons.Add("${path}: application source -> Unit, Component, Integration") }
        } elseif ($path -match '^tests/PremiereCalendar\.(Unit|Component|Integration|Browser)Tests/') {
            $isApp = $true
            $suite = $Matches[1]
            if ($suite -eq 'Browser') { $browserProject = $true; $browser = $true; $reasons.Add("${path}: Browser tests need the prepared glibc browser image; not run by default") }
            else { [void]$suites.Add($suite); $reasons.Add("${path}: $suite test project -> $suite") }
        } elseif ($path -match '^tests/Shared/|^(Dockerfile|compose\.yaml|compose\.dev\.yaml|global\.json|Directory\.Build\.props|\.dockerignore|PremiereCalendar\.slnx)$|^tests/Directory\.Build\.props$') {
            $isApp = $true
            foreach ($s in 'Unit', 'Component', 'Integration') { [void]$suites.Add($s) }
            $reasons.Add("${path}: container/dependency/cross-cutting file -> Unit, Component, Integration")
        }
        if ($isApp) { $app = $true }
        elseif (-not $isDoc -and $covered.Count -eq 0) {
            $runAll = $true; $reasons.Add("${path}: not covered by any declared check -> all static checks")
        } elseif (-not $isDoc) { $reasons.Add("${path}: -> $((@($covered).Name) -join ', ')") }
    }
    $docsOnly = $paths.Count -gt 0 -and -not $nonDoc
    [void]$names.Add('GitDiffCheck')
    [pscustomobject]@{
        Paths = $paths; DocsOnly = $docsOnly; RunAll = $runAll
        StaticNames = @($Table | Where-Object { $names.Contains($_.Name) } | ForEach-Object Name)
        BuildApp = $app
        Suites = @(@('Unit', 'Component', 'Integration') | Where-Object { $suites.Contains($_) })
        BrowserNeeded = $browser; BrowserProject = $browserProject
        Reasons = @($reasons)
    }
}

# Builder commands (Windows/WSL) for an application change; never part of the portable static lane.
function Get-AppFollowUpCommands($Classification, [string] $Root) {
    $commands = [Collections.Generic.List[object]]::new()
    if ($Classification.BuildApp) {
        $commands.Add([pscustomobject]@{ Label = 'App build/check'; File = (Join-Path $Root 'eng/wsl-docker.ps1'); Arguments = @('check', '-Project', 'App') })
    }
    if ($Classification.Suites.Count -gt 0) {
        $commands.Add([pscustomobject]@{ Label = "Tests: $($Classification.Suites -join ', ')"; File = (Join-Path $Root 'eng/Test-WithPostgres.ps1'); Arguments = @('-Project', ($Classification.Suites -join ',')) })
    }
    $commands.ToArray()
}

# Changed paths: committed since the merge-base with $Base, plus staged, unstaged and untracked files.
function Get-ChangedPaths([string] $Root, [string] $Base = 'origin/main') {
    $mergeBase = (& git -C $Root merge-base HEAD $Base 2>$null)
    if ($LASTEXITCODE -ne 0 -or -not $mergeBase) { throw "Could not find a merge-base between HEAD and '$Base'. Pass -Base <ref>." }
    $tracked = (& git -C $Root diff --name-only --no-renames -z $mergeBase.Trim()) -split "`0"
    if ($LASTEXITCODE -ne 0) { throw 'git diff failed.' }
    $untracked = (& git -C $Root ls-files --others --exclude-standard -z) -split "`0"
    if ($LASTEXITCODE -ne 0) { throw 'git ls-files failed.' }
    @($tracked + $untracked | Where-Object { $_ } | Sort-Object -Unique)
}
