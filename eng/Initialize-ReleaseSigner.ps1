[CmdletBinding()]
param(
    [string] $PublicCertificatePath,
    [ValidateRange(1, 10)][int] $ValidYears = 5
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$signingRoot = [System.IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts\release-signing'))
if ([string]::IsNullOrWhiteSpace($PublicCertificatePath)) {
    $PublicCertificatePath = Join-Path $signingRoot 'premiere-calendar-release-signing.cer'
}
$resolvedCertificate = [System.IO.Path]::GetFullPath($PublicCertificatePath)
$allowedPrefix = $signingRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if (-not $resolvedCertificate.StartsWith($allowedPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'The public release certificate must remain under artifacts\release-signing.'
}
if (Test-Path -LiteralPath $resolvedCertificate) {
    throw 'The public release certificate already exists. Reuse its matching signer or perform an explicit trust rotation.'
}

$certificateParameters = @{
    Type = 'Custom'
    Subject = 'CN=PremiereCalendar household release signer'
    CertStoreLocation = 'Cert:\CurrentUser\My'
    KeyAlgorithm = 'RSA'
    KeyLength = 3072
    HashAlgorithm = 'SHA256'
    KeyExportPolicy = 'NonExportable'
    KeyUsage = 'DigitalSignature'
    TextExtension = @('2.5.29.37={text}1.3.6.1.5.5.7.3.3')
    NotAfter = [DateTimeOffset]::UtcNow.AddYears($ValidYears).UtcDateTime
}
$certificate = New-SelfSignedCertificate @certificateParameters
if (-not $certificate.HasPrivateKey) { throw 'The release signer was created without a private key.' }
New-Item -ItemType Directory -Path (Split-Path -Parent $resolvedCertificate) -Force | Out-Null
Export-Certificate -Cert $certificate -FilePath $resolvedCertificate -Type CERT | Out-Null
$metadata = [ordered]@{
    schemaVersion = 1
    subject = $certificate.Subject
    thumbprint = $certificate.Thumbprint
    notAfterUtc = $certificate.NotAfter.ToUniversalTime().ToString('O')
    publicCertificatePath = $resolvedCertificate
    privateKeyLocation = 'CurrentUser certificate store; non-exportable'
}
[System.IO.File]::WriteAllText(
    (Join-Path (Split-Path -Parent $resolvedCertificate) 'signer-metadata.json'),
    ($metadata | ConvertTo-Json),
    [System.Text.UTF8Encoding]::new($false))
Write-Host "Release signer initialized. Thumbprint: $($certificate.Thumbprint)"
Write-Host "Public certificate: $resolvedCertificate"
