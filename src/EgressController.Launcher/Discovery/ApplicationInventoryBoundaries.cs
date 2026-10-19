using EgressController.Core.Models;

namespace EgressController.Launcher.Discovery;

public static class ApplicationInventoryBoundaries
{
    public static bool IsPlatformClient(LaunchTarget target)
        => new[] { "steam.exe", "wegame.exe", "tgpclient.exe" }.Contains(Path.GetFileName(target.CanonicalExecutable ?? ""), StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> ExcludedRoots(LaunchTarget target, IReadOnlyList<LaunchTarget> catalog)
    {
        if (!IsPlatformClient(target)) return [];
        // Steam's default library lives under its client root. WeGame can use this layout too.
        // Selecting the client must never select all the separately installed games.
        return target.OwnedRoots.SelectMany(root => new[] { Path.Combine(root, "steamapps"), Path.Combine(root, "WeGameApps") })
            .Concat(catalog.Where(item => item.DiscoveryKey != target.DiscoveryKey && item.Source is "Steam 游戏库" or "游戏平台安装登记")
                .SelectMany(item => item.OwnedRoots).Where(root => target.OwnedRoots.Any(parent => IsInside(root, parent))))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public static bool IsInside(string path, string root)
        => Path.GetFullPath(path).StartsWith(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
