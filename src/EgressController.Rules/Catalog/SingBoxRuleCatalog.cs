using System.Collections.Frozen;

namespace EgressController.Rules.Catalog;

/// <summary>One SRS artifact exposed by the MetaCubeX sing catalog.</summary>
public sealed record SingBoxRuleCatalogEntry(
    string Name,
    string Path,
    string BlobSha,
    long? Size);

/// <summary>A commit-pinned catalog of the generated sing/geosite SRS files.</summary>
public sealed record SingBoxRuleCatalogSnapshot(
    string CommitSha,
    string TreeSha,
    IReadOnlyList<SingBoxRuleCatalogEntry> Entries);

/// <summary>
/// Immutable local search index. Searching this object never performs network I/O; callers must
/// explicitly invoke <see cref="RuleCatalogService.UpdateAsync"/> to refresh it.
/// </summary>
public sealed class SingBoxRuleCatalog
{
    private readonly SingBoxRuleCatalogSnapshot _snapshot;
    private readonly FrozenDictionary<string, SingBoxRuleCatalogEntry> _byName;

    public SingBoxRuleCatalog(SingBoxRuleCatalogSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshot = snapshot;
        _byName = snapshot.Entries.ToFrozenDictionary(
            entry => entry.Name.ToLowerInvariant(),
            StringComparer.Ordinal);
    }

    public SingBoxRuleCatalogSnapshot Snapshot => _snapshot;

    public IReadOnlyList<SingBoxRuleCatalogEntry> Entries => _snapshot.Entries;

    public int Count => _byName.Count;

    public IReadOnlyList<SingBoxRuleCatalogEntry> Search(string query, int max = 50)
    {
        if (max <= 0)
            return Array.Empty<SingBoxRuleCatalogEntry>();

        string needle = query.Trim().ToLowerInvariant();
        if (needle.Length == 0)
            return Array.Empty<SingBoxRuleCatalogEntry>();

        return _byName
            .Where(pair => pair.Key.Contains(needle, StringComparison.Ordinal))
            .Select(pair => pair.Value)
            .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
            .Take(max)
            .ToArray();
    }

    public bool TryGet(string name, out SingBoxRuleCatalogEntry? entry)
    {
        entry = null;
        if (string.IsNullOrWhiteSpace(name))
            return false;
        return _byName.TryGetValue(name.Trim().ToLowerInvariant(), out entry);
    }
}
