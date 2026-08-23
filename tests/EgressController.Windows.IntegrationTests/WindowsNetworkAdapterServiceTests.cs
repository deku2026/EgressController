using EgressController.Windows.Network;

namespace EgressController.Windows.IntegrationTests;

public sealed class WindowsNetworkAdapterServiceTests
{
    [Fact]
    public void Selectable_adapter_enumeration_excludes_loopback_and_tunnel_interfaces()
    {
        IReadOnlyList<EgressController.Core.Models.NetworkAdapterInfo> adapters =
            new WindowsNetworkAdapterService().EnumerateSelectable();

        Assert.DoesNotContain(adapters, adapter => adapter.InterfaceType is 24 or 131);
        Assert.DoesNotContain(adapters, adapter =>
            $"{adapter.Identity.NameSnapshot} {adapter.Description}"
                .Contains("loopback", StringComparison.OrdinalIgnoreCase));
        Assert.All(adapters, adapter => Assert.NotEqual(Guid.Empty, adapter.Identity.Guid));
    }
}
