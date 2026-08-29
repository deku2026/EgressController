using Microsoft.Win32;
using System.Diagnostics;
using System.Security.Principal;
using System.Xml.Linq;
using EgressController.Core.Models;
using Windows.ApplicationModel;
using Windows.Management.Deployment;

namespace EgressController.Launcher.Discovery;

/// <summary>
/// Discovers launchable Windows applications from the same primary sources used by
/// BCUninstaller: the per-user Store package API, the 32/64-bit uninstall registry and
/// App Paths. PATH/CLI entries and shortcuts are excluded. The resulting broad discovery is
/// filtered through <see cref="SupportedApplicationCatalog"/> so the UI only exposes AI clients
/// and browsers; no manual EXE fallback exists.
/// </summary>
public sealed class WindowsLaunchTargetScanner
{
    private const string AppModelPackages =
        "Software\\Classes\\Local Settings\\Software\\Microsoft\\Windows\\CurrentVersion\\AppModel\\Repository\\Packages";
    private const string AppPaths = "Software\\Microsoft\\Windows\\CurrentVersion\\App Paths";
    private const string UninstallRoot = "SOFTWARE\\Microsoft\\Windows\\CurrentVersion\\Uninstall";
    private readonly Dictionary<string, IReadOnlyList<string>> _inventoryCache = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<LaunchTarget> Scan()
    {
        _inventoryCache.Clear();
        var registry = new LaunchTargetRegistry();
        DiscoverStorePackages(registry);
        DiscoverInstalledRegistryApps(registry);
        DiscoverAppPaths(registry);
        DiscoverProgramFilesApps(registry);
        return registry.All().Where(SupportedApplicationCatalog.IsSupported).ToArray();
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
                        package.Id.Name,
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
                        string[] parts = packageKeyName.Split(new[] { "__" }, StringSplitOptions.None);
                        string family = parts.Length > 1 && parts[^1].Length > 0
                            ? (parts[0].Length > 0 ? parts[0] : packageKeyName) + "_" + parts[^1]
                            : packageKeyName;
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
            string family = string.IsNullOrWhiteSpace(packageFamily) ? identityName : packageFamily;

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

                var roots = new[] { installedRoot, effectiveRoot }
                    .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
                    .Select(p => NormalizeRoot(p!))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (roots.Length == 0)
                    continue;

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
                    OwnedExecutables = CollectExecutables(roots, fullExe),
                    EsimSelected = false,
                    // AUMID activation is valid even when the manifest omits Executable. The
                    // launch service correlates the resulting process against OwnedRoots.
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

                            string? iconPath = ResolveRegisteredPath(GetString(key, "DisplayIcon"));
                            string? installLocation = ResolveDirectory(GetString(key, "InstallLocation"));
                            string? executable = FindRegisteredExecutable(name, iconPath, installLocation);
                            if (executable is null)
                                continue;

                            var roots = new[] { installLocation, Path.GetDirectoryName(executable) }
                                .Where(p => !string.IsNullOrWhiteSpace(p) && Directory.Exists(p))
                                .Select(p => NormalizeRoot(p!))
                                .Distinct(StringComparer.OrdinalIgnoreCase)
                                .ToArray();
                            if (roots.Length == 0)
                                continue;

                            registry.Add(new LaunchTarget
                            {
                                Id = $"arp:{hive}:{view}:{keyName}",
                                Name = name.Trim(),
                                Kind = LaunchKind.DirectExe,
                                Command = executable,
                                CanonicalExecutable = executable,
                                OwnedRoots = roots,
                                OwnedExecutables = CollectExecutables(roots, executable),
                                EsimSelected = false,
                                ResolutionUnsupported = false,
                                IconPath = iconPath,
                                Source = "Windows 卸载注册表",
                                Publisher = GetString(key, "Publisher"),
                                Version = GetString(key, "DisplayVersion"),
                            });
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

    private static string? FindRegisteredExecutable(string displayName, string? iconPath, string? installLocation)
    {
        if (IsExecutable(iconPath))
            return Path.GetFullPath(iconPath!);
        if (string.IsNullOrWhiteSpace(installLocation) || !Directory.Exists(installLocation))
            return null;

        string displayKey = SimplifyName(displayName);
        try
        {
            var candidates = Directory.EnumerateFiles(installLocation, "*.exe", SearchOption.TopDirectoryOnly)
                .Where(path => !IsLikelyInstaller(path))
                .Select(path => new
                {
                    Path = Path.GetFullPath(path),
                    Score = ScoreExecutable(Path.GetFileNameWithoutExtension(path), displayKey)
                })
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return candidates.Length == 0 ? null : candidates[0].Path;
        }
        catch
        {
            return null;
        }
    }

    private static int ScoreExecutable(string fileName, string displayKey)
    {
        string key = SimplifyName(fileName);
        if (key.Length == 0)
            return 0;
        if (key.Equals(displayKey, StringComparison.Ordinal))
            return 100;
        if (displayKey.Length > 0 && (key.Contains(displayKey, StringComparison.Ordinal) || displayKey.Contains(key, StringComparison.Ordinal)))
            return 50;
        return 1;
    }

    private static bool IsLikelyInstaller(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        return name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)
            || name.Contains("unins", StringComparison.OrdinalIgnoreCase)
            || name.Contains("setup", StringComparison.OrdinalIgnoreCase)
            || name.Contains("update", StringComparison.OrdinalIgnoreCase)
            || name.Contains("updater", StringComparison.OrdinalIgnoreCase)
            || name.Contains("repair", StringComparison.OrdinalIgnoreCase);
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
                            string? exe = ResolveRegisteredPath(GetString(key, string.Empty));
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

    /// <summary>
    /// Mirrors BCUninstaller's DirectoryFactory for applications which are not registered in ARP
    /// (portable installs and vendors that only place a launcher under Program Files). Only the
    /// first level is used to identify an application; once identified, its whole directory is
    /// recursively indexed for process ownership.
    /// </summary>
    private void DiscoverProgramFilesApps(LaunchTargetRegistry registry)
    {
        string[] knownRoots = registry.All()
            .SelectMany(target => target.OwnedRoots)
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(NormalizeRoot)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (string programFilesRoot in GetProgramFilesRoots())
        {
            try
            {
                foreach (string directory in Directory.EnumerateDirectories(programFilesRoot, "*", SearchOption.TopDirectoryOnly))
                {
                    if (IsSystemProgramFilesDirectory(directory)
                        || knownRoots.Any(root => IsWithin(directory, root) || IsWithin(root, directory)))
                        continue;

                    string[] candidates = FindProgramDirectoryExecutables(directory);
                    if (candidates.Length == 0)
                        continue;

                    string executable = SelectPrimaryExecutable(directory, candidates);
                    string root = NormalizeRoot(directory);
                    IReadOnlyList<string> ownedExecutables = CollectExecutables(new[] { root }, executable);
                    if (ownedExecutables.Count == 0)
                        continue;

                    FileVersionInfo? versionInfo = TryGetVersionInfo(executable);
                    string name = FirstValid(versionInfo?.ProductName, versionInfo?.FileDescription, Path.GetFileName(directory))
                        ?? Path.GetFileName(directory);

                    registry.Add(new LaunchTarget
                    {
                        Id = "dir:" + root.ToLowerInvariant(),
                        Name = name,
                        Kind = LaunchKind.DirectExe,
                        Command = executable,
                        CanonicalExecutable = executable,
                        OwnedRoots = new[] { root },
                        OwnedExecutables = ownedExecutables,
                    EsimSelected = false,
                        ResolutionUnsupported = false,
                        IconPath = executable,
                        Source = "Program Files",
                        Publisher = FirstValid(versionInfo?.CompanyName),
                        Version = FirstValid(versionInfo?.ProductVersion, versionInfo?.FileVersion),
                    });
                }
            }
            catch
            {
                // Program Files may contain protected or transient vendor directories.
            }
        }
    }

    private static IEnumerable<string> GetProgramFilesRoots()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("ProgramW6432"),
            Environment.GetEnvironmentVariable("ProgramFiles"),
            Environment.GetEnvironmentVariable("ProgramFiles(x86)"),
        };

        return candidates
            .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .Select(path => Path.GetFullPath(path!))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static string[] FindProgramDirectoryExecutables(string directory)
    {
        try
        {
            var candidates = Directory.EnumerateFiles(directory, "*.exe", SearchOption.TopDirectoryOnly)
                .Where(path => !IsLikelyInstaller(path))
                .Select(Path.GetFullPath)
                .ToList();

            // Match BCU's binary-directory convention for apps whose root contains only x64/x86/bin.
            foreach (string childName in new[] { "x64", "x86", "bin", "app", "program" })
            {
                string child = Path.Combine(directory, childName);
                if (!Directory.Exists(child))
                    continue;

                candidates.AddRange(Directory.EnumerateFiles(child, "*.exe", SearchOption.TopDirectoryOnly)
                    .Where(path => !IsLikelyInstaller(path))
                    .Select(Path.GetFullPath));
            }

            return candidates
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(40) // The same safety bound used by BCU's directory factory.
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string SelectPrimaryExecutable(string directory, IReadOnlyList<string> candidates)
    {
        string directoryKey = SimplifyName(Path.GetFileName(directory));
        return candidates
            .Select(path => new
            {
                Path = path,
                Score = ScoreExecutable(Path.GetFileNameWithoutExtension(path), directoryKey)
                    + (Path.GetDirectoryName(path)?.Equals(directory, StringComparison.OrdinalIgnoreCase) == true ? 5 : 0),
            })
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .First().Path;
    }

    private static FileVersionInfo? TryGetVersionInfo(string path)
    {
        try { return FileVersionInfo.GetVersionInfo(path); }
        catch { return null; }
    }

    private static bool IsSystemProgramFilesDirectory(string directory)
    {
        string name = Path.GetFileName(directory);
        return name.Equals("Common Files", StringComparison.OrdinalIgnoreCase)
            || name.Equals("WindowsApps", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Windows Defender", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Microsoft SDKs", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Reference Assemblies", StringComparison.OrdinalIgnoreCase)
            || name.Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Internet Explorer", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Windows Media Player", StringComparison.OrdinalIgnoreCase);
    }

    private void AddNative(LaunchTargetRegistry registry, string executable, string name, LaunchKind kind, string source)
    {
        string full = Path.GetFullPath(executable);
        string[] roots = new[] { NormalizeRoot(Path.GetDirectoryName(full) ?? string.Empty) };
        registry.Add(new LaunchTarget
        {
            Id = (kind == LaunchKind.CliNative ? "cli:" : "exe:") + full.ToLowerInvariant(),
            Name = name,
            Kind = kind,
            Command = full,
            CanonicalExecutable = full,
            OwnedRoots = roots,
            OwnedExecutables = CollectExecutables(roots, full),
            EsimSelected = false,
            IconPath = full,
            Source = source,
        });
    }

    private static string? ResolveRegisteredPath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith("@", StringComparison.Ordinal))
            return null;

        string text = value.Trim();
        if (text.StartsWith('"'))
        {
            int end = text.IndexOf('"', 1);
            if (end > 1)
                text = text[1..end];
        }
        else
        {
            int comma = text.IndexOf(',');
            int space = text.IndexOf(' ');
            int end = comma >= 0 && (space < 0 || comma < space) ? comma : space;
            if (end > 0)
                text = text[..end];
        }

        text = Environment.ExpandEnvironmentVariables(text.Trim().Trim('"'));
        return Path.IsPathFullyQualified(text) ? Path.GetFullPath(text) : null;
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

    private static string SimplifyName(string value)
        => new string(value.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();

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

    private IReadOnlyList<string> CollectExecutables(IEnumerable<string> roots, string? primary)
        => ExecutableInventory.Collect(roots, primary, _inventoryCache);
}
