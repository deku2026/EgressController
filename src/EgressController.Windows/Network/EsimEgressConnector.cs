using System.Net;
using System.Net.Sockets;
using EgressController.Core.Contracts;
using EgressController.Core.Models;

namespace EgressController.Windows.Network;

/// <summary>
/// ESIM direct: interface-pinned DNS → bound socket → stream. Composes the resolver + socket
/// factory. Stagger strategy (plan §Step 02): IPv4 is tried first, then IPv6, with a bounded
/// per-attempt timeout, so a dead IPv6 path cannot stall the request. Fail-closed:
/// any failure throws <see cref="EsimConnectException"/>; no fallback to PRIMARY/upstream.
/// </summary>
public sealed class EsimEgressConnector : IEsimEgressConnector
{
    private readonly IEsimDnsResolver _dns;
    private readonly IEsimSocketFactory _sockets;

    public EsimEgressConnector(IEsimDnsResolver dns, IEsimSocketFactory sockets)
    {
        _dns = dns;
        _sockets = sockets;
    }

    /// <summary>Per-attempt connect budget (keeps fail-fast rather than a multi-second hang).</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(8);

    public ValueTask<Stream> ConnectAsync(string host, int port, NetworkAdapterInfo esim, CancellationToken cancellationToken)
    {
        var resolved = _dns.Resolve(host, esim.IfIndex, cancellationToken);

        // Prefer IPv4 so a broken IPv6 scope cannot trigger the "try bad IPv6 for 5s" stall.
        IEnumerable<IPAddress> order = resolved
            .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1);

        Exception? last = null;
        foreach (var address in order)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCts.CancelAfter(ConnectTimeout);

            try
            {
                var socket = _sockets.Connect(address, port, esim, attemptCts.Token);
                // The socket factory already did a bounded connect; hand the stream to the caller.
                return ValueTask.FromResult<Stream>(new NetworkStream(socket, ownsSocket: true));
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                last = new TimeoutException($"connect to {address}:{port} timed out after {ConnectTimeout.TotalSeconds}s (ESIM).");
            }
            catch (SocketException ex)
            {
                last = ex;
            }
        }

        throw new EsimConnectException(host, port, last);
    }
}