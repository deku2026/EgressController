using EgressController.Core.Contracts;
using EgressController.Core.Profile;

namespace EgressController.Core.Protection;

/// <summary>Keep the selected spelling and its resolved identity; all consumers share both.</summary>
public static class ApplicationPathAliases
{
    public static string[] Expand(IEnumerable<string> paths, IExecutablePathCanonicalizer resolver)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string path in paths)
        {
            string full = Path.GetFullPath(path);
            result.Add(full);
            string? final = resolver.Canonicalize(full);
            if (!string.IsNullOrWhiteSpace(final)) result.Add(Path.GetFullPath(final));
        }
        return result.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static EgressProfileDocument Expand(EgressProfileDocument profile, IExecutablePathCanonicalizer resolver)
        => profile with
        {
            Applications = profile.Applications.Select(item => item with
            {
                ExecutablePaths = Expand(item.ExecutablePaths.Concat(item.DiscoveryKey.StartsWith("exe:", StringComparison.OrdinalIgnoreCase)
                    ? [item.DiscoveryKey[4..]] : []), resolver),
                DirectoryRoots = Expand(item.DirectoryRoots, resolver),
            }).ToArray(),
        };
}
