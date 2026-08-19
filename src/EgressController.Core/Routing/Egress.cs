namespace EgressController.Core.Routing;

/// <summary>
/// The only two egresses in the product (plan §0.2). There is deliberately no "Direct"
/// default: anything unmatched goes to <see cref="UpstreamProxy"/>. Fail-closed.
/// </summary>
public enum Egress
{
    /// <summary>Bind a real physical interface (ESIM) for DNS + connect.</summary>
    Esim,

    /// <summary>Forward the hostname verbatim to the configured HTTP-compatible upstream proxy.</summary>
    UpstreamProxy,
}