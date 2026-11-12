using System.Reflection;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class ProviderWorkSchedulerTests
{
    private async Task PublishRetryingFinalAsync(ProviderWorkSnapshot job, DateTimeOffset retryAfter)
        => await _scheduler.PublishAsync(job, Progress(job, "partial", new PremiereLoadProgress("Complete", 1, 1, [Item(1)], IsFinal: true)
        {
            HasSourceErrors = true, RetryAfterUtc = retryAfter, FailedSourceNames = ["TMDb"]
        }), CancellationToken.None);

    [Fact]
    public async Task ResultCarryingARetryTimeIsRequeuedAtMostThreeTimesThenEndsPartialWithGrowingDelays()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        _scheduler = NewScheduler(clock);
        var job = await ClaimCalendarAsync("deferral-cap");
        var synthetic = TimeSpan.FromSeconds(60);

        for (var deferral = 1; deferral <= ProviderWorkScheduler.MaximumConsecutiveDeferrals; deferral++)
        {
            var requested = clock.GetUtcNow().Add(synthetic);
            await PublishRetryingFinalAsync(job, requested);
            await _scheduler.CompleteAsync(job, CancellationToken.None);

            var saved = (await _scheduler.GetAsync(job.JobId))!;
            Assert.Equal(ProviderWorkState.RetryWaiting, saved.State);
            Assert.Equal(0, saved.AttemptCount);
            // The requested deadline is always honoured; repeated deferrals wait at least 2x and 4x the requested delay.
            if (deferral == 1) Assert.Equal(requested, saved.NextAttemptUtc);
            else Assert.True(saved.NextAttemptUtc >= clock.GetUtcNow().Add(synthetic * (1 << (deferral - 1))));
            clock.Advance(TimeSpan.FromHours(1));
            job = (await _scheduler.ClaimNextAsync(false, $"deferral-owner-{deferral}", CancellationToken.None))!;
            Assert.NotNull(job);
        }

        await PublishRetryingFinalAsync(job, clock.GetUtcNow().Add(synthetic));
        await _scheduler.CompleteAsync(job, CancellationToken.None);

        var final = (await _scheduler.GetAsync(job.JobId))!;
        Assert.Equal(ProviderWorkState.Completed, final.State);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Null(await _scheduler.ClaimNextAsync(false, "no-fifth-run", CancellationToken.None));
    }

    [Fact]
    public async Task ExceptionCarryingARetryTimeIsDeferredAtMostThreeTimesThenUsesTheBoundedAttemptPath()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        _scheduler = NewScheduler(clock);
        var job = await ClaimCalendarAsync("deferral-cap-failure");
        for (var deferral = 1; deferral <= ProviderWorkScheduler.MaximumConsecutiveDeferrals; deferral++)
        {
            await _scheduler.FailAsync(job, new ProviderRetryAfterException("tmdb", clock.GetUtcNow().AddSeconds(60)), CancellationToken.None);
            Assert.Equal(0, (await _scheduler.GetAsync(job.JobId))!.AttemptCount);
            clock.Advance(TimeSpan.FromHours(1));
            job = (await _scheduler.ClaimNextAsync(false, $"failure-owner-{deferral}", CancellationToken.None))!;
        }

        await _scheduler.FailAsync(job, new ProviderRetryAfterException("tmdb", clock.GetUtcNow().AddSeconds(60)), CancellationToken.None);

        // The fourth deferral is refused: the failure now spends a normal retry attempt instead.
        Assert.Equal(1, (await _scheduler.GetAsync(job.JobId))!.AttemptCount);
    }

    [Fact]
    public async Task ASuccessfulCompletionAfterOneDeferralClearsTheCounter()
    {
        var clock = new MutableTimeProvider(DateTimeOffset.UtcNow);
        _scheduler = NewScheduler(clock);
        var job = await ClaimCalendarAsync("deferral-reset");
        await PublishRetryingFinalAsync(job, clock.GetUtcNow().AddMinutes(10));
        await _scheduler.CompleteAsync(job, CancellationToken.None);
        var counters = (System.Collections.IDictionary)typeof(ProviderWorkScheduler)
            .GetField("_deferrals", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(_scheduler)!;
        Assert.Single(counters);

        clock.Advance(TimeSpan.FromHours(1));
        job = (await _scheduler.ClaimNextAsync(false, "reset-owner", CancellationToken.None))!;
        await _scheduler.PublishAsync(job, Progress(job, "ok", new PremiereLoadProgress("Complete", 1, 1, [Item(1)], IsFinal: true)), CancellationToken.None);
        await _scheduler.CompleteAsync(job, CancellationToken.None);

        Assert.Equal(ProviderWorkState.Completed, (await _scheduler.GetAsync(job.JobId))!.State);
        Assert.Empty(counters);
    }
}
