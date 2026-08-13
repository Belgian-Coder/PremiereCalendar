using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace PremiereCalendar.Services;

public sealed class PremiereTelemetry
{
    public const string ActivitySourceName = "PremiereCalendar";
    public const string MeterName = "PremiereCalendar";

    public static ActivitySource ActivitySource { get; } = new(ActivitySourceName);
    public static Meter Meter { get; } = new(MeterName);

    private readonly Histogram<double> _providerDuration = Meter.CreateHistogram<double>(
        "premierecalendar.provider.request.duration",
        "ms");
    private readonly Counter<long> _providerRequests = Meter.CreateCounter<long>("premierecalendar.provider.requests");
    private readonly Histogram<double> _jobDuration = Meter.CreateHistogram<double>("premierecalendar.scheduler.job.duration", "ms");
    private readonly Counter<long> _jobOutcomes = Meter.CreateCounter<long>("premierecalendar.scheduler.job.outcomes");
    private readonly Histogram<double> _migrationDuration = Meter.CreateHistogram<double>("premierecalendar.database.migration.duration", "ms");
    private readonly Histogram<double> _calendarFirstResult = Meter.CreateHistogram<double>("premierecalendar.calendar.first_result.duration", "ms");
    private readonly Histogram<double> _calendarCompletion = Meter.CreateHistogram<double>("premierecalendar.calendar.completion.duration", "ms");
    private readonly Histogram<int> _calendarCardCount = Meter.CreateHistogram<int>("premierecalendar.calendar.card.count");
    private readonly Counter<long> _circuitEvents = Meter.CreateCounter<long>("premierecalendar.blazor.circuit.events");
    private readonly Counter<long> _providerCircuitEvents = Meter.CreateCounter<long>("premierecalendar.provider.circuit.events");
    private readonly Counter<long> _updateOutcomes = Meter.CreateCounter<long>("premierecalendar.application_update.outcomes");
    private readonly ConcurrentDictionary<string, ProviderGaugeState> _providerGauges = new(StringComparer.OrdinalIgnoreCase);
    private long _queuedJobs;
    private long _runningJobs;

    public PremiereTelemetry()
    {
        Meter.CreateObservableGauge("premierecalendar.scheduler.jobs.queued", () => Volatile.Read(ref _queuedJobs));
        Meter.CreateObservableGauge("premierecalendar.scheduler.jobs.running", () => Volatile.Read(ref _runningJobs));
        Meter.CreateObservableGauge("premierecalendar.provider.concurrency.limit", ObserveProviderLimits);
        Meter.CreateObservableGauge("premierecalendar.provider.concurrency.active", ObserveProviderActive);
    }

    public Activity? StartActivity(string operation, ActivityKind kind = ActivityKind.Internal)
        => ActivitySource.StartActivity(operation, kind);

    public void RecordProviderRequest(string provider, string outcome, TimeSpan duration, int concurrency, ProviderCircuitState circuit)
    {
        var tags = new TagList
        {
            { "provider", provider },
            { "outcome", outcome },
            { "circuit", circuit.ToString() }
        };
        _providerRequests.Add(1, tags);
        _providerDuration.Record(duration.TotalMilliseconds, tags);
    }

    public void SetProviderConcurrency(string provider, int limit, int active, string circuit)
        => _providerGauges[provider] = new ProviderGaugeState(limit, active, circuit);

    public void SetSchedulerCounts(long queued, long running)
    {
        Interlocked.Exchange(ref _queuedJobs, queued);
        Interlocked.Exchange(ref _runningJobs, running);
    }

    public void RecordJob(string kind, string outcome, TimeSpan duration)
    {
        var tags = new TagList { { "kind", kind }, { "outcome", outcome } };
        _jobOutcomes.Add(1, tags);
        _jobDuration.Record(duration.TotalMilliseconds, tags);
    }

    public void RecordMigration(int version, string outcome, TimeSpan duration)
        => _migrationDuration.Record(duration.TotalMilliseconds,
            new TagList { { "version", version }, { "outcome", outcome } });

    public void RecordCalendarFirstResult(TimeSpan duration, bool fromCache)
        => _calendarFirstResult.Record(duration.TotalMilliseconds, new TagList { { "cache", fromCache ? "hit" : "miss" } });

    public void RecordCalendarCompletion(TimeSpan duration, int cardCount, bool fromCache)
    {
        var tags = new TagList { { "cache", fromCache ? "hit" : "miss" } };
        _calendarCompletion.Record(duration.TotalMilliseconds, tags);
        _calendarCardCount.Record(cardCount, tags);
    }

    public void RecordCircuitEvent(string outcome)
        => _circuitEvents.Add(1, new TagList { { "outcome", outcome } });

    public void RecordProviderCircuitEvent(string provider, string state)
        => _providerCircuitEvents.Add(1, new TagList { { "provider", provider }, { "state", state } });

    public void RecordApplicationUpdate(string operation, string outcome)
        => _updateOutcomes.Add(1, new TagList { { "operation", operation }, { "outcome", outcome } });

    private IEnumerable<Measurement<int>> ObserveProviderLimits()
        => _providerGauges.Select(entry => new Measurement<int>(entry.Value.Limit, new KeyValuePair<string, object?>("provider", entry.Key)));

    private IEnumerable<Measurement<int>> ObserveProviderActive()
        => _providerGauges.Select(entry => new Measurement<int>(entry.Value.Active, new KeyValuePair<string, object?>("provider", entry.Key)));

    private sealed record ProviderGaugeState(int Limit, int Active, string Circuit);
}
