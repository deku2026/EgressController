using EgressController.Core.Contracts;
using EgressController.Core.Rules;
using EgressController.Rules.Parsing;

namespace EgressController.Rules.Matcher;

/// <summary>
/// Immutable DomainMatcher snapshot (plan §1.4 / §Step 05). Multiple selected rule sets are kept
/// separate (not merged into a bool-only set) so every hit returns concrete provenance
/// (RuleSetName / RuleKind / RuleText). Manual rules take priority (queried first); within a set,
/// the most-specific rule wins (Exact > SuffixInclusive > LabelWildcard > SubdomainSuffix);
/// ties break by first-encountered order so the result does not drift with HashSet enum order.
/// Matching is O(total rules) worst-case but avoids per-rule string recompilation; the corpus is
/// small enough and the guarantee is no O(n^2)/regex. Query is hostname-only.
/// </summary>
public sealed class DomainMatcher : IDomainMatcher
{
    /// <summary>A single ordered rule set + display name.</summary>
    public sealed record RuleSetView(string Name, IReadOnlyList<CompiledDomainRule> Rules);

    private readonly IReadOnlyList<RuleSetView> _sets;

    public DomainMatcher(IReadOnlyList<RuleSetView> sets)
    {
        _sets = sets;
    }

    public bool IsEmpty => _sets.Count == 0;

    public DomainMatchResult Match(string host)
    {
        string query = NormalizeQuery(host);
        if (query.Length == 0)
            return DomainMatchResult.NoMatch;

        int bestSpecificity = -1;
        string bestRuleSet = string.Empty;
        string bestRuleText = string.Empty;
        DomainRuleKind bestKind = 0;

        foreach (RuleSetView set in _sets)
        {
            foreach (CompiledDomainRule rule in set.Rules)
            {
                int specificity = Specificity(rule, query);
                if (specificity > bestSpecificity)
                {
                    bestSpecificity = specificity;
                    bestRuleSet = set.Name;
                    bestRuleText = rule.OriginalText;
                    bestKind = rule.Kind;
                }
            }
        }

        return bestSpecificity < 0
            ? DomainMatchResult.NoMatch
            : new DomainMatchResult(true, bestRuleSet, bestKind, bestRuleText);
    }

    private static int Specificity(CompiledDomainRule rule, string query)
    {
        switch (rule.Kind)
        {
            case DomainRuleKind.Exact:
                return string.Equals(rule.Base, query, StringComparison.Ordinal) ? 4 : -1;

            case DomainRuleKind.SuffixInclusive:
                return (query == rule.Base || query.EndsWith("." + rule.Base, StringComparison.Ordinal)) ? 3 : -1;

            case DomainRuleKind.SubdomainSuffix:
                return query.EndsWith("." + rule.Base, StringComparison.Ordinal) ? 1 : -1;

            case DomainRuleKind.LabelWildcard:
                return MatchWildcard(rule, query) ? 2 : -1;

            default:
                return -1;
        }
    }

    private static bool MatchWildcard(CompiledDomainRule rule, string query)
    {
        string[] allowed = rule.WildcardLabels!;
        string[] actual = query.Split('.');
        if (actual.Length != allowed.Length)
            return false;

        for (int i = 0; i < allowed.Length; i++)
            if (allowed[i] != "*" && !string.Equals(allowed[i], actual[i], StringComparison.Ordinal))
                return false;
        return true;
    }

    /// <summary>Lowercase + strip a single trailing dot (no IDN here; query already ascii).</summary>
    internal static string NormalizeQuery(string host)
        => host.Trim().TrimEnd('.').ToLowerInvariant();
}