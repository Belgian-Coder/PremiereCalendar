using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class ProviderRefreshScopeTests
{
    [Fact]
    public async Task VerifiedAbsenceIsReusedOnlyWithinOneGeneration()
    {
        var calls = 0;
        Task<ProviderRefreshResult<string?>> Fetch(CancellationToken _) =>
            Task.FromResult(new ProviderRefreshResult<string?>(Interlocked.Increment(ref calls) > 0 ? null : "unused", true));
        var first = ProviderRefreshScope.Create();
        Assert.Null(await first.RunAsync("provider:key", true, Fetch, CancellationToken.None));
        Assert.Null(await first.RunAsync("provider:key", true, Fetch, CancellationToken.None));
        Assert.Equal(1, calls);
        Assert.Null(await ProviderRefreshScope.Create().RunAsync("provider:key", true, Fetch, CancellationToken.None));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task UnsuccessfulOutcomeRemainsRetryable()
    {
        var calls = 0;
        Task<ProviderRefreshResult<int>> Fetch(CancellationToken _) =>
            Task.FromResult(new ProviderRefreshResult<int>(++calls, calls > 1));
        var scope = ProviderRefreshScope.Create();
        Assert.Equal(1, await scope.RunAsync("key", true, Fetch, CancellationToken.None));
        Assert.Equal(2, await scope.RunAsync("key", true, Fetch, CancellationToken.None));
        Assert.Equal(2, await scope.RunAsync("key", true, Fetch, CancellationToken.None));
    }

    [Fact]
    public async Task ForceInvalidatesEarlierOrdinaryMemoAndExternalPublicationIsValidated()
    {
        var current = "ordinary";
        var calls = 0;
        Task<ProviderRefreshResult<string>> Fetch(CancellationToken _)
        {
            calls++;
            return Task.FromResult(new ProviderRefreshResult<string>(current, true));
        }
        var scope = ProviderRefreshScope.Create();
        bool Current(string value) => value == current;
        Assert.Equal("ordinary", await scope.RunAsync("key", false, Fetch, CancellationToken.None, Current));
        current = "forced";
        Assert.Equal("forced", await scope.RunAsync("key", true, Fetch, CancellationToken.None, Current));
        Assert.Equal("forced", await scope.RunAsync("key", false, Fetch, CancellationToken.None, Current));
        current = "another-job";
        Assert.Equal("another-job", await scope.RunAsync("key", true, Fetch, CancellationToken.None, Current));
        Assert.Equal(4, calls);
    }

    [Fact]
    public async Task CancelledWaiterDoesNotCancelAnotherWaiterAndFailedFlightCanRetry()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken underlying = default;
        var calls = 0;
        async Task<ProviderRefreshResult<int>> Fetch(CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            underlying = token;
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return new(42, true);
        }
        var scope = ProviderRefreshScope.Create();
        using var cancelled = new CancellationTokenSource();
        var first = scope.RunAsync("key", true, Fetch, cancelled.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = scope.RunAsync("key", true, Fetch, CancellationToken.None);
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(underlying.IsCancellationRequested);
        release.TrySetResult();
        Assert.Equal(42, await second.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SharedFlightReflectsForegroundDemandAndDemotesWhenItLeaves()
    {
        var flights = new SingleFlightCoordinator();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<ProviderWorkPriority>? actualPriority = null;
        var calls = 0;
        async Task<int> Fetch(CancellationToken token)
        {
            Interlocked.Increment(ref calls);
            actualPriority = ProviderExecutionContext.CapturePriorityAccessor();
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            return 1;
        }
        Task<int> background;
        using (ProviderExecutionContext.Push(() => ProviderWorkPriority.Adjacent))
            background = flights.RunAsync("same", Fetch, CancellationToken.None);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ProviderWorkPriority.Adjacent, actualPriority!());
        using var foregroundCancellation = new CancellationTokenSource();
        Task<int> foreground;
        using (ProviderExecutionContext.Push(() => ProviderWorkPriority.Foreground))
            foreground = flights.RunAsync("same", Fetch, foregroundCancellation.Token);
        Assert.Equal(ProviderWorkPriority.Foreground, actualPriority!());
        foregroundCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => foreground);
        Assert.Equal(ProviderWorkPriority.Adjacent, actualPriority!());
        release.TrySetResult();
        Assert.Equal(1, await background.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, calls);
        Assert.Equal(ProviderWorkPriority.Foreground, ProviderExecutionContext.CapturePriorityAccessor()());
    }
}
