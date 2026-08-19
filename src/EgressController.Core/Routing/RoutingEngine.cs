using EgressController.Core.Contracts;
using EgressController.Core.Rules;

namespace EgressController.Core.Routing;

/// <summary>
/// Decides Managed-App, Domain and Default branches (plan §Step 06/11). Fail-closed: unmatched
/// traffic goes to the upstream proxy.
/// </summary>
public sealed class RoutingEngine
{
    private readonly Func<IDomainMatcher> _domains;

    public RoutingEngine(IDomainMatcher domains)
        : this(() => domains)
    {
    }

    /// <summary>Uses the current immutable matcher snapshot for each request.</summary>
    public RoutingEngine(Func<IDomainMatcher> domains)
        => _domains = domains ?? throw new ArgumentNullException(nameof(domains));

    /// <summary>
    /// Decide for a request that reached this proxy. A non-null launch session is the explicit
    /// accept-time process-ownership signal; ordinary and unresolved sources remain un-managed.
    /// </summary>
    public RouteDecision Decide(string host, string? launchSessionId = null)
    {
        if (launchSessionId is not null)
        {
            // Managed Launch Session root/owned-component → ESIM (top priority, §0.4).
            return new RouteDecision(Egress.Esim, RouteReason.ManagedApp, null, launchSessionId);
        }

        IDomainMatcher domains = _domains();
        if (domains.IsEmpty)
            return RouteDecision.DefaultUpstream;

        DomainMatchResult match = domains.Match(host);
        return match.Matched
            ? new RouteDecision(Egress.Esim, RouteReason.DomainMatch, match, null)
            : RouteDecision.DefaultUpstream;
    }
}
