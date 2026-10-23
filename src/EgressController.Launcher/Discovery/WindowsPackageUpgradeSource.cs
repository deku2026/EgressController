using System.Security.Principal;
using EgressController.Core.Profile;
using EgressController.Core.Protection;
using Windows.Management.Deployment;

namespace EgressController.Launcher.Discovery;

/// <summary>Bounded metadata checks for selected Store apps; never enumerate a directory tree.</summary>
public static class WindowsPackageUpgradeSource
{
    public static PackagePathUpgrade Read(EgressProfileDocument profile)
    {
        RuntimeSafety.RequireLiveOperations();
        var families = profile.Applications.Select(item => PackagePathUpgrade.FamilyOfSelection(item.DiscoveryKey))
            .OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (families.Count == 0) return new([]);
        string user = WindowsIdentity.GetCurrent().User?.Value ?? throw new IOException("无法读取当前 Windows 用户身份。");
        var locations = new List<RegisteredPackageLocation>();
        foreach (var package in new PackageManager().FindPackagesForUserWithPackageTypes(user, PackageTypes.Main))
        {
            if (!families.Contains(package.Id.FamilyName) || package.IsFramework || package.IsResourcePackage
                || package.Status.Disabled || package.Status.NotAvailable) continue;
            locations.Add(new(package.Id.FamilyName, new[] { package.InstalledLocation.Path, package.EffectiveLocation.Path }
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray()));
        }
        var identities = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        string? Family(string root)
        {
            if (identities.TryGetValue(root, out var family)) return family;
            try { family = WindowsPackageLocations.FamilyFromFullName(Path.GetFileName(Path.TrimEndingDirectorySeparator(root))); }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or ArgumentException) { family = null; }
            identities[root] = family;
            return family;
        }
        return PackagePathUpgrade.Plan(profile, locations, Family, File.Exists);
    }
}
