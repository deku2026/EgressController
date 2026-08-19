using System.Net;
using EgressController.Core.Contracts;
using EgressController.Windows.Interop;

namespace EgressController.Windows.Network;

/// <summary>
/// Interface-pinned DNS via DnsQueryEx (synchronous, BYPASS_CACHE, InterfaceIndex = ESIM).
/// Queries A + AAAA so the ESIM backend can pick per-family when it connects.
/// </summary>
public sealed unsafe class EsimDnsResolver : IEsimDnsResolver
{
    public IReadOnlyList<IPAddress> Resolve(string host, int interfaceIndex, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host))
            throw new ArgumentException("host must not be empty", nameof(host));

        // IP literals need no resolver and must survive a hotspot that only does DNS for names.
        if (IPAddress.TryParse(host, out IPAddress? literal))
            return new[] { literal };

        var addresses = new List<IPAddress>(4);
        QueryFamily(host, interfaceIndex, NativeDns.TypeA, addresses, cancellationToken);
        QueryFamily(host, interfaceIndex, NativeDns.TypeAaaa, addresses, cancellationToken);
        return addresses;
    }

    private static void QueryFamily(
        string host,
        int interfaceIndex,
        ushort queryType,
        List<IPAddress> sink,
        CancellationToken ct)
    {
        fixed (char* namePointer = host)
        {
            NativeDns.DnsQueryRequest request = default;
            request.Version = 1;
            request.QueryName = namePointer;
            request.QueryType = queryType;
            request.QueryOptions = NativeDns.DnsQueryBypassCache;
            request.InterfaceIndex = (uint)interfaceIndex;
            // QueryContext left NULL -> synchronous call. QueryResults.Reserved must be NULL.

            NativeDns.DnsQueryResult result = default;
            result.Version = 1;

            uint status;
            try
            {
                ct.ThrowIfCancellationRequested();
                status = NativeDns.DnsQueryEx(&request, &result, (void*)null);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (status != 0)
                return; // DNS_STATUS nonzero -> this family failed (or host absent); not fatal.

            try
            {
                for (var record = result.QueryRecordsFilter; record is not null; record = record->Next)
                {
                    ct.ThrowIfCancellationRequested();
                    if (NativeDns.TryReadAddress(record, out IPAddress ip))
                        sink.Add(ip);
                }
            }
            catch (OperationCanceledException) { /* leave whatever we collected */ }
            finally
            {
                NativeDns.DnsRecordListFree(result.QueryRecordsFilter, NativeDns.DnsFreeRecordList);
            }
        }
    }
}