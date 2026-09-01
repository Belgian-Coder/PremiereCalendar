[CmdletBinding()]
param(
    [ValidateSet('Build', 'TestUnit', 'TestComponent', 'TestIntegration', 'TestBrowser', 'TestPostgreSql', 'Fast', 'Qualify', 'Package', 'Installed')]
    [string] $Mode = 'Fast',
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version,
    [uri] $InstalledBaseUri,
    [switch] $NoResume,
    [string] $QualificationReport
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$artifactsRoot = Join-Path $root 'artifacts\workflow'
$dotnet = Join-Path $root '.dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) { $dotnet = 'dotnet' }

function Invoke-Native {
    param(
        [Parameter(Mandatory)][string] $FilePath,
        [Parameter(Mandatory)][string[]] $ArgumentList,
        [switch] $SensitiveEnvironment
    )
    if (-not $SensitiveEnvironment) {
        Add-Content -LiteralPath $script:currentLog -Value ("$FilePath " + ($ArgumentList -join ' '))
    }
    & $FilePath @ArgumentList 2>&1 | Tee-Object -FilePath $script:currentLog -Append
    $exitCode = $LASTEXITCODE
    if ($exitCode -ne 0) { throw "$FilePath exited with code $exitCode." }
}

function Get-Hash {
    param([Parameter(Mandatory)][string] $Path)
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Get-RelativeHashRecords {
    param([Parameter(Mandatory)][string[]] $Paths)
    @($Paths | Where-Object { Test-Path -LiteralPath (Join-Path $root $_) -PathType Leaf } |
        Sort-Object -Unique | ForEach-Object {
            [ordered]@{ path = ($_ -replace '\\', '/'); sha256 = Get-Hash (Join-Path $root $_) }
        })
}

function Get-CombinedHash {
    param([Parameter(Mandatory)] $Records)
    $text = (($Records | ForEach-Object { "$($_.path)`0$($_.sha256)" }) -join "`n")
    $bytes = [Text.Encoding]::UTF8.GetBytes($text)
    ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))).ToLowerInvariant()
}

function Get-GitLines {
    param([Parameter(Mandatory)][string[]] $Arguments)
    $lines = @(& git -C $root @Arguments)
    if ($LASTEXITCODE -ne 0) { throw "git $($Arguments -join ' ') failed." }
    @($lines | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
}

function Get-WorkflowIdentity {
    $tracked = Get-GitLines @('ls-files')
    $untracked = Get-GitLines @('ls-files', '--others', '--exclude-standard')
    $sourceRecords = Get-RelativeHashRecords @($tracked + $untracked)
    $dependencyPaths = @('global.json', 'Directory.Build.props') +
        @(Get-ChildItem -LiteralPath $root -Recurse -Filter '*.csproj' -File |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
            ForEach-Object { [IO.Path]::GetRelativePath($root, $_.FullName) }) +
        @(Get-ChildItem -LiteralPath $root -Recurse -Filter 'packages.lock.json' -File |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } |
            ForEach-Object { [IO.Path]::GetRelativePath($root, $_.FullName) })
    $dependencyRecords = Get-RelativeHashRecords $dependencyPaths
    $assetPaths = @(Get-ChildItem -LiteralPath (Join-Path $root 'PremiereCalendar\wwwroot') -Recurse -File |
        ForEach-Object { [IO.Path]::GetRelativePath($root, $_.FullName) }) +
        @('Dockerfile', 'compose.yaml', 'compose.dev.yaml', '.dockerignore')
    $assetRecords = Get-RelativeHashRecords $assetPaths
    $head = @(Get-GitLines @('rev-parse', 'HEAD'))[0].Trim()
    $branch = (Get-GitLines @('branch', '--show-current') | Select-Object -First 1)
    $upstream = @(& git -C $root rev-parse --abbrev-ref '@{upstream}' 2>$null)
    $upstreamCommit = $null
    if ($LASTEXITCODE -eq 0 -and $upstream.Count -gt 0) {
        $upstreamCommit = @(Get-GitLines @('rev-parse', $upstream[0]))[0].Trim()
    }
    $status = @(Get-GitLines @('status', '--porcelain=v1'))
    [ordered]@{
        commit = $head
        branch = $branch
        upstream = if ($upstream.Count -gt 0) { $upstream[0] } else { $null }
        upstreamCommit = $upstreamCommit
        remoteUrl = (& git -C $root remote get-url origin).Trim()
        dirty = $status.Count -gt 0
        worktreeStatus = $status
        sourceFingerprint = Get-CombinedHash $sourceRecords
        dependenciesFingerprint = Get-CombinedHash $dependencyRecords
        generatedAssetsFingerprint = Get-CombinedHash $assetRecords
        dependencyFiles = $dependencyRecords
        generatedAssets = $assetRecords
    }
}

function Get-ToolIdentity {
    $dotnetVersion = (& $dotnet --version).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Pinned .NET SDK is unavailable.' }
    [ordered]@{
        powershell = $PSVersionTable.PSVersion.ToString()
        dotnetSdk = $dotnetVersion
        git = (& git --version).Trim()
        os = [Environment]::OSVersion.VersionString
        architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    }
}

function Save-Report {
    $script:report.updatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    $script:report | ConvertTo-Json -Depth 12 |
        Set-Content -LiteralPath $script:reportPath -Encoding utf8NoBOM
}

function Get-LatestStage {
    param([Parameter(Mandatory)][string] $Name)
    @($script:report.stages | Where-Object name -eq $Name | Sort-Object attempt -Descending |
        Select-Object -First 1) | Select-Object -First 1
}

function Invoke-Stage {
    param(
        [Parameter(Mandatory)][string] $Name,
        [Parameter(Mandatory)][scriptblock] $Action
    )
    $previous = Get-LatestStage $Name
    if (-not $NoResume -and $null -ne $previous -and $previous.result -eq 'passed') {
        Write-Host "RESUME $Name (already passed)"
        return
    }
    $attempt = if ($null -eq $previous) { 1 } else { [int]$previous.attempt + 1 }
    $safeName = $Name -replace '[^A-Za-z0-9_.-]', '-'
    $logName = ('{0:D2}-{1}-attempt-{2}.log' -f ($script:report.stages.Count + 1), $safeName, $attempt)
    $script:currentLog = Join-Path $script:runRoot $logName
    $started = [DateTimeOffset]::UtcNow
    $stopwatch = [Diagnostics.Stopwatch]::StartNew()
    $result = 'passed'
    $failure = $null
    Write-Host "START  $Name"
    try {
        & $Action
    }
    catch {
        $result = 'failed'
        $failure = $_.Exception.Message
        throw
    }
    finally {
        $stopwatch.Stop()
        $record = [ordered]@{
            name = $Name
            attempt = $attempt
            startedUtc = $started.ToString('O')
            endedUtc = [DateTimeOffset]::UtcNow.ToString('O')
            durationSeconds = [math]::Round($stopwatch.Elapsed.TotalSeconds, 3)
            result = $result
            log = $logName
            failure = $failure
        }
        $script:report.stages = @($script:report.stages) + @($record)
        if ($result -eq 'failed') { $script:report.result = 'failed' }
        Save-Report
        Write-Host ("{0} {1} ({2:n1}s)" -f $result.ToUpperInvariant(), $Name, $stopwatch.Elapsed.TotalSeconds)
    }
}

function Initialize-WslDocker {
    & (Join-Path $root 'eng\wsl-docker.ps1') logs | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'WSL Docker could not be initialized.' }
    $resolved = (& wsl.exe -d Ubuntu-24.04 -- wslpath -a ($root -replace '\\', '/')).Trim()
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($resolved)) {
        throw 'Repository path could not be resolved in Ubuntu-24.04.'
    }
    $resolved
}

function Invoke-WslBash {
    param([Parameter(Mandatory)][string] $Command)
    Invoke-Native -FilePath 'wsl.exe' -ArgumentList @('-d', 'Ubuntu-24.04', '--', 'bash', '-lc', $Command)
}

function Invoke-PostgreSqlTests {
    $script:wslRoot = Initialize-WslDocker
    $wasRunning = [string]::Join('', @(& wsl.exe -d Ubuntu-24.04 -- bash -lc "cd '$script:wslRoot' && docker compose -f compose.dev.yaml ps -q database")).Trim()
    try {
        if ([string]::IsNullOrWhiteSpace($wasRunning)) {
            Invoke-WslBash "cd '$script:wslRoot' && docker compose -f compose.dev.yaml up -d database --wait"
        }
        $passwordPath = Join-Path $root '.secrets\dev-postgres-password'
        if (-not (Test-Path -LiteralPath $passwordPath -PathType Leaf)) { throw 'Local PostgreSQL secret file is missing.' }
        $password = (Get-Content -LiteralPath $passwordPath -Raw).Trim()
        $prior = $env:PREMIERECALENDAR_TEST_POSTGRES
        try {
            $env:PREMIERECALENDAR_TEST_POSTGRES = "Host=127.0.0.1;Port=55432;Database=postgres;Username=premierecalendar;Password=$password"
            Invoke-Native $dotnet @('test', (Join-Path $root 'tests\PremiereCalendar.IntegrationTests\PremiereCalendar.IntegrationTests.csproj'), '-c', 'Release', '--no-build', '--no-restore', '--nologo', '--filter', 'FullyQualifiedName~PostgresPersistenceTests', '--logger', 'trx;LogFilePrefix=postgresql') -SensitiveEnvironment
        } finally {
            $env:PREMIERECALENDAR_TEST_POSTGRES = $prior
        }
    } finally {
        if ([string]::IsNullOrWhiteSpace($wasRunning)) {
            Invoke-WslBash "cd '$script:wslRoot' && docker compose -f compose.dev.yaml stop database"
        }
    }
}

function Complete-Run {
    $latest = @{}
    foreach ($stage in $script:report.stages) { $latest[$stage.name] = $stage }
    $failed = @($latest.Values | Where-Object result -ne 'passed')
    $script:report.result = if ($failed.Count -eq 0) { 'passed' } else { 'failed' }
    $script:report.completedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    $startedAt = [DateTimeOffset]$script:report.startedUtc
    $completedAt = [DateTimeOffset]$script:report.completedUtc
    $script:report.durationSeconds = [math]::Round(($completedAt - $startedAt).TotalSeconds, 3)
    $script:report.slowestStages = @($latest.Values | Sort-Object durationSeconds -Descending |
        Select-Object -First 5 name, durationSeconds)
    if (Test-Path -LiteralPath (Join-Path $root 'artifacts\workflow-baseline\baseline.json')) {
        $baseline = Get-Content -LiteralPath (Join-Path $root 'artifacts\workflow-baseline\baseline.json') -Raw | ConvertFrom-Json
        $script:report.baseline = [ordered]@{
            measuredUtc = $baseline.measuredUtc
            durationSeconds = $baseline.totalSeconds
            deltaSeconds = [math]::Round($script:report.durationSeconds - [double]$baseline.totalSeconds, 3)
        }
    }
    Save-Report
    if ($script:report.mode -eq 'Qualify') {
        Copy-Item -LiteralPath $script:reportPath -Destination (Join-Path $artifactsRoot 'latest-qualification.json') -Force
    }
    Write-Host "Report: $script:reportPath"
    if ($script:report.result -ne 'passed') { throw "Workflow $Mode failed." }
}

function Invoke-InstalledValidation {
    if ($null -eq $InstalledBaseUri) { throw '-InstalledBaseUri is required for Installed mode.' }
    New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
    $base = $InstalledBaseUri.AbsoluteUri.TrimEnd('/')
    $started = [DateTimeOffset]::UtcNow
    $checks = @()
    foreach ($item in @(
        @{ name = 'readiness'; path = '/health/ready' },
        @{ name = 'version'; path = '/health/version' },
        @{ name = 'root'; path = '/' },
        @{ name = 'blazor-boot'; path = '/_framework/blazor.web.js' }
    )) {
        $uri = "$base$($item.path)"
        $sw = [Diagnostics.Stopwatch]::StartNew()
        $response = Invoke-WebRequest -Uri $uri -UseBasicParsing -TimeoutSec 20
        $sw.Stop()
        if ($response.StatusCode -ne 200) { throw "$uri returned $($response.StatusCode)." }
        $checks += [ordered]@{
            name = $item.name
            uri = $uri
            status = [int]$response.StatusCode
            durationSeconds = [math]::Round($sw.Elapsed.TotalSeconds, 3)
            bytes = $response.RawContentLength
            contentSha256 = ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($response.Content)))).ToLowerInvariant()
        }
        switch ($item.name) {
            'readiness' { if ($response.Content.Trim() -ne 'Healthy') { throw 'Readiness is not Healthy.' } }
            'version' {
                $versionInfo = $response.Content | ConvertFrom-Json
                if (-not $versionInfo.sourceRevision -or -not $versionInfo.buildId -or -not $versionInfo.database.healthy -or
                    $versionInfo.database.currentSchemaVersion -ne $versionInfo.database.targetSchemaVersion) {
                    throw 'Runtime/version/database identity is incomplete or unhealthy.'
                }
            }
            'root' { if ($response.Content -notmatch '_framework/blazor\.web\.js') { throw 'Root does not reference the Blazor boot asset.' } }
            'blazor-boot' { if ($response.Content -notmatch 'Blazor') { throw 'Blazor boot asset content is invalid.' } }
        }
    }
    $report = [ordered]@{
        schemaVersion = 1
        mode = 'Installed'
        result = 'passed'
        readOnly = $true
        baseUri = $base
        startedUtc = $started.ToString('O')
        completedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        runtime = $versionInfo
        checks = $checks
        note = 'HTTP/runtime acceptance only; browser rendering is recorded separately.'
    }
    $path = Join-Path $artifactsRoot ('installed-{0}.json' -f ([DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')))
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding utf8NoBOM
    Write-Host "Installed validation report: $path"
}

function Invoke-Package {
    if ([string]::IsNullOrWhiteSpace($Version)) { throw '-Version is required for Package mode.' }
    if ([string]::IsNullOrWhiteSpace($QualificationReport)) {
        $QualificationReport = Join-Path $artifactsRoot 'latest-qualification.json'
    }
    if (-not (Test-Path -LiteralPath $QualificationReport -PathType Leaf)) { throw 'Qualification report was not found.' }
    $qualified = Get-Content -LiteralPath $QualificationReport -Raw | ConvertFrom-Json
    $current = Get-WorkflowIdentity
    if ($qualified.result -ne 'passed' -or $qualified.mode -ne 'Qualify') { throw 'Qualification did not pass completely.' }
    if ($qualified.identity.sourceFingerprint -ne $current.sourceFingerprint -or
        $qualified.identity.dependenciesFingerprint -ne $current.dependenciesFingerprint -or
        $qualified.identity.generatedAssetsFingerprint -ne $current.generatedAssetsFingerprint) {
        throw 'Qualification evidence is stale or belongs to different inputs.'
    }
    if ($current.dirty -or $qualified.identity.dirty) { throw 'Release packaging requires a clean qualified worktree.' }
    $image = [string]$qualified.outputs.containerImage
    $expectedId = [string]$qualified.outputs.containerImageId
    $wslRoot = Initialize-WslDocker
    $actualId = (& wsl.exe -d Ubuntu-24.04 -- bash -lc "docker image inspect --format '{{.Id}}' '$image'").Trim()
    if ($LASTEXITCODE -ne 0 -or $actualId -ne $expectedId) { throw 'Qualified local image is missing or has changed.' }
    $packageRoot = Join-Path $root "artifacts\packages\$Version"
    if (Test-Path -LiteralPath $packageRoot) { throw "Package output already exists: $packageRoot" }
    New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
    $wslPackageRoot = (& wsl.exe -d Ubuntu-24.04 -- wslpath -a ($packageRoot -replace '\\', '/')).Trim()
    $archive = Join-Path $packageRoot "premierecalendar-$Version-image.tar"
    & wsl.exe -d Ubuntu-24.04 -- bash -lc "docker save --output '$wslPackageRoot/premierecalendar-$Version-image.tar' '$image'"
    if ($LASTEXITCODE -ne 0) { throw 'Qualified image export failed.' }
    $manifest = [ordered]@{
        schemaVersion = 1
        version = $Version
        commit = $current.commit
        sourceFingerprint = $current.sourceFingerprint
        qualificationReportSha256 = Get-Hash $QualificationReport
        image = $image
        imageId = $actualId
        archive = Split-Path $archive -Leaf
        archiveSha256 = Get-Hash $archive
        createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
    }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $packageRoot 'package-manifest.json') -Encoding utf8NoBOM
    Write-Host "Package manifest: $(Join-Path $packageRoot 'package-manifest.json')"
}

if ($Mode -eq 'Installed') { Invoke-InstalledValidation; return }
if ($Mode -eq 'Package') { Invoke-Package; return }

$lockFiles = @(Get-ChildItem -LiteralPath $root -Recurse -Filter 'packages.lock.json' -File |
    Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' })
if ($lockFiles.Count -lt 5) {
    throw 'NuGet lock files are missing. Run the pinned SDK restore once and commit all project packages.lock.json files.'
}

$identity = Get-WorkflowIdentity
$tools = Get-ToolIdentity
$identityText = "$($identity.sourceFingerprint)`n$($identity.dependenciesFingerprint)`n$($identity.generatedAssetsFingerprint)`n$($tools.dotnetSdk)`n$Mode"
$runId = ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($identityText)))).ToLowerInvariant().Substring(0, 20)
$script:runRoot = Join-Path $artifactsRoot "$($Mode.ToLowerInvariant())-$runId"
$script:reportPath = Join-Path $script:runRoot 'report.json'
New-Item -ItemType Directory -Path $script:runRoot -Force | Out-Null
if (-not $NoResume -and (Test-Path -LiteralPath $script:reportPath)) {
    $script:report = Get-Content -LiteralPath $script:reportPath -Raw | ConvertFrom-Json
} else {
    $script:report = [pscustomobject][ordered]@{
        schemaVersion = 1
        mode = $Mode
        runId = $runId
        result = 'running'
        releaseReusable = -not $identity.dirty
        startedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        updatedUtc = $null
        completedUtc = $null
        durationSeconds = $null
        identity = $identity
        tools = $tools
        stages = @()
        outputs = [pscustomobject][ordered]@{}
        slowestStages = @()
        baseline = $null
    }
    Save-Report
}

try {
    $workflowTarget = switch ($Mode) {
        'Fast' { Join-Path $root 'PremiereCalendar.Fast.slnx' }
        'TestUnit' { Join-Path $root 'tests\PremiereCalendar.UnitTests\PremiereCalendar.UnitTests.csproj' }
        'TestComponent' { Join-Path $root 'tests\PremiereCalendar.ComponentTests\PremiereCalendar.ComponentTests.csproj' }
        'TestIntegration' { Join-Path $root 'tests\PremiereCalendar.IntegrationTests\PremiereCalendar.IntegrationTests.csproj' }
        'TestBrowser' { Join-Path $root 'tests\PremiereCalendar.BrowserTests\PremiereCalendar.BrowserTests.csproj' }
        'TestPostgreSql' { Join-Path $root 'tests\PremiereCalendar.IntegrationTests\PremiereCalendar.IntegrationTests.csproj' }
        default { Join-Path $root 'PremiereCalendar.slnx' }
    }
    Invoke-Stage 'locked-restore' {
        Invoke-Native $dotnet @('restore', $workflowTarget, '--locked-mode', '--nologo')
    }
    Invoke-Stage 'release-build' {
        Invoke-Native $dotnet @('build', $workflowTarget, '-c', 'Release', '--no-restore', '--nologo')
    }
    if ($Mode -eq 'Build') {
        # Restore and build are the complete Build workflow.
    } elseif ($Mode -eq 'Fast') {
        Invoke-Stage 'unit-component-tests' {
            Invoke-Native $dotnet @('test', (Join-Path $root 'PremiereCalendar.Fast.slnx'), '-c', 'Release', '--no-build', '--no-restore', '--nologo', '--logger', 'trx;LogFilePrefix=fast')
        }
    } elseif ($Mode -eq 'TestUnit') {
        Invoke-Stage 'unit-tests' {
            Invoke-Native $dotnet @('test', $workflowTarget, '-c', 'Release', '--no-build', '--no-restore', '--nologo', '--logger', 'trx;LogFilePrefix=unit')
        }
    } elseif ($Mode -eq 'TestComponent') {
        Invoke-Stage 'component-tests' {
            Invoke-Native $dotnet @('test', $workflowTarget, '-c', 'Release', '--no-build', '--no-restore', '--nologo', '--logger', 'trx;LogFilePrefix=component')
        }
    } elseif ($Mode -eq 'TestIntegration') {
        Invoke-Stage 'integration-tests' {
            Invoke-Native $dotnet @('test', $workflowTarget, '-c', 'Release', '--no-build', '--no-restore', '--nologo', '--filter', 'FullyQualifiedName!~PostgresPersistenceTests', '--logger', 'trx;LogFilePrefix=integration')
        }
    } elseif ($Mode -eq 'TestBrowser') {
        Invoke-Stage 'playwright-runtime' {
            $installer = Join-Path $root 'tests\PremiereCalendar.BrowserTests\bin\Release\net11.0\playwright.ps1'
            Invoke-Native $installer @('install', 'chromium')
        }
        Invoke-Stage 'browser-tests' {
            Invoke-Native $dotnet @('test', $workflowTarget, '-c', 'Release', '--no-build', '--no-restore', '--nologo', '--logger', 'trx;LogFilePrefix=browser')
        }
    } elseif ($Mode -eq 'TestPostgreSql') {
        Invoke-Stage 'postgresql-tests' { Invoke-PostgreSqlTests }
    } else {
        Invoke-Stage 'playwright-runtime' {
            $installer = Join-Path $root 'tests\PremiereCalendar.BrowserTests\bin\Release\net11.0\playwright.ps1'
            Invoke-Native $installer @('install', 'chromium')
        }
        Invoke-Stage 'non-browser-tests' {
            Invoke-Native $dotnet @('test', (Join-Path $root 'PremiereCalendar.NonBrowser.slnx'), '-c', 'Release', '--no-build', '--no-restore', '--nologo', '--filter', 'FullyQualifiedName!~PostgresPersistenceTests', '--logger', 'trx;LogFilePrefix=qualification')
        }
        Invoke-Stage 'browser-tests' {
            Invoke-Native $dotnet @('test', (Join-Path $root 'tests\PremiereCalendar.BrowserTests\PremiereCalendar.BrowserTests.csproj'), '-c', 'Release', '--no-build', '--no-restore', '--nologo', '--logger', 'trx;LogFilePrefix=qualification-browser')
        }
        Invoke-Stage 'postgresql-tests' { Invoke-PostgreSqlTests }
        $image = "premierecalendar-qualified:$($identity.sourceFingerprint.Substring(0, 16))"
        Invoke-Stage 'container-publish' {
            $publishRoot = Join-Path $script:runRoot 'qualification-publish'
            $buildTime = @(Get-GitLines @('show', '-s', '--format=%cI', $identity.commit))[0].Trim()
            Invoke-Native $dotnet @(
                'publish', (Join-Path $root 'PremiereCalendar\PremiereCalendar.csproj'),
                '-c', 'Release', '--no-restore', '--nologo', '-o', $publishRoot,
                '-p:Version=0.0.0-qualification',
                "-p:InformationalVersion=0.0.0-qualification+$($identity.sourceFingerprint)",
                "-p:SourceRevisionId=$($identity.commit)",
                "-p:BuildId=$($identity.sourceFingerprint)",
                "-p:BuildTimeUtc=$buildTime",
                '-p:UseAppHost=false'
            )
            $bootAsset = Join-Path $publishRoot 'wwwroot\_framework\blazor.web.js'
            if (-not (Test-Path -LiteralPath $bootAsset -PathType Leaf)) {
                throw 'Qualified publish output is missing the Blazor boot asset.'
            }
        }
        Invoke-Stage 'container-context' {
            $contextTar = Join-Path $script:runRoot 'container-context.tar'
            Invoke-Native 'tar.exe' @(
                '-cf', $contextTar,
                'Dockerfile',
                '-C', $script:runRoot, 'qualification-publish'
            )
            $script:report.outputs | Add-Member -NotePropertyName containerContextSha256 -NotePropertyValue (Get-Hash $contextTar) -Force
        }
        Invoke-Stage 'container-build' {
            $buildTime = @(Get-GitLines @('show', '-s', '--format=%cI', $identity.commit))[0].Trim()
            $contextTar = Join-Path $script:runRoot 'container-context.tar'
            $wslContext = (& wsl.exe -d Ubuntu-24.04 -- wslpath -a ($contextTar -replace '\\', '/')).Trim()
            Invoke-WslBash "docker build --target qualified-runtime --tag '$image' --build-arg 'VERSION=0.0.0-qualification' --build-arg 'SOURCE_REVISION=$($identity.commit)' --build-arg 'BUILD_ID=$($identity.sourceFingerprint)' --build-arg 'BUILD_TIME_UTC=$buildTime' - < '$wslContext'"
            $imageId = (& wsl.exe -d Ubuntu-24.04 -- bash -lc "docker image inspect --format '{{.Id}}' '$image'").Trim()
            if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($imageId)) { throw 'Built image identity is unavailable.' }
            $script:report.outputs | Add-Member -NotePropertyName containerImage -NotePropertyValue $image -Force
            $script:report.outputs | Add-Member -NotePropertyName containerImageId -NotePropertyValue $imageId -Force
        }
        Invoke-Stage 'container-smoke' {
            $evidence = Join-Path $script:runRoot 'container-smoke.json'
            $wslEvidence = (& wsl.exe -d Ubuntu-24.04 -- wslpath -a ($evidence -replace '\\', '/')).Trim()
            Invoke-WslBash "cd '$script:wslRoot' && bash eng/container-smoke.sh '$image' '$wslEvidence'"
        }
        Invoke-Stage 'container-security' {
            $scan = Join-Path $script:runRoot 'trivy-results.json'
            $wslRunRoot = (& wsl.exe -d Ubuntu-24.04 -- wslpath -a ($script:runRoot -replace '\\', '/')).Trim()
            Invoke-WslBash "docker run --rm -v /var/run/docker.sock:/var/run/docker.sock -v premierecalendar-trivy-cache:/root/.cache/ -v '${wslRunRoot}:/output' aquasec/trivy:0.65.0 image --scanners vuln --skip-version-check --format json --output /output/trivy-results.json --severity HIGH,CRITICAL --ignore-unfixed --exit-code 1 '$image'"
            $script:report.outputs | Add-Member -NotePropertyName securityScanSha256 -NotePropertyValue (Get-Hash $scan) -Force
        }
    }
    Complete-Run
}
catch {
    $script:report.result = 'failed'
    Save-Report
    Write-Error $_
    exit 1
}
