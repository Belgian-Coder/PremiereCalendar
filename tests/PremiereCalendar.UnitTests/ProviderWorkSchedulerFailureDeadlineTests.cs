using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class ProviderWorkSchedulerTests
{
    [Fact]
    public async Task MixedTerminalPartialWithOptionalNoticeDoesNotCreateAnAutomaticRetry()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        _scheduler = NewScheduler(clock);
        var job = await ClaimCalendarAsync("mixed-terminal-partial");
        await _scheduler.PublishAsync(job, Progress(job, "partial", new PremiereLoadProgress("Complete", 1, 1, [Item(1)], IsFinal: true)
        {
            HasSourceErrors = true, IsDegradedResult = false, RetryAfterUtc = null,
            FailedSourceNames = ["Trakt", "Other source"],
            UnavailableSources = [new("trakt", "authentication_denied", 403, clock.GetUtcNow().AddMinutes(15))]
        }), CancellationToken.None);
        await _scheduler.CompleteAsync(job, CancellationToken.None);
        Assert.Equal(ProviderWorkState.Completed, (await _scheduler.GetAsync(job.JobId))!.State);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Null(await _scheduler.ClaimNextAsync(false, "no-mixed-requeue", CancellationToken.None));
    }

    [Theory]
    [InlineData("trakt", true, true)]
    [InlineData("trakt", true, false)]
    [InlineData("tmdb", false, true)]
    public async Task UsableOptionalAccessDenialTerminatesWithoutAutomaticRequeue(string provider, bool completes, bool cached)
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        _scheduler = NewScheduler(clock);
        var job = await ClaimCalendarAsync("degraded-provider-deadline");
        var deadline = clock.GetUtcNow().AddMinutes(15);
        await _scheduler.PublishAsync(job, Progress(job, "degraded", new PremiereLoadProgress("Week cache", 1, 1, [Item(1)], IsFinal: true)
        {
            HasSourceErrors = true, IsReusableDegraded = cached, IsDegradedResult = true, RetryAfterUtc = deadline,
            UnavailableSources = [new(provider, "authentication_denied", 403, deadline)]
        }), CancellationToken.None);

        await _scheduler.CompleteAsync(job, CancellationToken.None);

        var saved = (await _scheduler.GetAsync(job.JobId))!;
        Assert.Equal(completes ? ProviderWorkState.Completed : ProviderWorkState.RetryWaiting, saved.State);
        Assert.Equal(0, saved.AttemptCount);
        if (completes)
        {
            Assert.NotNull(saved.CompletedUtc);
            clock.Advance(TimeSpan.FromMinutes(15));
            Assert.Null(await _scheduler.ClaimNextAsync(false, "no-automatic-requeue", CancellationToken.None));
        }
        else Assert.Equal(deadline, saved.NextAttemptUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TypedProviderFailurePreservesDeadlineAndDoesNotConsumeRetry(bool nested)
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        _scheduler = NewScheduler(clock);
        var job = await ClaimCalendarAsync("typed-provider-deadline");
        var deadline = clock.GetUtcNow().AddMinutes(15);
        Exception failure = new ProviderRequestException("trakt", new("authentication_denied", 403, deadline));
        if (nested) failure = new AggregateException(new InvalidOperationException("source failed", failure));

        await _scheduler.FailAsync(job, failure, CancellationToken.None);

        var saved = (await _scheduler.GetAsync(job.JobId))!;
        Assert.Equal(ProviderWorkState.RetryWaiting, saved.State);
        Assert.Equal(deadline, saved.NextAttemptUtc);
        Assert.Equal(0, saved.AttemptCount);
        Assert.Null(await _scheduler.ClaimNextAsync(false, "before-deadline", CancellationToken.None));
        clock.Advance(TimeSpan.FromMinutes(15));
        Assert.Equal(job.JobId, (await _scheduler.ClaimNextAsync(false, "at-deadline", CancellationToken.None))!.JobId);
    }
}
