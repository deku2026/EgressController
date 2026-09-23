using EgressController.App;
using EgressController.App.ViewModels;
using EgressController.Core.Profile;

namespace EgressController.Windows.IntegrationTests;

// Pure view-model checks: no AppController, Avalonia lifetime, sockets or TUN are started.
public sealed class RouteSelectionViewModelTests
{
    [Fact]
    public void Unchecked_row_defaults_to_esim_and_saves_its_chosen_port_only_when_enabled()
    {
        var saves = new List<(bool Enabled, EgressRouteTarget Target)>();
        var vm = new RouteSelectionViewModel(Profile(), null, (enabled, target) =>
        {
            saves.Add((enabled, target));
            return Task.FromResult(ControllerOperationResult.Success());
        }, () => { });

        Assert.False(vm.IsSelected);
        Assert.Equal(EgressRouteTarget.Esim, vm.SelectedRoute.Target);
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
        var vm = new RouteSelectionViewModel(Profile(), EgressRouteTarget.Esim,
            (_, _) => pending.Task, () => changed.SetResult());

        vm.SelectedRoute = vm.Options.Single(option => option.Target == EgressRouteTarget.ForPort(7891));
        Assert.False(vm.CanEdit);
        vm.IsSelected = false; // Disabled controls cannot overwrite an in-flight choice.
        pending.SetResult(ControllerOperationResult.Failure("端口保存失败"));
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.True(vm.CanEdit);
        Assert.True(vm.IsSelected);
        Assert.Equal(EgressRouteTarget.Esim, vm.SelectedRoute.Target);
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
        Assert.Equal("默认 · 7891", vm.SelectedRoute.Label);
    }

    private static EgressProfileDocument Profile() => new() { UpstreamPorts = [7890, 7891] };
}
