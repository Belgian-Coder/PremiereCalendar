[CmdletBinding()]
param(
    [string] $LogDirectory = (Join-Path $PSScriptRoot '../PremiereCalendar/App_Data/logs/performance'),
    [guid] $LoadId = [guid]::Empty,
    [guid] $JobId = [guid]::Empty,
    [guid] $InstanceId = [guid]::Empty,
    [ValidatePattern('^\d{4}-\d{2}-\d{2}$')][string] $WeekStart,
    [DateTimeOffset] $SinceUtc = [DateTimeOffset]::UtcNow.AddDays(-7),
    [DateTimeOffset] $UntilUtc = [DateTimeOffset]::UtcNow,
    [hashtable] $ProviderResponseBudgets = @{},
    [ValidateRange(1,500000)][int] $MaximumEvents = 100000,
    [switch] $Summary,
    [switch] $CacheAudit
)

$ErrorActionPreference = 'Stop'
if ($UntilUtc -le $SinceUtc) { throw 'UntilUtc must be later than SinceUtc. The upper bound is exclusive.' }
$budgets = @{}
foreach ($provider in $ProviderResponseBudgets.Keys) {
    $limit = 0L
    if ([string]$provider -notmatch '^[a-zA-Z0-9._-]{1,64}$' -or
        ![long]::TryParse([string]$ProviderResponseBudgets[$provider], [ref]$limit) -or $limit -lt 0) {
        throw 'ProviderResponseBudgets must map provider identifiers to nonnegative whole response counts.'
    }
    $budgets[[string]$provider] = $limit
}
$filePattern = if ($InstanceId -eq [guid]::Empty) { 'performance-*.ndjson*' }
    else { 'performance-*-' + $InstanceId.ToString('N') + '-*.ndjson*' }
$allFiles = @(Get-ChildItem -LiteralPath $LogDirectory -Filter $filePattern -File -Force | Sort-Object Name)
# Raw remains authoritative during interrupted archive/index publication. Read each logical trace once.
$rawNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($file in $allFiles) { if ($file.Name.EndsWith('.ndjson')) { [void]$rawNames.Add($file.FullName) } }
$files = @($allFiles | Where-Object {
    $_.Name.EndsWith('.ndjson') -or ($_.Name.EndsWith('.ndjson.gz') -and !$rawNames.Contains($_.FullName.Substring(0, $_.FullName.Length - 3)))
})
$readStats = @{ MalformedOrIncomplete = 0; RotatedFiles = 0; InvalidTimestamps = 0; ArchivedFiles = 0;
    SkippedIndexedFiles = 0; InvalidIndexes = 0; IncompleteFiles = 0; UnindexedArchives = 0 }
$indexes = @{}
$archiveIndexes = @{}
foreach ($file in $files) {
    if (!$file.Name.EndsWith('.gz')) { continue }
    $readStats.ArchivedFiles++
    $indexPath = $file.FullName + '.index.json'
    if (!(Test-Path -LiteralPath $indexPath)) { $readStats.UnindexedArchives++; continue }
    try {
        if ((Get-Item -LiteralPath $indexPath).Length -gt 4096) { throw 'Oversized trace index' }
        $index = Get-Content -LiteralPath $indexPath -Raw | ConvertFrom-Json
        if ($index.version -ne 1 -or [long]$index.compressedBytes -ne $file.Length) { throw 'Incompatible trace index' }
        $archiveIndexes[$file.FullName] = $index
        $maximum = if ($index.maximumTimestampUtc -is [DateTimeOffset]) { $index.maximumTimestampUtc }
            elseif ($index.maximumTimestampUtc -is [DateTime]) { [DateTimeOffset]$index.maximumTimestampUtc }
            else { [DateTimeOffset]::Parse([string]$index.maximumTimestampUtc, [Globalization.CultureInfo]::InvariantCulture) }
        $minimum = if ($null -eq $index.minimumTimestampUtc) { [DateTimeOffset]::MinValue }
            elseif ($index.minimumTimestampUtc -is [DateTimeOffset]) { $index.minimumTimestampUtc }
            elseif ($index.minimumTimestampUtc -is [DateTime]) { [DateTimeOffset]$index.minimumTimestampUtc }
            else { [DateTimeOffset]::Parse([string]$index.minimumTimestampUtc, [Globalization.CultureInfo]::InvariantCulture) }
        if ($index.complete -eq $true -and ($maximum -lt $SinceUtc -or $minimum -ge $UntilUtc)) {
            $indexes[$file.FullName] = $index
            $readStats.SkippedIndexedFiles++
        }
    } catch {
        $readStats.InvalidIndexes++
        $readStats.UnindexedArchives++
        Write-Verbose "Ignoring unavailable or invalid index for $($file.Name); scanning trace instead."
    }
}
$loads = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$jobs = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
if ($LoadId -ne [guid]::Empty) { [void]$loads.Add($LoadId.ToString('N')) }
if ($JobId -ne [guid]::Empty) { [void]$jobs.Add($JobId.ToString('N')) }

function Read-Events {
    param([switch] $CollectReadStats)
    foreach ($file in $files) {
        $stream = $null
        $reader = $null
        $decompressor = $null
        if ($indexes.ContainsKey($file.FullName)) { continue }
        if ($CollectReadStats -and $archiveIndexes.ContainsKey($file.FullName) -and
            $archiveIndexes[$file.FullName].complete -ne $true) { $readStats.IncompleteFiles++ }
        try {
            # Allow the active writer and retention replacement while examining a live file.
            $stream = [IO.FileStream]::new($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read,
                [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
            if ($file.Name.EndsWith('.gz')) {
                $decompressor = [IO.Compression.GZipStream]::new($stream, [IO.Compression.CompressionMode]::Decompress)
                $reader = [IO.StreamReader]::new($decompressor)
            } else { $reader = [IO.StreamReader]::new($stream) }
            while ($null -ne ($line = $reader.ReadLine())) {
                try { $event = $line | ConvertFrom-Json -ErrorAction Stop }
                catch {
                    if ($CollectReadStats) { $readStats.MalformedOrIncomplete++ }
                    Write-Verbose "Skipped incomplete or malformed record in $($file.Name)"
                    continue
                }
                $timestamp = [DateTimeOffset]::MinValue
                $validTime = $true
                if ($event.timestampUtc -is [DateTimeOffset]) { $timestamp = $event.timestampUtc }
                elseif ($event.timestampUtc -is [DateTime]) { $timestamp = [DateTimeOffset]$event.timestampUtc }
                else {
                    $validTime = [DateTimeOffset]::TryParse([string]$event.timestampUtc,
                        [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind, [ref]$timestamp)
                }
                if (!$validTime -and $CollectReadStats) { $readStats.InvalidTimestamps++ }
                if ($validTime -and $timestamp -ge $SinceUtc -and $timestamp -lt $UntilUtc) { $event }
            }
            if ($CollectReadStats -and !$file.Name.EndsWith('.gz') -and $stream.Length -gt 0) {
                $stream.Position = $stream.Length - 1
                if ($stream.ReadByte() -ne 10) { $readStats.IncompleteFiles++ }
            }
        }
        catch [IO.InvalidDataException] {
            if ($CollectReadStats) { $readStats.MalformedOrIncomplete++ }
            Write-Verbose "Incomplete or damaged archive: $($file.Name)"
        }
        catch [IO.IOException] {
            if ($CollectReadStats) { $readStats.RotatedFiles++ }
            Write-Verbose "File rotated while reading: $($file.Name)"
        }
        finally {
            if ($null -ne $reader) { $reader.Dispose() }
            elseif ($null -ne $decompressor) { $decompressor.Dispose() }
            elseif ($null -ne $stream) { $stream.Dispose() }
        }
    }
}

# Follow load-to-job attachment records so a single query includes durable workers and HTTP bodies.
if ($WeekStart -or $LoadId -ne [guid]::Empty -or $JobId -ne [guid]::Empty) {
Read-Events | ForEach-Object {
    $event = $_
    $eventWeek = if ($event.details.weekStart -is [DateTime]) { $event.details.weekStart.ToString('yyyy-MM-dd') } else { [string]$event.details.weekStart }
    $matchesWeek = $WeekStart -and $eventWeek -eq $WeekStart
    if ($matchesWeek) {
        if ($event.loadId) { [void]$loads.Add([string]$event.loadId) }
        if ($event.jobId) { [void]$jobs.Add([string]$event.jobId) }
    }
    if ($event.loadId -and $loads.Contains([string]$event.loadId) -and $event.jobId) {
        [void]$jobs.Add([string]$event.jobId)
    }
}

}
$selected = [Collections.Generic.Queue[object]]::new()
$omitted = 0
Read-Events -CollectReadStats | ForEach-Object {
        $event = $_
        $eventWeek = if ($event.details.weekStart -is [DateTime]) { $event.details.weekStart.ToString('yyyy-MM-dd') } else { [string]$event.details.weekStart }
        if ((!$WeekStart -and $LoadId -eq [guid]::Empty -and $JobId -eq [guid]::Empty) -or
            ($event.event -eq 'diagnostics.loss') -or
            ($WeekStart -and $eventWeek -eq $WeekStart) -or
            ($event.loadId -and $loads.Contains([string]$event.loadId)) -or
            ($event.jobId -and $jobs.Contains([string]$event.jobId))) {
            if ($selected.Count -eq $MaximumEvents) { [void]$selected.Dequeue(); $omitted++ }
            $selected.Enqueue($event)
        }
}
if ($readStats.MalformedOrIncomplete -or $readStats.InvalidTimestamps -or $readStats.RotatedFiles -or $readStats.IncompleteFiles -or $readStats.UnindexedArchives) {
    Write-Warning "Trace evidence is incomplete: malformed=$($readStats.MalformedOrIncomplete), invalid timestamps=$($readStats.InvalidTimestamps), rotated=$($readStats.RotatedFiles), unterminated files=$($readStats.IncompleteFiles), unindexed archives=$($readStats.UnindexedArchives)."
}
Write-Verbose "Trace scan: $($files.Count) logical files, $($readStats.ArchivedFiles) archives, $($readStats.SkippedIndexedFiles) outside the time window."
if ($omitted) { Write-Warning "Omitted $omitted matching events; narrow the query or increase MaximumEvents. Summary covers retained events only." }
$filtered = @($selected.ToArray())
$filtered = @($filtered | Sort-Object timestampUtc, instanceId, eventSequence)
if (@($filtered | Where-Object { $_.event -eq 'diagnostics.loss' }).Count) {
    Write-Warning 'Diagnostic loss markers exist in the requested time window; inspect them before treating the timeline as complete.'
}
$lossMarkers = @($filtered | Where-Object event -eq 'diagnostics.loss').Count
$completeObservedWindow = $omitted -eq 0 -and $readStats.MalformedOrIncomplete -eq 0 -and
    $readStats.InvalidTimestamps -eq 0 -and $readStats.RotatedFiles -eq 0 -and $readStats.IncompleteFiles -eq 0 -and
    $readStats.UnindexedArchives -eq 0 -and $lossMarkers -eq 0
if ($CacheAudit) {
    function Measure-Durations($Events) {
        $values = @($Events | Where-Object { $null -ne $_.details.durationMilliseconds } |
            ForEach-Object { [double]$_.details.durationMilliseconds } | Sort-Object)
        [pscustomobject]@{
            Samples = $values.Count
            P50Milliseconds = if ($values.Count) { [math]::Round($values[[math]::Ceiling($values.Count * .50) - 1], 3) } else { $null }
            P95Milliseconds = if ($values.Count) { [math]::Round($values[[math]::Ceiling($values.Count * .95) - 1], 3) } else { $null }
            MaximumMilliseconds = if ($values.Count) { [math]::Round($values[-1], 3) } else { $null }
        }
    }
    function Group-Decisions($Events) {
        @($Events | Group-Object { "$($_.event)|$($_.details.mediaType)|$($_.details.stage)|$($_.details.provider)|$($_.details.outcome)|$($_.details.reason)" } |
            ForEach-Object {
                $first = $_.Group[0]
                [pscustomobject]@{
                    Event = $first.event; MediaType = $first.details.mediaType; Stage = $first.details.stage; Provider = $first.details.provider
                    Outcome = $first.details.outcome; Reason = $first.details.reason; Count = $_.Count
                    DistinctItems = @($_.Group | Where-Object { $null -ne $_.details.itemId } |
                        ForEach-Object { "$($_.details.mediaType):$($_.details.stage):$($_.details.itemId)" } | Sort-Object -Unique).Count
                    ExampleItemIds = @($_.Group | Where-Object { $null -ne $_.details.itemId } |
                        ForEach-Object { $_.details.itemId } | Sort-Object -Unique | Select-Object -First 10)
                    Timings = Measure-Durations $_.Group
                }
            })
    }
    function Get-ProviderTraffic {
        param([object[]] $Events, [switch] $ApplyBudgets)
        $names = @($Events | Where-Object { $_.details.provider } | ForEach-Object { [string]$_.details.provider })
        if ($ApplyBudgets) { $names += @($budgets.Keys) }
        @($names | Sort-Object -Unique | ForEach-Object {
            $provider = $_
            $records = @($Events | Where-Object { $_.details.provider -eq $provider })
            $responses = @($records | Where-Object { $_.event -eq 'provider.headers.completed' -and $null -ne $_.details.statusCode })
            $hasBudget = $ApplyBudgets -and $budgets.ContainsKey($provider)
            $status = if (!$hasBudget) { 'not_configured' }
                elseif ($responses.Count -gt $budgets[$provider]) { 'exceeded' }
                elseif (!$completeObservedWindow -or !$records.Count) { 'unknown' }
                else { 'within_budget' }
            [pscustomobject]@{
                Provider = $provider
                ActualHttpResponses = $responses.Count
                ResponseStatusCodes = @($responses | Group-Object { $_.details.statusCode } | ForEach-Object {
                    [pscustomobject]@{ StatusCode = [int]$_.Name; Responses = $_.Count }
                })
                HeaderFailuresWithoutStatus = @($records | Where-Object { $_.event -eq 'provider.headers.completed' -and $null -eq $_.details.statusCode }).Count
                LogicalRequestsStarted = @($records | Where-Object event -eq 'provider.request.started').Count
                LogicalRequestsCompleted = @($records | Where-Object event -eq 'provider.request.completed').Count
                AttemptsStarted = @($records | Where-Object event -eq 'provider.attempt.started').Count
                AttemptsCompleted = @($records | Where-Object event -eq 'provider.attempt.completed').Count
                LocalDeferrals = @($records | Where-Object event -eq 'provider.admission.deferred').Count
                ResponseBudget = if ($hasBudget) { $budgets[$provider] } else { $null }
                BudgetStatus = $status
            }
        })
    }
    function Get-PageCompletion($Finished) {
        if ($null -eq $Finished) { return 'unfinished' }
        if ($Finished.details.outcome -eq 'deadline') { return 'background_deadline' }
        if ($Finished.details.outcome -eq 'canceled') {
            if ($Finished.details.reason -eq 'superseded') { return 'navigation_canceled' }
            if ($Finished.details.reason -eq 'disposed') { return 'disposed_canceled' }
            return 'canceled'
        }
        return [string]$Finished.details.outcome
    }
    $pageEvents = @($filtered | Where-Object { $_.event -like 'calendar.page.*' -and $_.loadId })
    $pages = @($pageEvents | Group-Object loadId | ForEach-Object {
        $events = $_.Group
        $pageLoadId = $_.Name
        $started = $events | Where-Object event -eq 'calendar.page.started' | Select-Object -First 1
        $first = $events | Where-Object event -eq 'calendar.page.first_nonempty_result' | Select-Object -First 1
        $result = $events | Where-Object event -eq 'calendar.page.result_completed' | Select-Object -Last 1
        $finished = $events | Where-Object event -eq 'calendar.page.finished' | Select-Object -Last 1
        $attachedJobs = @($filtered | Where-Object { $_.loadId -eq $pageLoadId -and $_.event -eq 'calendar.job.attached' } |
            ForEach-Object jobId | Sort-Object -Unique)
        $attributed = @($filtered | Where-Object { $_.loadId -eq $pageLoadId -or ($_.jobId -and $attachedJobs -contains $_.jobId) })
        [pscustomobject]@{
            LoadId = $_.Name; StartedUtc = $started.timestampUtc
            Profile = $started.details.stage; WeekStart = $started.details.weekStart
            FirstNonemptyMilliseconds = $first.details.durationMilliseconds; InitialRows = $first.details.count
            FinishedMilliseconds = $finished.details.durationMilliseconds; Outcome = $finished.details.outcome
            Reason = $finished.details.reason; CompletionKind = Get-PageCompletion $finished
            ProviderTraffic = @(Get-ProviderTraffic -Events $attributed)
            FinalRows = $result.details.count; ResultOutcome = $result.details.outcome
            JobIds = $attachedJobs
        }
    })
    $responses = @($filtered | Where-Object { $_.event -eq 'provider.headers.completed' -and $null -ne $_.details.statusCode })
    $http = @($responses | Group-Object { "$($_.details.provider)|$($_.details.statusCode)" } | ForEach-Object {
        [pscustomobject]@{ Provider = $_.Group[0].details.provider; StatusCode = $_.Group[0].details.statusCode; Responses = $_.Count }
    })
    $deferrals = @($filtered | Where-Object { $_.event -eq 'provider.admission.deferred' -or $_.event -eq 'provider.outcome' })
    $traffic = @(Get-ProviderTraffic -Events $filtered -ApplyBudgets)
    $budgetStatus = if (!$budgets.Count) { 'not_configured' }
        elseif (@($traffic | Where-Object BudgetStatus -eq 'exceeded').Count) { 'exceeded' }
        elseif (@($traffic | Where-Object BudgetStatus -eq 'unknown').Count) { 'unknown' }
        else { 'within_budget' }
    $durableEvents = @('scheduler.checkpoint.completed', 'scheduler.cache.publish.completed',
        'scheduler.job.completed', 'scheduler.job.failed', 'scheduler.job.deferred', 'scheduler.job.retry.scheduled', 'scheduler.job.preempted')
    $jobSummaries = @($filtered | Where-Object { $_.jobId -and $_.event -like 'scheduler.*' } | Group-Object jobId | ForEach-Object {
        $id = $_.Name
        $events = $_.Group
        $lastFinal = $events | Where-Object { $_.event -in @('scheduler.job.completed', 'scheduler.job.failed') } | Select-Object -Last 1
        [pscustomobject]@{
            JobId = $id
            HasFinalDurableTransition = ($null -ne $lastFinal)
            FinalDurableOutcome = $lastFinal.details.outcome
            LoadIds = @($filtered | Where-Object { $_.jobId -eq $id -and $_.loadId } | ForEach-Object loadId | Sort-Object -Unique)
            ExecutionStarts = @($events | Where-Object event -eq 'scheduler.execution.started').Count
            ExecutionFinishes = @($events | Where-Object event -eq 'scheduler.execution.finished').Count
            ExecutionDurations = Measure-Durations @($events | Where-Object event -eq 'scheduler.execution.finished')
            DurableWriteDurations = Measure-Durations @($events | Where-Object { $durableEvents -contains $_.event })
            DurableOperations = @($events | Where-Object { $durableEvents -contains $_.event } |
                Select-Object event, @{n='Outcome';e={$_.details.outcome}}, @{n='DurationMilliseconds';e={$_.details.durationMilliseconds}})
            Outcomes = @(Group-Decisions @($events | Where-Object { $_.event -in @('scheduler.execution.finished', 'scheduler.job.completed', 'scheduler.job.failed', 'scheduler.job.deferred') }))
        }
    })
    [pscustomobject]@{
        CapturedUtc = [DateTimeOffset]::UtcNow
        SinceUtc = $SinceUtc; UntilUtc = $UntilUtc; FileCount = $files.Count; SelectedEvents = $filtered.Count
        Evidence = [pscustomobject]@{
            HasEvents = ($filtered.Count -gt 0); OmittedEvents = $omitted
            MalformedOrIncompleteRecords = $readStats.MalformedOrIncomplete; InvalidTimestampRecords = $readStats.InvalidTimestamps
            RotatedFiles = $readStats.RotatedFiles; ArchivedFiles = $readStats.ArchivedFiles
            SkippedIndexedFiles = $readStats.SkippedIndexedFiles; InvalidIndexes = $readStats.InvalidIndexes
            IncompleteFiles = $readStats.IncompleteFiles; UnindexedArchives = $readStats.UnindexedArchives
            CompleteObservedWindow = $completeObservedWindow
            LossMarkers = $lossMarkers
            Note = 'Missing refresh/clear events are not proof of correctness. Check health/version for writer health. Timings are server-side; overlapping work is not additive.'
        }
        Pages = $pages
        Traffic = [pscustomobject]@{
            Providers = $traffic; BudgetStatus = $budgetStatus; BudgetScope = 'selected_time_window'
            CountsAreLowerBounds = !$completeObservedWindow
            Note = 'HTTP responses count only status-coded header events. Attempts, logical requests and local deferrals are separate observations. Per-load rows attribute shared jobs to each attached load and must not be added together.'
        }
        Completion = [pscustomobject]@{
            EvidenceComplete = $completeObservedWindow
            Pages = $pages.Count
            JobsWithFinalDurableTransition = @($jobSummaries | Where-Object HasFinalDurableTransition -eq $true).Count
            BackgroundDeadlines = @($pages | Where-Object CompletionKind -eq 'background_deadline').Count
            NavigationCancellations = @($pages | Where-Object CompletionKind -eq 'navigation_canceled').Count
            DisposalCancellations = @($pages | Where-Object CompletionKind -eq 'disposed_canceled').Count
            FailedPages = @($pages | Where-Object CompletionKind -eq 'failed').Count
            UnfinishedPages = @($pages | Where-Object CompletionKind -eq 'unfinished').Count
            Jobs = $jobSummaries
            ExecutionDurations = Measure-Durations @($filtered | Where-Object event -eq 'scheduler.execution.finished')
            DurableWriteDurations = Measure-Durations @($filtered | Where-Object { $durableEvents -contains $_.event })
            Note = 'Execution duration and durable-write operation duration are distinct. Writes can overlap or be nested; these measurements are not an end-to-end sum. Missing terminals can be outside the selected window.'
        }
        StatusCodedResponses = $responses.Count; HttpResponses = $http
        ProviderDecisions = @(Group-Decisions $deferrals)
        ProviderRetryDeadlines = @($deferrals | Where-Object { $_.details.retryAtUtc } |
            Select-Object timestampUtc, @{n='Provider';e={$_.details.provider}}, @{n='Reason';e={$_.details.reason}}, @{n='RetryAtUtc';e={$_.details.retryAtUtc}})
        ItemDecisions = @(Group-Decisions @($filtered | Where-Object event -eq 'item_cache.item'))
        ComponentDecisions = @(Group-Decisions @($filtered | Where-Object event -eq 'item_cache.decision'))
        ComponentResults = @(Group-Decisions @($filtered | Where-Object { $_.event -in @('item_cache.component','item_cache.component.deferred') }))
        RatingValueChanges = @(Group-Decisions @($filtered | Where-Object event -eq 'item_cache.result'))
        Writes = @(Group-Decisions @($filtered | Where-Object event -eq 'item_cache.write.item'))
        ComponentWrites = @(Group-Decisions @($filtered | Where-Object event -eq 'item_cache.write.component'))
        ReconciliationReasons = @(Group-Decisions @($filtered | Where-Object event -eq 'item_cache.reconcile.item'))
        IncompleteWeeks = @(Group-Decisions @($filtered | Where-Object event -eq 'calendar.cache.incomplete'))
        DegradedWeeks = @(Group-Decisions @($filtered | Where-Object event -eq 'calendar.cache.degraded'))
        ValidatedPartialPublications = @(Group-Decisions @($filtered | Where-Object event -eq 'calendar.cache.validated_partial'))
        SourceRetryPolicies = @(Group-Decisions @($filtered | Where-Object event -eq 'source.retry.policy'))
        CacheOperations = @(Group-Decisions @($filtered | Where-Object { $_.event -in @('item_cache.read','item_cache.write','item_cache.reconcile') }))
        SchedulerOutcomes = @(Group-Decisions @($filtered | Where-Object event -eq 'scheduler.execution.finished'))
    }
    return
}
if (!$Summary) { $filtered; return }

$filtered | Group-Object { "$($_.event) / $($_.details.stage) / $($_.details.provider) / $($_.details.outcome)" } |
    ForEach-Object {
        $durations = @($_.Group | Where-Object { $null -ne $_.details.durationMilliseconds } |
            ForEach-Object { [double]$_.details.durationMilliseconds } | Sort-Object)
        [pscustomobject]@{
            EventStageProviderOutcome = $_.Name
            Count = $_.Count
            TotalMilliseconds = if ($durations.Count) { [math]::Round(($durations | Measure-Object -Sum).Sum, 2) } else { $null }
            P50Milliseconds = if ($durations.Count) { [math]::Round($durations[[math]::Max(0, [math]::Ceiling($durations.Count * .50) - 1)], 2) } else { $null }
            P95Milliseconds = if ($durations.Count) { [math]::Round($durations[[math]::Max(0, [math]::Ceiling($durations.Count * .95) - 1)], 2) } else { $null }
            MaximumMilliseconds = if ($durations.Count) { [math]::Round($durations[-1], 2) } else { $null }
        }
    }
