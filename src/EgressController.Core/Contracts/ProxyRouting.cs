using System.Net;
using EgressController.Core.Routing;

namespace EgressController.Core.Contracts;

/// <summary>
/// A "connect to host:port and give me a usable stream" abstraction, so the LocalRoutingProxy
/// data plane can ride either an ESIM-direct bound connector or the upstream HTTP-connector
/// without knowing which it got. Both are fail-closed streams.
/// </summary>
public interface IConnectTarget
{
    /// <summary>Establish a byte-path tunnel to <paramref name="host"/>:<paramref name="port"/>.
    /// For ESIM this is a direct interface-bound connection; for upstream it performs the HTTP
    /// CONNECT negotiation and returns the tunnel. Used by the CONNECT handler.</summary>
    ValueTask<System.IO.Stream> ConnectTunnelAsync(string host, int port, CancellationToken cancellationToken = default);

    /// <summary>Open a raw next hop for plain-HTTP forwarding. For upstream this is the proxy
    /// socket (no CONNECT) and host/port are ignored; for ESIM it is a direct connection to the
    /// origin <paramref name="host"/>:<paramref name="port"/>.</summary>
    ValueTask<System.IO.Stream> OpenNextHopAsync(string host, int port, CancellationToken cancellationToken = default);

    /// <summary>Diagnostic label, e.g. "ESIM(13)" or "upstream 127.0.0.1:7890".</summary>
    string Description { get; }
}

/// <summary>One resolved route: the decision plus the concrete target that will carry it.</summary>
public sealed record ProxyRoute(RouteDecision Decision, IConnectTarget Target);

/// <summary>
/// Identity observed for the client side of one local-proxy connection.  A null
/// <see cref="SessionId"/> is intentional: unknown or ordinary processes still route through
/// domain/default rules and are never guessed into a managed session.
/// </summary>
public sealed record ProxySource(
    uint? Pid,
    string ProcessName,
    string? FinalExePath,
    string? SessionId,
    string? TargetName);

/// <summary>Resolves the owner of an accepted loopback TCP connection at accept time.</summary>
public interface IProxySourceResolver
{
    ProxySource? Resolve(IPEndPoint clientLocal, IPEndPoint listenerLocal, CancellationToken cancellationToken);
}

/// <summary>
/// Resolves a request host/port to a route on the proxy data plane. Plumbing between
/// <see cref="RoutingEngine"/> (domain/managed decision) and the connectors is done in the
/// composition root (App). Called per connection (CONNECT) / per request (plain HTTP).
/// </summary>
public interface IProxyRouteSource
{
    ProxyRoute? Resolve(string host, int port, ProxySource? source = null);
}
