using EgressController.SingBox.Runtime;

namespace EgressController.SingBox.Tests;

public sealed class StartupDiagnosticsTests
{
    [Fact]
    public async Task Timeout_retains_last_exact_route_conflict_instead_of_generic_failure()
    {
        var error = await Assert.ThrowsAsync<SingBoxServiceException>(() => SingBoxStartupCheck.WaitAsync(
            _ => Task.FromResult<string?>("TUN 路由冲突：mock other VPN · 8000::/1"),
            TestContext.Current.CancellationToken, TimeSpan.FromMilliseconds(30)));
        Assert.Equal("tun.takeover", error.Code);
        Assert.Contains("8000::/1", error.Message);
    }

    [Fact]
    public async Task Hung_api_is_bounded_and_retains_previous_check_reason()
    {
        int checks = 0;
        var error = await Assert.ThrowsAsync<SingBoxServiceException>(() => SingBoxStartupCheck.WaitAsync(async token =>
        {
            if (++checks == 1) return "mock TUN route missing";
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return (string?)null;
        }, TestContext.Current.CancellationToken, TimeSpan.FromSeconds(1)));
        Assert.Contains("mock TUN route missing", error.Message);
        Assert.Equal(2, checks);
    }

    [Fact]
    public async Task Transient_failure_then_success_is_ready_and_external_cancellation_propagates()
    {
        int checks = 0;
        Assert.True(await SingBoxStartupCheck.WaitAsync(_ => Task.FromResult<string?>(
            ++checks == 1 ? "interface still creating" : null), TestContext.Current.CancellationToken));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SingBoxStartupCheck.WaitAsync(
            _ => Task.FromResult<string?>(null), cancellation.Token));
    }

    [Fact]
    public async Task Api_failure_keeps_inner_exception_for_diagnostics()
    {
        var failure = new IOException("mock localhost API refused");
        var error = await Assert.ThrowsAsync<SingBoxServiceException>(() => SingBoxStartupCheck.WaitAsync(
            _ => Task.FromException<string?>(failure), TestContext.Current.CancellationToken, TimeSpan.FromMilliseconds(30)));
        Assert.Same(failure, error.InnerException);
        Assert.Contains("mock localhost API refused", error.Message);
    }
}
