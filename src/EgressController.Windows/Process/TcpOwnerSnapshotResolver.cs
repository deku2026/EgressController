using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using EgressController.Core.Contracts;
using Windows.Win32;
using Windows.Win32.NetworkManagement.IpHelper;
using Windows.Win32.Networking.WinSock;

namespace EgressController.Windows.Process;

/// <summary>
/// Resolves an accepted proxy connection's owning PID via GetExtendedTcpTable (OWNER_PID_ALL).
/// Each call takes a fresh snapshot; there is deliberately NO endpoint→PID TTL cache (plan §1.4).
/// The client-side row has local = client's own endpoint and remote = the proxy's listener.
/// The server-side row has the opposite orientation and belongs to the Router process; it must
/// not be accepted, otherwise a loopback connection can be attributed to the controller itself.
/// Non-resolvable (or PID race) → null → the caller treats the source as not-managed.
/// </summary>
public sealed unsafe class TcpOwnerSnapshotResolver : IConnectionOwnerResolver
{
    public uint? ResolveOwner(IPEndPoint clientLocal, IPEndPoint listenerLocal, CancellationToken cancellationToken)
    {
        // A loopback proxy accept may surface as IPv4-mapped IPv6 ([::ffff:127.0.0.1]) even though
        // the row lives in the IPv4 table; normalize so the IPv4 matcher sees a plain 127.0.0.1.
        clientLocal = Normalize(clientLocal);
        listenerLocal = Normalize(listenerLocal);

        uint? pid = LookupV4(clientLocal, listenerLocal, cancellationToken);
        return pid ?? LookupV6(clientLocal, listenerLocal, cancellationToken);
    }

    private static IPEndPoint Normalize(IPEndPoint ep)
        => ep.Address.IsIPv4MappedToIPv6
            ? new IPEndPoint(ep.Address.MapToIPv4(), ep.Port)
            : ep;

    private uint? LookupV4(IPEndPoint clientLocal, IPEndPoint listenerLocal, CancellationToken ct)
    {
        uint size = 0;
        _ = PInvoke.GetExtendedTcpTable(null, &size, bOrder: false, (uint)ADDRESS_FAMILY.AF_INET, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL, Reserved: 0);
        if (size == 0)
            return null;
        size += 128 * 1024;

        byte* buffer = (byte*)NativeMemory.Alloc(size);
        try
        {
            uint rc = PInvoke.GetExtendedTcpTable(buffer, &size, bOrder: false, (uint)ADDRESS_FAMILY.AF_INET, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL, Reserved: 0);
            if (rc != 0)
                return null;

            var table = (MIB_TCPTABLE_OWNER_PID*)buffer;
            int count = (int)table->dwNumEntries;
            for (int i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var row = (MIB_TCPROW_OWNER_PID*)((byte*)&table->table + i * sizeof(MIB_TCPROW_OWNER_PID));
                if (MatchV4(*row, clientLocal, listenerLocal))
                    return row->dwOwningPid;
            }
            return null;
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    private uint? LookupV6(IPEndPoint clientLocal, IPEndPoint listenerLocal, CancellationToken ct)
    {
        uint size = 0;
        _ = PInvoke.GetExtendedTcpTable(null, &size, bOrder: false, (uint)ADDRESS_FAMILY.AF_INET6, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL, Reserved: 0);
        if (size == 0)
            return null;
        size += 128 * 1024;

        byte* buffer = (byte*)NativeMemory.Alloc(size);
        try
        {
            uint rc = PInvoke.GetExtendedTcpTable(buffer, &size, bOrder: false, (uint)ADDRESS_FAMILY.AF_INET6, TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL, Reserved: 0);
            if (rc != 0)
                return null;

            var table = (MIB_TCP6TABLE_OWNER_PID*)buffer;
            int count = (int)table->dwNumEntries;
            for (int i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var row = (MIB_TCP6ROW_OWNER_PID*)((byte*)&table->table + i * sizeof(MIB_TCP6ROW_OWNER_PID));
                if (MatchV6(*row, clientLocal, listenerLocal))
                    return row->dwOwningPid;
            }
            return null;
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    // ---- pure, unit-testable matchers ----
    // Match only the client orientation. The endpoint set is not enough: the listener-side row
    // carries the Router PID and would make every managed connection look like an unknown/self
    // connection.

    internal static bool MatchV4(in MIB_TCPROW_OWNER_PID row, IPEndPoint clientLocal, IPEndPoint listenerLocal)
    {
        if (row.dwState != MIB_TCP_STATE.MIB_TCP_STATE_ESTAB)
            return false;
        return ToPort(row.dwLocalPort) == clientLocal.Port && ToUInt32(clientLocal.Address) == row.dwLocalAddr
              && ToPort(row.dwRemotePort) == listenerLocal.Port && ToUInt32(listenerLocal.Address) == row.dwRemoteAddr;
    }

    internal static bool MatchV6(in MIB_TCP6ROW_OWNER_PID row, IPEndPoint clientLocal, IPEndPoint listenerLocal)
    {
        if (row.dwState != MIB_TCP_STATE.MIB_TCP_STATE_ESTAB)
            return false;
        return ToPort(row.dwLocalPort) == clientLocal.Port && Bytes16(row.ucLocalAddr) == UnsafeBytes(clientLocal.Address)
              && ToPort(row.dwRemotePort) == listenerLocal.Port && Bytes16(row.ucRemoteAddr) == UnsafeBytes(listenerLocal.Address);
    }

    internal static ushort ToPort(uint mibPort)
        => (ushort)(((mibPort & 0xFF) << 8) | ((mibPort >> 8) & 0xFF));

    internal static uint ToUInt32(IPAddress ip)
        => BitConverter.ToUInt32(ip.GetAddressBytes(), 0);

    internal static unsafe byte[] Bytes16(__byte_16 inline)
        => ToArray16(&inline);

    internal static unsafe byte[] UnsafeBytes(IPAddress ip)
        => ip.GetAddressBytes();

    internal static unsafe byte[] ToArray16(void* p)
    {
        var result = new byte[16];
        for (int i = 0; i < 16; i++) result[i] = ((byte*)p)[i];
        return result;
    }
}
