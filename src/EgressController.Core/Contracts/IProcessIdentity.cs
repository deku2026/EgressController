using System.Net;
using EgressController.Core.Models;

namespace EgressController.Core.Contracts;

/// <summary>
/// Resolves the owning PID of an accepted local-proxy connection (plan §Step 08). A proxy
/// connection's client row appears in the TCP owner table with a reversed tuple:
/// local = client's remote endpoint, remote = the proxy's local listener. Resolvers supply a
/// fresh snapshot per accept; the caller must NOT cache PID by endpoint for seconds (§1.4/§08).
/// </summary>
public interface IConnectionOwnerResolver
{
    /// <summary>Owning PID of the socket whose local endpoint is <paramref name="clientLocal"/> and connected
    /// to <paramref name="listenerLocal"/>, or null when not resolvable (→ no managed identity).</summary>
    uint? ResolveOwner(IPEndPoint clientLocal, IPEndPoint listenerLocal, CancellationToken cancellationToken);
}

/// <summary>PID → path + start-time, guarding against PID reuse.</summary>
public interface IProcessIdentityResolver
{
    /// <summary>Null when the PID is already gone or not inspectable (never guess managed).</summary>
    ProcessIdentity? Resolve(uint pid);
}

/// <summary>
/// Windows lexical path → canonical final path (GetFinalPathNameByHandle), resolving junctions,
/// symlinks, <c>..</c> and 8.3 aliases before ownership containment (plan §1.6). Must not be the
/// sole ownership evidence; a failed canonicalization returns null and the process is not managed.
/// </summary>
public interface IExecutablePathCanonicalizer
{
    string? Canonicalize(string path);
}