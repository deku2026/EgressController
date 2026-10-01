using EgressController.App;
using EgressController.App.ViewModels;
using EgressController.Core.Profile;

namespace EgressController.Windows.IntegrationTests;

// Pure view-model checks: no AppController, Avalonia lifetime, sockets or TUN are started.
public sealed class RouteSelectionViewModelTests
{
    [Fact]
    public void Unchecked_row_defaults_to_default_adapter_and_saves_its_chosen_port_only_when_enabled()
    {
        var saves = new List<(bool Enabled, EgressRouteTarget Target)>();
        var vm = new RouteSelectionViewModel(Profile(), null, (enabled, target) =>
        {
            saves.Add((enabled, target));
            return Task.FromResult(ControllerOperationResult.Success());
        }, () => { });

        Assert.False(vm.IsSelected);
        Assert.Equal(EgressRouteTarget.DefaultAdapter, vm.SelectedRoute.Target);
        vm.SelectedRoute = vm.Options.Single(option => option.Target == EgressRouteTarget.ForPort(7891));
        Assert.Empty(saves);
        vm.IsSelected = true;
        Assert.Equal((true, EgressRouteTarget.ForPort(7891)), Assert.Single(saves));
        vm.IsSelected = false;
        Assert.False(saves[1].Enabled);
    }

    [Fact]
    public async Task Failed_route_change_restores_selection_and_reports_the_error()
    {
        var pending = new TaskCompletionSource<ControllerOperationResult>();
        var changed = new TaskCompletionSource();
        var vm = new RouteSelectionViewModel(Profile(), EgressRouteTarget.DefaultAdapter,
            (_, _) => pending.Task, () => changed.SetResult());

        vm.SelectedRoute = vm.Options.Single(option => option.Target == EgressRouteTarget.ForPort(7891));
        Assert.False(vm.CanEdit);
        vm.IsSelected = false; // Disabled controls cannot overwrite an in-flight choice.
        pending.SetResult(ControllerOperationResult.Failure("端口保存失败"));
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(vm.CanEdit);
        Assert.True(vm.IsSelected);
        Assert.Equal(EgressRouteTarget.DefaultAdapter, vm.SelectedRoute.Target);
        Assert.Equal("端口保存失败", vm.Status);
    }

    [Fact]
    public void Refresh_updates_default_label_without_saving_or_discarding_an_unchecked_choice()
    {
        int writes = 0;
        var vm = new RouteSelectionViewModel(Profile(), null, (_, _) =>
        {
            writes++;
            return Task.FromResult(ControllerOperationResult.Success());
        }, () => { });
        vm.SelectedRoute = vm.Options.Single(option => option.Target == EgressRouteTarget.Default);
        vm.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(vm.Options))
                vm.SelectedRoute = vm.Options[0]; // Simulate ComboBox resetting during ItemsSource replacement.
        };
        vm.Refresh(Profile().SetDefaultPort(7891), null);

        Assert.Equal(0, writes);
        Assert.False(vm.IsSelected);
        Assert.Equal(EgressRouteTarget.Default, vm.SelectedRoute.Target);
        Assert.Equal("默认端口 · 7891", vm.SelectedRoute.Label);
    }

    [Fact]
    public void Categories_have_independent_default_first_options_and_can_select_any_configured_adapter()
    {
        string a = "11111111-1111-1111-1111-111111111111", b = "22222222-2222-2222-2222-222222222222";
        var profile = Profile().AddAdapter(a, "Redmi").AddAdapter(b, "Ethernet");
        var vm = new RouteSelectionViewModel(profile, null, (_, _) => Task.FromResult(ControllerOperationResult.Success()), () => { });
        Assert.Equal("网卡", vm.SelectedKind);
        Assert.Equal(EgressRouteTarget.DefaultAdapter, vm.VisibleOptions[0].Target);
        Assert.All(vm.VisibleOptions, option => Assert.True(option.Target.IsAdapter));
        vm.SelectedRoute = vm.VisibleOptions.Single(option => option.Target.AdapterId == b);
        Assert.Equal(b, vm.SelectedRoute.Target.AdapterId);
        vm.SelectedKind = "端口";
        Assert.Equal(EgressRouteTarget.Default, vm.VisibleOptions[0].Target);
        Assert.All(vm.VisibleOptions, option => Assert.False(option.Target.IsAdapter));
        Assert.Equal("默认端口 · 7890", vm.SelectedRoute.Label);
    }

    [Fact]
    public void Protection_records_show_path_pid_reason_and_failure_without_claiming_success()
    {
        var row = new ProtectionEventViewModel(new(DateTimeOffset.UtcNow, "Browser", @"C:\Browser\browser.exe", 42, false, "CF 超时", "权限不足"));
        Assert.True(row.Failed);
        Assert.Contains("42", row.Summary);
        Assert.Contains("终止失败", row.Summary);
        Assert.Equal(@"C:\Browser\browser.exe", row.Path);
        Assert.Contains("CF 超时", row.Detail);
        Assert.Contains("权限不足", row.Detail);
    }

    private static EgressProfileDocument Profile() => new() { UpstreamPorts = [7890, 7891] };
}
