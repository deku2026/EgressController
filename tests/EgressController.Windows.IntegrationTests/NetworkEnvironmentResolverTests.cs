using System.Net;
using EgressController.Core.Models;
using EgressController.Core.Profile;
using EgressController.Windows.Network;

namespace EgressController.Windows.IntegrationTests;

public sealed class NetworkEnvironmentResolverTests
{
    private static readonly Guid PrimaryId = Guid.Parse("d3f02c20-56f8-4a18-9408-19a7f819bd01");
    private static readonly Guid EsimId = Guid.Parse("d3f02c20-56f8-4a18-9408-19a7f819bd02");
    private static EgressProfileDocument Profile() => new EgressProfileDocument()
        .AddAdapter(PrimaryId.ToString("D"), "Redmi").AddAdapter(EsimId.ToString("D"), "Ethernet");

    [Fact]
    public void Default_dns_follows_default_adapter_but_explicit_dns_does_not()
    {
        var adapters = new[] { Adapter(PrimaryId, "USB", true, ["192.0.2.10"]), Adapter(EsimId, "Ethernet", true, ["198.51.100.10"]) };
        var resolver = new NetworkEnvironmentResolver();
        Assert.Equal(PrimaryId, resolver.Resolve(Profile(), adapters).DnsAdapter.AdapterId);
        EgressProfileDocument changed = Profile().SetDefaultAdapter(EsimId.ToString("D"));
        Assert.Equal(EsimId, resolver.Resolve(changed, adapters).DnsAdapter.AdapterId);
        Assert.Equal(PrimaryId, resolver.Resolve(changed with { DnsAdapterId = PrimaryId.ToString("D") }, adapters).DnsAdapter.AdapterId);
    }

    [Fact]
    public void Missing_or_offline_selected_adapter_is_retained_without_falling_back()
    {
        var other = new[] { Adapter(EsimId, "Ethernet", true, ["198.51.100.10"]) };
        EgressProfileDocument profile = NetworkEnvironmentResolver.EnsureAutomaticDefaults(Profile(), other);
        Assert.Equal(PrimaryId.ToString("D"), profile.DefaultAdapterId);
        var state = new NetworkEnvironmentResolver().Resolve(profile, other);
        Assert.False(state.DefaultAdapter.IsReady);
        Assert.False(state.IsDnsReady);
        Assert.True(state.Find(EsimId.ToString("D"))!.IsReady);
    }

    [Fact]
    public void Renaming_interface_preserves_saved_guid_and_uses_current_binding()
    {
        var state = new NetworkEnvironmentResolver().Resolve(Profile(), [Adapter(PrimaryId, "New USB name", true, ["192.0.2.55", "2001:db8::1"])]);
        Assert.Equal("New USB name", state.DefaultAdapter.Alias);
        Assert.Equal(IPAddress.Parse("192.0.2.55"), state.DefaultAdapter.Ipv4BindAddress);
        Assert.True(state.DefaultAdapter.HasIpv6);
    }

    [Theory]
    [InlineData(6)] [InlineData(71)] [InlineData(243)]
    public void Physical_exit_selection_is_not_limited_to_wifi_or_cellular(uint interfaceType)
    {
        var adapter = Adapter(PrimaryId, "Redmi", true, ["192.0.2.10"], interfaceType);
        Assert.True(NetworkEnvironmentResolver.IsSelectable(adapter));
        Assert.Equal(PrimaryId.ToString("D"), NetworkEnvironmentResolver.EnsureAutomaticDefaults(new(), [adapter]).DefaultAdapterId);
    }

    [Fact]
    public void Loopback_and_own_tunnel_cannot_be_selected_as_exits()
    {
        Assert.False(NetworkEnvironmentResolver.IsSelectable(Adapter(PrimaryId, "loopback", true, ["127.0.0.1"])));
        Assert.False(NetworkEnvironmentResolver.IsSelectable(Adapter(PrimaryId, "sing-box", true, ["172.19.0.1"])));
    }

    [Fact]
    public void Completely_offline_machine_can_prepare_an_unavailable_environment()
    {
        var state = new NetworkEnvironmentResolver().Resolve(new(), []);
        Assert.False(state.DefaultAdapter.IsReady);
        Assert.False(state.IsDnsReady);
    }

    private static NetworkAdapterInfo Adapter(Guid id, string name, bool isUp, IReadOnlyList<string> addresses, uint interfaceType = 6)
        => new()
        {
            Identity = new NetworkAdapterIdentity(id, name),
            Description = name + " physical",
            Luid = (ulong)id.GetHashCode(),
            IfIndex = 10,
            Ipv6IfIndex = 10,
            IsUp = isUp,
            Addresses = addresses.Select(IPAddress.Parse).ToArray(),
            Gateways = [IPAddress.Parse("192.0.2.254")],
            DnsServers = [IPAddress.Parse("192.0.2.53")],
            InterfaceType = interfaceType,
        };
}
