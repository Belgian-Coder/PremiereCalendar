#Requires -Version 7.0
# Native feedback lane (macOS/Linux) checks that need no SDK, PostgreSQL, WSL or Docker: lane decision, plan output on a
# forced native lane, tool resolution and refusal, result-cache key composition and the throwaway server's cleanup order
# (with a stubbed process boundary). Runs on Windows and on macOS/Linux.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $root 'eng/Harness.Common.ps1')
. (Join-Path $root 'eng/StaticChecks.Common.ps1')
$count = 0
function Assert-Native([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw "Native lane check failed: $Message" }
    $script:count++
}
function Assert-Throws([scriptblock] $Action, [string] $Pattern, [string] $Message) {
    $text = $null
    try { & $Action } catch { $text = $_.Exception.Message }
    Assert-Native ($null -ne $text -and $text -match $Pattern) "$Message (got: $text)"
}
$savedLane = $env:PC_HARNESS_LANE
$env:PC_HARNESS_LANE = $null   # the Auto decision below must not be overridden by the caller's environment
$sandbox = Join-Path ([IO.Path]::GetTempPath()) ('pc-native-' + [Guid]::NewGuid().ToString('N').Substring(0, 12))
[void] (New-Item -ItemType Directory -Path $sandbox)
try {
    # 1. The OS decision is one function: Windows -> WSL, everything else -> native; a wrong explicit choice is refused.
    Assert-Native ((Resolve-HarnessHostLane 'Auto' $true) -eq 'Wsl') 'Windows must keep the WSL lane.'
    Assert-Native ((Resolve-HarnessHostLane 'Auto' $false) -eq 'Native') 'A non-Windows host must run natively.'
    Assert-Native ((Resolve-HarnessHostLane 'Wsl' $false) -eq 'Wsl' -and (Resolve-HarnessHostLane 'Native' $true) -eq 'Native') 'An explicit -Lane must win over the host.'
    $platform = Get-HarnessNativePlatform 'macos' 'arm64'
    Assert-Native ($platform.Rid -eq 'osx-arm64' -and $platform.Name -eq 'macos-arm64') 'macOS arm64 platform identity.'
    Assert-Native ((Get-HarnessNativePlatform 'linux' 'x64').Rid -eq 'linux-x64') 'Linux x64 platform identity.'
    $context = Get-HarnessContext $root
    $storage = Get-HarnessNativeStorage $context $platform
    $linuxStorage = Get-HarnessNativeStorage $context (Get-HarnessNativePlatform 'linux' 'x64')
    Assert-Native ($storage.BuildDirectory -ne $linuxStorage.BuildDirectory -and $storage.BuildDirectory -match 'native-macos-arm64$') 'Native build output is not isolated per OS/architecture.'
    Assert-Native ($storage.BuildDirectory.StartsWith((Join-Path $context.Root 'artifacts'))) 'Native build output left the checkout artifacts folder.'
    Assert-Native ((Get-HarnessNativeBudget 'auto' 12) -eq 6 -and (Get-HarnessNativeBudget 'auto' 2) -eq 2 -and (Get-HarnessNativeBudget 'auto' 200) -eq 64 -and (Get-HarnessNativeBudget '3' 12) -eq 3) 'Native auto budget is not max(2, CPUs/2).'

    # 2. Plan on a forced native lane: native fields, no container fields, nothing executed, Browser and container-only switches refused.
    $plan = & (Join-Path $root 'eng/Test-WithPostgres.ps1') -Lane Native -Project Unit -Filter ViewSync -Plan
    Assert-Native ($plan.Lane -eq 'native' -and $plan.Projects -contains 'UnitTests') 'Native plan lost its lane or projects.'
    foreach ($property in 'Dotnet', 'Sdk', 'SdkRequired', 'PostgresBin', 'BuildOutput', 'RuntimeIdentifier', 'Results', 'Acceptance') {
        Assert-Native ($null -ne $plan.PSObject.Properties[$property]) "Native plan has no $property."
    }
    foreach ($property in 'WslDistribution', 'DevImage', 'PostgreSqlImage', 'SourceMount') {
        Assert-Native ($null -eq $plan.PSObject.Properties[$property]) "Native plan still reports container property $property."
    }
    Assert-Native (($plan.RunnerArguments -join ' ') -match '-method \*ViewSync\* -maxThreads \d+$') 'Native plan did not resolve auto threads to a number.'
    Assert-Native ($plan.BuildOutput -match 'artifacts[\\/]build[\\/]native-' -and ($plan.BuildArguments -join ' ') -match '-maxcpucount:\d+') 'Native plan build output or build budget.'
    Assert-Native ($plan.Acceptance -match 'feedback only') 'Native plan does not state it is feedback only.'
    Assert-Throws { & (Join-Path $root 'eng/Test-WithPostgres.ps1') -Lane Native -Project Browser -Plan } 'Browser' 'The Browser suite must be refused on the native lane.'
    Assert-Throws { & (Join-Path $root 'eng/Test-WithPostgres.ps1') -Lane Native -WslDistribution X -Plan } 'WSL/container lane only' 'Container-only switches must be refused natively.'

    # 3. PostgreSQL binary resolution: complete folders only, an explicit path never falls back, nothing is installed.
    $good = Join-Path $sandbox 'pg/bin'; $partial = Join-Path $sandbox 'partial/bin'
    foreach ($dir in $good, $partial) { [void] (New-Item -ItemType Directory -Path $dir -Force) }
    foreach ($tool in 'initdb', 'pg_ctl', 'postgres', 'psql') { [IO.File]::WriteAllText((Join-Path $good $tool), ''); [IO.File]::WriteAllText((Join-Path $partial $tool), '') }
    Remove-Item -LiteralPath (Join-Path $partial 'psql')
    $found = Resolve-HarnessPostgresBin -Candidates @((Join-Path $sandbox 'none'), $partial, $good)
    Assert-Native ($found.Ok -and $found.Bin -eq [IO.Path]::GetFullPath($good)) 'The first complete PostgreSQL folder was not chosen.'
    Assert-Native ((Resolve-HarnessPostgresBin (Join-Path $sandbox 'pg')).Ok) 'An install root with bin/ was not accepted.'
    $missing = Resolve-HarnessPostgresBin -Candidates @($partial)
    Assert-Native (-not $missing.Ok -and $missing.Error -match 'missing psql' -and $missing.Error -match 'Nothing is installed') 'The refusal does not name what is missing.'
    $explicit = Resolve-HarnessPostgresBin (Join-Path $sandbox 'nowhere')
    Assert-Native (-not $explicit.Ok -and $explicit.Error -match 'nowhere') 'An explicit wrong -PostgresBin must not fall back.'

    # 4. SDK selection follows global.json (same major.minor, not older, no prerelease) and a missing SDK is a clear refusal.
    $required = Get-HarnessRequiredSdk $root
    Assert-Native ($required.Version -match '^\d+\.\d+\.\d+$') 'global.json SDK version unreadable.'
    $v = [version] $required.Version
    $newer = "$($v.Major).$($v.Minor).$($v.Build + 100)"
    Assert-Native ((Select-HarnessSdk @("$($v.Major - 1).0.100", "$($v.Major).$($v.Minor).$($v.Build)-preview.1", $required.Version, $newer, "$($v.Major).$($v.Minor + 1).0") $required) -eq $newer) 'SDK selection did not pick the highest matching feature band.'
    Assert-Native ($null -eq (Select-HarnessSdk @("$($v.Major - 1).0.100", "$($v.Major).$($v.Minor + 1).100") $required)) 'A different major.minor SDK was accepted.'
    $fakeDotnet = Join-Path $sandbox 'dotnet'; [IO.File]::WriteAllText($fakeDotnet, '')
    $sdkOk = Resolve-HarnessDotnet $root @($fakeDotnet) -Probe -ListSdks { @($required.Version) }
    Assert-Native ($sdkOk.Ok -and $sdkOk.Sdk -eq $required.Version -and $sdkOk.Path -eq $fakeDotnet) 'A matching SDK was not resolved.'
    $sdkBad = Resolve-HarnessDotnet $root @($fakeDotnet, (Join-Path $sandbox 'absent')) -Probe -ListSdks { @('9.0.100') }
    Assert-Native (-not $sdkBad.Ok -and $sdkBad.Error -match 'global\.json' -and $sdkBad.Error -match 'Nothing is installed') 'A missing SDK was not refused clearly.'
    $planned = Resolve-HarnessDotnet $root @((Join-Path $sandbox 'absent'))
    Assert-Native (-not $planned.Ok) 'Plan-time resolution must not throw when dotnet is absent.'

    # 5. Cache identity: SDK, RID and PostgreSQL version join the key; the lane is part of the inputs; container runs never match.
    $idA = Get-HarnessNativeEnvironmentId '11.0.100' 'osx-arm64' 'postgres 18.6'
    Assert-Native ($idA -match '^sha256:[0-9a-f]{64}$') 'The native environment id is not image-id shaped.'
    Assert-Native ($idA -ne (Get-HarnessNativeEnvironmentId '11.0.101' 'osx-arm64' 'postgres 18.6') -and $idA -ne (Get-HarnessNativeEnvironmentId '11.0.100' 'linux-x64' 'postgres 18.6') -and $idA -ne (Get-HarnessNativeEnvironmentId '11.0.100' 'osx-arm64' 'postgres 18.7')) 'SDK, RID or PostgreSQL version do not change the native environment id.'
    $inputs = New-HarnessNativeCacheInputs ('a' * 64) @('Unit', 'Component') @('X') @() '' @() 'Release' $true '11.0.100' 'osx-arm64'
    $other = New-HarnessNativeCacheInputs ('a' * 64) @('Component', 'Unit') @('X') @() '' @() 'Release' $true '11.0.100' 'osx-arm64'
    Assert-Native ((Get-HarnessResultInputsHash $inputs) -eq (Get-HarnessResultInputsHash $other)) 'Suite order changed the cache key.'
    foreach ($change in @({ $inputs.manifest = 'b' * 64 }, { $inputs.configuration = 'Debug' }, { $inputs.runtimeAsync = $false }, { $inputs.sdk = '11.0.101' }, { $inputs.rid = 'linux-arm64' }, { $inputs.filter = @('Y') }, { $inputs.lane = 'wsl' })) {
        $changed = New-HarnessNativeCacheInputs ('a' * 64) @('Unit', 'Component') @('X') @() '' @() 'Release' $true '11.0.100' 'osx-arm64'
        $inputs = $changed.Clone(); & $change
        Assert-Native ((Get-HarnessResultInputsHash $inputs) -ne (Get-HarnessResultInputsHash $changed)) 'A key input did not change the cache key.'
    }
    $results = Join-Path $sandbox 'test-results'; $folder = Join-Path $results '20261122-010203'
    [void] (New-Item -ItemType Directory -Path $folder -Force)
    [IO.File]::WriteAllText((Join-Path $folder 'Unit.ctrf.json'), '{"results":{"summary":{"tests":3,"failed":0,"other":0,"pending":0}}}')
    $hash = Get-HarnessResultInputsHash $other
    Write-HarnessResultKey $folder $other $hash $idA @('Unit')
    $hit = @(Find-HarnessReusableResults $results $hash @('Unit'))
    Assert-Native ($hit.Count -eq 1 -and $hit[0].ImageId -ceq $idA) 'A native passing run was not found for its key.'
    Assert-Native (@($hit | Where-Object { $_.ImageId -ceq (Get-HarnessNativeEnvironmentId '11.0.101' 'osx-arm64' 'postgres 18.6') }).Count -eq 0) 'A different SDK matched a recorded native run.'

    # 6. Follow-up selection for Invoke-Validation -Run: non-Windows runs only the service tests natively.
    $commands = @([pscustomobject]@{ Label = 'App'; File = '/r/eng/wsl-docker.ps1'; Arguments = @('check') }, [pscustomobject]@{ Label = 'Tests'; File = '/r/eng/Test-WithPostgres.ps1'; Arguments = @('-Project', 'Unit') })
    $mac = Select-RunnableFollowUps $commands $false
    Assert-Native ($mac.Lane -eq 'native' -and $mac.Run.Count -eq 1 -and $mac.Run[0].Label -eq 'Tests' -and $mac.Skipped.Count -eq 1) 'Non-Windows -Run must run only the native test command.'
    Assert-Native ((Select-RunnableFollowUps $commands $true).Run.Count -eq 2) 'Windows -Run must keep both builder commands.'

    # 7. Cleanup order of the throwaway server, with the process boundary stubbed (nothing is started).
    $script:calls = [Collections.Generic.List[string]]::new()
    $script:directoryAtStop = $null
    $script:failStep = $null
    function Invoke-HarnessNativeCommand([string] $File, [string[]] $Arguments = @(), [hashtable] $Environment = @{}, [int] $TimeoutSeconds = 60, [string] $LogPath, [switch] $Echo, [string] $WorkingDirectory) {
        $tool = [IO.Path]::GetFileNameWithoutExtension($File)
        $step = if ($tool -eq 'pg_ctl') { "pg_ctl-$($Arguments[0])" } else { $tool }
        $script:calls.Add($step)
        if ($step -eq 'initdb') { [void] (New-Item -ItemType Directory -Path $Arguments[1] -Force) }
        if ($step -eq 'pg_ctl-stop') { $script:directoryAtStop = Test-Path -LiteralPath (Split-Path -Parent $Arguments[2]) }
        $code = if ($script:failStep -eq $step) { 1 } else { 0 }
        [pscustomobject]@{ ExitCode = $code; Output = if ($step -eq 'psql') { "1`n" } else { '' }; TimedOut = $false }
    }
    $seen = $null; $passwordFileText = $null
    Invoke-HarnessWithNativePostgres '/nonexistent/bin' { param($variables)
        $script:seen = $variables
        $script:passwordFileText = [IO.File]::ReadAllText($variables.PREMIERECALENDAR_TEST_POSTGRES_PASSWORD_FILE)
        $script:modeInt = if ($IsWindows) { 384 } else { [int][IO.File]::GetUnixFileMode($variables.PREMIERECALENDAR_TEST_POSTGRES_PASSWORD_FILE) }
        $script:folder = Split-Path -Parent $variables.PREMIERECALENDAR_TEST_POSTGRES_PASSWORD_FILE
    } -TempRoot $sandbox
    Assert-Native (($script:calls -join ',') -eq 'initdb,pg_ctl-start,psql,pg_ctl-stop') "Unexpected server step order: $($script:calls -join ',')"
    Assert-Native ($script:directoryAtStop -eq $true) 'The server was stopped after its folder was already removed.'
    Assert-Native (-not (Test-Path -LiteralPath $script:folder)) 'The throwaway server folder was not deleted.'
    Assert-Native ($seen.PREMIERECALENDAR_TEST_POSTGRES -match '^Host=127\.0\.0\.1;Port=\d+;Database=postgres;Username=premierecalendar;') 'The connection string differs from the container lane.'
    Assert-Native ($passwordFileText -match '^[0-9a-f]{32}$' -and $seen.PREMIERECALENDAR_TEST_POSTGRES -notmatch $passwordFileText) 'The password must live in the password file only.'
    Assert-Native ($script:modeInt -eq 384) "The password file is not private (mode $($script:modeInt), expected 0600)."
    # A failing body still stops the server and deletes the folder.
    $script:calls.Clear(); $script:folder = $null
    Assert-Throws { Invoke-HarnessWithNativePostgres '/nonexistent/bin' { param($variables) $script:folder = Split-Path -Parent $variables.PREMIERECALENDAR_TEST_POSTGRES_PASSWORD_FILE; throw 'suite exploded' } -TempRoot $sandbox } 'suite exploded' 'The body failure was swallowed.'
    Assert-Native (($script:calls -join ',') -eq 'initdb,pg_ctl-start,psql,pg_ctl-stop' -and -not (Test-Path -LiteralPath $script:folder)) 'A failing body skipped the cleanup.'
    # A server that never becomes ready fails within the bound and is still stopped and removed.
    $script:calls.Clear(); $script:failStep = 'psql'
    $watch = [Diagnostics.Stopwatch]::StartNew()
    Assert-Throws { Invoke-HarnessWithNativePostgres '/nonexistent/bin' { param($variables) throw 'must not run' } -ReadySeconds 1 -TempRoot $sandbox } 'did not become ready' 'A server that never answers was not reported.'
    Assert-Native ($watch.Elapsed.TotalSeconds -lt 20 -and $script:calls -contains 'pg_ctl-stop') 'Readiness waiting is unbounded or skipped the stop.'
    # initdb failure: nothing to stop, folder still removed.
    $script:calls.Clear(); $script:failStep = 'initdb'
    Assert-Throws { Invoke-HarnessWithNativePostgres '/nonexistent/bin' { param($variables) throw 'must not run' } -TempRoot $sandbox } 'initdb failed' 'An initdb failure was not reported.'
    Assert-Native (($script:calls -join ',') -eq 'initdb' -and @(Get-ChildItem -LiteralPath $sandbox -Directory -Filter 'pcpg-*').Count -eq 0) 'An initdb failure left a folder behind or tried to stop a server.'
    $script:failStep = $null
    Assert-Throws { Remove-HarnessNativePostgresDirectory $sandbox ([IO.Path]::GetDirectoryName($sandbox)) } 'Refusing' 'The folder removal accepts arbitrary paths.'
    Remove-Item function:Invoke-HarnessNativeCommand
    . (Join-Path $root 'eng/Harness.Common.ps1')   # restores the real process boundary the stub replaced

    # 8. The real process boundary (non-Windows only): exit codes, merged streams, environment and the deadline.
    if (-not $IsWindows) {
        # A host that dies without running `finally` (SIGTERM/SIGKILL) must not leave the server or folder: the watchdog cleans up.
        $orphanDir = Join-Path $sandbox 'pcpg-0123456789ab'
        [void] (New-Item -ItemType Directory -Path (Join-Path $orphanDir 'data') -Force)
        $parentStart = [Diagnostics.ProcessStartInfo]::new('/bin/sh'); foreach ($w in '-c', 'sleep 60') { $parentStart.ArgumentList.Add($w) }
        $parentStart.UseShellExecute = $false
        $parent = [Diagnostics.Process]::Start($parentStart)
        $dog = Start-HarnessNativeWatchdog $parent.Id $orphanDir '/usr/bin/true' (Join-Path $orphanDir 'data')
        Start-Sleep -Milliseconds 1500
        Assert-Native ($null -ne $dog -and -not $dog.HasExited -and (Test-Path -LiteralPath $orphanDir)) 'The watchdog acted while its parent was alive.'
        $parent.Kill(); $parent.WaitForExit(5000) | Out-Null; $parent.Dispose()
        $limit = [DateTime]::UtcNow.AddSeconds(15)
        while ((Test-Path -LiteralPath $orphanDir) -and [DateTime]::UtcNow -lt $limit) { Start-Sleep -Milliseconds 200 }
        Assert-Native (-not (Test-Path -LiteralPath $orphanDir)) 'The watchdog did not remove the folder after its parent died.'
        [void] $dog.WaitForExit(5000); Assert-Native $dog.HasExited 'The watchdog kept running after cleaning up.'; $dog.Dispose()
        $run = Invoke-HarnessNativeCommand '/bin/sh' @('-c', 'echo out; echo err 1>&2; echo "$PC_NATIVE_PROBE"; exit 3') -Environment @{ PC_NATIVE_PROBE = 'probe-value' } -TimeoutSeconds 20
        Assert-Native ($run.ExitCode -eq 3 -and $run.Output -match 'out' -and $run.Output -match 'err' -and $run.Output -match 'probe-value' -and -not $run.TimedOut) 'The process boundary lost output, environment or exit code.'
        $log = Join-Path $sandbox 'run.log'
        $slow = Invoke-HarnessNativeCommand '/bin/sh' @('-c', 'echo started; sleep 30') -TimeoutSeconds 1 -LogPath $log
        Assert-Native ($slow.TimedOut -and $slow.ExitCode -eq 124 -and (Get-Content -LiteralPath $log -Raw) -match 'TIMEOUT') 'A command that exceeds its deadline was not stopped and logged.'
    }
} finally {
    $env:PC_HARNESS_LANE = $savedLane
    if ((Split-Path -Leaf $sandbox) -match '^pc-native-[0-9a-f]{12}$' -and (Test-Path -LiteralPath $sandbox)) { Remove-Item -LiteralPath $sandbox -Recurse -Force -ErrorAction SilentlyContinue }
}
Write-Host "$count native lane checks passed; no SDK, PostgreSQL, WSL or Docker was used."
