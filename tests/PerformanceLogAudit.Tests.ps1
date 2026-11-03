# Standalone check: pwsh -NoProfile -File tests/PerformanceLogAudit.Tests.ps1
[CmdletBinding()]
param([string] $OutputDirectory = (Join-Path ([IO.Path]::GetTempPath()) ('premiere-log-audit-' + [guid]::NewGuid().ToString('N'))))
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$instance = '11111111111111111111111111111111'
$load = '22222222222222222222222222222222'
$job = '33333333333333333333333333333333'
$log = Join-Path $OutputDirectory "performance-20261103T170000Z-$instance-000001.ndjson"
$rows = @(
    @{ event='calendar.page.started'; loadId=$load; details=@{stage='series';weekStart='2026-11-02'} },
    @{ event='calendar.job.attached'; loadId=$load; jobId=$job; details=@{outcome='created'} },
    @{ event='calendar.page.first_nonempty_result'; loadId=$load; details=@{durationMilliseconds=25;count=33} },
    @{ event='provider.headers.completed'; jobId=$job; details=@{provider='omdb';statusCode=200} },
    @{ event='provider.attempt.completed'; jobId=$job; details=@{provider='watchmode';outcome='failed'} },
    @{ event='provider.admission.deferred'; jobId=$job; details=@{provider='watchmode';outcome='deferred';reason='local_backoff';retryAtUtc='2026-11-03T17:15:00Z'} },
    @{ event='item_cache.component'; jobId=$job; details=@{provider='omdb';stage='external_ratings';outcome='provider_verified_empty';reason='verified_empty';itemId=901;mediaType='Movie';durationMilliseconds=10} },
    @{ event='item_cache.result'; jobId=$job; details=@{stage='ratings';outcome='cleared';reason='accepted_projection';itemId=901;mediaType='Movie';count=1;total=1;durationMilliseconds=12} },
    @{ event='item_cache.result'; jobId=$job; details=@{stage='ratings';outcome='cleared';reason='accepted_projection';itemId=901;mediaType='Series';count=1;total=1;durationMilliseconds=12} },
    @{ event='item_cache.write.component'; jobId=$job; details=@{stage='ratings';outcome='accepted_projection';itemId=901;mediaType='Movie';durationMilliseconds=12} },
    @{ event='item_cache.reconcile.item'; jobId=$job; details=@{stage='Series';outcome='discovery_required';reason='canonical_date_changed';itemId=902} },
    @{ event='item_cache.write.item'; jobId=$job; details=@{stage='Series';outcome='concurrency_rejected';reason='metadata_generation_changed';itemId=903} },
    @{ event='calendar.page.finished'; loadId=$load; details=@{outcome='partial';durationMilliseconds=100} },
    @{ event='diagnostics.loss'; details=@{count=2} }
)
$sequence = 0
$rows | ForEach-Object {
    $sequence++
    $_.timestampUtc = '2026-11-03T17:00:00Z'; $_.instanceId = $instance; $_.eventSequence = $sequence
    $_ | ConvertTo-Json -Depth 8 -Compress
} | Set-Content -LiteralPath $log -Encoding utf8
Add-Content -LiteralPath $log -Value '{"incomplete":'
if ($IsWindows) { (Get-Item -LiteralPath $log -Force).Attributes = [IO.FileAttributes]::Hidden }
$script = Join-Path $PSScriptRoot '../eng/Read-CalendarPerformance.ps1'
$audit = & $script -LogDirectory $OutputDirectory -LoadId $load -InstanceId $instance -SinceUtc '2026-11-03T16:00:00Z' -CacheAudit -WarningAction SilentlyContinue
if ($audit.SelectedEvents -ne $rows.Count -or $audit.StatusCodedResponses -ne 1) { throw 'Correlation or HTTP response counting regressed.' }
if ($audit.Evidence.MalformedOrIncompleteRecords -ne 1 -or $audit.Evidence.LossMarkers -ne 1) { throw 'Missing evidence must be reported.' }
if ($audit.Pages.Count -ne 1 -or $audit.Pages[0].FirstNonemptyMilliseconds -ne 25 -or $audit.Pages[0].JobIds -notcontains $job) { throw 'Page/job timing correlation failed.' }
if (@($audit.ComponentResults | Where-Object Outcome -eq 'provider_verified_empty').Count -ne 1) { throw 'Verified-empty fetch evidence missing.' }
if (@($audit.RatingValueChanges | Where-Object { $_.Outcome -eq 'cleared' -and $_.Reason -eq 'accepted_projection' }).Count -ne 2) { throw 'Movie/series clearing identities were conflated.' }
if (@($audit.ComponentWrites | Where-Object Outcome -eq 'accepted_projection').Count -ne 1) { throw 'Accepted database projection evidence missing.' }
if (@($audit.ReconciliationReasons | Where-Object Reason -eq 'canonical_date_changed').Count -ne 1) { throw 'Reconciliation reason missing.' }
if (@($audit.Writes | Where-Object Outcome -eq 'concurrency_rejected').Count -ne 1) { throw 'Rejected write evidence missing.' }
if ($audit.ProviderRetryDeadlines.Count -ne 1) { throw 'Provider retry deadline missing.' }
$limited = & $script -LogDirectory $OutputDirectory -LoadId $load -SinceUtc '2026-11-03T16:00:00Z' -MaximumEvents 2 -CacheAudit -WarningAction SilentlyContinue
if ($limited.Evidence.OmittedEvents -ne ($rows.Count - 2)) { throw 'Truncated evidence must be explicit.' }
$empty = & $script -LogDirectory $OutputDirectory -SinceUtc '2026-11-04T00:00:00Z' -CacheAudit
if ($empty.Evidence.HasEvents -or $empty.SelectedEvents -ne 0) { throw 'An empty window cannot report evidence.' }
[pscustomobject]@{Passed=$true;Checks=11;FixtureDirectory=$OutputDirectory}
