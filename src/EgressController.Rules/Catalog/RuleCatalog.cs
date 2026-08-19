using System.Collections.Frozen;
using System.Collections.ObjectModel;

namespace EgressController.Rules.Catalog;

/// <summary>
/// In-memory, immutable search index over a <see cref="RuleCatalogSnapshot"/>. Searching is
/// strictly local (never a network call). Build once on catalog refresh; queries hit a frozen
/// lower-cased name index (§Step 07 — "永远只搜内存/本地 catalog").
/// </summary>
public sealed class RuleCatalog
{
    private readonly RuleCatalogSnapshot _snapshot;
    private readonly FrozenDictionary<string, RuleCatalogEntry> _byName;

    public RuleCatalog(RuleCatalogSnapshot snapshot)
    {
        _snapshot = snapshot;
        _byName = snapshot.Entries.ToFrozenDictionary(e => e.Name.ToLowerInvariant(), StringComparer.Ordinal);
    }

    public RuleCatalogSnapshot Snapshot => _snapshot;

    /// <summary>Search names only (substring, case-insensitive) — local, ready for UI autocomplete.</summary>
    public IReadOnlyList<RuleCatalogEntry> Search(string query, int max = 50)
    {
        string needle = query.Trim().ToLowerInvariant();
        if (needle.Length == 0)
            return Array.Empty<RuleCatalogEntry>();

        var results = new List<RuleCatalogEntry>();
        foreach (var kv in _byName)
            if (kv.Key.Contains(needle, StringComparison.Ordinal))
            {
                results.Add(kv.Value);
                if (results.Count >= max)
                    break;
            }
        return results;
    }

    public bool TryGet(string name, out RuleCatalogEntry? entry)
        => _byName.TryGetValue(name.ToLowerInvariant(), out entry);

    public int Count => _byName.Count;
}