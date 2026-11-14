#Requires -Version 7.0
<#
.SYNOPSIS
Publishes (or previews) the staged canonical NAS documents produced by render_nas_docs.py.

.DESCRIPTION
Without -Apply it only prints the manifest. With -Apply it opens the five fixed canonical targets exclusively
(keeping their ownership and ACLs; no rename or delete), verifies every staged file hash and every canonical
preimage hash, saves the preimages next to the manifest and writes the replacements in place. A changed preimage
stops everything before a byte is written; there is no automatic rollback. Only manifests marked readyToApply and
located under -StageRoot are accepted. Read the NAS AGENTS.md files first: no deletions, no secrets, no evidence
bundles in that workspace.

.PARAMETER ManifestPath
Full path of the staged manifest.json.

.PARAMETER StageRoot
Full path of the staging folder that contains the manifest (default: the manifest's parent's parent).

.PARAMETER Apply
Actually write the canonical files.

.EXAMPLE
pwsh -File D:\Projects\PremiereCalendar\eng\release\nas-docs\Apply-NasDocs.ps1 -ManifestPath D:\Projects\PremiereCalendar\artifacts\release\1.2.20.private\nas-docs\candidate\manifest.json
#>
[CmdletBinding()]
param([Parameter(Mandatory)][string] $ManifestPath, [string] $StageRoot, [switch] $Apply)

$ErrorActionPreference = 'Stop'
$manifestFile = [IO.Path]::GetFullPath($ManifestPath)
if (-not $StageRoot) { $StageRoot = Split-Path -Parent (Split-Path -Parent $manifestFile) }
$stage = [IO.Path]::GetFullPath($StageRoot).TrimEnd('\') + '\'
if (-not $manifestFile.StartsWith($stage, [StringComparison]::OrdinalIgnoreCase)) { throw 'The manifest must be inside the staging root.' }
$manifest = Get-Content -LiteralPath $manifestFile -Raw | ConvertFrom-Json
if ($manifest.phase -notin @('candidate', 'installed')) { throw 'Unexpected staged release manifest.' }
if (-not $Apply) { $manifest | Select-Object version, phase, readyToApply, documents; return }
if ($manifest.readyToApply -ne $true) { throw 'Pending identities or required gate evidence remain; the canonical files were not opened for writing.' }
$canonicalRoot = ([string]$manifest.canonicalRoot).TrimEnd('\') + '\'
$allowed = @('services/premierecalendar/README.md', 'README.md', '08-applications-and-services.md', '04-change-management.md', 'CHANGELOG.md')
if (@($manifest.documents).Count -ne $allowed.Count) { throw 'Expected exactly five canonical documents.' }
$opened = [Collections.Generic.List[object]]::new()
$receipt = [Collections.Generic.List[object]]::new()
$backupDirectory = Join-Path (Split-Path -Parent $manifestFile) ('apply-backups-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffffffZ'))
try {
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($document in $manifest.documents) {
        if ($document.relativePath -notin $allowed -or -not $seen.Add([string]$document.relativePath)) { throw 'Unapproved or duplicate canonical target.' }
        $expected = [IO.Path]::GetFullPath((Join-Path $canonicalRoot ([string]$document.relativePath)))
        if (-not $expected.Equals([IO.Path]::GetFullPath([string]$document.canonicalPath), [StringComparison]::OrdinalIgnoreCase)) { throw 'The canonical path does not match the fixed target list.' }
        $rendered = [IO.Path]::GetFullPath([string]$document.renderedPath)
        if (-not $rendered.StartsWith($stage, [StringComparison]::OrdinalIgnoreCase)) { throw 'The rendered path escaped staging.' }
        $replacement = [IO.File]::ReadAllBytes($rendered)
        if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($replacement)) -ne $document.afterSha256.ToUpperInvariant()) { throw 'The rendered candidate changed after staging; render it again.' }
        # Hold all five files exclusively from the hash check through publication. Opening existing files keeps
        # ownership and ACLs; no rename or delete is used.
        $stream = [IO.FileStream]::new($expected, [IO.FileMode]::Open, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $original = [byte[]]::new($stream.Length)
        $opened.Add([pscustomobject]@{ Document = $document; Stream = $stream; Original = $original; Replacement = $replacement })
        $stream.ReadExactly($original, 0, $original.Length)
        if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($original)) -ne $document.beforeSha256.ToUpperInvariant()) { throw 'A canonical document changed concurrently; nothing was written. Render again from the current files.' }
    }
    if (Test-Path -LiteralPath $backupDirectory) { throw 'Backup directory collision.' }
    New-Item -ItemType Directory -Path $backupDirectory | Out-Null
    foreach ($entry in $opened) { [IO.File]::WriteAllBytes((Join-Path $backupDirectory (([string]$entry.Document.relativePath).Replace('/', '__'))), $entry.Original) }
    foreach ($entry in $opened) {
        $entry.Stream.Position = 0
        $entry.Stream.Write($entry.Replacement, 0, $entry.Replacement.Length)
        $entry.Stream.SetLength($entry.Replacement.Length)
        $entry.Stream.Flush($true)
        $receipt.Add([pscustomobject]@{ relativePath = $entry.Document.relativePath; beforeSha256 = $entry.Document.beforeSha256; afterSha256 = $entry.Document.afterSha256; writtenUtc = [DateTimeOffset]::UtcNow })
    }
}
finally {
    foreach ($entry in $opened) { $entry.Stream.Dispose() }
    if ($receipt.Count -gt 0) { $receipt.ToArray() | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $backupDirectory 'apply-receipt.json') -Encoding utf8 }
}
[pscustomobject]@{ phase = $manifest.phase; appliedDocuments = $receipt.Count; preimageBackup = $backupDirectory }
