using System.Net;
using EgressController.Core.Models;
using EgressController.Core.Profile;
using EgressController.Windows.Network;

namespace EgressController.Windows.IntegrationTests;

public sealed class NetworkEnvironmentResolverTests
{
    private static readonly Guid PrimaryId = Guid.Parse("d3f02c20-56f8-4a18-9408-19a7f819bd01");
    private static readonly Guid EsimId = Guid.Parse("d3f02c20-56f8-4a18-9408-19a7f819bd02");

    [Fact]
    public void Resolves_stable_ids_to_current_alias_and_address_family_bindings()
    {
        var adapters = new[]
        {
            Adapter(PrimaryId, "PRIMARY-WIFI", true, ["192.0.2.10", "2001:db8::10"]),
            Adapter(EsimId, "ESIM-WIFI", true, ["198.51.100.10"]),
        };
        var profile = new EgressProfileDocument
        {
            PrimaryAdapterId = PrimaryId.ToString(),
            EsimAdapterId = EsimId.ToString(),
        };

        NetworkEnvironmentSnapshot snapshot = new NetworkEnvironmentResolver().Resolve(profile, adapters);

        Assert.Equal("PRIMARY-WIFI", snapshot.Primary.Alias);
        Assert.Equal(IPAddress.Parse("192.0.2.10"), snapshot.Primary.Ipv4BindAddress);
        Assert.Equal(IPAddress.Parse("2001:db8::10"), snapshot.Primary.Ipv6BindAddress);
        Assert.Equal(AdapterAddressState.DualStack, snapshot.Primary.AddressState);
        Assert.Equal(AdapterAddressState.Ipv4Only, snapshot.Esim.AddressState);
    }

    [Fact]
    public void Renaming_an_adapter_does_not_break_stable_guid_selection()
    {
        var adapters = new[]
        {
            Adapter(PrimaryId, "Renamed primary", true, ["192.0.2.10"]),
            Adapter(EsimId, "Renamed esim", true, ["198.51.100.10"]),
        };

        NetworkEnvironmentSnapshot snapshot = new NetworkEnvironmentResolver().Resolve(
            new EgressProfileDocument
            {
                PrimaryAdapterId = PrimaryId.ToString(),
                EsimAdapterId = EsimId.ToString(),
            },
            adapters);

        Assert.Equal(PrimaryId, snapshot.Primary.AdapterId);
        Assert.Equal("Renamed primary", snapshot.Primary.Alias);
    }

    [Fact]
    public void Same_adapter_and_missing_adapter_are_precise_failures()
    {
        var same = new EgressProfileDocument
        {
            PrimaryAdapterId = PrimaryId.ToString(),
            EsimAdapterId = PrimaryId.ToString(),
        };
        var resolver = new NetworkEnvironmentResolver();

        NetworkEnvironmentException sameError = Assert.Throws<NetworkEnvironmentException>(
            () => resolver.Resolve(same, [Adapter(PrimaryId, "primary", true, ["192.0.2.1"])]));
        Assert.Equal("adapter.same", sameError.Code);

        NetworkEnvironmentException missingError = Assert.Throws<NetworkEnvironmentException>(
            () => resolver.Resolve(
                new EgressProfileDocument
                {
                    PrimaryAdapterId = PrimaryId.ToString(),
                    EsimAdapterId = EsimId.ToString(),
                },
                [Adapter(PrimaryId, "primary", true, ["192.0.2.1"])]));
        Assert.Equal("eSIM 网卡.missing", missingError.Code);
    }

    [Fact]
    public void Offline_and_single_stack_statuses_are_not_collapsed_into_global_failure()
    {
        var primary = Adapter(PrimaryId, "primary", true, ["192.0.2.1"]);
        var esim = Adapter(EsimId, "esim", false, ["192.0.2.2"]);

        NetworkEnvironmentSnapshot snapshot = new NetworkEnvironmentResolver().Resolve(
            new EgressProfileDocument
            {
                PrimaryAdapterId = PrimaryId.ToString(),
                EsimAdapterId = EsimId.ToString(),
            },
            [primary, esim]);

        Assert.Equal(AdapterAddressState.Ipv4Only, snapshot.Primary.AddressState);
        Assert.Equal(AdapterAddressState.Offline, snapshot.Esim.AddressState);
        Assert.False(snapshot.IsDualStack);
    }

    private static NetworkAdapterInfo Adapter(Guid id, string name, bool isUp, IReadOnlyList<string> addresses)
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
            InterfaceType = 6,
        };
}
