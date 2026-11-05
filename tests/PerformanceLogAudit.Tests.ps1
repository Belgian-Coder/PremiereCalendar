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
# Interrupted publication must not duplicate raw + gzip events.
$archivePath = $log + '.gz'
$archive = [IO.File]::Create($archivePath)
$compressor = [IO.Compression.GZipStream]::new($archive, [IO.Compression.CompressionLevel]::Fastest)
try {
    $sourceStream = [IO.File]::OpenRead($log)
    try { $sourceStream.CopyTo($compressor) } finally { $sourceStream.Dispose() }
} finally { $compressor.Dispose() }
$duplicate = & $script -LogDirectory $OutputDirectory -SinceUtc '2026-11-03T16:00:00Z' -CacheAudit -WarningAction SilentlyContinue
if ($duplicate.SelectedEvents -ne $rows.Count -or $duplicate.Evidence.ArchivedFiles -ne 0) { throw 'Interrupted archive publication duplicated events.' }
Remove-Item -LiteralPath $log -Force
$archived = & $script -LogDirectory $OutputDirectory -SinceUtc '2026-11-03T16:00:00Z' -CacheAudit -WarningAction SilentlyContinue
if ($archived.SelectedEvents -ne $rows.Count -or $archived.Evidence.MalformedOrIncompleteRecords -ne 1 -or
    $archived.Evidence.ArchivedFiles -ne 1 -or $archived.Evidence.UnindexedArchives -ne 1) { throw 'Legacy archive fallback lost events or evidence.' }
@{ version=1; compressedBytes=(Get-Item -LiteralPath $archivePath).Length; complete=$false; maximumTimestampUtc='2026-11-03T17:00:00Z' } |
    ConvertTo-Json -Compress | Set-Content -LiteralPath ($archivePath + '.index.json')
$indexed = & $script -LogDirectory $OutputDirectory -SinceUtc '2026-11-03T16:00:00Z' -CacheAudit -WarningAction SilentlyContinue
if ($indexed.SelectedEvents -ne $rows.Count -or $indexed.Evidence.IncompleteFiles -ne 1) { throw 'Incomplete indexed archives must remain visible evidence.' }
# An indexed trace outside the window is skipped without opening/decompressing its body.
$oldArchive = Join-Path $OutputDirectory "performance-20260101T1700000000000Z-$instance-000002.ndjson.gz"
[IO.File]::WriteAllBytes($oldArchive, [byte[]]@(0,1,2,3))
@{ version=1; compressedBytes=4; complete=$true; maximumTimestampUtc='2026-01-01T17:00:00Z' } |
    ConvertTo-Json -Compress | Set-Content -LiteralPath ($oldArchive + '.index.json')
$skipped = & $script -LogDirectory $OutputDirectory -SinceUtc '2026-11-03T16:00:00Z' -CacheAudit -WarningAction SilentlyContinue
if ($skipped.Evidence.SkippedIndexedFiles -ne 1 -or $skipped.SelectedEvents -ne $rows.Count) { throw 'Indexed old-window skip failed.' }
# A full calendar-month window aggregates jobs once and retains each page's attachments.
$monthDirectory = Join-Path $OutputDirectory 'month'
New-Item -ItemType Directory -Path $monthDirectory -Force | Out-Null
$loadA = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'; $loadB = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'
$jobA = 'cccccccccccccccccccccccccccccccc'; $jobB = 'dddddddddddddddddddddddddddddddd'
$sharedJob = 'eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee'
$monthLog = Join-Path $monthDirectory "performance-20261101T0000000000000Z-$instance-000001.ndjson"
$monthRows = @(
    @{event='calendar.page.started';loadId=$loadA;details=@{weekStart='2026-11-02';stage='series'}},
    @{event='calendar.job.attached';loadId=$loadA;jobId=$jobA},
    @{event='calendar.job.attached';loadId=$loadA;jobId=$sharedJob},
    @{event='calendar.page.finished';loadId=$loadA;details=@{outcome='deadline';reason='background_budget';durationMilliseconds=10000}},
    @{event='calendar.page.started';loadId=$loadB;details=@{weekStart='2026-11-16';stage='series'}},
    @{event='calendar.job.attached';loadId=$loadB;jobId=$jobB},
    @{event='calendar.job.attached';loadId=$loadB;jobId=$sharedJob},
    @{event='calendar.page.finished';loadId=$loadB;details=@{outcome='canceled';reason='superseded';durationMilliseconds=2500}},
    @{event='scheduler.execution.started';jobId=$jobA},
    @{event='provider.request.started';jobId=$jobA;details=@{provider='watchmode'}},
    @{event='provider.attempt.started';jobId=$jobA;details=@{provider='watchmode';attempt=1}},
    @{event='provider.headers.completed';jobId=$jobA;details=@{provider='watchmode';statusCode=429}},
    @{event='provider.attempt.completed';jobId=$jobA;details=@{provider='watchmode';outcome='throttled';attempt=1}},
    @{event='provider.attempt.started';jobId=$jobA;details=@{provider='watchmode';attempt=2}},
    @{event='provider.headers.completed';jobId=$jobA;details=@{provider='watchmode';statusCode=200}},
    @{event='provider.attempt.completed';jobId=$jobA;details=@{provider='watchmode';outcome='success';attempt=2}},
    @{event='provider.request.completed';jobId=$jobA;details=@{provider='watchmode';outcome='success'}},
    @{event='provider.request.started';jobId=$jobA;details=@{provider='watchmode'}},
    @{event='provider.admission.deferred';jobId=$jobA;details=@{provider='watchmode';reason='local_backoff'}},
    @{event='provider.request.completed';jobId=$jobA;details=@{provider='watchmode';outcome='deferred'}},
    @{event='scheduler.execution.finished';jobId=$jobA;details=@{outcome='completed';durationMilliseconds=5000}},
    @{event='scheduler.job.completed';jobId=$jobA;details=@{outcome='completed';stage='durable';durationMilliseconds=12}},
    @{event='scheduler.execution.started';jobId=$jobB},
    @{event='provider.request.started';jobId=$jobB;details=@{provider='omdb'}},
    @{event='provider.attempt.started';jobId=$jobB;details=@{provider='omdb'}},
    @{event='provider.headers.completed';jobId=$jobB;details=@{provider='omdb';statusCode=401}},
    @{event='provider.attempt.completed';jobId=$jobB;details=@{provider='omdb';outcome='authenticationfailure'}},
    @{event='provider.request.completed';jobId=$jobB;details=@{provider='omdb';outcome='authenticationfailure'}},
    @{event='provider.request.started';jobId=$jobB;details=@{provider='omdb'}},
    @{event='provider.attempt.started';jobId=$jobB;details=@{provider='omdb'}},
    @{event='provider.headers.completed';jobId=$jobB;details=@{provider='omdb';outcome='networkfailure'}},
    @{event='provider.attempt.completed';jobId=$jobB;details=@{provider='omdb';outcome='networkfailure'}},
    @{event='provider.request.completed';jobId=$jobB;details=@{provider='omdb';outcome='networkfailure'}},
    @{event='scheduler.execution.finished';jobId=$jobB;details=@{outcome='partial';durationMilliseconds=8000}},
    @{event='scheduler.job.completed';jobId=$jobB;details=@{outcome='partial';stage='durable';durationMilliseconds=15}},
    @{event='scheduler.execution.started';jobId=$sharedJob},
    @{event='provider.request.started';jobId=$sharedJob;details=@{provider='fanart'}},
    @{event='provider.attempt.started';jobId=$sharedJob;details=@{provider='fanart'}},
    @{event='provider.headers.completed';jobId=$sharedJob;details=@{provider='fanart';statusCode=403}},
    @{event='provider.attempt.completed';jobId=$sharedJob;details=@{provider='fanart';outcome='accessdenied'}},
    @{event='provider.request.completed';jobId=$sharedJob;details=@{provider='fanart';outcome='accessdenied'}},
    @{event='scheduler.execution.finished';jobId=$sharedJob;details=@{outcome='failed';durationMilliseconds=900}},
    @{event='scheduler.job.failed';jobId=$sharedJob;details=@{outcome='failed';stage='durable';durationMilliseconds=7}}
)
$sequence = 0
$monthRows | ForEach-Object {
    $sequence++
    $_.timestampUtc = if ($_.loadId -eq $loadA) { '2026-11-03T12:00:00Z' } else { '2026-11-20T12:00:00Z' }
    $_.instanceId = $instance; $_.eventSequence = $sequence
    $_ | ConvertTo-Json -Depth 8 -Compress
} | Set-Content -LiteralPath $monthLog -Encoding utf8
# The upper boundary is exclusive even if another job writes while the analyzer runs.
@{event='provider.headers.completed';timestampUtc='2026-12-01T00:00:00Z';jobId=$jobB;details=@{provider='omdb';statusCode=500}} |
    ConvertTo-Json -Compress | Add-Content -LiteralPath $monthLog
$monthArgs = @{LogDirectory=$monthDirectory;SinceUtc='2026-11-01T00:00:00Z';UntilUtc='2026-12-01T00:00:00Z';CacheAudit=$true}
$month = & $script @monthArgs -ProviderResponseBudgets @{watchmode=2;omdb=1;fanart=1}
if ($month.SelectedEvents -ne $monthRows.Count -or $month.Traffic.BudgetStatus -ne 'within_budget' -or
    !$month.Evidence.CompleteObservedWindow) { throw 'Bounded month-window aggregation or explicit budgets failed.' }
$watchTraffic = $month.Traffic.Providers | Where-Object Provider -eq 'watchmode'
$omdbTraffic = $month.Traffic.Providers | Where-Object Provider -eq 'omdb'
if ($watchTraffic.ActualHttpResponses -ne 2 -or $watchTraffic.LogicalRequestsStarted -ne 2 -or
    $watchTraffic.AttemptsStarted -ne 2 -or $watchTraffic.LocalDeferrals -ne 1 -or
    $omdbTraffic.ActualHttpResponses -ne 1 -or $omdbTraffic.HeaderFailuresWithoutStatus -ne 1) { throw 'Responses, attempts, logical requests or local deferrals were conflated.' }
$pageA = $month.Pages | Where-Object LoadId -eq $loadA; $pageB = $month.Pages | Where-Object LoadId -eq $loadB
if (@($pageA.ProviderTraffic | Where-Object Provider -eq 'omdb').Count -ne 0 -or
    @($pageB.ProviderTraffic | Where-Object Provider -eq 'watchmode').Count -ne 0 -or
    ($pageA.ProviderTraffic | Where-Object Provider -eq 'fanart').ActualHttpResponses -ne 1 -or
    ($pageB.ProviderTraffic | Where-Object Provider -eq 'fanart').ActualHttpResponses -ne 1 -or
    ($month.Traffic.Providers | Where-Object Provider -eq 'fanart').ActualHttpResponses -ne 1) { throw 'Shared-job per-load attribution double-counted or leaked traffic.' }
if ($month.Completion.BackgroundDeadlines -ne 1 -or $month.Completion.NavigationCancellations -ne 1 -or
    $month.Completion.ExecutionDurations.MaximumMilliseconds -ne 8000 -or
    $month.Completion.DurableWriteDurations.MaximumMilliseconds -ne 15 -or
    ($month.Completion.Jobs | Where-Object JobId -eq $sharedJob).LoadIds.Count -ne 2) { throw 'Completion causes or execution/durable-write timings were conflated.' }
$exceeded = & $script @monthArgs -ProviderResponseBudgets @{watchmode=1;omdb=1;fanart=1}
if ($exceeded.Traffic.BudgetStatus -ne 'exceeded' -or
    ($exceeded.Traffic.Providers | Where-Object Provider -eq 'watchmode').BudgetStatus -ne 'exceeded') { throw 'An explicit exceeded response budget was not flagged.' }
$unconfigured = & $script @monthArgs
if ($unconfigured.Traffic.BudgetStatus -ne 'not_configured') { throw 'No response limits may be invented.' }
$unknown = & $script @monthArgs -ProviderResponseBudgets @{tmdb=0}
if ($unknown.Traffic.BudgetStatus -ne 'unknown') { throw 'Missing provider evidence cannot pass a response budget.' }
@{event='diagnostics.loss';timestampUtc='2026-11-20T12:01:00Z';droppedEvents=1} | ConvertTo-Json -Compress | Add-Content -LiteralPath $monthLog
$lossBudget = & $script @monthArgs -ProviderResponseBudgets @{watchmode=100;omdb=100;fanart=100} -WarningAction SilentlyContinue
$omittedBudget = & $script @monthArgs -MaximumEvents 2 -ProviderResponseBudgets @{watchmode=100} -WarningAction SilentlyContinue
if ($lossBudget.Traffic.BudgetStatus -ne 'unknown' -or !$lossBudget.Traffic.CountsAreLowerBounds -or
    $omittedBudget.Traffic.BudgetStatus -ne 'unknown') { throw 'Lost or omitted evidence cannot pass a response budget.' }
[pscustomobject]@{Passed=$true;Checks=23;FixtureDirectory=$OutputDirectory}
