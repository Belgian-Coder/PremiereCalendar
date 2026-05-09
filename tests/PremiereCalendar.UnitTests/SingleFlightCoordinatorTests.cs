using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class SingleFlightCoordinatorTests
{
    [Fact]
    public async Task RunAsync_CoalescesConcurrentCallsForSameKey()
    {
        var coordinator = new SingleFlightCoordinator();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        async Task<int> Factory(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return 42;
        }

        var first = coordinator.RunAsync("same-key", Factory, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = coordinator.RunAsync("same-key", Factory, CancellationToken.None);

        release.SetResult();

        Assert.Equal(42, await first);
        Assert.Equal(42, await second);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RunAsync_RemovesFlightAfterCompletion()
    {
        var coordinator = new SingleFlightCoordinator();
        var calls = 0;

        Task<int> Factory(CancellationToken _)
        {
            return Task.FromResult(Interlocked.Increment(ref calls));
        }

        var first = await coordinator.RunAsync("repeat-key", Factory, CancellationToken.None);
        var second = await coordinator.RunAsync("repeat-key", Factory, CancellationToken.None);

        Assert.Equal(1, first);
        Assert.Equal(2, second);
        Assert.Equal(2, calls);
    }
}
