using EgressController.Core.Models;
using EgressController.Core.Profile;

namespace EgressController.Launcher.Discovery;

public sealed record RegisteredPackageLocation(string FamilyName, IReadOnlyList<string> Roots);
public sealed record PackagePathReplacement(string Previous, string Current, string Root);
public sealed record PackageApplicationUpgrade(string DiscoveryKey, string Name, IReadOnlyList<PackagePathReplacement> Paths);

/// <summary>Rebase only previously selected relative EXE paths into an OS-registered package
/// of the exact same family. No wildcard rules, recursive scan or executable-name matching.</summary>
public sealed class PackagePathUpgrade(IReadOnlyList<PackageApplicationUpgrade> applications)
{
    public IReadOnlyList<PackageApplicationUpgrade> Applications { get; } = applications;
    public bool HasChanges => Applications.Count > 0;

    public static string? FamilyOfSelection(string key)
    {
        if (!key.StartsWith("pkg:", StringComparison.Ordinal)) return null;
        int end = key.IndexOf(':', 4);
        if (end < 0) return null;
        string family = key[4..end];
        return PackageExecutableRoots.IsFamilyName(family) && key[(end + 1)..].StartsWith(family + "!", StringComparison.OrdinalIgnoreCase)
            ? family : null;
    }

    public static PackagePathUpgrade Plan(EgressProfileDocument profile, IReadOnlyList<RegisteredPackageLocation> registered,
        Func<string, string?> familyOfRoot, Func<string, bool> fileExists)
    {
        var upgrades = new List<PackageApplicationUpgrade>();
        foreach (var selected in profile.Applications)
        {
            string? family = FamilyOfSelection(selected.DiscoveryKey);
            if (family is null) continue;
            // Multiple active registrations are ambiguous; wait for Windows to finish the update.
            var locations = registered.Where(item => string.Equals(item.FamilyName, family, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (locations.Length != 1) continue;
            string[] roots = locations[0].Roots.Where(Path.IsPathFullyQualified)
                .Where(root => string.Equals(familyOfRoot(root), family, StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var known = selected.ExecutablePaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var replacements = new Dictionary<string, PackagePathReplacement>(StringComparer.OrdinalIgnoreCase);
            foreach (string previousRoot in selected.DirectoryRoots.Where(Path.IsPathFullyQualified))
            {
                if (!string.Equals(familyOfRoot(previousRoot), family, StringComparison.OrdinalIgnoreCase)) continue;
                foreach (string previous in selected.ExecutablePaths.Where(Path.IsPathFullyQualified))
                {
                    string relative = Path.GetRelativePath(previousRoot, previous);
                    if (Path.IsPathFullyQualified(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar)
                        || !relative.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (string root in roots)
                    {
                        string current = Path.GetFullPath(Path.Combine(root, relative));
                        if (!known.Contains(current) && fileExists(current))
                            replacements.TryAdd(current, new(previous, current, root));
                    }
                }
            }
            if (replacements.Count > 0) upgrades.Add(new(selected.DiscoveryKey, selected.DisplayName ?? family, replacements.Values.ToArray()));
        }
        return new(upgrades);
    }

    // Re-read selection at commit time: a concurrent deselection/route change must win.
    public EgressProfileDocument Apply(EgressProfileDocument current)
    {
        bool changed = false;
        var selections = current.Applications.Select(selected =>
        {
            var upgrade = Applications.FirstOrDefault(item => item.DiscoveryKey == selected.DiscoveryKey);
            var paths = upgrade?.Paths.Where(item => selected.ExecutablePaths.Contains(item.Previous, StringComparer.OrdinalIgnoreCase)
                && !selected.ExecutablePaths.Contains(item.Current, StringComparer.OrdinalIgnoreCase)).ToArray() ?? [];
            if (paths.Length == 0) return selected;
            changed = true;
            return selected with
            {
                ExecutablePaths = selected.ExecutablePaths.Concat(paths.Select(item => item.Current)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                DirectoryRoots = selected.DirectoryRoots.Concat(paths.Select(item => item.Root)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            };
        }).ToArray();
        return changed ? current with { Applications = selections } : current;
    }

    public LaunchTarget UpdateTarget(LaunchTarget target, EgressProfileDocument applied)
    {
        var upgrade = Applications.FirstOrDefault(item => item.DiscoveryKey == target.DiscoveryKey);
        var selection = applied.Applications.FirstOrDefault(item => item.DiscoveryKey == target.DiscoveryKey);
        if (upgrade is null || selection is null) return target;
        var active = upgrade.Paths.Where(item => selection.ExecutablePaths.Contains(item.Current, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (active.Length == 0) return target;
        string? executable = active.FirstOrDefault(item => string.Equals(item.Previous, target.CanonicalExecutable, StringComparison.OrdinalIgnoreCase))?.Current;
        var roots = active.Select(item => item.Root).Concat(target.OwnedRoots).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return RestoreInventory(target.WithInventory(target.OwnedExecutables, target.InventoryReady, roots, executable: executable), applied);
    }

    // The profile is authoritative even if the optional catalog cache was not saved before exit.
    // Unknown/new data roots still need an explicit inventory refresh.
    public static LaunchTarget RestoreInventory(LaunchTarget target, EgressProfileDocument profile)
    {
        if (FamilyOfSelection(target.DiscoveryKey) is null) return target;
        var selected = profile.Applications.FirstOrDefault(item => item.DiscoveryKey == target.DiscoveryKey);
        if (selected is null) return target;
        var roots = target.OwnedRoots.Concat(selected.DirectoryRoots).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var paths = target.OwnedExecutables.Concat(selected.ExecutablePaths).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        bool covered = selected.ExecutablePaths.Count > 0 && roots.All(root => PackageExecutableRoots.Covers(selected.DirectoryRoots, root))
            && target.CanonicalExecutable is { } main && selected.ExecutablePaths.Contains(main, StringComparer.OrdinalIgnoreCase);
        return target.WithInventory(paths, target.InventoryReady || covered, roots);
    }
}
