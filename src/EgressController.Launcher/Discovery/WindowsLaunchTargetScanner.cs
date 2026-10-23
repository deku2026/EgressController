using Microsoft.Win32;
using System.Security.Principal;
using System.Xml.Linq;
using EgressController.Core.Models;
using EgressController.Core.Protection;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace EgressController.Launcher.Discovery;

/// <summary>
/// Discovers launchable Windows applications from the same primary sources used by
/// BCUninstaller: the per-user Store package API, the 32/64-bit uninstall registry and
/// App Paths, game-library install records and native AI commands resolved by Get-Command.
/// Only metadata and immediate directory entries are read; full EXE inventories are explicit work.
/// </summary>
public sealed class WindowsLaunchTargetScanner
{
    private const string AppModelPackages =
        "Software\\Classes\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\AppModel\\Repository\\Packages";
    private const string AppPaths = "Software\\Microsoft\\Windows\\CurrentVersion\\App Paths";
    private const string UninstallRoot = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall";
    private string _localAppData = "";

    public IReadOnlyList<string> Notices { get; private set; } = [];

    public IReadOnlyList<LaunchTarget> Scan()
    {
        RuntimeSafety.RequireLiveOperations();
        _localAppData = WindowsPackageLocations.CurrentUserLocalAppData();
        var registry = new LaunchTargetRegistry();
        DiscoverStorePackages(registry);
        DiscoverInstalledRegistryApps(registry);
        DiscoverAppPaths(registry);
        var notices = new List<string>();
        try { DiscoverPlatformLibraries(registry, notices); }
        catch (Exception error) { notices.Add("游戏平台目录读取未完成：" + error.Message); }
        try
        {
            var cli = new WindowsCliDiscovery(new PowerShellCommandLocationSource());
            foreach (var target in cli.Discover()) registry.Add(target);
            notices.AddRange(cli.Notices);
        }
        catch (Exception error) { notices.Add("CLI 路径查询未完成：" + error.Message); }
        Notices = notices;
        return ApplicationCatalogProjection.Project(registry.All());
    }

    /// <summary>
    /// BCUninstaller uses PackageManager rather than guessing package names from the AppModel
    /// registry. Keep the registry walk as a compatibility fallback for restricted or older
    /// Windows environments where the WinRT enumeration is unavailable.
    /// </summary>
    private void DiscoverStorePackages(LaunchTargetRegistry registry)
    {
        bool apiCompleted = false;
        try
        {
            string? userSid = WindowsIdentity.GetCurrent().User?.Value;
            if (!string.IsNullOrWhiteSpace(userSid))
            {
                var packageManager = new PackageManager();
                foreach (Package package in packageManager.FindPackagesForUserWithPackageTypes(userSid, PackageTypes.Main))
                {
                    if (package.Status.Disabled || package.Status.NotAvailable || package.IsFramework || package.IsResourcePackage)
                        continue;

                    string root = package.InstalledLocation.Path;
                    string effective = package.EffectiveLocation.Path;
                    DiscoverPackageManifest(
                        registry,
                        package.Id.FullName,
                        package.Id.FamilyName,
                        root,
                        string.Equals(root, effective, StringComparison.OrdinalIgnoreCase) ? null : effective,
                        package.DisplayName,
                        package.PublisherDisplayName);
                }

                apiCompleted = true;
            }
        }
        catch
        {
            // Fall back below. A single inaccessible WindowsApps package must not hide ARP apps.
        }

        if (!apiCompleted)
            DiscoverPackagesFromRepositoryRegistry(registry);
    }

    private void DiscoverPackagesFromRepositoryRegistry(LaunchTargetRegistry registry)
    {
        foreach (RegistryKey hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using RegistryKey? packages = hive.OpenSubKey(AppModelPackages, writable: false);
                if (packages is null)
                    continue;

                foreach (string packageKeyName in packages.GetSubKeyNames())
                {
                    try
                    {
                        using RegistryKey? package = packages.OpenSubKey(packageKeyName, writable: false);
                        string? root = package?.GetValue("PackageRootFolder") as string;
                        if (string.IsNullOrWhiteSpace(root))
                            continue;

                        string? packageName = package?.GetValue("DisplayName") as string;
                        string family = WindowsPackageLocations.FamilyFromFullName(packageKeyName);
                        DiscoverPackageManifest(registry, packageKeyName, family, root, null, packageName, null);
                    }
                    catch
                    {
                        // One stale package registration must not hide the rest of the catalog.
                    }
                }
            }
            catch
            {
                // Registry access can be denied for another user's package hive.
            }
        }
    }

    private void DiscoverPackageManifest(
        LaunchTargetRegistry registry,
        string packageName,
        string? packageFamily,
        string installedRoot,
        string? effectiveRoot,
        string? registeredDisplayName,
        string? publisher)
    {
        string manifestRoot = File.Exists(Path.Combine(installedRoot, "AppxManifest.xml"))
            ? installedRoot
            : effectiveRoot is not null && File.Exists(Path.Combine(effectiveRoot, "AppxManifest.xml"))
                ? effectiveRoot
                : string.Empty;
        if (manifestRoot.Length == 0)
            return;

        string manifestPath = Path.Combine(manifestRoot, "AppxManifest.xml");
        try
        {
            XDocument document = XDocument.Load(manifestPath, LoadOptions.None);
            XElement? identity = document.Descendants().FirstOrDefault(e => e.Name.LocalName == "Identity");
            string identityName = (string?)identity?.Attribute("Name") ?? packageName;
            string family = string.IsNullOrWhiteSpace(packageFamily) ? WindowsPackageLocations.FamilyFromFullName(packageName) : packageFamily;

            foreach (XElement application in document.Descendants().Where(e => e.Name.LocalName == "Application"))
            {
                XElement? visualElements = application.Elements()
                    .FirstOrDefault(e => e.Name.LocalName == "VisualElements");
                if (string.Equals((string?)visualElements?.Attribute("AppListEntry"), "none", StringComparison.OrdinalIgnoreCase))
                    continue;

                string appId = (string?)application.Attribute("Id") ?? "App";
                string? executable = (string?)application.Attribute("Executable");
                string? fullExe = ResolvePackageExecutable(installedRoot, effectiveRoot, executable);
                string aumid = family + "!" + appId;
                string display = FirstValid(
                        (string?)visualElements?.Attribute("DisplayName"),
                        registeredDisplayName,
                        identityName)
                    ?? identityName;

                var roots = PackageExecutableRoots.Resolve(installedRoot, effectiveRoot, _localAppData, family);

                registry.Add(new LaunchTarget
                {
                    Id = "pkg:" + aumid,
                    Name = display,
                    Kind = LaunchKind.PackagedAumid,
                    Command = fullExe,
                    CanonicalExecutable = fullExe,
                    PackageFamily = family,
                    Aumid = aumid,
                    OwnedRoots = roots,
                    OwnedExecutables = [], InventoryReady = false,
                    RouteSelected = false,
                    // Windows activates the registered AUMID. Launch UI separately requires
                    // a known primary EXE in the saved inventory; no PID/descendant correlation.
                    ResolutionUnsupported = string.IsNullOrWhiteSpace(aumid),
                    IconPath = FindManifestIcon(document, installedRoot, effectiveRoot),
                    Source = "Microsoft Store",
                    Publisher = FirstValid(publisher),
                });
            }
        }
        catch
        {
            // XML can be temporarily incomplete while Store updates a package.
        }
    }

    private static string? ResolvePackageExecutable(string installedRoot, string? effectiveRoot, string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
            return null;

        foreach (string root in new[] { installedRoot, effectiveRoot }.Where(p => !string.IsNullOrWhiteSpace(p))!)
        {
            string path = Path.GetFullPath(Path.Combine(root, executable));
            if (IsWithin(path, root) && File.Exists(path))
                return path;
        }
        return null;
    }

    private static string? FindManifestIcon(XDocument document, string installedRoot, string? effectiveRoot)
    {
        var resources = new List<string?>();
        resources.AddRange(document.Descendants()
            .Where(e => e.Name.LocalName == "Properties")
            .Select(e => (string?)e.Element(e.Name.Namespace + "Logo")));
        resources.AddRange(document.Descendants()
            .Where(e => e.Name.LocalName == "VisualElements")
            .SelectMany(e => new[]
            {
                (string?)e.Attribute("Square44x44Logo"),
                (string?)e.Attribute("Square150x150Logo"),
                (string?)e.Attribute("Logo")
            }));

        foreach (string root in new[] { installedRoot, effectiveRoot }.Where(p => !string.IsNullOrWhiteSpace(p))!)
        {
            foreach (string? resource in resources)
            {
                string? path = ResolvePackageAsset(root, resource);
                if (path is not null)
                    return path;
            }
        }
        return null;
    }

    /// <summary>Mirrors BCUninstaller's scale-100 and localized asset fallback, with common
    /// Store asset qualifiers added for packages that ship only scale-125/200/targetsize files.</summary>
    private static string? ResolvePackageAsset(string root, string? resource)
    {
        if (string.IsNullOrWhiteSpace(resource)
            || resource.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase)
            || resource.Contains(':', StringComparison.Ordinal))
            return null;

        string relative = resource.Trim().TrimStart('\\', '/').Replace('/', Path.DirectorySeparatorChar);
        var candidates = new List<string>();
        foreach (string baseRoot in new[] { root, Path.Combine(root, "en-us") })
        {
            string basePath = Path.GetFullPath(Path.Combine(baseRoot, relative));
            if (!IsWithin(basePath, baseRoot))
                continue;

            candidates.Add(basePath);
            string directory = Path.GetDirectoryName(basePath) ?? baseRoot;
            string stem = Path.Combine(directory, Path.GetFileNameWithoutExtension(basePath));
            string extension = Path.GetExtension(basePath);
            if (extension.Length == 0)
                extension = ".png";

            foreach (string qualifier in new[] { "scale-100", "scale-125", "scale-150", "scale-200", "scale-400", "targetsize-16", "targetsize-24", "targetsize-48", "targetsize-256" })
                candidates.Add(stem + "." + qualifier + extension);
        }

        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).FirstOrDefault(File.Exists);
    }

    private void DiscoverInstalledRegistryApps(LaunchTargetRegistry registry)
    {
        foreach (RegistryHive hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        {
            foreach (RegistryView view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using RegistryKey baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using RegistryKey? uninstall = baseKey.OpenSubKey(UninstallRoot, writable: false);
                    if (uninstall is null)
                        continue;

                    foreach (string keyName in uninstall.GetSubKeyNames())
                    {
                        try
                        {
                            using RegistryKey? key = uninstall.OpenSubKey(keyName, writable: false);
                            if (key is null)
                                continue;

                            string? name = GetString(key, "DisplayName");
                            if (string.IsNullOrWhiteSpace(name) || IsHiddenRegistryEntry(key))
                                continue;

                            string? iconPath = RegisteredExecutablePath.Parse(GetString(key, "DisplayIcon"), icon: true);
                            string? installLocation = ResolveDirectory(GetString(key, "InstallLocation"))
                                ?? NativeInstallLocations.MsiLocation(keyName, hive == RegistryHive.LocalMachine);
                            AddInstalled(registry, new($"arp:{hive}:{view}:{keyName}", name.Trim(), installLocation, null,
                                iconPath, GetString(key, "UninstallString"), "Windows 安装登记", GetString(key, "Publisher"), GetString(key, "DisplayVersion")));
                        }
                        catch
                        {
                            // Ignore one malformed or inaccessible ARP entry.
                        }
                    }
                }
                catch
                {
                    // A missing registry view is normal on some Windows installations.
                }
            }
        }
    }

    private static bool IsHiddenRegistryEntry(RegistryKey key)
    {
        if (ToInt32(key.GetValue("SystemComponent", 0)) != 0 || ToInt32(key.GetValue("NoDisplay", 0)) != 0)
            return true;
        if (!string.IsNullOrWhiteSpace(GetString(key, "ParentKeyName")))
            return true;

        string? releaseType = GetString(key, "ReleaseType");
        return releaseType is not null
            && (releaseType.Contains("update", StringComparison.OrdinalIgnoreCase)
                || releaseType.Contains("hotfix", StringComparison.OrdinalIgnoreCase));
    }

    private void DiscoverAppPaths(LaunchTargetRegistry registry)
    {
        foreach (RegistryKey hive in new[] { RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default), RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default) })
        {
            try
            {
                using (hive)
                using (RegistryKey? root = hive.OpenSubKey(AppPaths, writable: false))
                {
                    if (root is null)
                        continue;

                    foreach (string name in root.GetSubKeyNames())
                    {
                        try
                        {
                            using RegistryKey? key = root.OpenSubKey(name, writable: false);
                            string? exe = RegisteredExecutablePath.Parse(GetString(key, string.Empty));
                            if (!IsExecutable(exe))
                                continue;
                            AddNative(registry, exe!, Path.GetFileNameWithoutExtension(name), LaunchKind.DirectExe, "App Paths");
                        }
                        catch
                        {
                            // Ignore one broken App Paths entry.
                        }
                    }
                }
            }
            catch
            {
                // Registry access is optional.
            }
        }
    }

    private void AddNative(LaunchTargetRegistry registry, string executable, string name, LaunchKind kind, string source)
    {
        string full = Path.GetFullPath(executable);
        if (InstalledApplicationResolver.IsInstaller(full)) return;
        string[] roots = new[] { NormalizeRoot(Path.GetDirectoryName(full) ?? string.Empty) };
        registry.Add(new LaunchTarget
        {
            Id = (kind == LaunchKind.CliNative ? "cli:" : "exe:") + full.ToLowerInvariant(),
            Name = name,
            Kind = kind,
            Command = full,
            CanonicalExecutable = full,
            OwnedRoots = roots,
            OwnedExecutables = [], InventoryReady = false,
            RouteSelected = false,
            IconPath = full,
            Source = source,
        });
    }

    private static string? ResolveDirectory(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        string full = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
        return Directory.Exists(full) ? Path.GetFullPath(full) : null;
    }

    private static bool IsExecutable(string? path)
        => path is not null && File.Exists(path) && Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase);

    private static string? GetString(RegistryKey? key, string name)
        => key?.GetValue(name) is object value ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) : null;

    private static int ToInt32(object? value)
    {
        try { return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture); }
        catch { return 0; }
    }

    private static string? FirstValid(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)
            && !value.StartsWith("@", StringComparison.Ordinal)
            && !value.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase));

    private static bool IsWithin(string path, string root)
    {
        string fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(fullPath, fullRoot, StringComparison.OrdinalIgnoreCase)
            || (fullPath.Length > fullRoot.Length
                && fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)
                && fullPath[fullRoot.Length] == Path.DirectorySeparatorChar);
    }

    private static string NormalizeRoot(string root)
        => root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static void AddInstalled(LaunchTargetRegistry registry, InstalledApplication application)
        => registry.Add(InstalledApplicationResolver.Resolve(application, File.Exists, Directory.Exists,
            root => Directory.EnumerateFiles(root, "*.exe", SearchOption.TopDirectoryOnly)));

    private static void DiscoverPlatformLibraries(LaunchTargetRegistry registry, List<string> notices)
    {
        foreach (string steam in NativeInstallLocations.SteamRoots(notices))
            AddInstalled(registry, new("steam-client:" + steam.ToLowerInvariant(), "Steam", steam, Path.Combine(steam, "steam.exe"), Path.Combine(steam, "steam.exe"), null, "游戏平台安装登记"));
        foreach (var game in NativeInstallLocations.TencentGames(notices))
        {
            try { AddInstalled(registry, game); }
            catch (Exception error) { notices.Add(game.Name + " 安装目录读取失败：" + error.Message); }
        }
        var steamRoots = registry.All().Where(target => target.Name.Equals("Steam", StringComparison.OrdinalIgnoreCase))
            .SelectMany(target => target.OwnedRoots).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var libraries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string steam in steamRoots)
        {
            libraries.Add(steam);
            foreach (string relative in new[] { @"steamapps\libraryfolders.vdf", @"config\libraryfolders.vdf" })
            {
                string file = Path.Combine(steam, relative);
                if (!File.Exists(file)) continue;
                try { foreach (string root in GameLibraryManifest.SteamLibraries(steam, File.ReadAllText(file))) libraries.Add(root); }
                catch (Exception error) { notices.Add("Steam 游戏库读取失败：" + error.Message); }
            }
        }
        foreach (string library in libraries)
        {
            string manifests = Path.Combine(library, "steamapps");
            if (!Directory.Exists(manifests)) continue;
            try
            {
                foreach (string path in Directory.EnumerateFiles(manifests, "appmanifest_*.acf", SearchOption.TopDirectoryOnly))
                    try
                    {
                        if (GameLibraryManifest.SteamGame(library, File.ReadAllText(path)) is { } game && Directory.Exists(game.Location)) AddInstalled(registry, game);
                    }
                    catch (Exception error) { notices.Add("Steam 游戏清单读取失败：" + Path.GetFileName(path) + " · " + error.Message); }
            }
            catch (Exception error) { notices.Add("Steam 游戏库不可读取：" + error.Message); }
        }
    }

}
