using EgressController.Core.Models;

namespace EgressController.Launcher.Discovery;

public sealed record InstalledApplication(string Key, string Name, string? Location, string? Entry,
    string? Icon, string? UninstallCommand, string Source, string? Publisher = null, string? Version = null);

/// <summary>Metadata-first discovery. Resolving a registered root never enumerates its descendants.</summary>
public static class InstalledApplicationResolver
{
    public static LaunchTarget Resolve(InstalledApplication app, Func<string, bool> fileExists,
        Func<string, bool> directoryExists, Func<string, IEnumerable<string>> topExecutables)
    {
        string? entry = ExistingExecutable(app.Entry, fileExists);
        string? icon = RegisteredExecutablePath.Parse(app.Icon, icon: true);
        string? root = DirectoryPath(app.Location, directoryExists);
        entry ??= ExistingExecutable(icon, fileExists);
        root ??= entry is null ? null : DirectoryPath(Path.GetDirectoryName(entry), directoryExists);
        if (root is null && UninstallExecutable(app.UninstallCommand) is { } uninstaller && fileExists(uninstaller))
        {
            string? candidate = DirectoryPath(Path.GetDirectoryName(uninstaller), directoryExists);
            if (candidate is not null && !candidate.Contains("\\Package Cache\\", StringComparison.OrdinalIgnoreCase)) root = candidate;
        }
        if (entry is null && root is not null)
        {
            try
            {
                // A directory inventory is not a launch contract. Never choose an arbitrary
                // helper just because it happens to sort first among the EXEs.
                entry = topExecutables(root).Where(path => ExistingExecutable(path, fileExists) is not null).Take(64)
                    .Where(path => Path.GetFileNameWithoutExtension(path).Equals(Path.GetFileName(root), StringComparison.OrdinalIgnoreCase)
                        || Path.GetFileNameWithoutExtension(path).Equals(app.Name, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(path => Path.GetFileNameWithoutExtension(path).Equals(Path.GetFileName(root), StringComparison.OrdinalIgnoreCase))
                    .ThenByDescending(path => Path.GetFileNameWithoutExtension(path).Equals(app.Name, StringComparison.OrdinalIgnoreCase))
                    .ThenBy(path => path, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Keep the registered app visible; explicit inventory building reports the exact error. */ }
        }
        string[] aliases = entry is null ? [] : ["exe:" + entry];
        return new() { Id = app.Key, SavedDiscoveryKey = app.Key, Name = app.Name, Kind = LaunchKind.DirectExe,
            Command = entry, CanonicalExecutable = entry, OwnedRoots = root is null ? [] : [root],
            OwnedExecutables = [], InventoryReady = false, RelatedDiscoveryKeys = aliases,
            IconPath = icon is not null && !IsInstaller(icon) && fileExists(icon) ? icon : entry, Source = app.Source, Publisher = app.Publisher, Version = app.Version };
    }

    public static bool IsInstaller(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        return new[] { "uninstall", "unins", "setup", "repair", "卸载", "安装" }.Any(value => name.Contains(value, StringComparison.OrdinalIgnoreCase))
            || new[] { "msiexec", "rundll32", "cmd", "powershell", "pwsh" }.Contains(name, StringComparer.OrdinalIgnoreCase);
    }
    public static string? UninstallExecutable(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        string text = Environment.ExpandEnvironmentVariables(command.Trim());
        if (text.StartsWith('"')) { int end = text.IndexOf('"', 1); return end > 1 ? RegisteredExecutablePath.Parse(text[..(end + 1)]) : null; }
        int exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe < 0 ? null : RegisteredExecutablePath.Parse(text[..(exe + 4)]);
    }
    private static string? ExistingExecutable(string? path, Func<string, bool> exists)
    {
        string? full = RegisteredExecutablePath.Parse(path);
        return full is not null && full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !IsInstaller(full) && exists(full) ? full : null;
    }
    private static string? DirectoryPath(string? path, Func<string, bool> exists)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            string full = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim().Trim('"')));
            ApplicationDirectoryBoundary.RequireScoped(full);
            return exists(full) ? full : null;
        }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException) { return null; }
    }
}
