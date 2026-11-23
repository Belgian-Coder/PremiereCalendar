#Requires -Version 7.0
# Regression checks for eng/release/Invoke-LocalGates.ps1 with a stub step launcher: no Docker, WSL, network or real gate runs.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$orchestrator = Join-Path $root 'eng/release/Invoke-LocalGates.ps1'
$work = Join-Path $root ('artifacts/release-orchestrator-tests/' + [guid]::NewGuid().ToString('N'))
[void] (New-Item -ItemType Directory -Path $work)
$revision = '0123456789abcdef0123456789abcdef01234567'
$script:Passed = 0; $script:Failures = [Collections.Generic.List[string]]::new()
function Assert-That([bool] $Condition, [string] $Message) { if ($Condition) { $script:Passed++ } else { $script:Failures.Add($Message); Write-Host "FAIL: $Message" } }

# --------------------------------------------------------------------------- stub launcher
$global:Calls = [Collections.Generic.List[object]]::new()
$global:Scenario = @{}          # id -> @{ Ms; Exit; FingerprintAtMs }
$global:FingerprintCreated = $null
function New-StubRunner {
    {
        param($step)
        $plan = $global:Scenario[$step.Id]
        if (-not $plan) { $plan = @{ Ms = 4; Exit = 0 } }
        $record = [pscustomobject]@{ Id = $step.Id; Arguments = $step.Arguments; Start = [DateTime]::UtcNow; Done = $null; LogPath = $step.LogPath }
        $global:Calls.Add($record)
        [IO.File]::WriteAllText($step.LogPath, "stub $($step.Id)`n")       # CreateNew semantics are the launcher's contract
        $handle = [pscustomobject]@{ Record = $record; Plan = $plan; Polls = 0; FingerprintFile = $global:FingerprintFile; Killed = $false; KillNote = '' }
        Add-Member -InputObject $handle -MemberType ScriptMethod -Name Kill -Value { param($note) $this.Killed = $true; $this.KillNote = $note }
        Add-Member -InputObject $handle -MemberType NoteProperty -Name ExitCode -Value ([int]$plan.Exit)
        Add-Member -InputObject $handle -MemberType ScriptProperty -Name IsDone -Value {
            if ($this.Plan.ContainsKey('FingerprintAtMs') -and -not (Test-Path -LiteralPath $this.FingerprintFile) -and $this.Polls -ge $this.Plan.FingerprintAtMs) {
                [IO.File]::WriteAllText($this.FingerprintFile, '{"a":"b"}'); $global:FingerprintCreated = [DateTime]::UtcNow
            }
            $this.Polls++
            $done = if ($this.Plan.ContainsKey('Hang')) { $this.Killed } else { $this.Polls -ge $this.Plan.Ms }
            if ($done -and -not $this.Record.Done) { $this.Record.Done = [DateTime]::UtcNow }
            $done
        }
        $handle
    }
}
# Durations are counted in orchestrator polls (every running step is polled once per loop), not wall time, so the checks do not depend on host load.
$happy = { @{ tests = @{ Ms = 60; Exit = 0; FingerprintAtMs = 4 }; 'restore-init' = @{ Ms = 3; Exit = 0 }; restore = @{ Ms = 5; Exit = 0 }; build = @{ Ms = 12; Exit = 0 }
        'nuget-audit' = @{ Ms = 5; Exit = 0 }; scans = @{ Ms = 8; Exit = 0 }; smoke = @{ Ms = 6; Exit = 0 }; 'backup-compat' = @{ Ms = 6; Exit = 0 } } }

$script:CaseNumber = 0
function New-Bundle {
    $script:CaseNumber++
    $bundle = Join-Path $work "bundle$($script:CaseNumber)"
    [void] (New-Item -ItemType Directory -Path $bundle, "$bundle.private")
    [IO.File]::WriteAllText((Join-Path $bundle 'release-config.json'), '{"schema":1,"version":"1.2.20","bundleName":"stub","resourcePrefix":"stub"}')
    [IO.File]::WriteAllText("$bundle.private/backup-summary.json", '{}')
    $global:FingerprintFile = Join-Path $bundle 'tested-source-fingerprints.json'
    $bundle
}
function Invoke-Gates([string] $Bundle, [hashtable] $Extra = @{}, [bool] $UseStub = $true) {
    $global:Calls.Clear(); $global:FingerprintCreated = $null
    $arguments = @{ Bundle = $Bundle; ExpectedRevision = $revision; PollMilliseconds = 2; SkipHostPreflight = $true }
    if ($UseStub) { $arguments.StepRunner = (New-StubRunner) }
    foreach ($key in $Extra.Keys) { $arguments[$key] = $Extra[$key] }
    $global:LASTEXITCODE = 0
    $text = ''
    try { $text = (& $orchestrator @arguments *>&1 | Out-String) } catch { $text = "EXCEPTION: $($_.Exception.Message)" }
    $timings = @(Get-ChildItem -LiteralPath (Join-Path $Bundle 'gates') -Filter 'stage-timings*.json' -ErrorAction SilentlyContinue | Sort-Object Name)
    [pscustomobject]@{ Exit = $global:LASTEXITCODE; Text = $text; Timings = $timings }
}
function Get-Call([string] $Id) { @($global:Calls | Where-Object { $_.Id -eq $Id }) | Select-Object -First 1 }
function Test-Overlap($A, $B) { $A.Start -lt $B.Done -and $B.Start -lt $A.Done }
function Get-MaxConcurrency { $m = 0; foreach ($c in $global:Calls) { $n = @($global:Calls | Where-Object { $_.Start -le $c.Start -and $_.Done -gt $c.Start }).Count; if ($n -gt $m) { $m = $n } }; $m }
$dependencies = @{ restore = 'restore-init'; 'nuget-audit' = 'build'; scans = 'nuget-audit'; smoke = 'build'; 'backup-compat' = 'build', 'restore' }

try {
    # 1. default parallel run: ordering, concurrency limits, fingerprint ordering, timings
    $global:Scenario = & $happy
    $bundle = New-Bundle
    $r = Invoke-Gates $bundle
    Assert-That ($r.Exit -eq 0) "parallel run succeeds (exit $($r.Exit))`n$($r.Text)"; if ($r.Exit -ne 0) { Write-Host $r.Text }
    Assert-That ($global:Calls.Count -eq 8) 'every step is launched exactly once'
    foreach ($id in $dependencies.Keys) { foreach ($d in $dependencies[$id]) { Assert-That ((Get-Call $id).Start -ge (Get-Call $d).Done) "$id starts only after $d finished" } }
    $tests = Get-Call 'tests'; $build = Get-Call 'build'; $smoke = Get-Call 'smoke'; $compat = Get-Call 'backup-compat'
    Assert-That ($global:Calls[0].Id -eq 'tests') 'the test lane is started first'
    Assert-That ($null -ne $global:FingerprintCreated -and $build.Start -ge $global:FingerprintCreated) 'Build-Image starts only after the test run froze the fingerprints'
    Assert-That ($build.Start -lt $tests.Done -and (Test-Overlap $tests $build)) 'the build overlaps the running test lane'
    Assert-That ((Test-Overlap $tests (Get-Call 'scans')) -or (Test-Overlap $tests (Get-Call 'nuget-audit'))) 'audit or scans overlap the test lane'
    Assert-That (-not (Test-Overlap $tests $smoke) -and -not (Test-Overlap $tests $compat)) 'the CPU-heavy test lane never overlaps smoke or backup-compat'
    Assert-That ($smoke.Start -ge $tests.Done -and $compat.Start -ge $tests.Done) 'timing-sensitive steps start after the tests finished'
    Assert-That (-not (Test-Overlap $smoke $compat)) 'smoke and backup-compat do not overlap by default'
    foreach ($heavy in 'scans', 'restore') { foreach ($timing in $smoke, $compat) { Assert-That (-not (Test-Overlap (Get-Call $heavy) $timing)) "$($timing.Id) never overlaps $heavy" } }
    Assert-That ((Get-MaxConcurrency) -le 3 -and (Get-MaxConcurrency) -ge 2) "concurrency stays within the default cap (max $(Get-MaxConcurrency))"
    $t = Get-Content -LiteralPath $r.Timings[0].FullName -Raw | ConvertFrom-Json
    Assert-That ($r.Timings.Count -eq 1 -and $r.Timings[0].Name -eq 'stage-timings.json') 'stage-timings.json is written once'
    Assert-That ($t.schema -eq 1 -and $t.mode -eq 'parallel' -and $t.succeeded -eq $true -and @($t.steps).Count -eq 8) 'timings header and step count'
    Assert-That (@($t.steps | Where-Object { $_.status -ne 'passed' -or $_.exitCode -ne 0 -or -not $_.startUtc -or -not $_.endUtc -or $_.seconds -le 0 -or -not $_.log }).Count -eq 0) 'every timing row has status, exit code, start, end, duration and log'
    # Overlap itself is proven by the call-interval assertions above; wall versus sum depends on host load, so only
    # the load-independent invariants of the timing record are asserted here.
    Assert-That ($t.wallSeconds -gt 0 -and $t.criticalPathSeconds -le $t.sumOfStepSeconds + 0.001 -and $t.criticalPathSeconds -le $t.wallSeconds + 0.05) 'the critical path is within both the wall time and the sum of the steps'
    $logs = @(Get-ChildItem -LiteralPath (Join-Path $bundle 'gates') -Filter '*.log')
    Assert-That ($logs.Count -eq 8) 'one new log per step in gates/'

    # 2. serial: one step at a time in topological order
    $bundle = New-Bundle
    $r = Invoke-Gates $bundle @{ Serial = $true }
    Assert-That ($r.Exit -eq 0 -and (Get-MaxConcurrency) -eq 1) "-Serial never overlaps steps (max $(Get-MaxConcurrency))"
    Assert-That (($global:Calls.Id -join ',') -eq 'tests,restore-init,restore,build,nuget-audit,scans,smoke,backup-compat') "-Serial order is deterministic: $($global:Calls.Id -join ',')"
    Assert-That ((Get-Content -LiteralPath $r.Timings[0].FullName -Raw | ConvertFrom-Json).mode -eq 'serial') 'timings record serial mode'

    # 3. opt-in overlap of the runtime checks
    $bundle = New-Bundle
    $r = Invoke-Gates $bundle @{ ParallelRuntimeChecks = $true }
    Assert-That ($r.Exit -eq 0 -and (Test-Overlap (Get-Call 'smoke') (Get-Call 'backup-compat')) -and -not (Test-Overlap (Get-Call 'tests') (Get-Call 'smoke'))) '-ParallelRuntimeChecks overlaps smoke with compat but still not with tests'

    # 3b. per-step deadline: a hung step is killed, recorded as failed (exit 124), nothing else starts, markers and timings are written
    $global:Scenario = & $happy
    $global:Scenario['restore-init'] = @{ Ms = 1; Exit = 0; Hang = $true }
    $bundle = New-Bundle
    $r = Invoke-Gates $bundle @{ StepDeadlineSeconds = @{ 'restore-init' = 1 }; Serial = $true }
    $t = Get-Content -LiteralPath $r.Timings[0].FullName -Raw | ConvertFrom-Json
    $hung = @($t.steps | Where-Object { $_.id -eq 'restore-init' })[0]
    Assert-That ($r.Exit -eq 1 -and $t.failedStep -eq 'restore-init' -and $hung.exitCode -eq 124 -and $hung.deadlineSeconds -eq 1 -and $hung.note -match 'deadline') "a hung step is killed at its deadline and reported failed`n$($r.Text)"
    Assert-That ($r.Text -match "exceeded its deadline of 1 s" -and (Test-Path -LiteralPath (Join-Path $bundle 'gates/restore-init.result.json'))) 'the deadline message is clear and the step marker is still written'
    Assert-That (@($t.steps | Where-Object { $_.status -eq 'not-started' }).Count -eq 6 -and @($global:Calls | Where-Object { $_.Id -eq 'restore' }).Count -eq 0) 'nothing is started after a deadline failure'
    $bundle = New-Bundle
    $r = Invoke-Gates $bundle @{ StepDeadlineSeconds = @{ nonsense = 5 } }
    Assert-That ($r.Text -match 'unknown step') 'an unknown step id in -StepDeadlineSeconds is rejected'


    # 4. stop on first failure: running steps finish, nothing new starts, no retry
    $global:Scenario = & $happy
    $global:Scenario.build = @{ Ms = 6; Exit = 7 }
    $bundle = New-Bundle
    $r = Invoke-Gates $bundle
    Assert-That ($r.Exit -eq 1) 'a failed step ends the run with exit 1'
    Assert-That ((@($global:Calls | Where-Object { $_.Id -eq 'build' }).Count) -eq 1) 'a failed step is never retried'
    Assert-That ($null -eq (Get-Call 'nuget-audit') -and $null -eq (Get-Call 'scans') -and $null -eq (Get-Call 'smoke') -and $null -eq (Get-Call 'backup-compat')) 'no dependent or later step is launched after the failure'
    Assert-That ((Get-Call 'tests').Done -ge (Get-Call 'build').Done) 'a step already running is allowed to finish (tests ended after the failed build)'
    $t = Get-Content -LiteralPath $r.Timings[0].FullName -Raw | ConvertFrom-Json
    Assert-That ($t.failedStep -eq 'build' -and $t.succeeded -eq $false -and (@($t.steps | Where-Object { $_.id -eq 'smoke' }).status -eq 'not-started') -and (@($t.steps | Where-Object { $_.id -eq 'tests' }).status -eq 'passed')) 'timings record the failed step and the steps that never started'
    Assert-That ($r.Text -match 'Evidence present after the stop') 'the stop report lists the evidence that exists'
    Assert-That ((@($t.steps | Where-Object { $_.id -eq 'build' }).exitCode) -eq 7) 'the child exit code is recorded'

    # 4b. a slow audit lets smoke start first: scans must then wait for the timing-sensitive steps (reverse guard)
    $global:Scenario = @{ tests = @{ Ms = 8; Exit = 0; FingerprintAtMs = 2 }; 'restore-init' = @{ Ms = 2; Exit = 0 }; restore = @{ Ms = 3; Exit = 0 }; build = @{ Ms = 4; Exit = 0 }
        'nuget-audit' = @{ Ms = 12; Exit = 0 }; scans = @{ Ms = 6; Exit = 0 }; smoke = @{ Ms = 30; Exit = 0 }; 'backup-compat' = @{ Ms = 30; Exit = 0 } }
    $r = Invoke-Gates (New-Bundle)
    Assert-That ($r.Exit -eq 0) "slow-audit run succeeds`n$($r.Text)"
    $scansCall = Get-Call 'scans'
    Assert-That ($null -ne $scansCall -and -not (Test-Overlap $scansCall (Get-Call 'smoke')) -and -not (Test-Overlap $scansCall (Get-Call 'backup-compat'))) 'scans never start beside a running smoke or backup-compat'
    # The scenario must really put the audit's end inside smoke, otherwise the guard is not exercised.
    Assert-That ((Get-Call 'smoke').Start -lt (Get-Call 'nuget-audit').Done -and (Get-Call 'nuget-audit').Done -lt (Get-Call 'smoke').Done) 'the slow-audit scenario finishes the audit while smoke runs'

    # 5. resume: complete evidence bound to this revision and the tested bytes is skipped, never re-run
    function Write-BoundEvidence([string] $Bundle, [string] $Revision, [switch] $Unbound) {
        $fp = Join-Path $Bundle 'tested-source-fingerprints.json'; [IO.File]::WriteAllText($fp, '{"tested":true}')
        $fpHash = (Get-FileHash -LiteralPath $fp -Algorithm SHA256).Hash.ToLowerInvariant()
        [IO.File]::WriteAllText((Join-Path $Bundle 'test-run.log'), 'log'); $null = New-Item -ItemType Directory -Path (Join-Path $Bundle 'tests')
        [IO.File]::WriteAllText((Join-Path $Bundle 'test-run.json'), (@{ sourceRevision = $Revision } | ConvertTo-Json))
        [IO.File]::WriteAllText((Join-Path $Bundle 'test-summary.json'), (ConvertTo-Json -InputObject @('unit', 'component', 'integration' | ForEach-Object { @{ suite = $_; passed = 1; failed = 0; sourceRevision = $Revision } })))
        foreach ($f in 'source-1.2.20.tar', 'source-archive-verification.json', 'premierecalendar-1.2.20.tar', 'build.log') { [IO.File]::WriteAllText((Join-Path $Bundle $f), 'x') }
        $bound = if ($Unbound) { $null } else { $fpHash }
        [IO.File]::WriteAllText((Join-Path $Bundle 'build-metadata.json'), (@{ sourceRevision = $Revision; testedSourceFingerprintsSha256 = $bound } | ConvertTo-Json))
        [IO.File]::WriteAllText((Join-Path $Bundle 'release-nuget-audit.log'), "Project 'x' has no vulnerable packages given the current sources.`n")
    }
    $global:Scenario = & $happy
    $bundle = New-Bundle
    Write-BoundEvidence $bundle $revision
    $r = Invoke-Gates $bundle
    Assert-That ($r.Exit -eq 0 -and $null -eq (Get-Call 'tests') -and $null -eq (Get-Call 'build') -and $null -eq (Get-Call 'nuget-audit') -and $global:Calls.Count -eq 5) "complete bound evidence is skipped (launched: $(@($global:Calls | ForEach-Object Id) -join ','))`n$($r.Text)"
    if ($r.Timings.Count -gt 0) {
        $t = Get-Content -LiteralPath $r.Timings[0].FullName -Raw | ConvertFrom-Json
        Assert-That ((@($t.steps | Where-Object { $_.id -eq 'build' }).status) -eq 'skipped' -and (@($t.steps | Where-Object { $_.id -eq 'tests' }).status) -eq 'skipped' -and $t.succeeded -eq $true) 'skipped steps are recorded and the run still succeeds'
    } else { Assert-That $false 'the resumed run wrote no timings' }

    # 5a. complete-looking evidence of a step this orchestrator saw fail is refused; a completed restore skips restore-init
    $bundle = New-Bundle
    Write-BoundEvidence $bundle $revision
    $null = New-Item -ItemType Directory -Path (Join-Path $bundle 'gates') -Force
    [IO.File]::WriteAllText((Join-Path $bundle 'gates/nuget-audit.result.json'), '{"exitCode":1}')
    $r = Invoke-Gates $bundle
    Assert-That ($r.Exit -eq 2 -and $global:Calls.Count -eq 0 -and $r.Text -match 'the latest run of this step failed') "evidence of a failed run is refused`n$($r.Text)"
    # a failure that a later run of the same step repaired is history: the newest marker decides
    [IO.File]::WriteAllText((Join-Path $bundle 'gates/nuget-audit.2.result.json'), '{"exitCode":0}')
    $global:Scenario = & $happy
    $r = Invoke-Gates $bundle
    Assert-That ($r.Exit -eq 0 -and $null -eq (Get-Call 'nuget-audit') -and $null -eq (Get-Call 'build')) "a re-run that succeeded makes the evidence reusable on a third run`n$($r.Text)"
    $global:Scenario = & $happy
    $bundle = New-Bundle
    [IO.File]::WriteAllText((Join-Path $bundle 'backup-restore.json'), '{"restoreExitCode":0,"timedOut":false,"inputPipeFailed":false}')
    [IO.File]::WriteAllText((Join-Path "$bundle.private" 'backup-restore.json'), '{}')
    $r = Invoke-Gates $bundle
    Assert-That ($r.Exit -eq 0 -and $null -eq (Get-Call 'restore-init') -and $null -eq (Get-Call 'restore')) "a completed restore also skips restore-init`n$($r.Text)"

    # 5c. an audit log that also reports NU1900 is never resumed as clean evidence
    $bundle = New-Bundle
    Write-BoundEvidence $bundle $revision
    Add-Content -LiteralPath (Join-Path $bundle 'release-nuget-audit.log') -Value 'warning NU1900: Error occurred while getting package vulnerability data'
    $r = Invoke-Gates $bundle
    Assert-That ($r.Exit -eq 2 -and $global:Calls.Count -eq 0 -and $r.Text -match 'NU1900-NU1905') "an audit log with NU1900 is refused on resume`n$($r.Text)"

    # 5b. resume refuses evidence of another revision and a build that is not bound to the tested fingerprints
    foreach ($case in @(@{ Name = 'other revision'; Revision = 'fedcba9876543210fedcba9876543210fedcba98'; Unbound = $false; Text = 'another source revision' },
                        @{ Name = 'unbound build'; Revision = $revision; Unbound = $true; Text = 'not bound to the tested source fingerprints' })) {
        $bundle = New-Bundle
        Write-BoundEvidence $bundle $case.Revision -Unbound:$case.Unbound
        $r = Invoke-Gates $bundle
        Assert-That ($r.Exit -eq 2 -and $global:Calls.Count -eq 0 -and $r.Text -match [regex]::Escape($case.Text)) "resume refuses $($case.Name) without launching a step`n$($r.Text)"
    }

    # 6. partial evidence: refuse before launching anything
    $bundle = New-Bundle
    [IO.File]::WriteAllText((Join-Path $bundle 'build-metadata.json'), 'x')
    $r = Invoke-Gates $bundle
    Assert-That ($r.Exit -eq 2 -and $global:Calls.Count -eq 0 -and $r.Text -match 'REFUSED: build: partial') "partial evidence is refused without launching a step`n$($r.Text)"
    Assert-That (-not (Test-Path -LiteralPath (Join-Path $bundle 'gates/build.log'))) 'a refused run creates no step log'

    # 7. failed evidence (complete files, failing content) is refused too
    $bundle = New-Bundle
    [IO.File]::WriteAllText((Join-Path $bundle 'release-nuget-audit.log'), "Project 'x' has the following vulnerable packages`n")
    $r = Invoke-Gates $bundle
    Assert-That ($r.Exit -eq 2 -and $global:Calls.Count -eq 0 -and $r.Text -match 'REFUSED: nuget-audit: invalid') 'a failed NuGet audit log is refused'
    $bundle = New-Bundle
    [IO.File]::WriteAllText((Join-Path $bundle 'backup-restore.json'), '{"restoreExitCode":1}'); [IO.File]::WriteAllText("$bundle.private/backup-restore.json", '{}')
    $r = Invoke-Gates $bundle
    Assert-That ($r.Exit -eq 2 -and $r.Text -match 'REFUSED: restore: invalid') 'a failed restore proof is refused'

    # 8. a step that failed without evidence may be run again by the operator; earlier logs are preserved
    $global:Scenario = & $happy
    $global:Scenario.restore = @{ Ms = 4; Exit = 5 }; $global:Scenario.tests = @{ Ms = 10; Exit = 0 }   # the stub writes no fingerprints, so a re-run starts clean
    $bundle = New-Bundle
    $r = Invoke-Gates $bundle
    Assert-That ($r.Exit -eq 1) 'first run fails in restore'
    $firstTimings = Get-FileHash -LiteralPath $r.Timings[0].FullName
    $global:Scenario = & $happy
    $r = Invoke-Gates $bundle
    Assert-That ($r.Exit -eq 0 -and $null -eq (Get-Call 'restore-init')) 'the successful restore-init is not repeated (result marker)'
    Assert-That ((Get-Call 'restore').LogPath -like '*restore.2.log' -and (Test-Path -LiteralPath (Join-Path $bundle 'gates/restore.log'))) 'the re-run uses a new log number and keeps the old log'
    Assert-That ($r.Timings.Count -eq 2 -and (Get-FileHash -LiteralPath (Join-Path $bundle 'gates/stage-timings.json')).Hash -eq $firstTimings.Hash) 'a second timings file is written; the first is never overwritten'

    # 9. fingerprint wait is bounded
    $global:Scenario = & $happy
    $global:Scenario.tests = @{ Ms = 400; Exit = 0 }
    $bundle = New-Bundle
    $r = Invoke-Gates $bundle @{ FingerprintWaitSeconds = 1; PollMilliseconds = 10 }
    Assert-That ($r.Exit -eq 1 -and $null -eq (Get-Call 'build') -and $r.Text -match 'did not appear') 'Build-Image is not started when the fingerprints never appear'

    # 10. budget forwarding
    $global:Scenario = & $happy
    $bundle = New-Bundle
    $r = Invoke-Gates $bundle @{ Cpus = 3; MaxThreads = 2; BuildMaxCpuCount = 2; TimeoutSeconds = 900; IgnoreUnfixed = $true; RestoreTimeoutSeconds = 600 }
    $joined = { param($id) (Get-Call $id).Arguments -join ' ' }
    Assert-That ((& $joined 'tests') -match '-Cpus 3 -MaxThreads 2 -BuildMaxCpuCount 2 -TimeoutSeconds 900' -and (& $joined 'tests') -match "-ExpectedRevision $revision") 'test budget parameters reach Invoke-ReleaseTests'
    Assert-That ((& $joined 'scans') -match '-IgnoreUnfixed' -and (& $joined 'restore') -match '-TimeoutSeconds 600') '-IgnoreUnfixed and the restore timeout are forwarded'
    Assert-That ((& $joined 'build') -notmatch 'Cpus' -and (& $joined 'smoke') -notmatch 'IgnoreUnfixed') 'parameters are not leaked to unrelated steps'
    $bundle = New-Bundle
    $r = Invoke-Gates $bundle
    Assert-That ((& $joined 'tests') -notmatch 'Cpus|MaxThreads|TimeoutSeconds' -and (& $joined 'scans') -notmatch 'IgnoreUnfixed') 'unsupplied parameters are not forwarded (child defaults apply)'

    # 11. -WhatIf runs nothing and creates nothing
    $bundle = New-Bundle
    $r = Invoke-Gates $bundle @{ WhatIf = $true }
    Assert-That ($r.Exit -eq 0 -and $global:Calls.Count -eq 0 -and -not (Test-Path -LiteralPath (Join-Path $bundle 'gates')) -and $r.Text -match 'WHAT IF: nothing was executed' -and $r.Text -match 'Build-Image.ps1') '-WhatIf prints the commands and writes nothing'

    # 12. the real child-process launcher: stand-in scripts in a copy of eng/release (real pwsh children, real logs and exit codes)
    $fake = Join-Path $work 'fakerepo/eng/release'
    [void] (New-Item -ItemType Directory -Path $fake -Force)
    Copy-Item -LiteralPath $orchestrator -Destination $fake
    foreach ($name in 'Invoke-ReleaseTests', 'Initialize-RestoreCheck', 'Restore-Backup', 'Build-Image', 'Invoke-NuGetAudit', 'Invoke-Scans', 'Invoke-Smoke', 'Test-BackupCompatibility') {
        $body = "param([string] `$Bundle, [string] `$ExpectedRevision, [switch] `$IgnoreUnfixed, [int] `$Cpus)`nWrite-Output 'out $name'`n[Console]::Error.WriteLine('err $name')`n"
        if ($name -eq 'Invoke-ReleaseTests') { $body += "Set-Content -LiteralPath (Join-Path `$Bundle 'tested-source-fingerprints.json') -Value '{}'`nStart-Sleep -Seconds 4`n" }
        if ($name -eq 'Restore-Backup') { $body += "exit 3`n" }
        [IO.File]::WriteAllText((Join-Path $fake "$name.ps1"), $body)
    }
    $bundle = New-Bundle
    $global:LASTEXITCODE = 0
    $text = & (Join-Path $fake 'Invoke-LocalGates.ps1') -Bundle $bundle -ExpectedRevision $revision -SkipHostPreflight -PollMilliseconds 20 -Cpus 2 *>&1 | Out-String
    $timings = Get-Content -LiteralPath (Join-Path $bundle 'gates/stage-timings.json') -Raw | ConvertFrom-Json
    Assert-That ($global:LASTEXITCODE -eq 1 -and $timings.failedStep -eq 'restore') "a real child's non-zero exit stops the run`n$text"
    Assert-That ((Get-Content -LiteralPath (Join-Path $bundle 'gates/restore.log') -Raw) -match 'out Restore-Backup' -and (Get-Content -LiteralPath (Join-Path $bundle 'gates/restore.log') -Raw) -match 'err Restore-Backup') 'the child log keeps stdout and stderr'
    Assert-That ((@($timings.steps | Where-Object { $_.id -eq 'restore' }).exitCode) -eq 3 -and (@($timings.steps | Where-Object { $_.id -eq 'smoke' }).status) -eq 'not-started') 'the real exit code is recorded and later steps are not started'
    Assert-That ((Get-Content -LiteralPath (Join-Path $bundle 'gates/tests.log') -Raw) -match 'out Invoke-ReleaseTests') 'running children finish and keep their log after a failure'
}
finally {
    $resolved = [IO.Path]::GetFullPath($work)
    if ($resolved.StartsWith([IO.Path]::GetFullPath((Join-Path $root 'artifacts')) + [IO.Path]::DirectorySeparatorChar)) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
if ($script:Failures.Count -gt 0) { Write-Host "ReleaseOrchestrator tests: $($script:Passed) passed, $($script:Failures.Count) FAILED"; exit 1 }
Write-Host "ReleaseOrchestrator tests: $($script:Passed) passed, 0 failed"
exit 0
