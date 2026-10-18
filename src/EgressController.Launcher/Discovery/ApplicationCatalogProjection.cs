using EgressController.Core.Models;
using EgressController.Core.Profile;

namespace EgressController.Launcher.Discovery;

/// <summary>One product row owns its saved recursive paths; process ancestry is never consulted.</summary>
public static class ApplicationCatalogProjection
{
    public static IReadOnlyList<LaunchTarget> Project(IEnumerable<LaunchTarget> discovery)
    {
        var raw = discovery.ToArray();
        var candidates = raw.Where(SupportedApplicationCatalog.IsSupported)
            .OrderByDescending(item => item.Kind == LaunchKind.CliNative)
            .ThenByDescending(item => item.Kind == LaunchKind.PackagedAumid)
            .ThenByDescending(item => item.OwnedExecutables.Count).ThenBy(item => item.DiscoveryKey, StringComparer.Ordinal).ToArray();
        var accepted = new List<LaunchTarget>();
        foreach (var candidate in candidates)
        {
            var definition = SupportedApplicationCatalog.Identify(candidate)!;
            bool cli = candidate.Kind == LaunchKind.CliNative;
            if (accepted.Any(owner => SameFile(owner.CanonicalExecutable, candidate.CanonicalExecutable))) continue;
            if (!cli && accepted.Any(owner => owner.Kind != LaunchKind.CliNative && owner.CatalogId == definition.Id
                && Contains(owner, candidate.CanonicalExecutable))) continue;
            var aliases = raw.Where(item => item != candidate && item.Kind != LaunchKind.CliNative
                && (SameFile(item.CanonicalExecutable, candidate.CanonicalExecutable) || !cli && Contains(candidate, item.CanonicalExecutable)))
                .SelectMany(item => item.SelectionKeys).Concat(candidate.RelatedDiscoveryKeys).Distinct(StringComparer.Ordinal).ToArray();
            string? icon = candidate.IconPath;
            if (cli) icon = candidates.FirstOrDefault(item => item.Kind != LaunchKind.CliNative && SupportedApplicationCatalog.Identify(item)?.Id == definition.Id)?.IconPath ?? icon;
            accepted.Add(Copy(candidate, definition.Name + (cli ? " CLI" : ""), definition.Id, aliases, icon));
        }
        return AssignAliases(accepted);
    }

    // A rescan may lose access to a package or an older version. Keep its saved paths and
    // use exact membership to fold legacy helper selections into the visible owner.
    public static IReadOnlyList<LaunchTarget> RestoreSavedTargets(IReadOnlyList<LaunchTarget> discovered,
        IReadOnlyList<EgressApplicationSelection> saved)
    {
        var targets = discovered.ToList();
        foreach (var item in saved.Where(IsSupportedSaved).OrderByDescending(item => SupportedApplicationCatalog.ForExecutable(SelectionPath(item)) is not null || SelectionPath(item) is null))
        {
            if (targets.Any(target => target.SelectionKeys.Contains(item.DiscoveryKey))) continue;
            string? path = SelectionPath(item) ?? item.ExecutablePaths.FirstOrDefault();
            if (path is not null && targets.Any(target => Contains(target, path))) continue;
            var definition = SavedDefinition(item);
            bool cli = item.DiscoveryKey.StartsWith("cli:", StringComparison.OrdinalIgnoreCase);
            targets.Add(new()
            {
                Id = item.DiscoveryKey, SavedDiscoveryKey = item.DiscoveryKey,
                Name = definition!.Name + (cli ? " CLI" : ""), CatalogId = definition.Id,
                Kind = cli ? LaunchKind.CliNative : LaunchKind.DirectExe, CanonicalExecutable = path, Command = path,
                OwnedExecutables = item.ExecutablePaths, OwnedRoots = item.DirectoryRoots,
                IconPath = path, Source = "已保存的程序清单", RouteSelected = true,
            });
        }
        return AssignAliases(targets.Select(target =>
        {
            var paths = Merge(target, saved.Where(item => target.SelectionKeys.Contains(item.DiscoveryKey)), EgressRouteTarget.DefaultAdapter).ExecutablePaths;
            var aliases = saved.Where(item => SelectionPath(item) is { } path && paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                .Select(item => item.DiscoveryKey).Concat(target.RelatedDiscoveryKeys).Where(key => key != target.DiscoveryKey).Distinct(StringComparer.Ordinal).ToArray();
            return Copy(target, target.Name, target.CatalogId ?? "", aliases, target.IconPath);
        }).ToList());
    }

    private static IReadOnlyList<LaunchTarget> AssignAliases(List<LaunchTarget> targets)
    {
        // A separately discovered CLI keeps its own key even when installed inside the desktop
        // product directory. A legacy key can belong to only one visible row.
        var assigned = targets.Select(item => item.DiscoveryKey).ToHashSet(StringComparer.Ordinal);
        return targets.OrderByDescending(item => item.Kind == LaunchKind.CliNative)
            .Select(item => Copy(item, item.Name, item.CatalogId ?? "",
                item.RelatedDiscoveryKeys.Where(alias => assigned.Add(alias)).ToArray(), item.IconPath))
            .OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string? SelectionPath(EgressApplicationSelection item) =>
        item.DiscoveryKey.StartsWith("exe:", StringComparison.OrdinalIgnoreCase) || item.DiscoveryKey.StartsWith("cli:", StringComparison.OrdinalIgnoreCase)
            ? item.DiscoveryKey[4..] : null;
    private static SupportedApplication? SavedDefinition(EgressApplicationSelection item) =>
        SelectionPath(item) is { } path
            ? SupportedApplicationCatalog.ForExecutable(path) ?? (new[] { "claude-ssh-askpass.exe", "claude-ssh-proxy.exe" }.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                ? SupportedApplicationCatalog.ForName("Claude") : null)
            : SupportedApplicationCatalog.ForName(item.DisplayName);

    public static IReadOnlyList<EgressApplicationSelection> Reconcile(IReadOnlyList<EgressApplicationSelection> saved,
        IReadOnlyList<LaunchTarget> targets)
    {
        var consumed = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<EgressApplicationSelection>();
        foreach (var target in targets)
        {
            var entries = saved.Where(item => !consumed.Contains(item.DiscoveryKey) && target.SelectionKeys.Contains(item.DiscoveryKey)).ToArray();
            if (entries.Length == 0) continue;
            foreach (var item in entries) consumed.Add(item.DiscoveryKey);
            if (entries.Select(item => item.Target).Distinct().Count() > 1)
            {
                // Keep differing saved intent visible as a conflict until the user unifies this row.
                result.AddRange(entries); continue;
            }
            result.Add(Merge(target, entries, entries[0].Target));
        }
        result.AddRange(saved.Where(item => !consumed.Contains(item.DiscoveryKey) && IsSupportedSaved(item)));
        return result;
    }

    public static bool IsSupportedSaved(EgressApplicationSelection item) => SavedDefinition(item) is not null;

    public static EgressApplicationSelection Merge(LaunchTarget target, IEnumerable<EgressApplicationSelection> previous, EgressRouteTarget route)
    {
        var saved = previous.ToArray();
        return new()
        {
            DiscoveryKey = target.DiscoveryKey, DisplayName = target.Name, Target = route,
            ExecutablePaths = target.OwnedExecutables.Concat(saved.SelectMany(item => item.ExecutablePaths))
                .Concat(target.CanonicalExecutable is { } path ? [path] : []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            DirectoryRoots = target.OwnedRoots.Concat(saved.SelectMany(item => item.DirectoryRoots)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
        };
    }

    private static bool SameFile(string? first, string? second) => !string.IsNullOrWhiteSpace(first) && string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
    private static bool Contains(LaunchTarget target, string? path) => path is not null && target.OwnedExecutables.Contains(path, StringComparer.OrdinalIgnoreCase);
    private static LaunchTarget Copy(LaunchTarget item, string name, string catalogId, string[] aliases, string? icon) => new()
    {
        Id = item.Id, SavedDiscoveryKey = item.SavedDiscoveryKey, Name = name, CatalogId = catalogId, RelatedDiscoveryKeys = aliases,
        Kind = item.Kind, Command = item.Command, Arguments = item.Arguments, WorkingDirectory = item.WorkingDirectory,
        Environment = item.Environment, PackageFamily = item.PackageFamily, Aumid = item.Aumid, CanonicalExecutable = item.CanonicalExecutable,
        OwnedRoots = item.OwnedRoots, OwnedExecutables = item.OwnedExecutables, RouteSelected = item.RouteSelected,
        ResolutionUnsupported = item.ResolutionUnsupported, IconPath = icon, Source = item.Source, Publisher = item.Publisher, Version = item.Version,
    };
}
