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
        [pscustomobject]@{ Name = 'NativeLane';              Kind = 'pwsh';   Path = 'tests/NativeLane.Tests.ps1';              Args = @(); UsesPython = $false; Weight = 15;  Group = ''; Covers = @('^eng/(Test-WithPostgres|Harness\.Common|StaticChecks\.Common|Invoke-Validation)\.ps1$', '^global\.json$') }
        [pscustomobject]@{ Name = 'HarnessPolicy';           Kind = 'pwsh';   Path = 'tests/HarnessPolicy.Tests.ps1';           Args = @(); UsesPython = $false; Weight = 12;  Group = ''; Covers = @('^eng/(Test-WithPostgres|wsl-docker|Harness\.Common|StaticChecks\.Common|Invoke-Validation|Invoke-StaticChecks)\.ps1$', '^eng/release/(Invoke-ReleaseTests|Invoke-Candidate|Invoke-LocalGates)\.ps1$', '^AGENTS\.md$', '^compose\.dev\.yaml$') }
        [pscustomobject]@{ Name = 'ReleaseOrchestrator';     Kind = 'pwsh';   Path = 'tests/ReleaseOrchestrator.Tests.ps1';     Args = @(); UsesPython = $false; Weight = 45;  Group = ''; Covers = @('^eng/release/') }
        [pscustomobject]@{ Name = 'ReleaseSpeed';            Kind = 'pwsh';   Path = 'tests/ReleaseSpeed.Tests.ps1';            Args = @(); UsesPython = $true;  Weight = 20;  Group = ''; Covers = @('^eng/release/', '^Dockerfile$') }
        [pscustomobject]@{ Name = 'StaticChecks';           Kind = 'pwsh';   Path = 'tests/StaticChecks.Tests.ps1';            Args = @(); UsesPython = $true;  Weight = 8;   Group = ''; Covers = @('^eng/(StaticChecks\.Common|Invoke-StaticChecks|Invoke-Validation|Python\.Common)\.ps1$') }
        [pscustomobject]@{ Name = 'ScriptSyntax';            Kind = 'pwsh';   Path = 'eng/Test-ScriptSyntax.ps1';               Args = @(); UsesPython = $true;  Weight = 5;   Group = ''; Covers = @('\.(ps1|py|sh|sql)$', '^\.gitattributes$') }
        [pscustomobject]@{ Name = 'Docs';                    Kind = 'pwsh';   Path = 'eng/Test-Docs.ps1';                       Args = @('-SourceOnly'); UsesPython = $false; Weight = 7; Group = ''; Covers = @('\.md$', '^docs/', '^eng/.*\.ps1$', '^LICENSE$') }
        [pscustomobject]@{ Name = 'SourceFingerprints';      Kind = 'python'; Path = 'tests/test_source_fingerprints.py';       Args = @('-B'); UsesPython = $true;  Weight = 38;  Group = ''; Covers = @('^eng/release/') }
        [pscustomobject]@{ Name = 'ReviewInputs';            Kind = 'python'; Path = 'tests/test_review_inputs.py';             Args = @('-B'); UsesPython = $true;  Weight = 9;   Group = ''; Covers = @('^eng/release/') }
        [pscustomobject]@{ Name = 'GitDiffCheck';            Kind = 'git';    Path = '';                                        Args = @('diff', '--check', 'HEAD'); UsesPython = $false; Weight = 1; Group = ''; Covers = @() }
    )
}

# The running pwsh first: $PSHOME has no native pwsh when PowerShell is a .NET global tool (Linux/macOS installs
# via `dotnet tool`), so fall back from the process to $PSHOME to PATH, and fail clearly instead of every check.
function Get-PwshExecutable {
    $name = if ($IsWindows) { 'pwsh.exe' } else { 'pwsh' }
    $own = [Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
    if ($own -and [IO.Path]::GetFileNameWithoutExtension($own) -eq 'pwsh' -and (Test-Path -LiteralPath $own -PathType Leaf)) { return $own }
    $fromHome = Join-Path $PSHOME $name
    if (Test-Path -LiteralPath $fromHome -PathType Leaf) { return $fromHome }
    $command = Get-Command $name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { return [string]$command.Source }
    throw 'Could not locate the pwsh executable for the static checks.'
}

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
    # `pwsh -File ... -Only A,B` delivers "A,B" as one string; accept both forms.
    $Only = @($Only | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    $Exclude = @($Exclude | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
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
            $exit = $process.ExitCode
            $status = if ($exit -eq 0) { 'PASS' } else { 'FAIL' }
            # A leftover grandchild can hold the pipes open after the check exited; bound the drain instead of
            # waiting for end-of-stream forever (the parameterless WaitForExit would).
            if ([Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($out, $err), 30000)) {
                $text = $out.Result
                if ($err.Result) { $text += "`n[stderr]`n" + $err.Result }
            } else {
                $status = 'FAIL'
                $text = "The check exited ($exit) but its output pipes stayed open for 30 s (a leftover child process?); output incomplete."
            }
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
    $results = @($units | ForEach-Object -ThrottleLimit ([math]::Max(1, $ThrottleLimit)) -Parallel {
        $run = [scriptblock]::Create($using:runner)
        foreach ($command in $_) { & $run $command $using:LogDirectory $using:TimeoutSeconds }
    })
    # A worker that threw reports nothing; a check without a result must fail rather than drop out of the summary.
    $reported = @($results | ForEach-Object Name)
    foreach ($command in $Commands) {
        if ($command.Name -notin $reported) {
            $results += [pscustomobject]@{ Name = $command.Name; Status = 'FAIL'; ExitCode = -1; Seconds = 0; Log = (Join-Path $LogDirectory ($command.Name + '.log')) }
        }
    }
    $results
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
    # Every service test project references the application, so the test run's single MSBuild pass already
    # compiles it in the same configuration; a separate App check is only needed when no suite runs.
    if ($Classification.BuildApp -and $Classification.Suites.Count -eq 0) {
        $commands.Add([pscustomobject]@{ Label = 'App build/check'; File = (Join-Path $Root 'eng/wsl-docker.ps1'); Arguments = @('check', '-Project', 'App') })
    }
    if ($Classification.Suites.Count -gt 0) {
        $commands.Add([pscustomobject]@{ Label = "Tests: $($Classification.Suites -join ', ')"; File = (Join-Path $Root 'eng/Test-WithPostgres.ps1'); Arguments = @('-Project', ($Classification.Suites -join ',')) })
    }
    $commands.ToArray()
}

# Which follow-up commands -Run may execute on this host. Windows runs them all (WSL/Docker builder). macOS and Linux run only the
# service-test commands, which use the native feedback lane (SDK + throwaway PostgreSQL on the host); builder-only commands
# (wsl-docker.ps1) stay printed as follow-up for the Windows builder. Native runs are feedback, never release acceptance.
function Select-RunnableFollowUps($FollowUp, [bool] $WindowsHost) {
    $run = [Collections.Generic.List[object]]::new(); $skipped = [Collections.Generic.List[object]]::new()
    foreach ($c in @($FollowUp)) {
        if ($WindowsHost -or [IO.Path]::GetFileName([string] $c.File) -eq 'Test-WithPostgres.ps1') { $run.Add($c) } else { $skipped.Add($c) }
    }
    [pscustomobject]@{ Run = $run.ToArray(); Skipped = $skipped.ToArray(); Lane = if ($WindowsHost) { 'wsl' } else { 'native' } }
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

# ---------------------------------------------------------------- test impact selection
# Narrows the service-suite feedback run to the test classes a change can reach. Pure text analysis (no SDK, no build):
#   1. declared types of every changed C#/Razor file (Razor: the component name from the file name, plus the base types
#      and interfaces it declares, so an interface/implementation change reaches the consumers of the other side);
#   2. one bounded hop: application/helper files that reference those types contribute their own declared types;
#   3. test classes (top-level types of files with [Fact]/[Theory]) whose text mentions any of those names as a whole word.
# Anything unmappable falls back to the whole affected suite, and every fallback names its reason.
# It over-selects by design (strings, comments and generic overlaps count as references) and never under-selects on purpose.
function Get-TestImpactSuites { @('Unit', 'Component', 'Integration') }

# Names too common to identify a type reliably; a changed declaration with one of these falls back to the suites.
function Get-GenericTypeNames {
    @('Options', 'Option', 'Result', 'Results', 'Item', 'Items', 'Entry', 'Entries', 'Context', 'Settings', 'Service', 'Helper', 'Helpers',
        'Utils', 'Util', 'Extensions', 'Constants', 'Program', 'Startup', 'Model', 'Models', 'Request', 'Response', 'Client', 'Handler',
        'Provider', 'Factory', 'Store', 'Cache', 'Manager', 'Base', 'Node', 'Key', 'Value', 'State', 'Status', 'Kind', 'Type', 'Data',
        'Info', 'Layout', 'App', 'Routes', 'Index', 'Home', 'Page', 'Main', 'Error', 'Count', 'Name', 'Id', 'Text', 'Path', 'Task')
}

# Files whose change can reach everything (wiring, project/props, global usings, assembly attributes, imports).
function Test-TestImpactInfrastructurePath([string] $Path) {
    $Path -match '^PremiereCalendar/(Program\.cs$|Hosting/|Properties/|.*\.csproj$|appsettings.*\.json$|Components/(_Imports|App|Routes)\.razor$|.*GlobalUsings.*\.cs$)' -or
    $Path -match '^tests/PremiereCalendar\.\w+Tests/(AssemblyConfiguration\.cs$|.*\.csproj$|.*GlobalUsings.*\.cs$|.*\.json$)' -or
    $Path -match '^tests/Shared/'
}

# Paths that cannot change what a service suite builds or runs: documentation, the release-only scripts, the harness's own
# PowerShell/Python tests and fixtures, repository metadata. Everything not listed here and not mapped above is treated as
# unclassified by Get-TestImpact (conservative: whole suites).
function Test-TestImpactIrrelevantPath([string] $Path) {
    $Path -match '(\.md$|^docs/|^LICENSE$)' -or
    $Path -match '^tests/[^/]+\.(ps1|py)$|^tests/fixtures/' -or
    $Path -match '^eng/release/' -or
    $Path -match '^(\.gitignore|\.gitattributes)$|^\.github/'
}

function Read-TypeDeclarations([string] $Text) {
    $decl = [regex]::new('(?m)^([ \t]*)(?:(?:public|internal|private|protected|file|sealed|static|abstract|partial|readonly|ref|unsafe|new)\s+)*(?:record\s+struct|record\s+class|record|class|struct|interface|enum)\s+([A-Za-z_]\w*)([^\r\n{;=]*)')
    foreach ($m in $decl.Matches($Text)) {
        [pscustomobject]@{ Name = $m.Groups[2].Value; TopLevel = ($m.Groups[1].Value.Length -eq 0); Rest = $m.Groups[3].Value }
    }
}

# Reads and tokenizes every candidate source once: Path, Kind (app|test), Suite, Types, TopTypes, Tokens, HasTests, Rest.
function New-TestImpactIndex([string] $Root) {
    $files = [Collections.Generic.List[object]]::new()
    $sources = @()
    $app = Join-Path $Root 'PremiereCalendar'
    if (Test-Path -LiteralPath $app) { $sources += @(Get-ChildItem -LiteralPath $app -Recurse -File | Where-Object { $_.Extension -in '.cs', '.razor' -and $_.FullName -notmatch '[\\/](obj|bin)[\\/]' } | ForEach-Object { , @($_, 'app', '') }) }
    foreach ($suite in (Get-TestImpactSuites)) {
        $dir = Join-Path $Root "tests/PremiereCalendar.${suite}Tests"
        if (Test-Path -LiteralPath $dir) { $sources += @(Get-ChildItem -LiteralPath $dir -Recurse -File -Filter *.cs | Where-Object { $_.FullName -notmatch '[\\/](obj|bin)[\\/]' } | ForEach-Object { , @($_, 'test', $suite) }) }
    }
    $rootFull = [IO.Path]::GetFullPath($Root).TrimEnd('\', '/')
    foreach ($s in $sources) {
        $file = $s[0]
        $text = [IO.File]::ReadAllText($file.FullName)
        $relative = $file.FullName.Substring($rootFull.Length + 1).Replace('\', '/')
        $declarations = @(Read-TypeDeclarations $text)
        $types = [Collections.Generic.List[string]]::new()
        if ($file.Extension -eq '.razor') { $types.Add($file.Name.Substring(0, $file.Name.Length - $file.Extension.Length)) }
        foreach ($d in $declarations) { $types.Add($d.Name) }
        # One native split and a bulk set constructor: orders of magnitude faster than adding regex matches one by one.
        $tokens = [Collections.Generic.HashSet[string]]::new([string[]]($text -split '\W+'), [StringComparer]::Ordinal)
        $files.Add([pscustomobject]@{
            Path = $relative; Kind = $s[1]; Suite = $s[2]
            Types = @($types | Select-Object -Unique)
            TopTypes = @($declarations | Where-Object TopLevel | ForEach-Object Name | Select-Object -Unique)
            Tokens = $tokens
            HasTests = ($s[1] -eq 'test' -and $text -match '(?m)^\s*\[(\w*Fact|\w*Theory)\b')
            Rest = (@($declarations | ForEach-Object { $_.Rest }) -join ' ')
        })
    }
    , $files.ToArray()
}

# Test counts per class from the newest CTRF of each suite below the given results folders; {} when none exist.
function Get-TestCountIndex([string[]] $ResultRoots) {
    $counts = @{}
    foreach ($suite in (Get-TestImpactSuites)) {
        $newest = @($ResultRoots | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | ForEach-Object { Get-ChildItem -LiteralPath $_ -Directory } |
                Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "$suite.ctrf.json") } | Sort-Object Name -Descending | Select-Object -First 1)
        if (-not $newest) { continue }
        try { $ctrf = Get-Content -LiteralPath (Join-Path $newest[0].FullName "$suite.ctrf.json") -Raw | ConvertFrom-Json } catch { continue }
        foreach ($t in $ctrf.results.tests) {
            $type = [string]$t.extra.type
            if ($type) { $key = "$suite|" + $type.Substring($type.LastIndexOf('.') + 1); $counts[$key] = 1 + [int]$counts[$key] }
        }
    }
    $counts
}

# Select tests for a change. $ChangedPaths are repo-relative. Returns
#   Suites: ordered rows {Suite, Mode (Classes|Full), Classes, TotalClasses, Reasons, EstimatedTests, FullTests}
#   Notes: per-file explanations; Selected: suites with work to do.
function Get-TestImpact([string] $Root, [string[]] $ChangedPaths, $TestCounts = @{}, [int] $MaxPercent = 40, [int] $MaxClasses = 60, $Index = $null, [int] $MinClassesForPercent = 10, [int] $MaxTestPercent = 60) {
    $suites = Get-TestImpactSuites
    $generic = [Collections.Generic.HashSet[string]]::new([string[]](Get-GenericTypeNames), [StringComparer]::Ordinal)
    $paths = @($ChangedPaths | Where-Object { $_ } | ForEach-Object { $_.Replace('\', '/') } | Sort-Object -Unique)
    $full = @{}; $classes = @{}; $notes = [Collections.Generic.List[string]]::new()
    foreach ($s in $suites) { $full[$s] = [Collections.Generic.List[string]]::new(); $classes[$s] = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal) }
    function Add-Full([string[]] $Which, [string] $Reason) { foreach ($s in $Which) { $full[$s].Add($Reason) } }
    $needIndex = $false
    $work = [Collections.Generic.List[object]]::new()
    foreach ($path in $paths) {
        if ($path -match '^PremiereCalendar/') {
            if (Test-TestImpactInfrastructurePath $path) { Add-Full $suites "${path}: shared infrastructure/wiring"; continue }
            if ($path -match '\.(cs|razor)$') { $needIndex = $true; $work.Add(@($path, 'app', '')); continue }
            if ($path -match (Get-UiPathPattern)) { Add-Full @('Component') "${path}: UI asset has no type to follow"; continue }
            Add-Full $suites "${path}: not a mappable source file"; continue
        }
        if ($path -match '^tests/PremiereCalendar\.(Unit|Component|Integration)Tests/') {
            $suite = $Matches[1]
            if (Test-TestImpactInfrastructurePath $path) { Add-Full @($suite) "${path}: shared test-project infrastructure"; continue }
            if ($path -match '\.cs$') { $needIndex = $true; $work.Add(@($path, 'test', $suite)); continue }
            Add-Full @($suite) "${path}: test data/project file"; continue
        }
        if ($path -match '^tests/PremiereCalendar\.BrowserTests/') { continue }  # flagged by the classifier, needs the browser image
        if ((Test-TestImpactInfrastructurePath $path) -or $path -match '^(Dockerfile|compose\.yaml|compose\.dev\.yaml|global\.json|Directory\.Build\.props|\.dockerignore|PremiereCalendar\.slnx)$|^tests/Directory\.Build\.props$') {
            Add-Full $suites "${path}: container/dependency/cross-cutting file"; continue
        }
        if (Test-TestImpactIrrelevantPath $path) { continue }
        # Anything else (eng/*.ps1 or *.sh that run the tests, an unknown root file such as .editorconfig or a package
        # props file) can change how every test builds or runs and cannot be mapped to classes: whole suites, with the reason.
        Add-Full $suites "${path}: unclassified path that may affect how tests build or run"
    }
    if ($needIndex) {
        if (-not $Index) { $Index = New-TestImpactIndex $Root }
        $known = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($f in $Index) { foreach ($t in $f.Types) { [void]$known.Add($t) } }
        $testFiles = @($Index | Where-Object HasTests)
        $suiteTotals = @{}; foreach ($s in $suites) { $suiteTotals[$s] = @($testFiles | Where-Object Suite -eq $s | ForEach-Object { $_.TopTypes } | Select-Object -Unique).Count }
        foreach ($w in $work) {
            $path = $w[0]; $kind = $w[1]; $own = $w[2]
            $entry = $Index | Where-Object Path -eq $path | Select-Object -First 1
            $fallbackSuites = if ($kind -eq 'test') { @($own) } elseif ($path -match (Get-UiPathPattern) -or $path -match '\.razor$') { @('Component') } else { $suites }
            if (-not $entry) { Add-Full $fallbackSuites "${path}: file not found in the index (deleted or renamed)"; continue }
            if ($entry.Types.Count -eq 0) { Add-Full $fallbackSuites "${path}: declares no type"; continue }
            $badName = @($entry.Types | Where-Object { $generic.Contains($_) -or $_.Length -lt 4 }) | Select-Object -First 1
            if ($badName) { Add-Full $fallbackSuites "${path}: type name '$badName' is too generic to match reliably"; continue }
            $direct = [Collections.Generic.List[object]]::new()
            if ($entry.HasTests) { foreach ($t in $entry.TopTypes) { $direct.Add([pscustomobject]@{ Suite = $entry.Suite; Class = $t }) } }
            $t0 = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            foreach ($t in $entry.Types) { [void]$t0.Add($t) }
            # Base types/interfaces and primary-constructor types the changed file declares count as changed too.
            foreach ($m in [regex]::Matches($entry.Rest, '[A-Za-z_]\w*')) { if ($known.Contains($m.Value) -and $m.Value -ne $entry.Types[0] -and -not $generic.Contains($m.Value) -and $m.Value.Length -ge 4) { [void]$t0.Add($m.Value) } }
            # One hop: unchanged app/helper files (not DI wiring) that use a changed type contribute their own types.
            $t1 = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
            $skippedHop = [Collections.Generic.List[string]]::new()
            foreach ($f in $Index) {
                if ($f.Path -eq $path -or $f.HasTests) { continue }
                if (Test-TestImpactInfrastructurePath $f.Path) { continue }
                $uses = $false; foreach ($t in $t0) { if ($f.Tokens.Contains($t)) { $uses = $true; break } }
                if (-not $uses) { continue }
                foreach ($t in $f.Types) {
                    if ($t0.Contains($t)) { continue }
                    if ($generic.Contains($t) -or $t.Length -lt 4) { $skippedHop.Add("$($f.Path): '$t'"); continue }
                    [void]$t1.Add($t)
                }
            }
            $names = @($t0) + @($t1)
            $hits = [Collections.Generic.List[object]]::new()
            foreach ($f in $testFiles) {
                if ($f.Path -eq $path) { continue }
                foreach ($t in $names) { if ($f.Tokens.Contains($t)) { foreach ($c in $f.TopTypes) { $hits.Add([pscustomobject]@{ Suite = $f.Suite; Class = $c }) }; break } }
            }
            $all = @($direct) + @($hits)
            if ($all.Count -eq 0) { Add-Full $fallbackSuites "${path}: no test references $((@($t0) | Select-Object -First 3) -join ', ') or its direct consumers"; continue }
            foreach ($h in $all) { [void]$classes[$h.Suite].Add($h.Class) }
            $notes.Add("${path}: types $((@($t0) | Select-Object -First 4) -join ', '); +$($t1.Count) consumer type(s); $(@($all | Select-Object -Property Suite, Class -Unique).Count) test class(es)")
            if ($skippedHop.Count -gt 0) { $notes.Add("${path}: one-hop search skipped generic consumer type(s): $(($skippedHop | Select-Object -First 3) -join '; ')") }
        }
        # Threshold fallback per suite
        foreach ($s in $suites) {
            $total = [int]$suiteTotals[$s]
            if ($classes[$s].Count -gt 0 -and $total -gt 0 -and ($classes[$s].Count -gt $MaxClasses -or (($classes[$s].Count * 100 / $total) -gt $MaxPercent -and $classes[$s].Count -gt $MinClassesForPercent))) {
                Add-Full @($s) "$($classes[$s].Count) of $total $s test classes selected (limit $MaxClasses classes, or $MaxPercent% above $MinClassesForPercent classes)"
            }
            # A selection that still holds most of the tests saves too little to justify a filtered run.
            if ($classes[$s].Count -gt 0 -and $full[$s].Count -eq 0) {
                $suiteTests = 0; $selectedTests = 0
                foreach ($k in $TestCounts.Keys) { if ($k -like "$s|*") { $suiteTests += $TestCounts[$k] } }
                foreach ($c in $classes[$s]) { $selectedTests += [int]$TestCounts["$s|$c"] }
                if ($suiteTests -gt 0 -and ($selectedTests * 100 / $suiteTests) -gt $MaxTestPercent) {
                    Add-Full @($s) "selected classes hold ~$selectedTests of ~$suiteTests $s tests (limit $MaxTestPercent%), narrowing saves too little"
                }
            }
        }
    }
    $rows = foreach ($s in $suites) {
        $isFull = $full[$s].Count -gt 0
        $total = if ($needIndex) { [int]$suiteTotals[$s] } else { 0 }
        $selected = @($classes[$s] | Sort-Object)
        if (-not $isFull -and $selected.Count -eq 0) { continue }
        $est = 0; $fullEst = 0; $haveCounts = $false
        foreach ($k in $TestCounts.Keys) { if ($k -like "$s|*") { $fullEst += $TestCounts[$k]; $haveCounts = $true } }
        if ($haveCounts) { foreach ($c in $selected) { $est += [int]$TestCounts["$s|$c"] } }
        [pscustomobject]@{
            Suite = $s; Mode = if ($isFull) { 'Full' } else { 'Classes' }; Classes = if ($isFull) { @() } else { $selected }
            TotalClasses = $total; Reasons = @($full[$s]); EstimatedTests = if ($isFull) { $fullEst } else { $est }; FullTests = $fullEst; CountsKnown = $haveCounts
        }
    }
    [pscustomobject]@{ Suites = @($rows); Notes = @($notes) }
}

# Filter value for one class: fully-qualified test names are Namespace.Class.Method, so '.Class.' cannot hit Class2 or XClass.
function ConvertTo-TestClassFilter([string] $ClassName) { ".$ClassName." }

function Get-TestImpactCommands($Impact, [string] $Root) {
    $script = Join-Path $Root 'eng/Test-WithPostgres.ps1'
    $commands = [Collections.Generic.List[object]]::new()
    $fullNames = @($Impact.Suites | Where-Object Mode -eq 'Full' | ForEach-Object Suite)
    $narrow = @($Impact.Suites | Where-Object Mode -eq 'Classes')
    if ($fullNames.Count -gt 0) { $commands.Add([pscustomobject]@{ Label = "Tests: $($fullNames -join ', ') (whole suites)"; File = $script; Arguments = @('-Project', ($fullNames -join ',')) }) }
    if ($narrow.Count -gt 0) {
        $filter = @($narrow | ForEach-Object { $_.Classes } | Select-Object -Unique | ForEach-Object { ConvertTo-TestClassFilter $_ })
        $commands.Add([pscustomobject]@{ Label = "Tests: $((@($narrow.Suite)) -join ', ') (selected classes)"; File = $script; Arguments = @('-Project', (@($narrow.Suite) -join ','), '-Filter', ($filter -join ',')) })
    }
    $commands.ToArray()
}

function Write-TestImpactReport($Impact) {
    foreach ($r in $Impact.Suites) {
        $est = if ($r.CountsKnown) { if ($r.Mode -eq 'Full') { "~$($r.FullTests) tests" } else { "~$($r.EstimatedTests) of ~$($r.FullTests) tests" } } else { 'test count unknown (no earlier CTRF)' }
        if ($r.Mode -eq 'Full') { Write-Host "  $($r.Suite): WHOLE SUITE, $est" }
        else { Write-Host "  $($r.Suite): $($r.Classes.Count) of $($r.TotalClasses) test classes, ${est}: $($r.Classes -join ', ')" }
        foreach ($why in $r.Reasons | Select-Object -First 6) { Write-Host "      fallback: $why" }
        if ($r.Reasons.Count -gt 6) { Write-Host "      fallback: ... $($r.Reasons.Count - 6) more" }
    }
    foreach ($n in $Impact.Notes | Select-Object -First 12) { Write-Host "  selection: $n" }
}

# Result folders that may hold earlier CTRF files: this checkout's and, in a worktree, the primary checkout's.
function Get-TestResultRoots([string] $Root) {
    $roots = [Collections.Generic.List[string]]::new()
    $roots.Add((Join-Path $Root 'artifacts/test-results'))
    $common = (& git -C $Root rev-parse --git-common-dir 2>$null)
    if ($LASTEXITCODE -eq 0 -and $common) {
        $gitDir = ([string]$common).Trim(); if (-not [IO.Path]::IsPathRooted($gitDir)) { $gitDir = Join-Path $Root $gitDir }
        $primary = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($gitDir).TrimEnd('\', '/'))
        $roots.Add((Join-Path $primary 'artifacts/test-results'))
    }
    $roots.ToArray()
}
