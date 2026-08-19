using System.Globalization;
using EgressController.Core.Rules;

namespace EgressController.Rules.Parsing;

/// <summary>A single compiled rule in a form the matcher can query without re-parsing.</summary>
public readonly record struct CompiledDomainRule(DomainRuleKind Kind, string Base, string OriginalText)
{
    /// <summary>Caps the built matcher query to one label wildcard semantics.</summary>
    public string[]? WildcardLabels => Kind == DomainRuleKind.LabelWildcard ? Base.Split('.') : null;
}

/// <summary>Why a rule set failed strict parsing (plan §1.4: never silently drop an unknown line).</summary>
public sealed record RuleListParseFailure(int LineNumber, string LineText, string Reason);

/// <summary>
/// Strict parser for <c>geo/geosite/*.list</c> (plan §1.4 / §Step 05). Recognised syntax:
/// bare hostname → Exact; <c>+.domain</c> → SuffixInclusive; <c>.domain</c> → SubdomainSuffix;
/// Clash/Mihomo label wildcard (<c>*</c> = one label). Comments (<c>#</c>/<c>;</c>) and blanks
/// are skipped. Any other non-comment line returns a <see cref="RuleListParseFailure"/> — the
/// caller must treat the whole candidate rule set as Invalid (keep the old active set).
///
/// Real corpus census (2026-08-18, meta @ 7957abdce1): 224,662 lines / 0 wildcard / 0 comment /
/// 0 out-of-alpha chars — every line is bare-host or <c>+.suffix</c>; all supported.
/// </summary>
public static class StrictDomainListParser
{
    public const string UnsupportedSyntaxMessage = "unsupported rule syntax";

    public static bool TryParse(
        IEnumerable<string> lines,
        string ruleSetName,
        out IReadOnlyList<CompiledDomainRule> rules,
        out RuleListParseFailure? failure)
    {
        var compiled = new List<CompiledDomainRule>();
        int lineNumber = 0;
        foreach (string raw in lines)
        {
            lineNumber++;
            string line = raw.Trim();
            if (line.Length == 0)
                continue;
            if (line[0] == '#' || line[0] == ';')
                continue;

            if (TryCompileSingle(line, out CompiledDomainRule rule))
            {
                compiled.Add(rule);
            }
            else
            {
                rules = compiled;
                failure = new RuleListParseFailure(lineNumber, line, UnsupportedSyntaxMessage);
                return false;
            }
        }

        rules = compiled;
        failure = null;
        return true;
    }

    /// <summary>Compile a single list line; false if the syntax is unsupported.</summary>
    public static bool TryCompileSingle(string raw, out CompiledDomainRule rule)
    {
        rule = default;
        string line = raw.Trim();
        if (line.Length == 0 || line[0] == '#' || line[0] == ';')
            return true; // treat as nothing

        if (line.Contains('*'))
            return TryCompileWildcard(line, out rule);

        if (line.StartsWith("+.", StringComparison.Ordinal))
        {
            if (!TryNormalizeBase(line[2..], out string baseName))
                return false;
            rule = new CompiledDomainRule(DomainRuleKind.SuffixInclusive, baseName, line);
            return true;
        }

        if (line.StartsWith('.'))
        {
            if (!TryNormalizeBase(line[1..], out string baseName))
                return false;
            rule = new CompiledDomainRule(DomainRuleKind.SubdomainSuffix, baseName, line);
            return true;
        }

        if (!TryNormalizeBase(line, out string exact))
            return false;
        rule = new CompiledDomainRule(DomainRuleKind.Exact, exact, line);
        return true;
    }

    private static bool TryCompileWildcard(string line, out CompiledDomainRule rule)
    {
        rule = default;
        string trimmed = line.TrimEnd('.');
        string[] labels = trimmed.Split('.');
        if (labels.Length == 0)
            return false;

        var normalized = new List<string>(labels.Length);
        foreach (string label in labels)
        {
            if (label == "*")
            {
                normalized.Add("*");
            }
            else if (IsAsciiLabel(label) && TryNormalizeBase(label, out string fixedLabel))
            {
                normalized.Add(fixedLabel);
            }
            else
            {
                return false; // partial wildcard like "a*b" or non-domain chars
            }
        }

        // A wildcard rule needs at least one fixed label (bare "*" is ambiguous).
        if (normalized.All(l => l == "*"))
            return false;

        rule = new CompiledDomainRule(DomainRuleKind.LabelWildcard, string.Join('.', normalized), line);
        return true;
    }

    private static bool TryNormalizeBase(string domain, out string normalized)
    {
        normalized = string.Empty;
        domain = domain.Trim().TrimEnd('.');
        if (domain.Length == 0)
            return false;

        string[] labels = domain.Split('.');
        if (labels.Length == 0 || labels.All(l => l.Length == 0))
            return false;

        var output = new List<string>(labels.Length);
        foreach (string label in labels)
        {
            if (label.Length == 0)
                return false;

            // Accept plain-ASCII or IDN (punycode) labels; reject wildcards in non-wildcard rules.
            string ascii = label;
            if (label.Any(c => c > 0x7f))
            {
                try { ascii = new IdnMapping().GetAscii(label).ToLowerInvariant(); }
                catch (ArgumentException) { return false; }
            }

            if (!IsAsciiLabel(ascii))
                return false;
            output.Add(ascii.ToLowerInvariant());
        }

        normalized = string.Join('.', output);
        return true;
    }

    private static bool IsAsciiLabel(string label)
    {
        if (label.Length == 0 || label.Length > 63)
            return false;
        if (label[0] == '-' || label[^1] == '-')
            return false;
        foreach (char c in label)
            if (!((c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-'))
                return false;
        return true;
    }

    /// <summary>Default semantic for a user-typed domain: include root + subdomains (§1.4).</summary>
    public static CompiledDomainRule ManualDefault(string host, string targetName)
    {
        if (TryNormalizeBase(host, out string baseName))
            return new CompiledDomainRule(DomainRuleKind.SuffixInclusive, baseName, "+." + host);
        // Unparseable manual input -> an Exact rule that simply never matches the bad host.
        return new CompiledDomainRule(DomainRuleKind.Exact, string.Empty, host);
    }
}