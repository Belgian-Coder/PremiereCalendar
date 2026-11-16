#Requires -Version 7.0
# Execute only the cache launcher with shell builtins in a disposable native sandbox.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$parent = [IO.Path]::GetFullPath((Join-Path $root 'artifacts/harness-tests'))
$sandbox = Join-Path $parent ('sdk-cache-' + [guid]::NewGuid().ToString('N'))
$bash = if ($IsWindows) { Join-Path $env:ProgramFiles 'Git/bin/bash.exe' } else { (Get-Command bash -CommandType Application).Source }
$count = 0
function Assert-Cache([bool] $Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }; $script:count++
}
function Shell-Path([string] $Path) {
    if ($IsWindows) { '/' + $Path.Substring(0, 1).ToLowerInvariant() + '/' + ($Path.Substring(3) -replace '\\', '/') } else { $Path }
}
function Invoke-Cache([string] $Folder, [string] $Owner, [string] $CacheParent = '') {
    if (-not $CacheParent) { $CacheParent = $Folder }
    $target = Join-Path $CacheParent '.sdk-cache'
    $copy = Join-Path $Folder 'launcher.sh'
    $text = [IO.File]::ReadAllText((Join-Path $root 'eng/run-with-sdk-cache.sh'))
    # The sole substitution redirects the literal mount path to this test's directory.
    $text = $text.Replace('cache=/build/.sdk-cache', ('cache=' + "'" + (Shell-Path $target).Replace("'", "'\''") + "'"))
    [IO.File]::WriteAllText($copy, $text, [Text.UTF8Encoding]::new($false))
    $start = [Diagnostics.ProcessStartInfo]::new($bash)
    $start.UseShellExecute = $false; $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
    $start.Environment['PC_SDK_CACHE_OWNER'] = $Owner
    $start.Environment['DOTNET_GENERATE_ASPNET_CERTIFICATE'] = 'true'
    $start.Environment['NUGET_EXE_NO_PROMPT'] = 'false'
    $start.Environment['NuGetInteractive'] = 'true'
    foreach ($word in @((Shell-Path $copy), 'sh', '-c', 'printf "%s|%s|%s|%s|%s" "$DOTNET_CLI_HOME" "$NUGET_PLUGINS_CACHE_PATH" "$DOTNET_GENERATE_ASPNET_CERTIFICATE" "$NUGET_EXE_NO_PROMPT" "$NuGetInteractive"')) { $start.ArgumentList.Add($word) }
    $process = [Diagnostics.Process]::new(); $process.StartInfo = $start
    try {
        [void]$process.Start(); $output = $process.StandardOutput.ReadToEndAsync(); $errors = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(10000)) { $process.Kill(); throw 'Cache launcher fixture exceeded its deadline.' }
        [pscustomobject]@{ ExitCode = $process.ExitCode; Output = $output.GetAwaiter().GetResult(); Error = $errors.GetAwaiter().GetResult() }
    } finally { $process.Dispose() }
}
try {
    New-Item -ItemType Directory -Path $sandbox -Force | Out-Null
    $known = Join-Path $sandbox 'known'; New-Item -ItemType Directory -Path $known | Out-Null
    $result = Invoke-Cache $known 'fixture-linux-amd64-musl-async-check'
    Assert-Cache ($result.ExitCode -eq 0) 'Fresh owned SDK cache initialization failed.'
    Assert-Cache ($result.Output.EndsWith('|false|true|false')) 'Inherited prompting/certificate flags were not disabled.'
    $cache = Join-Path $known '.sdk-cache'; $marker = Join-Path $cache '.owner'
    Assert-Cache ([IO.File]::ReadAllText($marker) -ceq 'PremiereCalendarSdkCache:v1:fixture-linux-amd64-musl-async-check') 'SDK cache marker lost exact build identity.'
    Assert-Cache ($result.Output.Contains('/.sdk-cache/dotnet|') -and $result.Output.Contains('/.sdk-cache/nuget-plugins|')) 'CLI/plugin paths escaped the selected build view.'
    $canary = Join-Path $cache 'dotnet/existing-state'; [IO.File]::WriteAllText($canary, 'retain owned state')
    $digest = (Get-FileHash -LiteralPath $canary).Hash
    $repeat = Invoke-Cache $known 'fixture-linux-amd64-musl-async-check'
    Assert-Cache ($repeat.ExitCode -eq 0 -and (Get-FileHash -LiteralPath $canary).Hash -eq $digest) 'Matching use replaced persistent SDK state.'
    $foreign = Invoke-Cache $known 'fixture-linux-amd64-glibc-async-check'
    Assert-Cache ($foreign.ExitCode -eq 29 -and $foreign.Error.Contains('unknown-preserved') -and -not $foreign.Output) 'Foreign ABI owner was adopted or executed the command.'
    Assert-Cache ((Get-FileHash -LiteralPath $canary).Hash -eq $digest) 'Foreign-owner refusal changed old SDK state.'
    $unknown = Join-Path $sandbox 'unknown'; $unknownCache = Join-Path $unknown '.sdk-cache'
    New-Item -ItemType Directory -Path $unknownCache | Out-Null
    $old = Join-Path $unknownCache 'unknown-state'; [IO.File]::WriteAllText($old, 'preserve unknown state')
    $oldHash = (Get-FileHash -LiteralPath $old).Hash
    $refused = Invoke-Cache $unknown 'fixture-linux-amd64-musl-async-check'
    Assert-Cache ($refused.ExitCode -eq 29 -and $refused.Error.Contains('unknown-preserved') -and -not $refused.Output) 'Unmarked SDK state was silently adopted.'
    Assert-Cache ((Get-FileHash -LiteralPath $old).Hash -eq $oldHash -and -not (Test-Path -LiteralPath (Join-Path $unknownCache '.owner')) -and -not (Test-Path -LiteralPath (Join-Path $unknownCache 'dotnet'))) 'Unknown-state refusal wrote into the cache.'
    $invalid = Invoke-Cache $known 'foreign/route'
    Assert-Cache ($invalid.ExitCode -eq 29 -and $invalid.Error.Contains('invalid-owner')) 'A non-volume owner token was accepted.'
    $fresh = Join-Path $sandbox 'fresh-build'; New-Item -ItemType Directory -Path $fresh | Out-Null
    $missingParent = Join-Path $fresh 'build'
    Assert-Cache (-not (Test-Path -LiteralPath $missingParent)) 'Missing-parent fixture unexpectedly has a build directory.'
    $created = Invoke-Cache $fresh 'container-local' $missingParent
    Assert-Cache ($created.ExitCode -eq 0 -and [IO.File]::ReadAllText((Join-Path $missingParent '.sdk-cache/.owner')) -ceq 'PremiereCalendarSdkCache:v1:container-local') 'FreshBuild did not create its absent container-local parent and marked cache.'
    Write-Host "$count SDK cache checks passed; no SDK, WSL, Docker, credential provider or certificate command ran."
} finally {
    $resolved = [IO.Path]::GetFullPath($sandbox)
    if (-not $resolved.StartsWith($parent + [IO.Path]::DirectorySeparatorChar) -or (Get-Item -LiteralPath $resolved).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Preserving unexpected SDK test sandbox.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
