[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string] $Version,
    [Parameter(Mandatory)][ValidateLength(1, 4000)][string] $ReleaseNotes,
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string] $Repository = 'Belgian-Coder/PremiereCalendar',
    [Parameter(Mandatory)][ValidatePattern('^[A-Fa-f0-9]{40}$')][string] $SigningCertificateThumbprint,
    [switch] $SkipValidation,
    [switch] $NoUpload
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$tag = "v$Version"
$releaseRoot = Join-Path $projectRoot "artifacts\releases\$Version"
$publishPath = Join-Path $releaseRoot 'publish'
$packagePath = Join-Path $releaseRoot "premiere-calendar-$Version-win-x64.zip"
$manifestPath = Join-Path $releaseRoot 'stable.manifest.json'
$certificatePath = Join-Path $releaseRoot 'premiere-calendar-release-signing.cer'
$checksumsPath = Join-Path $releaseRoot 'SHA256SUMS.txt'
$installerPath = Join-Path $releaseRoot "PremiereCalendar-$Version-Windows-x64-installer.zip"
$dotnet = Join-Path $projectRoot '.dotnet\dotnet.exe'
if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) { $dotnet = 'dotnet' }

function Invoke-Checked {
    param([Parameter(Mandatory)][scriptblock] $Command, [Parameter(Mandatory)][string] $Failure)
    & $Command
    if ($LASTEXITCODE -ne 0) { throw $Failure }
}

function Get-RepositoryKey {
    param([Parameter(Mandatory)][string] $RemoteUrl)
    $value = $RemoteUrl.Trim().TrimEnd('/')
    if ($value -match '^(?i)https://github\.com/(?<path>[^/?#]+/[^/?#]+)$') {
        return ($Matches.path -replace '(?i)\.git$', '').ToLowerInvariant()
    }
    if ($value -match '^(?i)(?:git@)?github\.com:(?<path>[^/?#]+/[^/?#]+)$') {
        return ($Matches.path -replace '(?i)\.git$', '').ToLowerInvariant()
    }
    return $null
}

function Assert-ReleaseSource {
    $branch = (& git -C $projectRoot branch --show-current).Trim()
    if ($LASTEXITCODE -ne 0 -or $branch -ne 'main') { throw 'Releases must be created from main.' }
    $status = @(& git -C $projectRoot status --porcelain=v1)
    if ($LASTEXITCODE -ne 0 -or $status.Count -ne 0) { throw 'The release worktree must be clean.' }
    Invoke-Checked { git -C $projectRoot fetch origin main --tags --prune } 'Could not refresh origin.'
    $head = (& git -C $projectRoot rev-parse HEAD).Trim()
    $remoteHead = (& git -C $projectRoot rev-parse origin/main).Trim()
    if ($head -ne $remoteHead) { throw 'main must exactly match origin/main before release.' }
    $origin = (& git -C $projectRoot remote get-url origin).Trim()
    if ((Get-RepositoryKey $origin) -ne $Repository.ToLowerInvariant()) { throw "origin does not target $Repository." }
    return $head
}

function Assert-VersionIsNew {
    if ($NoUpload) { return }
    Invoke-Checked { gh auth status } 'GitHub CLI authentication is required.'
    $existingTag = @(& git -C $projectRoot ls-remote --tags origin "refs/tags/$tag")
    if ($LASTEXITCODE -ne 0) { throw 'Published tags could not be inspected.' }
    if ($existingTag.Count -ne 0) { throw "Tag $tag already exists on origin." }
    $versions = @(& gh api --paginate "repos/$Repository/releases?per_page=100" --jq '.[].tag_name')
    if ($LASTEXITCODE -ne 0) { throw 'Published release versions could not be inspected.' }
    $maximum = $versions |
        Where-Object { $_ -match '^v\d+\.\d+\.\d+$' } |
        ForEach-Object { [version]($_.Substring(1)) } |
        Sort-Object -Descending |
        Select-Object -First 1
    if ($null -ne $maximum -and [version]$Version -le $maximum) {
        throw "Version $Version must be newer than published version $maximum."
    }
}

function Get-Sha256 {
    param([Parameter(Mandatory)][string] $Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

if (Test-Path -LiteralPath $releaseRoot) { throw "Release output already exists: $releaseRoot" }
$head = Assert-ReleaseSource
Assert-VersionIsNew
$certificate = Get-Item "Cert:\CurrentUser\My\$SigningCertificateThumbprint" -ErrorAction Stop
if (-not $certificate.HasPrivateKey) { throw 'The release signing certificate has no private key.' }

if (-not $SkipValidation) {
    Invoke-Checked { & $dotnet restore (Join-Path $projectRoot 'PremiereCalendar.slnx') --nologo } 'Restore failed.'
    Invoke-Checked { & $dotnet build (Join-Path $projectRoot 'PremiereCalendar.slnx') -c Release --no-restore --nologo -p:UseSharedCompilation=false } 'Release build failed.'
    Invoke-Checked { & $dotnet test (Join-Path $projectRoot 'PremiereCalendar.slnx') -c Release --no-build --no-restore --nologo } 'Tests failed.'
}

# A solution restore does not create the runtime-specific assets graph required
# by a self-contained publish. Always restore this exact release target, including
# for -SkipValidation packaging runs.
Invoke-Checked {
    & $dotnet restore (Join-Path $projectRoot 'PremiereCalendar\PremiereCalendar.csproj') -r win-x64 --nologo
} 'win-x64 release restore failed.'

New-Item -ItemType Directory -Path $publishPath -Force | Out-Null
$fingerprintText = $head + [Environment]::NewLine + $Version
$buildId = ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($fingerprintText)))).ToLowerInvariant()
$publishArguments = @(
    'publish', (Join-Path $projectRoot 'PremiereCalendar\PremiereCalendar.csproj'),
    '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '--no-restore',
    '-o', $publishPath,
    "/p:Version=$Version", "/p:FileVersion=$Version.0", "/p:InformationalVersion=$Version+$buildId"
)
Invoke-Checked { & $dotnet @publishArguments } 'Self-contained publish failed.'
$metadata = [ordered]@{
    schemaVersion = 1
    version = $Version
    sourceRevision = $head
    buildId = $buildId
    builtUtc = [DateTimeOffset]::UtcNow.ToString('O')
}
[IO.File]::WriteAllText((Join-Path $publishPath 'build-metadata.json'), ($metadata | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
foreach ($required in @('PremiereCalendar.exe', 'PremiereCalendar.dll', 'build-metadata.json', 'appsettings.json', 'wwwroot')) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishPath $required))) { throw "Publish output is missing $required." }
}

Compress-Archive -Path (Join-Path $publishPath '*') -DestinationPath $packagePath -CompressionLevel Optimal
$packageHash = Get-Sha256 $packagePath
$normalizedNotes = $ReleaseNotes.Replace("`r`n", "`n").Replace("`r", "`n")
$payload = @('1', $Version, 'stable', (Split-Path $packagePath -Leaf), $packageHash.ToUpperInvariant(), '0', '2147483647', $normalizedNotes) -join [char]10
$rsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPrivateKey($certificate)
try {
    $signature = [Convert]::ToBase64String($rsa.SignData([Text.Encoding]::UTF8.GetBytes($payload), [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1))
}
finally { $rsa.Dispose() }
$manifest = [ordered]@{
    schemaVersion = 1
    version = $Version
    channel = 'stable'
    packageFileName = Split-Path $packagePath -Leaf
    packageSha256 = $packageHash
    minimumDatabaseSchemaVersion = 0
    maximumDatabaseSchemaVersion = 2147483647
    releaseNotes = $normalizedNotes
    signature = $signature
}
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Compress), [Text.UTF8Encoding]::new($false))
Export-Certificate -Cert $certificate -FilePath $certificatePath -Type CERT | Out-Null
$installerStage = Join-Path $releaseRoot 'installer'
New-Item -ItemType Directory -Path (Join-Path $installerStage 'deploy\Updates') -Force | Out-Null
Copy-Item -LiteralPath $packagePath, $manifestPath, $certificatePath -Destination $installerStage
Copy-Item -Path (Join-Path $projectRoot 'deploy\Updates\*.ps1') -Destination (Join-Path $installerStage 'deploy\Updates')
Compress-Archive -Path (Join-Path $installerStage '*') -DestinationPath $installerPath -CompressionLevel Optimal
$checksumLines = @($packagePath, $manifestPath, $certificatePath, $installerPath) | ForEach-Object { "$(Get-Sha256 $_)  $(Split-Path $_ -Leaf)" }
[IO.File]::WriteAllLines($checksumsPath, $checksumLines, [Text.UTF8Encoding]::new($false))

$publicCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
$publicRsa = [Security.Cryptography.X509Certificates.RSACertificateExtensions]::GetRSAPublicKey($publicCertificate)
try {
    if (-not $publicRsa.VerifyData([Text.Encoding]::UTF8.GetBytes($payload), [Convert]::FromBase64String($signature), [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)) {
        throw 'Generated manifest signature verification failed.'
    }
}
finally { $publicRsa.Dispose(); $publicCertificate.Dispose() }

if (-not $NoUpload) {
    Invoke-Checked { git -C $projectRoot tag -a $tag -m "PremiereCalendar $Version" $head } 'Annotated release tag creation failed.'
    Invoke-Checked { git -C $projectRoot push origin $tag } 'Release tag push failed.'
    Invoke-Checked {
        gh release create $tag --repo $Repository --verify-tag --draft --title $tag --notes $ReleaseNotes $packagePath $manifestPath $certificatePath $checksumsPath $installerPath
    } 'Draft GitHub release creation failed.'
    Invoke-Checked { gh release edit $tag --repo $Repository --draft=false } 'GitHub release publication failed.'
}
Write-Host "Release $Version validated at $releaseRoot"
