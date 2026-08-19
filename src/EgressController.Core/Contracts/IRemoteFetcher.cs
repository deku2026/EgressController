namespace EgressController.Core.Contracts;

/// <summary>
/// Control-plane remote fetch abstraction (plan §2.4). Must be implemented by the Transport
/// project and injected into Rules, so rule/catalog downloading NEVER goes through the
/// process-global Windows System Proxy (which would recurse back into our own 18080 router).
/// References to this interface in Core keep the dependency arrow "Rules → Core" without
/// Rules doing its own network I/O.
/// </summary>
public interface IRemoteFetcher
{
    /// <summary>
    /// Fetches an HTTP(S) resource with a hard byte cap. Implementations must not honor
    /// Windows System Proxy and must bound their total time.
    /// </summary>
    ValueTask<RemoteFetchResult> FetchAsync(Uri uri, int maxBytes, CancellationToken cancellationToken = default);
}

/// <summary>Result of a bounded control-plane fetch.</summary>
public readonly record struct RemoteFetchResult(bool Succeeded, int? StatusCode, byte[]? Body);