using EgressController.Core.Rules;

namespace EgressController.Core.Contracts;

/// <summary>
/// Immutable domain matcher over the currently-selected rule sets + manual domains, kept in Core
/// so <see cref="RoutingEngine"/> (Core) can decide without depending on the Rules assembly.
/// The Rules project implements it (<c>DomainMatcher : IDomainMatcher</c>).
/// </summary>
public interface IDomainMatcher
{
    /// <summary>Provenance-bearing match for a hostname; <see cref="DomainMatchResult.Matched"/> = false when none.</summary>
    DomainMatchResult Match(string host);

    /// <summary>True when no rules are active (fast path → default upstream).</summary>
    bool IsEmpty { get; }
}