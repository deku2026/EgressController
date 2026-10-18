namespace EgressController.Guardian.Tests;

public sealed class GuardPresentationTests
{
    [Fact]
    public async Task Read_error_overrides_a_stale_healthy_reply_in_text_and_icon()
    {
        var client = new Client { Snapshot = Snapshot(false) };
        var vm = new GuardManagementViewModel(client); await vm.RefreshAsync();
        Assert.Equal(GuardVisualState.Healthy, vm.VisualState);
        client.Snapshot = Snapshot(false) with { Error = "mock communication unavailable" };
        await vm.RefreshAsync();
        Assert.Equal(GuardVisualState.Unavailable, vm.VisualState);
        Assert.False(vm.Healthy); Assert.True(vm.Red);
        Assert.Contains("mock communication unavailable", vm.Summary);
        Assert.DoesNotContain("mock guard reason", vm.DisplayReason);
        client.Snapshot = Snapshot(true); await vm.RefreshAsync();
        Assert.Equal(GuardVisualState.Protecting, vm.VisualState);
        client.Snapshot = Snapshot(false); await vm.RefreshAsync();
        Assert.Equal(GuardVisualState.Healthy, vm.VisualState);
        Assert.NotEmpty(vm.LastFailure); // Historical errors do not keep the current icon red.
    }

    [Fact]
    public async Task Tray_log_write_failure_is_visible_alongside_original_operation_error()
    {
        var log = new EgressController.Diagnostics.DiagnosticJournal(Path.GetFullPath("mock-tray.log"),
            _ => throw new IOException("mock tray log denied"));
        var vm = new GuardManagementViewModel(new Client { Throw = true }, log);
        await vm.StopCommand.ExecuteAsync(null);
        Assert.Contains("mock UAC cancelled", vm.LastFailure);
        Assert.Contains("mock tray log denied", vm.LogFailure);
        Assert.Contains(log.LogPath, vm.LogFailure);
    }

    [Fact]
    public async Task Startup_click_during_refresh_is_applied_and_late_snapshot_cannot_undo_it()
    {
        var response = new TaskCompletionSource<GuardPanelSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        var client = new Client { PendingRead = response.Task };
        var vm = new GuardManagementViewModel(client);
        Task refresh = vm.RefreshAsync();
        vm.AutoStart = false;
        Assert.Equal(GuardAction.DisableAutoStart, Assert.Single(client.Actions));
        response.SetResult(Snapshot(false)); // Older response still has AutoStart = true.
        await refresh;
        Assert.False(vm.AutoStart);
    }
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

    [Fact]
    public async Task Failed_service_operation_is_logged_and_not_erased_by_later_success()
    {
        var entries = new List<EgressController.Diagnostics.CoreLogEntry>();
        var journal = new EgressController.Diagnostics.DiagnosticJournal(Path.GetFullPath("mock-guard.log"), entries.Add);
        var client = new Client { Throw = true };
        var vm = new GuardManagementViewModel(client, journal, @"C:\mock\logs");
        await vm.StopCommand.ExecuteAsync(null);
        Assert.Contains("mock UAC cancelled", vm.LastFailure);
        client.Throw = false;
        await vm.StartCommand.ExecuteAsync(null);
        await vm.RefreshAsync();
        Assert.Contains("mock UAC cancelled", vm.LastFailure);
        Assert.Contains(entries, entry => entry.Level == "error" && entry.Message.Contains("mock UAC cancelled"));
        Assert.Equal(@"C:\mock\logs", vm.LogDirectory);
    }

    [Fact]
    public async Task Communication_failure_is_recorded_even_when_read_returns_without_throwing()
    {
        var journal = new EgressController.Diagnostics.DiagnosticJournal(Path.GetFullPath("mock-guard.log"), _ => { });
        var client = new Client { Snapshot = Snapshot(false) with { Error = "mock pipe disconnected" } };
        var vm = new GuardManagementViewModel(client, journal);
        await vm.RefreshAsync();
        Assert.Contains("mock pipe disconnected", vm.LastFailure);
        Assert.Contains(journal.Snapshot(), issue => issue.Message.Contains("mock pipe disconnected"));
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
        public Task<GuardPanelSnapshot>? PendingRead = null;
        public Task<GuardPanelSnapshot> ReadAsync(CancellationToken token) => PendingRead ?? Task.FromResult(Snapshot);
        public Task ExecuteAsync(GuardAction action, CancellationToken token)
        {
            if (Throw) throw new InvalidOperationException("mock UAC cancelled");
            Actions.Add(action); return Task.CompletedTask;
        }
        public Task ClearHistoryAsync(CancellationToken token) { Clears++; return Task.CompletedTask; }
    }
}
