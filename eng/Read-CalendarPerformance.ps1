[CmdletBinding()]
param(
    [string] $LogDirectory = (Join-Path $PSScriptRoot '../PremiereCalendar/App_Data/logs/performance'),
    [guid] $LoadId = [guid]::Empty,
    [guid] $JobId = [guid]::Empty,
    [guid] $InstanceId = [guid]::Empty,
    [ValidatePattern('^\d{4}-\d{2}-\d{2}$')][string] $WeekStart,
    [DateTimeOffset] $SinceUtc = [DateTimeOffset]::UtcNow.AddDays(-7),
    [ValidateRange(1,500000)][int] $MaximumEvents = 100000,
    [switch] $Summary,
    [switch] $CacheAudit
)

$ErrorActionPreference = 'Stop'
$filePattern = if ($InstanceId -eq [guid]::Empty) { 'performance-*.ndjson' }
    else { 'performance-*-' + $InstanceId.ToString('N') + '-*.ndjson' }
$files = @(Get-ChildItem -LiteralPath $LogDirectory -Filter $filePattern -File -Force | Sort-Object Name)
$readStats = @{ MalformedOrIncomplete = 0; RotatedFiles = 0; InvalidTimestamps = 0 }
$loads = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$jobs = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
if ($LoadId -ne [guid]::Empty) { [void]$loads.Add($LoadId.ToString('N')) }
if ($JobId -ne [guid]::Empty) { [void]$jobs.Add($JobId.ToString('N')) }

function Read-Events {
    param([switch] $CollectReadStats)
    foreach ($file in $files) {
        $stream = $null
        $reader = $null
        try {
            # Allow the active writer and retention replacement while examining a live file.
            $stream = [IO.FileStream]::new($file.FullName, [IO.FileMode]::Open, [IO.FileAccess]::Read,
                [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
            $reader = [IO.StreamReader]::new($stream)
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
                if ($validTime -and $timestamp -ge $SinceUtc) { $event }
            }
        }
        catch [IO.IOException] {
            if ($CollectReadStats) { $readStats.RotatedFiles++ }
            Write-Verbose "File rotated while reading: $($file.Name)"
        }
        finally {
            if ($null -ne $reader) { $reader.Dispose() }
            elseif ($null -ne $stream) { $stream.Dispose() }
        }
    }
}

# Follow load-to-job attachment records so a single query includes durable workers and HTTP bodies.
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
if ($omitted) { Write-Warning "Omitted $omitted matching events; narrow the query or increase MaximumEvents. Summary covers retained events only." }
$filtered = @($selected.ToArray())
$filtered = @($filtered | Sort-Object timestampUtc, instanceId, eventSequence)
if (@($filtered | Where-Object { $_.event -eq 'diagnostics.loss' }).Count) {
    Write-Warning 'Diagnostic loss markers exist in the requested time window; inspect them before treating the timeline as complete.'
}
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
    $pageEvents = @($filtered | Where-Object { $_.event -like 'calendar.page.*' -and $_.loadId })
    $pages = @($pageEvents | Group-Object loadId | ForEach-Object {
        $events = $_.Group
        $pageLoadId = $_.Name
        $started = $events | Where-Object event -eq 'calendar.page.started' | Select-Object -First 1
        $first = $events | Where-Object event -eq 'calendar.page.first_nonempty_result' | Select-Object -First 1
        $result = $events | Where-Object event -eq 'calendar.page.result_completed' | Select-Object -Last 1
        $finished = $events | Where-Object event -eq 'calendar.page.finished' | Select-Object -Last 1
        [pscustomobject]@{
            LoadId = $_.Name; StartedUtc = $started.timestampUtc
            Profile = $started.details.stage; WeekStart = $started.details.weekStart
            FirstNonemptyMilliseconds = $first.details.durationMilliseconds; InitialRows = $first.details.count
            FinishedMilliseconds = $finished.details.durationMilliseconds; Outcome = $finished.details.outcome
            FinalRows = $result.details.count; ResultOutcome = $result.details.outcome
            JobIds = @($filtered | Where-Object { $_.loadId -eq $pageLoadId -and $_.event -eq 'calendar.job.attached' } |
                ForEach-Object jobId | Sort-Object -Unique)
        }
    })
    $responses = @($filtered | Where-Object { $_.event -eq 'provider.headers.completed' -and $null -ne $_.details.statusCode })
    $http = @($responses | Group-Object { "$($_.details.provider)|$($_.details.statusCode)" } | ForEach-Object {
        [pscustomobject]@{ Provider = $_.Group[0].details.provider; StatusCode = $_.Group[0].details.statusCode; Responses = $_.Count }
    })
    $deferrals = @($filtered | Where-Object { $_.event -eq 'provider.admission.deferred' -or $_.event -eq 'provider.outcome' })
    [pscustomobject]@{
        CapturedUtc = [DateTimeOffset]::UtcNow
        SinceUtc = $SinceUtc; FileCount = $files.Count; SelectedEvents = $filtered.Count
        Evidence = [pscustomobject]@{
            HasEvents = ($filtered.Count -gt 0); OmittedEvents = $omitted
            MalformedOrIncompleteRecords = $readStats.MalformedOrIncomplete; InvalidTimestampRecords = $readStats.InvalidTimestamps
            RotatedFiles = $readStats.RotatedFiles
            LossMarkers = @($filtered | Where-Object event -eq 'diagnostics.loss').Count
            Note = 'Missing refresh/clear events are not proof of correctness. Check health/version for writer health. Timings are server-side; overlapping work is not additive.'
        }
        Pages = $pages
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
