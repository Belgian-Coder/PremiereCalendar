#Requires -Version 7.0
# Regression checks for the one-command static lane: Python discovery, the parallel runner and the change classifier.
# No Docker, WSL, SDK or database; the runner checks use tiny fake pwsh child processes.
[CmdletBinding()]
param([string] $PythonExecutable)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $root 'eng/StaticChecks.Common.ps1')
$count = 0
function Assert-Static([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
    $script:count++
}
$sandboxParent = [IO.Path]::GetFullPath((Join-Path $root 'artifacts/harness-tests'))
$sandbox = Join-Path $sandboxParent ('static-checks-' + [guid]::NewGuid().ToString('N'))
try {
    # --- Python discovery -------------------------------------------------------------------------------------
    foreach ($stub in 'C:\Users\a\AppData\Local\Microsoft\WindowsApps\python.exe', 'C:\Users\a\AppData\Local\Microsoft\WindowsApps\python3.exe', 'C:\Users\a\AppData\Local\Microsoft\WindowsApps\python3.12.exe') {
        Assert-Static (Test-PythonStoreStub $stub) "Store alias stub was not recognised: $stub"
    }
    Assert-Static (-not (Test-PythonStoreStub 'C:\Users\a\AppData\Local\Microsoft\WindowsApps\PythonSoftwareFoundation.Python.3.12_x\python.exe')) 'A real packaged Python was treated as the alias stub.'
    Assert-Static (-not (Test-PythonStoreStub '/opt/homebrew/bin/python3')) 'A Unix interpreter was treated as the alias stub.'
    $probed = [Collections.Generic.List[string]]::new()
    $probe = { param($p) $probed.Add($p); $p -notlike '*old*' }
    $pick = Select-PythonCandidate @('C:\x\WindowsApps\python.exe', 'C:\x\old\python3.exe', 'C:\x\new\python3.exe', 'C:\x\other\python.exe') $probe
    Assert-Static ($pick -eq 'C:\x\new\python3.exe') 'Discovery did not skip the stub and the too-old interpreter, or lost PATH order.'
    Assert-Static (($probed -join '|') -eq 'C:\x\old\python3.exe|C:\x\new\python3.exe') 'Discovery probed the stub or kept probing after a match.'
    Assert-Static ($pick -is [string]) 'Discovery must return one string, never an array.'
    Assert-Static ($null -eq (Select-PythonCandidate @('C:\x\WindowsApps\python.exe') $probe)) 'Only a stub must yield no interpreter.'
    Assert-Static ($null -eq (Select-PythonCandidate @() $probe)) 'No candidates must yield no interpreter.'
    $failed = $false; try { Resolve-PythonExecutable (Join-Path $sandbox 'missing-python') | Out-Null } catch { $failed = $true }
    Assert-Static $failed 'An explicit missing interpreter must fail, not fall back.'
    $pwsh = Get-PwshExecutable
    $failed = $false; try { Resolve-PythonExecutable $pwsh | Out-Null } catch { $failed = $true }
    Assert-Static $failed 'An explicit non-Python executable was accepted.'
    $python = Resolve-PythonExecutable $PythonExecutable
    if ($python) {
        Assert-Static (($python -is [string]) -and (Test-Path -LiteralPath $python)) 'Real discovery did not return one existing path.'
        Assert-Static ((Resolve-PythonExecutable $python) -eq $python) 'An explicit interpreter was not returned unchanged.'
    } else { Write-Warning 'Python 3.10+ absent: real-interpreter discovery cases were not run.' }

    # --- table integrity and selection ------------------------------------------------------------------------
    $table = Get-StaticCheckTable
    Assert-Static (@($table.Name | Select-Object -Unique).Count -eq $table.Count) 'Static check names are not unique.'
    foreach ($check in $table | Where-Object Path) { Assert-Static (Test-Path -LiteralPath (Join-Path $root $check.Path)) "Table entry points at a missing file: $($check.Path)" }
    $declared = @($table.Path)
    foreach ($file in @(Get-ChildItem (Join-Path $root 'tests') -File | Where-Object { $_.Name -like '*.Tests.ps1' -or $_.Name -like 'test_*.py' })) {
        Assert-Static ($declared -contains ('tests/' + $file.Name)) "tests/$($file.Name) is missing from the static check table."
    }
    $only = Select-StaticChecks $table @('Harness*', 'Docs') @('HarnessCacheRoot')
    Assert-Static ((($only.Name | Sort-Object) -join ',') -eq 'Docs,Harness,HarnessProcess') '-Only/-Exclude wildcards selected the wrong checks.'
    $failed = $false; try { Select-StaticChecks $table @('NoSuchCheck') $null | Out-Null } catch { $failed = $true }
    $commaOnly = Select-StaticChecks $table @('Harness,Docs') @('HarnessCacheRoot,HarnessProcess')
    Assert-Static ((@($commaOnly.Name | Sort-Object) -join ',') -ceq 'Docs,Harness') 'A comma-separated -Only/-Exclude list (as pwsh -File delivers it) is split into names.'
    Assert-Static $failed 'An unknown -Only name was accepted.'

    # --- parallel runner with fake checks ---------------------------------------------------------------------
    [void][IO.Directory]::CreateDirectory($sandbox)
    function New-Fake([string] $Name, [string] $Code, [string] $Group = '', [double] $Weight = 1) {
        [pscustomobject]@{ Name = $Name; FileName = $pwsh; Arguments = [string[]]@('-NoProfile', '-NonInteractive', '-Command', $Code); WorkingDirectory = $sandbox; Weight = $Weight; Group = $Group }
    }
    function Get-Span([string] $Folder, [string] $Name) {
        $m = [regex]::Match((Get-Content -LiteralPath (Join-Path $Folder "$Name.log") -Raw), 'SPAN (\d+) (\d+)')
        if (-not $m.Success) { throw "No span in the log of $Name." }
        [int64[]]@($m.Groups[1].Value, $m.Groups[2].Value)
    }
    $stamp = '$a=[DateTime]::UtcNow.Ticks; Start-Sleep -Milliseconds 900; $b=[DateTime]::UtcNow.Ticks; "SPAN $a $b"'
    $badCode = ((1..40 | ForEach-Object { "Write-Output 'line$_'" }) -join '; ') + '; [Console]::Error.WriteLine("boom"); exit 3'
    # Barrier instead of timing: each independent check waits (bounded) for the other's marker file, so both
    # can only report MET when they really ran at the same time.
    $barrier = '$me=$args[0]; $other=$args[1]; New-Item -ItemType File -Path $me -Force | Out-Null; $t=[Diagnostics.Stopwatch]::StartNew(); while (-not (Test-Path $other) -and $t.Elapsed.TotalSeconds -lt 45) { Start-Sleep -Milliseconds 50 }; if (Test-Path $other) { "MET" } else { "ALONE" }'
    $m1 = Join-Path $sandbox 'p1.marker'; $m2 = Join-Path $sandbox 'p2.marker'
    function New-Barrier([string] $Name, [string] $Mine, [string] $Theirs) { New-Fake $Name ("& { $barrier } '$Mine' '$Theirs'") }
    $commands = @(
        (New-Fake 'ok' 'Write-Output fine'), (New-Fake 'bad' $badCode),
        (New-Fake 'g1' $stamp 'pair'), (New-Fake 'g2' $stamp 'pair'), (New-Barrier 'p1' $m1 $m2), (New-Barrier 'p2' $m2 $m1)
    )
    $logs = Join-Path $sandbox 'logs'
    $results = Invoke-StaticCheckSet $commands $logs 8 120
    $by = @{}; foreach ($r in $results) { $by[$r.Name] = $r }
    Assert-Static ($results.Count -eq 6) 'The runner lost results.'
    Assert-Static ($by['ok'].Status -eq 'PASS' -and $by['ok'].ExitCode -eq 0) 'A passing check was not reported as PASS.'
    Assert-Static ($by['bad'].Status -eq 'FAIL' -and $by['bad'].ExitCode -eq 3) 'A failing check did not propagate its exit code.'
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $hung = @(Invoke-StaticCheckSet @((New-Fake 'slow' 'Start-Sleep -Seconds 60')) (Join-Path $sandbox 'timeout') 2 3)
    $clock.Stop()
    Assert-Static ($hung[0].Status -eq 'TIMEOUT') 'A hung check was not stopped at its timeout.'
    Assert-Static ($clock.Elapsed.TotalSeconds -lt 40) 'The timeout did not bound the run.'
    $results = @($results) + $hung
    $badLog = Get-Content -LiteralPath (Join-Path $logs 'bad.log') -Raw
    Assert-Static ($badLog -match 'line40' -and $badLog -match 'boom') 'The failing check log lost its stdout or stderr.'
    $g1 = Get-Span $logs 'g1'; $g2 = Get-Span $logs 'g2'
    Assert-Static (($g1[1] -le $g2[0]) -or ($g2[1] -le $g1[0])) 'Checks in the same serial group overlapped.'
    Assert-Static ((Get-Content -LiteralPath (Join-Path $logs 'p1.log') -Raw) -match 'MET' -and (Get-Content -LiteralPath (Join-Path $logs 'p2.log') -Raw) -match 'MET') 'Independent checks did not run concurrently.'
    $serialLogs = Join-Path $sandbox 'serial'
    [void](Invoke-StaticCheckSet @((New-Fake 's1' $stamp), (New-Fake 's2' $stamp)) $serialLogs 1 30)
    $s1 = Get-Span $serialLogs 's1'; $s2 = Get-Span $serialLogs 's2'
    Assert-Static (($s1[1] -le $s2[0]) -or ($s2[1] -le $s1[0])) '-ThrottleLimit 1 did not run the checks one at a time.'

    # --- summary ----------------------------------------------------------------------------------------------
    $summary = (Write-StaticCheckSummary $results 12.3 30 6>&1 | ForEach-Object { "$_" }) -join "`n"
    Assert-Static ($summary -match 'Total wall time 12[.,]3 s' -and $summary -match '2 failed') 'The summary lost the wall time or failure count.'
    Assert-Static ($summary -match '(?m)^bad\s+FAIL' -and $summary -match '(?m)^ok\s+PASS' -and $summary -match '(?m)^slow\s+TIMEOUT') 'The summary table lost a check row.'
    Assert-Static ($summary -match 'line40' -and $summary -notmatch 'line10\b') 'The failure tail is not the last 30 lines of the failing log.'
    Assert-Static ($summary.IndexOf('bad  ') -lt $summary.IndexOf('ok  ') -and $summary.IndexOf('ok  ') -lt $summary.IndexOf('slow  ')) 'The summary rows are not sorted by name.'

    # --- entry scripts: exit codes without running any real check ----------------------------------------------
    $entry = Join-Path $root 'eng/Invoke-StaticChecks.ps1'
    & $pwsh -NoProfile -File $entry -Only 'NoSuchCheck' *> $null
    Assert-Static ($LASTEXITCODE -ne 0) 'An unknown -Only name did not fail the entry script.'
    $planText = (& $pwsh -NoProfile -File $entry -Plan -Only 'GitDiffCheck') -join "`n"
    Assert-Static ($LASTEXITCODE -eq 0 -and $planText -match 'Plan only' -and $planText -match 'GitDiffCheck') '-Plan did not print the table and stop.'

    # --- change classifier ------------------------------------------------------------------------------------
    $docs = Get-ChangeClassification @('docs/Testing.md', 'README.md')
    Assert-Static ($docs.DocsOnly -and -not $docs.BuildApp -and -not $docs.RunAll -and (($docs.StaticNames | Sort-Object) -join ',') -eq 'Docs,GitDiffCheck') 'Docs-only paths did not map to Test-Docs plus git diff --check.'
    $sdk = Get-ChangeClassification @('eng/run-with-sdk-cache.sh')
    Assert-Static (-not $sdk.RunAll -and $sdk.StaticNames -contains 'SdkCache' -and $sdk.StaticNames -contains 'ScriptSyntax' -and $sdk.StaticNames -notcontains 'HarnessCacheRoot' -and -not $sdk.BuildApp) 'A shell helper change did not select its covering checks only.'
    $static = Get-ChangeClassification @('eng/Invoke-StaticChecks.ps1')
    Assert-Static ($static.StaticNames -contains 'StaticChecks' -and $static.StaticNames -notcontains 'HarnessCacheRoot' -and $static.StaticNames -notcontains 'SourceFingerprints') 'A static-lane script change selected unrelated expensive checks.'
    $release = Get-ChangeClassification @('eng/release/Invoke-Candidate.ps1')
    Assert-Static ($release.StaticNames -contains 'HarnessCacheRoot' -and $release.StaticNames -contains 'ValidationTools' -and $release.StaticNames -contains 'SourceFingerprints') 'A release script change missed checks that cover it.'
    $testPy = Get-ChangeClassification @('tests/test_review_inputs.py')
    Assert-Static ($testPy.StaticNames -contains 'ReviewInputs' -and $testPy.StaticNames -contains 'ScriptSyntax' -and -not $testPy.RunAll) 'A Python test change did not select itself and the syntax check.'
    $unknownTest = Get-ChangeClassification @('tests/NewThing.Tests.ps1')
    Assert-Static ($unknownTest.StaticNames -contains 'ScriptSyntax' -and -not $unknownTest.RunAll) 'A new test script must at least be syntax-checked.'
    $unsure = Get-ChangeClassification @('eng/something.dat')
    Assert-Static ($unsure.RunAll) 'An uncovered tooling file did not fall back to all static checks.'
    $cs = Get-ChangeClassification @('PremiereCalendar/Services/Thing.cs')
    Assert-Static ($cs.BuildApp -and ($cs.Suites -join ',') -eq 'Unit,Component,Integration' -and -not $cs.BrowserNeeded -and -not $cs.DocsOnly) 'App C# did not map to all three service suites.'
    $razor = Get-ChangeClassification @('PremiereCalendar/Components/Card.razor', 'PremiereCalendar/wwwroot/app.css')
    Assert-Static ($razor.BuildApp -and ($razor.Suites -join ',') -eq 'Component' -and $razor.BrowserNeeded) 'UI files did not map to Component with the Browser note.'
    $testProject = Get-ChangeClassification @('tests/PremiereCalendar.UnitTests/ThingTests.cs')
    Assert-Static (($testProject.Suites -join ',') -eq 'Unit' -and $testProject.BuildApp) 'A unit-test file did not map to the Unit suite.'
    $mixed = Get-ChangeClassification @('tests/PremiereCalendar.IntegrationTests/A.cs', 'tests/PremiereCalendar.ComponentTests/B.cs', 'docs/Testing.md')
    Assert-Static (($mixed.Suites -join ',') -eq 'Component,Integration' -and -not $mixed.DocsOnly) 'Suites from several test projects were not combined.'
    $browser = Get-ChangeClassification @('tests/PremiereCalendar.BrowserTests/Thing.cs')
    Assert-Static ($browser.BrowserProject -and $browser.BrowserNeeded -and $browser.Suites.Count -eq 0) 'Browser tests must be flagged, not scheduled as a service suite.'
    $docker = Get-ChangeClassification @('Dockerfile')
    Assert-Static ($docker.BuildApp -and ($docker.Suites -join ',') -eq 'Unit,Component,Integration' -and -not $docker.RunAll) 'A Dockerfile change did not map to the cross-cutting builder checks.'
    $none = Get-ChangeClassification @()
    Assert-Static ((-not $none.DocsOnly) -and $none.StaticNames.Count -eq 1 -and $none.StaticNames[0] -eq 'GitDiffCheck') 'An empty change list must only keep git diff --check.'
    $commands = @(Get-AppFollowUpCommands $mixed $root)
    Assert-Static ($commands.Count -eq 2 -and (($commands[0].Arguments -join ' ') -eq 'check -Project App') -and ($commands[1].Arguments -join ' ') -eq '-Project Component,Integration') 'Builder follow-up commands are wrong.'
    Assert-Static (@(Get-AppFollowUpCommands $docs $root).Count -eq 0) 'Docs-only changes produced builder commands.'
} finally {
    if (Test-Path -LiteralPath $sandbox) {
        $resolved = [IO.Path]::GetFullPath($sandbox)
        if ($resolved.StartsWith($sandboxParent + [IO.Path]::DirectorySeparatorChar)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
    }
}
Write-Host "$count static-lane checks passed (Python discovery, parallel runner, change classifier); no Docker/WSL."
