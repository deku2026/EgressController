using EgressController.Core.Models;

namespace EgressController.Core.Contracts;

/// <summary>Deterministic failure when an ESIM-bound connect cannot be established (fail-closed).</summary>
public sealed class EsimConnectException : Exception
{
    public EsimConnectException(string host, int port, Exception? inner = null)
        : base($"ESIM connect failed for {host}:{port}: {(inner?.Message ?? "no usable address")} (no fallback to PRIMARY/upstream).", inner)
    {
        Host = host;
        Port = port;
    }

    public string Host { get; }
    public int Port { get; }
}

/// <summary>
/// Interface-pinned DNS (DnsQueryEx with <paramref name="interfaceIndex"/>). A + AAAA both
/// queried with BYPASS_CACHE so the answer is genuinely resolved out of the given interface.
/// </summary>
public interface IEsimDnsResolver
{
    IReadOnlyList<System.Net.IPAddress> Resolve(string host, int interfaceIndex, CancellationToken cancellationToken);
}

/// <summary>Creates a connected socket pinned to the ESIM interface (IP/IPV6 _UNICAST_IF).</summary>
public interface IEsimSocketFactory
{
    System.Net.Sockets.Socket Connect(
        System.Net.IPAddress target, int port, NetworkAdapterInfo esim, CancellationToken cancellationToken);
}

/// <summary>ESIM direct: DNS → bound connect → readable/writable stream.</summary>
public interface IEsimEgressConnector
{
    ValueTask<System.IO.Stream> ConnectAsync(
        string host, int port, NetworkAdapterInfo esim, CancellationToken cancellationToken);
}