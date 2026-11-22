#Requires -Version 7.0
# Docker-free checks for the release-step speed changes: shared cache volumes, the Trivy database age gate,
# concurrent Trivy/Syft failure propagation and the -WhatIf plans.
[CmdletBinding()]
param([string] $PythonExecutable)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $root 'eng/release/ReleaseCommon.ps1')
$count = 0
function Assert-Speed([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw "Release speed check failed: $Message" }
    $script:count++
}
if (-not $PythonExecutable) {
    $found = Get-Command python3, python -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $found) { throw 'Python 3 is required.' }
    $PythonExecutable = $found.Source
}
$work = Join-Path $root "artifacts/release-speed-tests/$([guid]::NewGuid().ToString('N').Substring(0, 8))"
$null = New-Item -ItemType Directory -Path $work -Force

# --- shared cache volumes: names, labels and the refuse-foreign-volume rule (stubbed Docker)
$trivy = Get-ReleaseCacheVolume 'trivy'; $nuget = Get-ReleaseCacheVolume 'nuget'
Assert-Speed ($trivy.Name -ceq 'premierecalendar-release-trivy-cache' -and $trivy.Label -ceq 'premierecalendar.release-cache=trivy') 'Trivy cache volume name/label changed.'
Assert-Speed ($nuget.Name -ceq 'premierecalendar-release-nuget-packages' -and $nuget.Label -ceq 'premierecalendar.release-cache=nuget') 'NuGet cache volume name/label changed.'
$script:Calls = [Collections.Generic.List[string]]::new(); $script:Existing = @{}
function Invoke-ReleaseDockerClient {
    param([string[]] $DockerArguments, [double] $CallLimit = 0, [switch] $CaptureOutput, $StandardErrorWriter)
    $script:Calls.Add("client $($DockerArguments -join ' ')")
    $name = $DockerArguments[-1]
    if ($script:Existing.ContainsKey($name)) { [pscustomobject]@{ ExitCode = 0; Output = "$($script:Existing[$name])`n" } } else { [pscustomobject]@{ ExitCode = 1; Output = '' } }
}
function Invoke-LocalDocker { $script:Calls.Add("run $($args -join ' ')") }
$null = Initialize-ReleaseCacheVolume 'trivy'
Assert-Speed (@($script:Calls | Where-Object { $_ -like 'run volume create --label premierecalendar.release-cache=trivy premierecalendar-release-trivy-cache' }).Count -eq 1) 'A missing cache volume was not created with its label.'
$script:Calls.Clear(); $script:Existing['premierecalendar-release-trivy-cache'] = 'trivy'
$null = Initialize-ReleaseCacheVolume 'trivy'
Assert-Speed ($script:Calls.Count -eq 1 -and $script:Calls[0] -like 'client volume inspect*') 'An existing labelled cache volume needed more than one Docker call or was recreated.'
$script:Existing['premierecalendar-release-nuget-packages'] = ''
$refused = $false
try { $null = Initialize-ReleaseCacheVolume 'nuget' } catch { $refused = $_.Exception.Message -like '*lacks the label*' }
Assert-Speed $refused 'A same-named volume without the release-cache label was accepted.'
Remove-Item Function:\Invoke-ReleaseDockerClient, Function:\Invoke-LocalDocker
. (Join-Path $root 'eng/release/ReleaseCommon.ps1')

# --- Trivy database age gate (summarize_scan.py), including that the other checks still decide the exit code
$bundle = Join-Path $work 'bundle'; $null = New-Item -ItemType Directory -Path $bundle
function New-ScanFixture([string] $Name, $ScannersJson, [switch] $Vulnerable) {
    $dir = Join-Path $work $Name; $null = New-Item -ItemType Directory -Path $dir
    $results = if ($Vulnerable) { '[{"Vulnerabilities":[{"Severity":"HIGH","FixedVersion":"1.2"}]}]' } else { '[{"Vulnerabilities":null}]' }
    Set-Content -LiteralPath (Join-Path $dir 'release-9.9.9-trivy.json') -Value "{`"Results`":$results}"
    Set-Content -LiteralPath (Join-Path $dir 'release-9.9.9-sbom.cdx.json') -Value '{"bomFormat":"CycloneDX","specVersion":"1.6","components":[{"name":"x"}]}'
    Set-Content -LiteralPath (Join-Path $dir 'release-9.9.9-audit.json') -Value '{"sourceRevision":"r","archiveSha256":"a","checks":{"ok":true}}'
    Set-Content -LiteralPath (Join-Path $dir 'release-nuget-audit.log') -Value 'Project has no vulnerable packages'
    if ($null -ne $ScannersJson) { Set-Content -LiteralPath (Join-Path $dir 'release-9.9.9-scanners.json') -Value $ScannersJson }
    $dir
}
function New-ScannersJson([string] $UpdatedAt, [string] $CheckedAt = '2026-11-22T12:00:00Z') {
    $raw = (@{ Version = 2; UpdatedAt = $UpdatedAt; NextUpdate = '2026-11-23T00:00:00Z'; DownloadedAt = '2026-11-21T00:00:00Z' } | ConvertTo-Json -Compress)
    @{ trivyDbMetadataRaw = $raw; trivyDbCheckedAtUtc = $CheckedAt } | ConvertTo-Json
}
function Invoke-Summary([string] $Dir) {
    $output = & $PythonExecutable -I (Join-Path $root 'eng/release/summarize_scan.py') --bundle $Dir --version 9.9.9 2>&1
    [pscustomobject]@{ Exit = $LASTEXITCODE; Report = (Get-Content -LiteralPath (Join-Path $Dir 'release-9.9.9-scan-summary.json') -Raw -ErrorAction SilentlyContinue); Output = "$output" }
}
$fresh = Invoke-Summary (New-ScanFixture 'fresh' (New-ScannersJson '2026-11-21T20:00:00.123456789Z'))
Assert-Speed ($fresh.Exit -eq 0 -and $fresh.Report -match '"trivyDbFresh": true' -and $fresh.Report -match '"ageHours": 16.0') 'A 16 hour old Trivy DB did not pass with its age recorded.'
$stale = Invoke-Summary (New-ScanFixture 'stale' (New-ScannersJson '2026-11-19T11:59:00Z'))
Assert-Speed ($stale.Exit -eq 1 -and $stale.Report -match '"trivyDbFresh": false') 'A Trivy DB older than 48 hours did not fail the scan summary.'
$unreadable = Invoke-Summary (New-ScanFixture 'unreadable' (@{ trivyDbMetadataRaw = 'not json'; trivyDbCheckedAtUtc = '2026-11-22T12:00:00Z' } | ConvertTo-Json))
Assert-Speed ($unreadable.Exit -eq 1 -and $unreadable.Report -match '"trivyDbFresh": false') 'Unreadable Trivy DB metadata did not fail.'
$future = Invoke-Summary (New-ScanFixture 'future' (New-ScannersJson '2026-11-23T12:00:00Z'))
Assert-Speed ($future.Exit -eq 1) 'A DB timestamp in the future (clock or metadata fault) was accepted.'
$missing = New-ScanFixture 'missing' $null
$missingFailed = $false
& $PythonExecutable -I (Join-Path $root 'eng/release/summarize_scan.py') --bundle $missing --version 9.9.9 2>&1 | Out-Null
Assert-Speed ($LASTEXITCODE -ne 0 -and -not (Test-Path -LiteralPath (Join-Path $missing 'release-9.9.9-scan-summary.json'))) 'A missing scanners evidence file did not stop the summary.'
$vulnerable = Invoke-Summary (New-ScanFixture 'vulnerable' (New-ScannersJson '2026-11-21T20:00:00Z') -Vulnerable)
Assert-Speed ($vulnerable.Exit -eq 1 -and $vulnerable.Report -match '"trivyZeroVulnerabilities": false' -and $vulnerable.Report -match '"trivyDbFresh": true') 'A vulnerability no longer fails when the DB is fresh.'

# --- concurrent Trivy/Syft script: failure propagation, separate logs, true overlap (bash with a stub docker)
$bash = $null
if ($IsWindows) {
    # Git for Windows' bash; the System32/WindowsApps bash.exe would start WSL instead.
    $bash = @(Get-Command bash -All -CommandType Application -ErrorAction SilentlyContinue | Where-Object { $_.Source -like '*\Git\*' } | ForEach-Object Source)[0]
}
else { $bash = (Get-Command bash -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1)?.Source }
$stub = 'docker() { case "$*" in *trivy-fixture*) sleep 2; echo trivy-out; return ${TRIVY_EXIT:-0};; *syft-fixture*) sleep 2; echo syft-out; return ${SYFT_EXIT:-0};; esac; return 99; }'
$stub = 'docker() { case "$*" in *trivy-fixture*) sleep 2; echo trivy-out; return ${TRIVY_EXIT:-0};; *syft-fixture*) sleep 2; echo syft-out; return ${SYFT_EXIT:-0};; *meta-fixture*) echo meta-out; return ${META_EXIT:-0};; esac; return 99; }'
function Invoke-ScanScript([int] $TrivyExit, [int] $SyftExit, [string] $Name, [int] $MetaExit = 0) {
    $dir = Join-Path $work $Name; $null = New-Item -ItemType Directory -Path $dir
    $staged = Join-Path $dir 'staged'; $null = New-Item -ItemType Directory -Path $staged
    $posix = $dir -replace '\\', '/'
    $text = Get-ConcurrentScanScript -TrivyArguments @('run', 'trivy-fixture', "it's") -SyftArguments @('run', 'syft-fixture') `
        -TrivyLog "$posix/t.log" -SyftLog "$posix/s.log" -MetadataArguments @('run', 'meta-fixture') -MetadataFile "$posix/meta.json" -CleanupPath "$posix/staged" -DockerDefinition $stub
    $scriptPath = Join-Path $dir 'scan.sh'
    [IO.File]::WriteAllText($scriptPath, $text.Replace("`r`n", "`n"))
    $env:TRIVY_EXIT = $TrivyExit; $env:SYFT_EXIT = $SyftExit; $env:META_EXIT = $MetaExit
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $null = & $bash ($scriptPath -replace '\\', '/') 2>&1
    $exit = $LASTEXITCODE; $elapsed = $clock.Elapsed.TotalSeconds
    Remove-Item Env:TRIVY_EXIT, Env:SYFT_EXIT, Env:META_EXIT
    [pscustomobject]@{ Exit = $exit; Elapsed = $elapsed; Dir = $dir; Script = $scriptPath; Staged = $staged }
}
if ($bash) {
    $both = Invoke-ScanScript 0 0 'scan-ok'
    Assert-Speed ($both.Exit -eq 0 -and $both.Elapsed -lt 3.6) "Trivy and Syft did not overlap or failed (exit $($both.Exit), $([math]::Round($both.Elapsed, 2)) s)."
    Assert-Speed ((Get-Content -LiteralPath (Join-Path $both.Dir 't.log') -Raw).Trim() -ceq 'trivy-out' -and (Get-Content -LiteralPath (Join-Path $both.Dir 's.log') -Raw).Trim() -ceq 'syft-out') 'Per-tool logs are mixed up.'
    Assert-Speed (-not (Test-Path -LiteralPath $both.Staged)) 'The staged copy was not removed when the scan script ended.'
    Assert-Speed ((Get-Content -LiteralPath (Join-Path $both.Dir 'meta.json') -Raw).Trim() -ceq 'meta-out') 'The Trivy DB metadata was not captured after a successful scan.'
    $trivyFailed = Invoke-ScanScript 3 0 'scan-trivy'
    Assert-Speed ($trivyFailed.Exit -eq 11 -and -not (Test-Path -LiteralPath (Join-Path $trivyFailed.Dir 'meta.json')) -and -not (Test-Path -LiteralPath $trivyFailed.Staged)) 'A Trivy failure was not reported as exit 11 with cleanup and no metadata read.'
    Assert-Speed ((Invoke-ScanScript 0 0 'scan-meta' 5).Exit -eq 14) 'A failed DB metadata read did not fail the step (exit 14).'
    Assert-Speed ((Invoke-ScanScript 0 4 'scan-syft').Exit -eq 12) 'A Syft failure was not reported as exit 12.'
    Assert-Speed ((Invoke-ScanScript 3 4 'scan-both').Exit -eq 13) 'A double failure was not reported as exit 13.'
    $env:TRIVY_EXIT = 0; $env:SYFT_EXIT = 0; $env:META_EXIT = 0
    $null = & $bash ($both.Script -replace '\\', '/') 2>&1
    $rerun = $LASTEXITCODE; Remove-Item Env:TRIVY_EXIT, Env:SYFT_EXIT, Env:META_EXIT
    Assert-Speed ($rerun -eq 13) 'Existing scan logs were overwritten instead of refused.'
}
else { Write-Host 'SKIPPED: no bash found for the concurrent scan script checks.' }

# --- -WhatIf plans (no Docker) for the changed steps
$plansBundle = Join-Path $work 'plan-bundle'; $null = New-Item -ItemType Directory -Path $plansBundle
$config = [ordered]@{
    schema = 1; version = '9.9.9'; bundleName = 'plan-bundle'; imageTag = 'premierecalendar:plan'; resourcePrefix = 'planprefix'
    sdkImage = 'sdk-fixture'; runtimeImage = 'rt-fixture'; trivyImage = 'trivy-fixture'; syftImage = 'syft-fixture' }
Set-Content -LiteralPath (Join-Path $plansBundle 'release-config.json') -Value ($config | ConvertTo-Json)
$pwshExe = (Get-Process -Id $PID).Path
$scans = (& $pwshExe -NoProfile -File (Join-Path $root 'eng/release/Invoke-Scans.ps1') -Bundle $plansBundle -WhatIf 2>&1) -join "`n"
Assert-Speed ($scans -like '*premierecalendar-release-trivy-cache*never deleted*' -and $scans -like '*side by side*' -and $scans -like '*older than 48 h*') 'Invoke-Scans -WhatIf lost the shared cache, concurrency or DB age lines.'
Assert-Speed ($scans -notlike '*scan cache volume*') 'Invoke-Scans still plans to delete a cache volume.'
$audit = (& $pwshExe -NoProfile -File (Join-Path $root 'eng/release/Invoke-NuGetAudit.ps1') -Bundle $plansBundle -WhatIf 2>&1) -join "`n"
Assert-Speed ($audit -like '*premierecalendar-release-nuget-packages*never deleted*' -and $audit -like '*HTTP cache on tmpfs*') 'Invoke-NuGetAudit -WhatIf lost the shared package volume or the fresh vulnerability metadata line.'
Assert-Speed (-not (Test-Path -LiteralPath (Join-Path $plansBundle 'release-nuget-audit.log'))) '-WhatIf wrote evidence.'

# --- NuGet audit container: read-only, tmpfs HTTP cache, packages on the labelled volume
$auditScript = Get-Content -LiteralPath (Join-Path $root 'eng/release/Invoke-NuGetAudit.ps1') -Raw
Assert-Speed ($auditScript -match '--read-only' -and $auditScript -match 'NUGET_HTTP_CACHE_PATH=/tmp/' -and $auditScript -match 'NUGET_PACKAGES=/nuget-packages' -and $auditScript -notmatch 'NUGET_PACKAGES=/tmp') 'The NuGet audit container lost read-only, the tmpfs HTTP cache or the package volume.'

Remove-Item -LiteralPath $work -Recurse -Force
Write-Host "$count release speed checks passed."
