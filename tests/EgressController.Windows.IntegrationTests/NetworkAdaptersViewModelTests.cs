using System.Net;
using EgressController.App;
using EgressController.App.ViewModels;
using EgressController.Core.Models;
using EgressController.Core.Profile;
using EgressController.Windows.Process;

namespace EgressController.Windows.IntegrationTests;

public sealed class NetworkAdaptersViewModelTests
{
    private const string A = "11111111-1111-1111-1111-111111111111";
    private const string B = "22222222-2222-2222-2222-222222222222";
    private const string C = "33333333-3333-3333-3333-333333333333";
    private const string D = "44444444-4444-4444-4444-444444444444";

    [Fact]
    public void Default_list_is_short_keeps_saved_offline_card_and_never_auto_selects()
    {
        var fixture = new Fixture
        {
            Profile = new EgressProfileDocument().SetAdapterRoles(A, B),
            Adapters = [Adapter(A, "Redmi"), Adapter(B, "Ethernet", false), Adapter(C, "vEthernet (WSL)"), Adapter(D, "unused", false)],
        };
        var vm = fixture.Create();
        Assert.Equal(2, vm.Available.Count);
        Assert.Equal(B, vm.SelectedProxy!.Id);
        Assert.Contains("已断开", vm.SelectedProxy.Display);
        vm.ShowAll = true;
        Assert.Equal(4, vm.Available.Count);
        vm.ShowAll = false;
        fixture.Adapters = [Adapter(A, "Redmi")];
        vm.Refresh();
        Assert.Equal(B, vm.SelectedProxy!.Id);
        Assert.Contains("未找到", vm.SelectedProxy.Display);
        Assert.Equal(0, fixture.Writes);
        var fresh = new Fixture { Adapters = fixture.Adapters }.Create();
        Assert.Null(fresh.SelectedDirect); Assert.Null(fresh.SelectedProxy);
    }

    [Fact]
    public async Task Two_role_save_is_explicit_atomic_and_dns_follows_direct_role()
    {
        var fixture = new Fixture { Profile = new EgressProfileDocument().SetAdapterRoles(A, B) with
        { DnsAdapterId = B, Domains = [new() { Name = "example.com", Target = EgressRouteTarget.ForAdapter(A) }] } };
        var vm = fixture.Create();
        vm.SwapCommand.Execute(null);
        vm.Refresh(); // Polling must keep the unsaved choices.
        Assert.Equal(B, vm.SelectedDirect!.Id);
        Assert.Equal(A, vm.SelectedProxy!.Id);
        Assert.Equal(0, fixture.Writes);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(1, fixture.Writes);
        Assert.Equal(B, fixture.Profile.DefaultAdapterId);
        Assert.Equal(A, fixture.Profile.ProxyAdapterId);
        Assert.Null(fixture.Profile.DnsAdapterId);
        Assert.Equal(B, fixture.Profile.EffectiveDnsAdapterId);
        Assert.Equal(B, Assert.Single(fixture.Profile.Domains).Target.AdapterId);
        Assert.Equal(7890, fixture.Profile.UpstreamPort);
    }

    [Fact]
    public async Task Duplicate_or_incomplete_selection_is_not_saved_and_failed_save_can_be_retried()
    {
        var fixture = new Fixture();
        var vm = fixture.Create();
        vm.SelectedDirect = vm.Available.Single(option => option.Id == A);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(0, fixture.Writes);
        vm.SelectedProxy = vm.SelectedDirect;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(0, fixture.Writes);
        Assert.Contains("同一张", vm.Status);
        vm.SelectedProxy = vm.Available.Single(option => option.Id == B);
        fixture.Failure = "mock apply failure";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal("mock apply failure", vm.Status);
        Assert.Null(fixture.Profile.DefaultAdapterId);
        Assert.Equal(A, vm.SelectedDirect!.Id);
        fixture.Failure = null;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(A, fixture.Profile.DefaultAdapterId);
        Assert.Equal(B, fixture.Profile.ProxyAdapterId);
    }

    [Fact]
    public void Cancel_restores_roles_without_changing_dns_or_ports()
    {
        var fixture = new Fixture { Profile = new EgressProfileDocument().SetAdapterRoles(A, B) };
        var vm = fixture.Create();
        vm.SelectedDirect = vm.Available.Single(option => option.Id == C);
        vm.CancelCommand.Execute(null);
        Assert.Equal(A, vm.SelectedDirect!.Id);
        Assert.Equal(A, fixture.Profile.EffectiveDnsAdapterId);
        Assert.Equal(0, fixture.Writes);
    }

    [Fact]
    public void Binding_row_refreshes_pid_path_and_errors_independently_of_profile_changes()
    {
        var vm = new UpstreamPortEntryViewModel(new(), 7890, () => Task.CompletedTask, () => Task.CompletedTask);
        vm.UpdateBinding(new(7890, [new(123, @"C:\Proxy\mihomo.exe")], null), "Redmi");
        Assert.Contains("123", vm.BindingSummary);
        Assert.Contains("Proxy-代理", vm.BindingSummary);
        Assert.Contains("Redmi", vm.BindingSummary);
        Assert.Equal(@"C:\Proxy\mihomo.exe", vm.OwnerPath);
        vm.UpdateBinding(new(7890, [], "端口未监听"), "Redmi");
        Assert.Equal("端口未监听", vm.BindingSummary);
        Assert.Empty(vm.OwnerPath);
        vm.UpdateBinding(new(7890, [new(456, @"C:\Proxy\mihomo.exe")], null), "Redmi");
        Assert.Contains("456", vm.BindingSummary);
    }

    private sealed class Fixture
    {
        public EgressProfileDocument Profile = new();
        public IReadOnlyList<NetworkAdapterInfo> Adapters = [Adapter(A, "Ethernet"), Adapter(B, "Redmi"), Adapter(C, "iPhone USB")];
        public int Writes;
        public string? Failure;
        public NetworkAdaptersViewModel Create() => new(() => Profile, () => Adapters, (direct, proxy) =>
        {
            Writes++;
            if (Failure is not null) return Task.FromResult(ControllerOperationResult.Failure(Failure));
            Profile = Profile.SetAdapterRoles(direct, proxy);
            return Task.FromResult(ControllerOperationResult.Success());
        });
    }

    private static NetworkAdapterInfo Adapter(string id, string name, bool up = true) => new()
    {
        Identity = new(Guid.Parse(id), name), Description = name, IsUp = up, InterfaceType = 6,
        Luid = 1, IfIndex = 1, Ipv6IfIndex = 1, Addresses = [IPAddress.Parse("192.0.2.2")], Gateways = [], DnsServers = [],
    };
}
