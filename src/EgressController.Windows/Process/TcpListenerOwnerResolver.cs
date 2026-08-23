using System.Net;
using System.Runtime.InteropServices;
using EgressController.Core.Contracts;
using Windows.Win32;
using Windows.Win32.NetworkManagement.IpHelper;
using Windows.Win32.Networking.WinSock;

namespace EgressController.Windows.Process;

public sealed record TcpListenerOwner(uint ProcessId, string? CanonicalExecutablePath)
{
    public bool IsResolved => !string.IsNullOrWhiteSpace(CanonicalExecutablePath);
}

/// <summary>
/// Reads the current IPv4/IPv6 TCP LISTEN tables and resolves every owner PID to a final EXE path.
/// A missing identity is retained as an unresolved owner so callers can fail closed.
/// </summary>
public sealed unsafe class TcpListenerOwnerResolver
{
    private readonly IProcessIdentityResolver _identityResolver;

    public TcpListenerOwnerResolver(IProcessIdentityResolver? identityResolver = null)
        => _identityResolver = identityResolver
            ?? new WindowsProcessIdentityResolver(new ExecutablePathCanonicalizer());

    public IReadOnlyList<TcpListenerOwner> Resolve(int port, CancellationToken cancellationToken = default)
    {
        if (port is < 1 or > ushort.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(port));

        var pids = new HashSet<uint>();
        AddV4(pids, port, cancellationToken);
        AddV6(pids, port, cancellationToken);

        return pids
            .OrderBy(pid => pid)
            .Select(pid => new TcpListenerOwner(pid, _identityResolver.Resolve(pid)?.ExePathFinal))
            .ToArray();
    }

    private static void AddV4(HashSet<uint> pids, int port, CancellationToken ct)
    {
        uint size = 0;
        _ = PInvoke.GetExtendedTcpTable(null, &size, bOrder: false, (uint)ADDRESS_FAMILY.AF_INET,
            TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_LISTENER, Reserved: 0);
        if (size == 0)
            return;

        size += 64 * 1024;
        byte* buffer = (byte*)NativeMemory.Alloc(size);
        try
        {
            uint rc = PInvoke.GetExtendedTcpTable(buffer, &size, bOrder: false, (uint)ADDRESS_FAMILY.AF_INET,
                TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_LISTENER, Reserved: 0);
            if (rc != 0)
                return;

            var table = (MIB_TCPTABLE_OWNER_PID*)buffer;
            for (int i = 0; i < table->dwNumEntries; i++)
            {
                ct.ThrowIfCancellationRequested();
                var row = (MIB_TCPROW_OWNER_PID*)((byte*)&table->table + i * sizeof(MIB_TCPROW_OWNER_PID));
                if (TcpOwnerSnapshotResolver.ToPort(row->dwLocalPort) == port
                    && row->dwOwningPid != 0)
                    pids.Add(row->dwOwningPid);
            }
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }

    private static void AddV6(HashSet<uint> pids, int port, CancellationToken ct)
    {
        uint size = 0;
        _ = PInvoke.GetExtendedTcpTable(null, &size, bOrder: false, (uint)ADDRESS_FAMILY.AF_INET6,
            TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_LISTENER, Reserved: 0);
        if (size == 0)
            return;

        size += 64 * 1024;
        byte* buffer = (byte*)NativeMemory.Alloc(size);
        try
        {
            uint rc = PInvoke.GetExtendedTcpTable(buffer, &size, bOrder: false, (uint)ADDRESS_FAMILY.AF_INET6,
                TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_LISTENER, Reserved: 0);
            if (rc != 0)
                return;

            var table = (MIB_TCP6TABLE_OWNER_PID*)buffer;
            for (int i = 0; i < table->dwNumEntries; i++)
            {
                ct.ThrowIfCancellationRequested();
                var row = (MIB_TCP6ROW_OWNER_PID*)((byte*)&table->table + i * sizeof(MIB_TCP6ROW_OWNER_PID));
                if (TcpOwnerSnapshotResolver.ToPort(row->dwLocalPort) == port
                    && row->dwOwningPid != 0)
                    pids.Add(row->dwOwningPid);
            }
        }
        finally
        {
            NativeMemory.Free(buffer);
        }
    }
}
