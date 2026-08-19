namespace EgressController.Core.Rules;

/// <summary>Semantics of a single domain rule (plan §1.4 / §6 / §Step 05).</summary>
public enum DomainRuleKind
{
    /// <summary>matches only the exact hostname (e.g. <c>example.com</c>)</summary>
    Exact = 0,

    /// <summary>root domain + any subdomain (e.g. <c>+.example.com</c>)</summary>
    SuffixInclusive = 1,

    /// <summary>subdomains only, NOT the root (e.g. <c>.example.com</c>)</summary>
    SubdomainSuffix = 2,

    /// <summary>Clash/Mihomo label wildcard; <c>*</c> = exactly one label (e.g. <c>xbox.*.microsoft.com</c>)</summary>
    LabelWildcard = 3,
}

/// <summary>A single normalized domain rule (plan §6 DomainRule).</summary>
public sealed record DomainRule(
    DomainRuleKind Kind,
    string NormalizedPattern,
    string OriginalText,
    string RuleSetName);

/// <summary>Matcher output with provenance so the Connection Log is explainable (plan §1.4).</summary>
public readonly record struct DomainMatchResult(bool Matched, string RuleSetName, DomainRuleKind RuleKind, string RuleText)
{
    public static readonly DomainMatchResult NoMatch = new(false, string.Empty, 0, string.Empty);
}