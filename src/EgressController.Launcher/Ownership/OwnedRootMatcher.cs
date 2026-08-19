using EgressController.Core.Models;

namespace EgressController.Launcher.Ownership;

/// <summary>
/// Decides whether a process's canonical final exe path belongs to a <see cref="LaunchTarget"/>
/// (plan §1.5 / §Step 09). Uses <b>path-segment</b> containment, never raw prefix: root
/// <c>C:\ExampleApp</c> must NOT claim <c>C:\ExampleApp-Evil\fake.exe</c>. The incoming path must be the
/// canonical final path (already junction/symlink-resolved by Step 08); a null final path means
/// the process is never owned (§1.6).
/// </summary>
public static class OwnedRootMatcher
{
    /// <summary>True when <paramref name="finalPath"/> is inside a root or equals an owned exe.</summary>
    public static bool IsOwned(string? finalPath, LaunchTarget target)
    {
        if (string.IsNullOrWhiteSpace(finalPath))
            return false;

        string f = Normalize(finalPath);

        foreach (string exe in target.OwnedExecutables)
            if (string.Equals(Normalize(exe), f, StringComparison.OrdinalIgnoreCase))
                return true;

        foreach (string root in target.OwnedRoots)
        {
            string r = Normalize(root);
            if (r.Length == 0)
                continue;

            // Exact root, or root followed by a directory separator (segment boundary).
            if (string.Equals(f, r, StringComparison.OrdinalIgnoreCase))
                return true;
            if (f.Length > r.Length
                && f.StartsWith(r, StringComparison.OrdinalIgnoreCase)
                && (f[r.Length] == '\\' || f[r.Length] == '/'))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Strict process ownership used for descendants of a launched application. The scanner
    /// populates <see cref="LaunchTarget.OwnedExecutables"/> recursively; a process whose final
    /// path is not in that snapshot is intentionally outside Managed routing, even if it happens
    /// to share a parent directory.
    /// </summary>
    public static bool IsScannedExecutable(string? finalPath, LaunchTarget target)
    {
        if (string.IsNullOrWhiteSpace(finalPath))
            return false;

        string normalized = Normalize(finalPath);
        return target.OwnedExecutables.Any(exe =>
            string.Equals(Normalize(exe), normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static string Normalize(string path)
        => path.Replace('/', '\\').TrimEnd('\\');
}
