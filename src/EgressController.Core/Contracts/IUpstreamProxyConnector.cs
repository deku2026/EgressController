namespace EgressController.Core.Contracts;

/// <summary>
/// Connects to the configured HTTP-compatible upstream proxy (default Mihomo 127.0.0.1:7897).
/// Lives in Transport (not Proxy) so it can be reused by the control plane (rule downloads).
/// Never honors Windows System Proxy; connects explicitly to the configured upstream endpoint.
/// </summary>
public interface IUpstreamProxyConnector
{
    /// <summary>Establishes an HTTP CONNECT tunnel to <paramref name="host"/>:<paramref name="port"/> via upstream.</summary>
    ValueTask<System.IO.Stream> ConnectTunnelAsync(string host, int port, CancellationToken cancellationToken = default);

    /// <summary>Opens a raw stream to upstream for plain-HTTP (non-tunneled) forwarding.</summary>
    ValueTask<System.IO.Stream> OpenNextHopAsync(CancellationToken cancellationToken = default);

    /// <summary>Human-readable upstream endpoint, e.g. "127.0.0.1:7897".</summary>
    string Endpoint { get; }
}

/// <summary>Thrown when the upstream proxy cannot be reached or refuses to tunnel (fail-closed).</summary>
public sealed class UpstreamUnavailableException : Exception
{
    public UpstreamUnavailableException(string endpoint, Exception? inner = null)
        : base($"Upstream proxy {endpoint} is unreachable or not HTTP-compatible: {(inner?.Message ?? "unknown")}", inner)
        => Endpoint = endpoint;

    public string Endpoint { get; }
}