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
        backend.SimulateHomeFailure = false; await shell.CheckHomeCommand.ExecuteAsync(); Assert.True(shell.HomeCheckReady); Assert.True(shell.NextSetupCommand.CanExecute(null));
        shell.DnsServer = "https://9.9.9.9/dns-query"; Assert.False(shell.HomeCheckReady); Assert.False(shell.NextSetupCommand.CanExecute(null));
        await shell.CheckHomeCommand.ExecuteAsync(); Assert.True(shell.HomeCheckReady);
        shell.SelectedHomeInterface = shell.SelectedNonHomeInterface; Assert.False(shell.HomeCheckReady); Assert.False(shell.NextSetupCommand.CanExecute(null));
        Assert.Equal(3, backend.HomeChecks); Assert.Equal(0, backend.Core.RestartCount);
    }
}
