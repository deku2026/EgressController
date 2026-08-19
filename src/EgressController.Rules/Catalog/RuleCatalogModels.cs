namespace EgressController.Rules.Catalog;

/// <summary>One searchable rule in the catalog (plan §Step 07).</summary>
public sealed record RuleCatalogEntry(string Name, string Path, string BlobSha);

/// <summary>
/// An immutable snapshot of the available geosite catalog, pinned to one commit (plan §Step 07).
/// All rule-list URLs referenced by this snapshot use <c>raw.githubusercontent.com/{CommitSha}/geo/geosite/{Name}.list</c>.
/// </summary>
public sealed record RuleCatalogSnapshot(string CommitSha, string TreeSha, IReadOnlyList<RuleCatalogEntry> Entries);

/// <summary>The currently-active rule snapshot (the commit all active selected rules came from).</summary>
public sealed record ActiveRuleSnapshot(
    string CommitSha,
    string TreeSha,
    IReadOnlyList<string> SelectedNames,
    IReadOnlyList<EgressController.Rules.Parsing.CompiledDomainRule> Rules,
    IReadOnlySet<string> RuleSetNames)
{
    public static readonly ActiveRuleSnapshot Empty =
        new(string.Empty, string.Empty, Array.Empty<string>(), Array.Empty<EgressController.Rules.Parsing.CompiledDomainRule>(),
            new HashSet<string>());
}