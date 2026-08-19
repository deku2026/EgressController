using EgressController.Core.Rules;

namespace EgressController.Core.Routing;

/// <summary>Why a connection/request was routed a particular way (plan §0.4 priority).</summary>
public enum RouteReason
{
    /// <summary>Source process resolved to a Managed Launch Session (Step 11; unused until then).</summary>
    ManagedApp,

    /// <summary>Host matched a selected geosite / manual domain → ESIM.</summary>
    DomainMatch,

    /// <summary>No higher-priority rule → configured upstream proxy (never DIRECT).</summary>
    DefaultUpstream,

    /// <summary>PID could not be resolved — no managed identity, falls to Domain/Default (Step 08).</summary>
    SourceUnknown,
}

/// <summary>
/// Final per-request routing decision (plan §Step 06 / §6 RouteDecision). Egress is either ESIM
/// or UpstreamProxy — there is no DIRECT. Includes match provenance for the Connection Log.
/// </summary>
public readonly record struct RouteDecision(
    Egress Egress,
    RouteReason Reason,
    DomainMatchResult? MatchedRule,
    string? LaunchSessionId)
{
    /// <summary>Everything unmatched: upstream proxy, fail-closed. This is the default.</summary>
    public static readonly RouteDecision DefaultUpstream =
        new(Egress.UpstreamProxy, RouteReason.DefaultUpstream, null, null);
}