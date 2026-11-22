using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed partial class ProviderWorkSchedulerTests
{
    [Fact]
    public async Task PublicationGateReentersWhileActiveAndReacquiresForAnExpiredDetachedContext()
    {
        var directory = Path.Combine(_root, "reentrant-publication");
        var startDetached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var detachedEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task detached = Task.CompletedTask;
        var nested = false;
        await CalendarCachePublicationLock.RunAsync(directory, async token =>
        {
            await CalendarCachePublicationLock.RunAsync(directory, _ =>
            {
                nested = true;
                return Task.CompletedTask;
            }, token);
            detached = Task.Run(async () =>
            {
                await startDetached.Task;
                await CalendarCachePublicationLock.RunAsync(directory, _ =>
                {
                    detachedEntered.TrySetResult();
                    return Task.CompletedTask;
                }, CancellationToken.None);
            });
        }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(nested);
        var holderEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHolder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = CalendarCachePublicationLock.RunAsync(directory, async _ =>
        {
            holderEntered.TrySetResult();
            await releaseHolder.Task;
        }, CancellationToken.None);
        await holderEntered.Task;
        try
        {
            startDetached.TrySetResult();
            Assert.NotSame(detachedEntered.Task, await Task.WhenAny(detachedEntered.Task, Task.Delay(150, TestContext.Current.CancellationToken)));
        }
        finally { releaseHolder.TrySetResult(); }
        await holder.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await detached.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(detachedEntered.Task.IsCompletedSuccessfully);
    }
}
