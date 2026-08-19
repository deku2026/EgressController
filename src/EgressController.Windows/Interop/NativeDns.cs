using System.Net;
using System.Runtime.InteropServices;

namespace EgressController.Windows.Interop;

/// <summary>
/// Hand-written, source-generated interop for interface-pinned DNS (DnsQueryEx / DnsRecordListFree).
///
/// **Justification (plan §1.2 exception, MUST stay isolated):** CsWin32 flattens the
/// <c>DNS_QUERY_REQUEST</c> context union into a wrong layout (it emits
/// <c>pQueryCompletionCallback</c> + <c>pQueryContext</c> as two sequential fields, which does
/// not match the native <c>PDNS_QUERY_REQUEST_CONTEXT</c> single pointer), so its generated
/// DnsQueryEx cannot be used reliably. This one file is the only hand-written native interop in
/// the product; everything else is CsWin32 or managed .NET. No <c>[DllImport]</c> anywhere else.
///
/// [LibraryImport] is a source-generated, AOT-safe P/Invoke (not runtime reflection).
/// </summary>
internal static unsafe partial class NativeDns
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct DnsQueryRequest
    {
        internal uint Version;
        internal char* QueryName;     // LPCWSTR
        internal ushort QueryType;    // WORD
        internal ulong QueryOptions;  // ULONG64
        internal void* DnsServerList; // PDNS_ADDR_ARRAY
        internal uint InterfaceIndex; // ULONG
        internal void* QueryContext;  // PDNS_QUERY_REQUEST_CONTEXT (NULL => synchronous)
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct DnsQueryResult
    {
        internal uint Version;
        internal int QueryStatus;      // DNS_STATUS
        internal ulong QueryOptions;
        internal DnsRecord* QueryRecordsFilter; // PDNS_RECORD
        internal void* Reserved;
    }

    /// <summary>Offset of the record's data union (after the fixed header).</summary>
    internal const int DataOffset = 32;

    [StructLayout(LayoutKind.Sequential)]
    internal struct DnsRecord
    {
        internal DnsRecord* Next;    // 0
        internal char* Name;         // 8
        internal ushort Type;        // 16
        internal ushort DataLength;  // 18
        internal uint Flags;         // 20
        internal uint Ttl;           // 24
        internal uint Reserved;      // 28  (union data begins at 32)
    }

    private const string DnsDll = "dnsapi";

    [LibraryImport(DnsDll, EntryPoint = "DnsQueryEx")]
    internal static partial uint DnsQueryEx(DnsQueryRequest* request, DnsQueryResult* result, void* pReserved);

    [LibraryImport(DnsDll, EntryPoint = "DnsRecordListFree")]
    internal static partial void DnsRecordListFree(DnsRecord* recordList, int freeType);

    // -- option / type / free constants (from windns.h) --
    internal const ulong DnsQueryBypassCache = 0x0000_0008;
    internal const ulong DnsQueryStandard = 0x0000_0000;
    internal const ushort TypeA = 1;
    internal const ushort TypeAaaa = 28;
    internal const int DnsFreeRecordList = 1;

    /// <summary>Reads the IPv4/IPv6 address carried at the record's data union.</summary>
    internal static bool TryReadAddress(DnsRecord* record, out IPAddress address)
    {
        address = null!;
        if (record is null)
            return false;

        byte* data = (byte*)record + DataOffset;
        switch (record->Type)
        {
            case TypeA:
                address = new IPAddress(new ReadOnlySpan<byte>(data, 4));
                return true;
            case TypeAaaa:
                address = new IPAddress(new ReadOnlySpan<byte>(data, 16));
                return true;
            default:
                return false;
        }
    }
}