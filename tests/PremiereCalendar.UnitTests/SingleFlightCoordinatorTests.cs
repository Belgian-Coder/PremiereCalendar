using System.Reflection;
using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class SingleFlightCoordinatorTests
{
    [Fact]
    public void FlightCancelAfterDisposeDoesNotThrow()
    {
        var flightType = typeof(SingleFlightCoordinator).GetNestedType("Flight", BindingFlags.NonPublic)!;
        var flight = Activator.CreateInstance(
            flightType,
            (Func<CancellationToken, Task<object?>>)(_ => Task.FromResult<object?>(42)))!;
        var cancel = flightType.GetMethod("Cancel", BindingFlags.Instance | BindingFlags.Public)!;
        var dispose = flightType.GetMethod("Dispose", BindingFlags.Instance | BindingFlags.Public)!;

        dispose.Invoke(flight, null);

        var exception = Record.Exception(() => cancel.Invoke(flight, null));

        Assert.Null(exception);
    }

    [Fact]
    public void FlightRejectsNewWaitersAfterLastWaiterBeginsCancellation()
    {
        var flightType = typeof(SingleFlightCoordinator).GetNestedType("Flight", BindingFlags.NonPublic)!;
        var flight = Activator.CreateInstance(
            flightType,
            (Func<CancellationToken, Task<object?>>)(async token =>
            {
                await Task.Delay(TimeSpan.FromMinutes(5), token);
                return 42;
            }))!;
        var tryAddWaiter = flightType.GetMethod("TryAddWaiter", BindingFlags.Instance | BindingFlags.Public)!;
        var releaseWaiter = flightType.GetMethod("ReleaseWaiter", BindingFlags.Instance | BindingFlags.Public)!;
        var cancel = flightType.GetMethod("Cancel", BindingFlags.Instance | BindingFlags.Public)!;
        var dispose = flightType.GetMethod("Dispose", BindingFlags.Instance | BindingFlags.Public)!;

        try
        {
            Assert.True((bool)tryAddWaiter.Invoke(flight, null)!);
            Assert.True((bool)releaseWaiter.Invoke(flight, null)!);
            Assert.False((bool)tryAddWaiter.Invoke(flight, null)!);
        }
        finally
        {
            cancel.Invoke(flight, null);
            dispose.Invoke(flight, null);
        }
    }

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

    [Fact]
    public async Task RunAsync_FirstWaiterCancellationDoesNotCancelSharedWorkForOtherWaiters()
    {
        var coordinator = new SingleFlightCoordinator();
        using var firstCancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        CancellationToken factoryToken = default;

        async Task<int> Factory(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            factoryToken = cancellationToken;
            entered.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return 42;
        }

        var first = coordinator.RunAsync("same-key", Factory, firstCancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = coordinator.RunAsync("same-key", Factory, CancellationToken.None);

        firstCancellation.Cancel();
        release.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(42, await second);
        Assert.Equal(1, calls);
        Assert.False(factoryToken.IsCancellationRequested);
    }

    [Fact]
    public async Task RunAsync_CancelsSharedWorkWhenAllWaitersCancel()
    {
        var coordinator = new SingleFlightCoordinator();
        using var firstCancellation = new CancellationTokenSource();
        using var secondCancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var factoryCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        async Task<int> Factory(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
                return 42;
            }
            catch (OperationCanceledException)
            {
                factoryCanceled.TrySetResult();
                throw;
            }
        }

        var first = coordinator.RunAsync("same-key", Factory, firstCancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = coordinator.RunAsync("same-key", Factory, secondCancellation.Token);

        firstCancellation.Cancel();
        secondCancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        await factoryCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RunAsync_StartsFreshFlightWhenNewWaiterArrivesAfterAllPreviousWaitersCancel()
    {
        var coordinator = new SingleFlightCoordinator();
        using var firstCancellation = new CancellationTokenSource();
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstFactoryCanceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstFactory = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;

        async Task<int> Factory(CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref calls);
            if (call == 1)
            {
                firstEntered.TrySetResult();
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(5), cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    firstFactoryCanceled.TrySetResult();
                    await releaseFirstFactory.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    throw;
                }
            }

            secondEntered.TrySetResult();
            return 42;
        }

        var first = coordinator.RunAsync("same-key", Factory, firstCancellation.Token);
        await firstEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        firstCancellation.Cancel();
        await firstFactoryCanceled.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var second = coordinator.RunAsync("same-key", Factory, CancellationToken.None);
        try
        {
            await secondEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            releaseFirstFactory.TrySetResult();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(42, await second);
        Assert.Equal(2, calls);
    }
}
