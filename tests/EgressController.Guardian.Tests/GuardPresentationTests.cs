namespace EgressController.Guardian.Tests;

public sealed class GuardPresentationTests
{
    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task Red_badge_follows_only_guard_verdict(bool protecting, bool red)
    {
        var client = new Client { Snapshot = Snapshot(protecting) };
        var vm = new GuardManagementViewModel(client);
        await vm.RefreshAsync();
        Assert.Equal(red, vm.Red); Assert.False(vm.Disabled);
        Assert.Equal("mock guard reason", vm.Summary);
        Assert.Empty(client.Actions); // Refreshing the checkbox never changes startup.
    }

    [Fact]
    public async Task Manual_stop_is_gray_but_unknown_communication_is_red()
    {
        var client = new Client { Snapshot = Snapshot(true) with
            { Service = Service with { State = GuardServiceState.Stopped }, UserStopped = true, Guard = null } };
        var vm = new GuardManagementViewModel(client);
        await vm.RefreshAsync();
        Assert.True(vm.Disabled); Assert.False(vm.Red);
        client.Snapshot = Snapshot(false) with { Error = "mock pipe lost" };
        await vm.RefreshAsync();
        Assert.False(vm.Disabled); Assert.True(vm.Red);
        Assert.Contains("mock pipe lost", vm.Details);
    }

    [Fact]
    public async Task All_service_controls_forward_exact_actions_and_display_results()
    {
        var client = new Client(); var vm = new GuardManagementViewModel(client);
        await vm.InstallCommand.ExecuteAsync(null); await vm.StartCommand.ExecuteAsync(null);
        await vm.StopCommand.ExecuteAsync(null); await vm.ForceStopCommand.ExecuteAsync(null);
        await vm.StopAndDisableCommand.ExecuteAsync(null); await vm.UninstallCommand.ExecuteAsync(null);
        Assert.Equal([GuardAction.Install, GuardAction.Start, GuardAction.Stop, GuardAction.ForceStop,
            GuardAction.StopAndDisable, GuardAction.Uninstall], client.Actions);
        await vm.ClearHistoryCommand.ExecuteAsync(null);
        Assert.Equal(1, client.Clears); Assert.True(vm.CanEdit);
    }

    [Fact]
    public async Task Failed_administrator_operation_is_visible_and_does_not_claim_completion()
    {
        var client = new Client { Throw = true }; var vm = new GuardManagementViewModel(client);
        await vm.StopCommand.ExecuteAsync(null);
        Assert.Contains("mock UAC cancelled", vm.Message);
        Assert.True(vm.CanEdit);
    }

    [Fact]
    public async Task Startup_checkbox_has_no_implicit_stop_or_start_action()
    {
        var client = new Client(); var vm = new GuardManagementViewModel(client);
        await vm.RefreshAsync();
        vm.AutoStart = false;
        Assert.Equal(GuardAction.DisableAutoStart, Assert.Single(client.Actions));
    }

    private static GuardServiceInfo Service => new("guard", GuardServiceState.Running, true, 7, @"C:\mock\guard.exe", "LocalSystem");
    private static GuardPanelSnapshot Snapshot(bool protecting) => new(Service,
        new(protecting, "mock guard reason", 1, 0, Fixture.Policy.Revision, []), false, null);
    private sealed class Client : IGuardManagementClient
    {
        public GuardPanelSnapshot Snapshot = GuardPresentationTests.Snapshot(true);
        public List<GuardAction> Actions = [];
        public int Clears;
        public bool Throw = false;
        public Task<GuardPanelSnapshot> ReadAsync(CancellationToken token) => Task.FromResult(Snapshot);
        public Task ExecuteAsync(GuardAction action, CancellationToken token)
        {
            if (Throw) throw new InvalidOperationException("mock UAC cancelled");
            Actions.Add(action); return Task.CompletedTask;
        }
        public Task ClearHistoryAsync(CancellationToken token) { Clears++; return Task.CompletedTask; }
    }
}
