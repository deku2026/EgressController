namespace EgressController.Launcher.Discovery;

/// <summary>Associate a registered package with its own user-data directory. These roots are
/// discovery inputs only: routing and protection still use the collected, saved EXE paths.</summary>
public static class PackageExecutableRoots
{
    public static string DataRoot(string localAppData, string packageFamilyName)
    {
        if (!Path.IsPathFullyQualified(localAppData)) throw new ArgumentException("LocalAppData 必须是绝对路径。", nameof(localAppData));
        if (!IsFamilyName(packageFamilyName)) throw new ArgumentException("无效的 Windows 包家族标识。", nameof(packageFamilyName));
        return Path.GetFullPath(Path.Combine(localAppData, "Packages", packageFamilyName));
    }

    public static IReadOnlyList<string> Resolve(string installedRoot, string? effectiveRoot, string localAppData, string packageFamilyName)
    {
        if (!Path.IsPathFullyQualified(installedRoot) || effectiveRoot is not null && !Path.IsPathFullyQualified(effectiveRoot))
            throw new ArgumentException("包安装目录必须是绝对路径。");
        // Keep the declared data root even before first launch creates it. A later explicit
        // inventory refresh can then discover it without a new installation/catalog scan.
        return new[] { installedRoot, effectiveRoot, DataRoot(localAppData, packageFamilyName) }
            .OfType<string>().Select(Path.GetFullPath).Select(Path.TrimEndingDirectorySeparator)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static bool IsFamilyName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        int separator = value.LastIndexOf('_');
        if (separator is < 3 or > 50 || value.Length - separator - 1 != 13) return false;
        return value[..separator].All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-')
            && value[(separator + 1)..].All(character => "0123456789abcdefghjkmnpqrstvwxyz".Contains(char.ToLowerInvariant(character)));
    }

    public static bool Covers(IEnumerable<string> indexedRoots, string candidate)
    {
        string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        return indexedRoots.Any(root =>
        {
            string parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
            return path.Equals(parent, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        });
    }
}
