using System.ComponentModel;
using EgressController.Core.Protection;
using Windows.Win32;
using Windows.Win32.NetworkManagement.IpHelper;
using Windows.Win32.Networking.WinSock;

namespace EgressController.Windows.Network;

public sealed unsafe class WindowsTunInspector
{
    private readonly WindowsNetworkAdapterService _adapters = new();

    public string? GetError()
    {
        try { return TunTakeover.GetError(_adapters.EnumerateAll(), ReadDefaultRoutes()); }
        catch (Exception exception) { return "无法确认 TUN 接管状态：" + exception.Message; }
    }

    private static IReadOnlyList<TunDefaultRoute> ReadDefaultRoutes()
    {
        MIB_IPFORWARD_TABLE2* table = null;
        uint error = (uint)PInvoke.GetIpForwardTable2(ADDRESS_FAMILY.AF_UNSPEC, &table);
        if (error != 0) throw new Win32Exception((int)error);
        try
        {
            var result = new List<TunDefaultRoute>();
            var rows = table->Table.AsSpan((int)table->NumEntries);
            foreach (ref readonly MIB_IPFORWARD_ROW2 row in rows)
            {
                if (row.DestinationPrefix.PrefixLength != 0 || row.ValidLifetime == 0) continue;
                var family = row.DestinationPrefix.Prefix.si_family;
                if (family is ADDRESS_FAMILY.AF_INET or ADDRESS_FAMILY.AF_INET6)
                    result.Add(new((int)row.InterfaceIndex, family == ADDRESS_FAMILY.AF_INET6));
            }
            return result;
        }
        finally { PInvoke.FreeMibTable(table); }
    }
}
