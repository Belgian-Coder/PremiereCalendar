[CmdletBinding()]
param(
    [string] $LogDirectory = (Join-Path $PSScriptRoot '../PremiereCalendar/App_Data/logs/performance'),
    [guid] $LoadId = [guid]::Empty,
    [guid] $JobId = [guid]::Empty,
    [ValidatePattern('^\d{4}-\d{2}-\d{2}$')][string] $WeekStart,
    [DateTimeOffset] $SinceUtc = [DateTimeOffset]::UtcNow.AddDays(-7),
    [ValidateRange(1,100000)][int] $MaximumEvents = 10000,
    [switch] $Summary
)

$ErrorActionPreference = 'Stop'
$files = @(Get-ChildItem -LiteralPath $LogDirectory -Filter 'performance-*.ndjson' -File | Sort-Object Name)
$loads = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$jobs = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
if ($LoadId -ne [guid]::Empty) { [void]$loads.Add($LoadId.ToString('N')) }
if ($JobId -ne [guid]::Empty) { [void]$jobs.Add($JobId.ToString('N')) }

function Read-Events {
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
                catch { Write-Verbose "Skipped incomplete or malformed record in $($file.Name)"; continue }
                $timestamp = [DateTimeOffset]::MinValue
                $validTime = $true
                if ($event.timestampUtc -is [DateTimeOffset]) { $timestamp = $event.timestampUtc }
                elseif ($event.timestampUtc -is [DateTime]) { $timestamp = [DateTimeOffset]$event.timestampUtc }
                else {
                    $validTime = [DateTimeOffset]::TryParse([string]$event.timestampUtc,
                        [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::RoundtripKind, [ref]$timestamp)
                }
                if ($validTime -and $timestamp -ge $SinceUtc) { $event }
            }
        }
        catch [IO.IOException] { Write-Verbose "File rotated while reading: $($file.Name)" }
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
Read-Events | ForEach-Object {
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
