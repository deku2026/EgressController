using System.Net;
using System.Net.Sockets;
using EgressController.Core.Contracts;
using EgressController.Core.Models;

namespace EgressController.Windows.Network;

/// <summary>
/// Creates a connected socket pinned to the ESIM interface by <b>binding the socket to the
/// interface's own local address</b> for the target address family. This forces the source
/// address (and therefore the egress interface + its route) to ESIM, independent of the ambient
/// route metrics, and is AOT-safe (managed Socket API).
///
/// <para><b>Real-machine finding (plan §Step 02 "以实际 API probe 为准"):</b> the documented
/// IP_UNICAST_IF(IPv4)/IPV6_UNICAST_IF option did NOT work here — even an on-link connect to the
/// ESIM gateway returned WSAEADDRNOTAVAIL(10049), while bind-to-local-address reached the same
/// gateway (ConnectionRefused ⇒ TCP reachable). So the product binds the local address instead.
/// This is recorded rather than papered over. See artifacts/evidence/step02/.</para>
///
/// Fail-closed: throws on connect failure (no fallback).
/// </summary>
public sealed class EsimSocketFactory : IEsimSocketFactory
{
    public Socket Connect(IPAddress target, int port, NetworkAdapterInfo esim, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };

        try
        {
            BindToEsimInterface(socket, target.AddressFamily, esim);

            var connectTask = socket.ConnectAsync(target, port, cancellationToken);
            connectTask.AsTask().GetAwaiter().GetResult();
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static void BindToEsimInterface(Socket socket, AddressFamily family, NetworkAdapterInfo esim)
    {
        // Pick a usable (non-loopback) address of the target family from the ESIM interface.
        IPAddress? local = esim.Addresses.FirstOrDefault(a => a.AddressFamily == family && !IPAddress.IsLoopback(a));
        if (local is null)
            throw new SocketException((int)SocketError.AddressNotAvailable);

        socket.Bind(new IPEndPoint(local, 0));
    }
}