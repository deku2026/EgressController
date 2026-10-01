using System.Net;
using EgressController.Core.Models;

namespace EgressController.Core.Tests;

public sealed class AdapterBindingCacheTests
{
    [Fact]
    public void Offline_bound_exit_keeps_the_same_configuration_and_recovery_only_changes_new_addresses()
    {
        var cache = new AdapterBindingCache();
        var direct = Adapter(Guid.NewGuid(), "ESIM", "192.0.2.10");
        var proxy = Adapter(Guid.NewGuid(), "Proxy", "198.51.100.10");
        var initial = cache.Resolve(Environment(direct, proxy));
        var offline = direct with { IsUp = false, Ipv4BindAddress = null, AddressState = AdapterAddressState.Offline };
        var retained = cache.Resolve(Environment(offline, proxy));
        Assert.Equal(initial.DefaultAdapter, retained.DefaultAdapter);
        Assert.Equal(initial.ProxyAdapter, retained.ProxyAdapter);
        Assert.Equal(initial.DefaultAdapter, cache.Resolve(Environment(direct, proxy)).DefaultAdapter);
        var changed = direct with { Ipv4BindAddress = IPAddress.Parse("192.0.2.11") };
        Assert.Equal(changed, cache.Resolve(Environment(changed, proxy)).DefaultAdapter);
    }

    [Fact]
    public void Startup_offline_and_explicit_replacement_never_borrow_the_other_nic()
    {
        var cache = new AdapterBindingCache();
        var direct = Adapter(Guid.NewGuid(), "ESIM", "192.0.2.10");
        var proxy = Adapter(Guid.NewGuid(), "Proxy", "198.51.100.10");
        var offline = direct with { IsUp = false, Ipv4BindAddress = null, AddressState = AdapterAddressState.Offline };
        Assert.False(cache.Resolve(Environment(offline, proxy)).DefaultAdapter.IsReady);
        cache.Resolve(Environment(direct, proxy));
        var replacement = offline with { AdapterId = Guid.NewGuid(), Alias = "New user selection" };
        Assert.Equal(replacement, cache.Resolve(Environment(replacement, proxy)).DefaultAdapter);
        Assert.Equal(offline, cache.Resolve(Environment(offline, proxy)).DefaultAdapter); // Deselected binding forgotten.
    }

    [Fact]
    public void A_different_nic_reusing_the_offline_alias_cannot_receive_the_cached_binding()
    {
        var cache = new AdapterBindingCache();
        var direct = Adapter(Guid.NewGuid(), "Ethernet", "192.0.2.10");
        var proxy = Adapter(Guid.NewGuid(), "Proxy", "198.51.100.10");
        cache.Resolve(Environment(direct, proxy));
        var offline = direct with { IsUp = false, Ipv4BindAddress = null, AddressState = AdapterAddressState.Offline };
        NetworkAdapterInfo replacement = new()
        {
            Identity = new(Guid.NewGuid(), "Ethernet"), Description = "different physical NIC", Luid = 9,
            IfIndex = 9, Ipv6IfIndex = 9, IsUp = true, Addresses = [IPAddress.Parse("192.0.2.10")],
            Gateways = [], DnsServers = [],
        };
        Assert.False(cache.Resolve(Environment(offline, proxy), [replacement]).DefaultAdapter.IsReady);
    }

    private static NetworkEnvironmentSnapshot Environment(AdapterSelection direct, AdapterSelection proxy)
        => new() { DefaultAdapter = direct, DnsAdapter = direct, ProxyAdapter = proxy };
    private static AdapterSelection Adapter(Guid id, string name, string ip) => new()
    {
        AdapterId = id, Alias = name, Luid = 1, IfIndex = 1, Ipv6IfIndex = 1, IsUp = true,
        Ipv4BindAddress = IPAddress.Parse(ip), AddressState = AdapterAddressState.Ipv4Only,
    };
}
