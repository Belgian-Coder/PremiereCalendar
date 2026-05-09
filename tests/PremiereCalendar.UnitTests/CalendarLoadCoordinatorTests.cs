using PremiereCalendar.Services;

namespace PremiereCalendar.UnitTests;

public sealed class CalendarLoadCoordinatorTests
{
    [Fact]
    public async Task TryBeginBackgroundLoadAsync_ReturnsNullWhileForegroundLoadIsActive()
    {
        var coordinator = new CalendarLoadCoordinator();

        using var foreground = coordinator.BeginForegroundLoad();
        using var background = await coordinator.TryBeginBackgroundLoadAsync(
            skipWhenForegroundActive: true,
            CancellationToken.None);

        Assert.True(coordinator.HasActiveForegroundLoad);
        Assert.Null(background);
    }

    [Fact]
    public async Task TryBeginBackgroundLoadAsync_AllowsOnlyOneBackgroundLoadAtATime()
    {
        var coordinator = new CalendarLoadCoordinator();

        using var first = await coordinator.TryBeginBackgroundLoadAsync(
            skipWhenForegroundActive: true,
            CancellationToken.None);
        using var second = await coordinator.TryBeginBackgroundLoadAsync(
            skipWhenForegroundActive: true,
            CancellationToken.None);

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public async Task BeginForegroundLoad_CancelsActiveBackgroundLoad()
    {
        var coordinator = new CalendarLoadCoordinator();

        using var background = await coordinator.TryBeginBackgroundLoadAsync(
            skipWhenForegroundActive: true,
            CancellationToken.None);

        Assert.NotNull(background);
        Assert.False(background.Token.IsCancellationRequested);

        using var foreground = coordinator.BeginForegroundLoad();

        Assert.True(background.Token.IsCancellationRequested);
    }
}
