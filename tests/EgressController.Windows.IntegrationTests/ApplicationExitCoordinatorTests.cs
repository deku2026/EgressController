using Avalonia.Controls;
using EgressController.App;

namespace EgressController.Windows.IntegrationTests;

public sealed class ApplicationExitCoordinatorTests
{
    [Theory]
    [InlineData(WindowCloseReason.WindowClosing, true)]
    [InlineData(WindowCloseReason.Undefined, true)]
    [InlineData(WindowCloseReason.ApplicationShutdown, false)]
    [InlineData(WindowCloseReason.OSShutdown, false)]
    [InlineData(WindowCloseReason.OwnerWindowClosing, false)]
    public void Ordinary_window_close_hides_to_tray_but_shutdown_reasons_do_not(
        WindowCloseReason reason,
        bool expected)
    {
        Assert.Equal(expected, ApplicationClosePolicy.ShouldHideToTray(exitRequested: false, reason));
        Assert.False(ApplicationClosePolicy.ShouldHideToTray(exitRequested: true, reason));
    }

    [Fact]
    public async Task Tray_exit_stops_tun_before_allowing_window_close_and_shutdown()
    {
        var order = new List<string>();
        var coordinator = new ApplicationExitCoordinator();

        await coordinator.ExitAsync(
            async () =>
            {
                order.Add("stop-start");
                await Task.Yield();
                order.Add("stop-finished");
            },
            () => order.Add("allow-close"),
            () => order.Add("shutdown"));

        Assert.Equal(["stop-start", "stop-finished", "allow-close", "shutdown"], order);
    }

    [Fact]
    public async Task Repeated_tray_exit_requests_only_run_cleanup_once()
    {
        var stopStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseStop = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var coordinator = new ApplicationExitCoordinator();
        int stopCount = 0;
        int closeCount = 0;
        int shutdownCount = 0;

        Task first = coordinator.ExitAsync(
            async () =>
            {
                Interlocked.Increment(ref stopCount);
                stopStarted.SetResult();
                await releaseStop.Task;
            },
            () => Interlocked.Increment(ref closeCount),
            () => Interlocked.Increment(ref shutdownCount));

        await stopStarted.Task;
        Task second = coordinator.ExitAsync(
            () => Task.CompletedTask,
            () => Interlocked.Increment(ref closeCount),
            () => Interlocked.Increment(ref shutdownCount));
        await second;
        releaseStop.SetResult();
        await first;

        Assert.Equal(1, stopCount);
        Assert.Equal(1, closeCount);
        Assert.Equal(1, shutdownCount);
    }

    [Fact]
    public async Task Stop_failure_still_allows_close_and_shutdown()
    {
        var order = new List<string>();
        var coordinator = new ApplicationExitCoordinator();

        await coordinator.ExitAsync(
            () => throw new InvalidOperationException("stop failed"),
            () => order.Add("allow-close"),
            () => order.Add("shutdown"));

        Assert.Equal(["allow-close", "shutdown"], order);
    }

    [Fact]
    public async Task Allow_close_failure_still_requests_shutdown()
    {
        var coordinator = new ApplicationExitCoordinator();
        int shutdownCount = 0;

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.ExitAsync(
            () => Task.CompletedTask,
            () => throw new InvalidOperationException("allow close failed"),
            () => Interlocked.Increment(ref shutdownCount)));

        Assert.Equal(1, shutdownCount);
    }
}
