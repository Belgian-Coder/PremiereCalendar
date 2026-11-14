#Requires -Version 7.0
<#
.SYNOPSIS
Freezes what the reviewers approve: the exact diff from the previous release source to the release commit.

.DESCRIPTION
Run after the release commit exists (clean tracked tree, HEAD = -ExpectedRevision) and before the review record is
completed. Writes review-source.patch and review-inputs-manifest.json into the bundle (never overwriting). The
reviewers' record <Bundle>\source-review.json must then contain:

  {
    "sourceRevision": "<release commit>", "baseCommit": "<-BaseCommit>",
    "inputManifestSha256": "<sha256 of review-inputs-manifest.json>", "reviewedDiffSha256": "<sha256 of review-source.patch>",
    "openP0P2": 0,
    "reviews": [ { "reviewer": "...", "model": "...", "effort": "...", "status": "completed", "invocation": "..." },
                 { "reviewer": "...", "status": "not-performed", "reason": "why" } ],
    "deferredP3": [ ... optional ... ]
  }

Load, Deploy and Close verify that record against the commit (verify_reviewed_source.py). Release notes and docs must
not change between the commit and Close-Release.ps1: add the installed section in the closure commit afterwards.

.PARAMETER Bundle
Full path of the release bundle.

.PARAMETER BaseCommit
The previous release's source commit (the diff base).

.PARAMETER ExpectedRevision
The exact 40-character release commit.

.EXAMPLE
pwsh -File D:\Projects\PremiereCalendar\eng\release\Freeze-ReviewInputs.ps1 -Bundle D:\Projects\PremiereCalendar\artifacts\release\1.2.20 -BaseCommit c4d863726eaf5a7a9bc6c9b4a1c58366229ff716 -ExpectedRevision 0123456789abcdef0123456789abcdef01234567
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)][string] $Bundle,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $BaseCommit,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-f]{40}$')][string] $ExpectedRevision
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'ReleaseCommon.ps1')
$ctx = Get-ReleaseContext -Bundle $Bundle
$plan = @(
    "Require HEAD = $ExpectedRevision and a clean tracked tree",
    "Refuse if review-source.patch or review-inputs-manifest.json already exist in $($ctx.Bundle)",
    "Write the exact diff $BaseCommit..$ExpectedRevision and the committed blob inventory (freeze_review_inputs.py)")
if (Show-ReleasePlan -Title 'Freeze-ReviewInputs' -Step $plan) { return }
Assert-CleanSourceAtRevision -Revision $ExpectedRevision
Invoke-ReleasePython (Join-Path $PSScriptRoot 'freeze_review_inputs.py') --repo $ctx.Repo --bundle $ctx.Bundle --base $BaseCommit --revision $ExpectedRevision
