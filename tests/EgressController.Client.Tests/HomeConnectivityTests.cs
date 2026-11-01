using EgressController.Desktop.Preview;
using EgressController.Desktop.ViewModels;
namespace EgressController.Client.Tests;
public sealed class HomeConnectivityTests
{
    [Fact]
    public async Task AttachedInterfacesCannotAdvanceUntilHome204PassesAndChangingHomeOrDnsInvalidatesProof()
    {
        PreviewEnvironment.Enter(); var backend = new FixtureClientBackend { SimulateHomeFailure = true };
        await using var shell = new ShellViewModel(backend); await shell.InitializeAsync(); shell.SetupStep = 0; shell.OnboardingVisible = true;
        Assert.NotNull(shell.SelectedHomeInterface); Assert.False(shell.NextSetupCommand.CanExecute(null));
        await shell.CheckHomeCommand.ExecuteAsync(); Assert.False(shell.HomeCheckReady); Assert.False(shell.NextSetupCommand.CanExecute(null));
        Assert.Contains("未通过", shell.HomeCheckStatus); Assert.Equal(0, backend.Core.RestartCount);
        Assert.True(shell.HomeCheckFailed);
        backend.SimulateHomeFailure = false; await shell.CheckHomeCommand.ExecuteAsync(); Assert.True(shell.HomeCheckReady); Assert.True(shell.NextSetupCommand.CanExecute(null));
        Assert.False(shell.HomeCheckFailed);
        shell.DnsServer = "https://9.9.9.9/dns-query"; Assert.False(shell.HomeCheckReady); Assert.False(shell.NextSetupCommand.CanExecute(null));
        await shell.CheckHomeCommand.ExecuteAsync(); Assert.True(shell.HomeCheckReady);
        shell.SelectedHomeInterface = shell.SelectedNonHomeInterface; Assert.False(shell.HomeCheckReady); Assert.False(shell.NextSetupCommand.CanExecute(null));
        Assert.Equal(3, backend.HomeChecks); Assert.Equal(0, backend.Core.RestartCount);
    }
    [Theory]
    [InlineData("interface.inactive", "网卡尚未激活")]
    [InlineData("interface.no-address", "网卡尚未获取可用 IP")]
    [InlineData("dns.failed", "解析失败")]
    [InlineData("network.unreachable", "无路由")]
    [InlineData("timeout", "超时")]
    public async Task FailedSetupShowsCauseAndChangingTheNetworkClearsAdvice(string code, string label)
    {
        PreviewEnvironment.Enter(); var backend = new FixtureClientBackend { SimulateHomeFailure = true, HomeFailureCode = code };
        await using var shell = new ShellViewModel(backend); await shell.InitializeAsync(); shell.SetupStep = 0;
        await shell.CheckHomeCommand.ExecuteAsync();
        Assert.True(shell.HomeCheckFailed); Assert.Contains(label, shell.HomeCheckStatus); Assert.False(shell.NextSetupCommand.CanExecute(null));
        shell.DnsServer = "https://9.9.9.9/dns-query";
        Assert.False(shell.HomeCheckFailed); Assert.False(shell.HomeCheckReady);
        await shell.CheckHomeCommand.ExecuteAsync(); Assert.True(shell.HomeCheckFailed);
        shell.SelectedHomeInterface = shell.SelectedNonHomeInterface;
        Assert.False(shell.HomeCheckFailed); Assert.False(shell.HomeCheckReady); Assert.Equal(0, backend.Core.RestartCount);
    }
    [Fact]
    public async Task RetryClearsOldAdviceAndCancellationDoesNotSuggestChangingSystemSettings()
    {
        PreviewEnvironment.Enter(); var backend = new FixtureClientBackend { SimulateHomeFailure = true };
        await using var shell = new ShellViewModel(backend); await shell.InitializeAsync(); shell.SetupStep = 0;
        await shell.CheckHomeCommand.ExecuteAsync(); Assert.True(shell.HomeCheckFailed);
        backend.HomeCheckGate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var retry = shell.CheckHomeCommand.ExecuteAsync();
        Assert.False(shell.HomeCheckFailed); Assert.False(shell.HomeCheckReady); Assert.False(shell.NextSetupCommand.CanExecute(null));
        shell.CancelOperationCommand.Execute(null); await retry;
        Assert.False(shell.HomeCheckFailed); Assert.False(shell.HomeCheckReady); Assert.Contains("已取消", shell.HomeCheckStatus);
        backend.HomeCheckGate = null; backend.HomeCheckError = new IOException("DNS query failed");
        await shell.CheckHomeCommand.ExecuteAsync(); Assert.True(shell.HomeCheckFailed); Assert.Contains("解析失败", shell.HomeCheckStatus);
        backend.HomeCheckError = null; backend.SimulateHomeFailure = false;
        await shell.CheckHomeCommand.ExecuteAsync(); Assert.False(shell.HomeCheckFailed); Assert.True(shell.HomeCheckReady); Assert.Equal(0, backend.Core.RestartCount);
    }
}
