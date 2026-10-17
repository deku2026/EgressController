using System.ComponentModel;
using System.Net;
using EgressController.Core.Protection;
using Windows.Win32;
using Windows.Win32.NetworkManagement.IpHelper;
using Windows.Win32.Networking.WinSock;

namespace EgressController.Windows.Network;

public sealed unsafe class WindowsTunInspector
{
    private readonly WindowsNetworkAdapterService _adapters = new();
    private Guid? _expectedInterface;
    private readonly object _gate = new();
    public void Reset() { lock (_gate) _expectedInterface = null; }
    public string? GetStartError(bool allowManagedInterface)
    {
        try { return TunTakeover.GetStartError(_adapters.EnumerateAll(), ReadRoutes(), allowManagedInterface); }
        catch (Exception exception) { return "无法检查 TUN 冲突：" + exception.Message; }
    }

    public string? GetError()
    {
        try
        {
            lock (_gate)
            {
                var adapters = _adapters.EnumerateAll();
                string? error = TunTakeover.GetError(adapters, ReadRoutes(), _expectedInterface);
                if (error is null) _expectedInterface = adapters.Single(adapter =>
                    adapter.Identity.NameSnapshot.Equals(TunTakeover.InterfaceName, StringComparison.OrdinalIgnoreCase)).Identity.Guid;
                return error;
            }
        }
        catch (Exception exception) { return "无法确认 TUN 接管状态：" + exception.Message; }
    }

    private static IReadOnlyList<TunDefaultRoute> ReadRoutes()
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
                if (row.ValidLifetime == 0) continue;
                var family = row.DestinationPrefix.Prefix.si_family;
                if (family is ADDRESS_FAMILY.AF_INET or ADDRESS_FAMILY.AF_INET6)
                {
                    SOCKADDR_INET prefix = row.DestinationPrefix.Prefix;
                    byte* bytes = (byte*)&prefix;
                    bool ipv6 = family == ADDRESS_FAMILY.AF_INET6;
                    var address = new IPAddress(new ReadOnlySpan<byte>(bytes + (ipv6 ? 8 : 4), ipv6 ? 16 : 4));
                    SOCKADDR_INET hop = row.NextHop;
                    byte* hopBytes = (byte*)&hop;
                    var nextHop = new IPAddress(new ReadOnlySpan<byte>(hopBytes + (ipv6 ? 8 : 4), ipv6 ? 16 : 4));
                    result.Add(new((int)row.InterfaceIndex, ipv6, address, row.DestinationPrefix.PrefixLength, row.Metric, nextHop));
                }
            }
            return result;
        }
        finally { PInvoke.FreeMibTable(table); }
    }
}
